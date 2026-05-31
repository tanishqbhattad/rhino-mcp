// RhinoAIBridge v4.7.5 â€” AIBridgeServer.cs
// by tanishqb | https://github.com/tanishqb/rhino-ai-bridge

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoAIBridge
{
    /// <summary>
    /// TCP server. Length-prefixed JSON frames. One persistent UI-thread dispatcher
    /// instead of one ManualResetEvent per call. Async accept loop â€” no Sleep(100) tax.
    /// </summary>
    public class AIBridgeServer
    {
        private const int PORT = 9544;
        public const string PROTOCOL_VERSION = "4.7";

        // Cap concurrent client connections so a flood of fire-and-forget tasks can't
        // exhaust threads/sockets. (security hardening #4)
        private const int MAX_CLIENTS = 8;

        // Idle read timeout (ms). A client that opens the socket and sends nothing
        // gets dropped instead of parking a thread forever. (bug 1.15)
        private const int IDLE_READ_TIMEOUT_MS = 60_000;

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly object _lifecycleLock = new object();
        private bool _running;
        private readonly CommandHandler _handler = new CommandHandler();

        // Per-session shared secret. Required as the first frame from every client so a
        // random local process can't drive Rhino over the loopback socket. (bug 1.2)
        private string _authToken;
        private bool _requireAuth;

        // Build hash captured once at startup â€” useful when 5 versions of the .rhp are on disk.
        public static string BuildHash { get; private set; } = ComputeBuildHash();

        public bool IsRunning => _running;

        private static string ComputeBuildHash()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var loc = asm.Location;
                if (string.IsNullOrEmpty(loc) || !File.Exists(loc)) return "unknown";
                using var s = File.OpenRead(loc);
                using var md5 = System.Security.Cryptography.MD5.Create();
                var bytes = md5.ComputeHash(s);
                return BitConverter.ToString(bytes, 0, 4).Replace("-", "").ToLowerInvariant();
            }
            catch { return "unknown"; }
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_running) { RhinoApp.WriteLine("AIBridge: Already running"); return; }
                _running = true;
            }
            RhinoApp.WriteLine("AIBridge: Starting...");
            try
            {
                AIBridgeLogger.Initialize();
            }
            catch (Exception ex)
            {
                lock (_lifecycleLock) _running = false;
                RhinoApp.WriteLine($"AIBridge: Logger init failed - {ex.Message}");
                return;
            }

            // Operating mode is enforced in CommandHandler.Dispatch (so it can't be bypassed by
            // talking raw TCP). Startup is SILENT and non-blocking (this runs inside Rhino's
            // auto-start Idle handler): take an env override if present, else default to Safe.
            // The user is asked to choose via the AIBridge command (PromptAndApplyMode). (bug 1.2)
            CommandHandler.Mode = ModeFromEnvOrDefault();
            AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Operating mode (startup default): {CommandHandler.Mode}");

            RhinoApp.WriteLine("AIBridge: Preparing local authentication...");
            InitAuthToken();
            UiDispatcher.Start();
            // SceneSnapshot registry must be initialized BEFORE the listener accepts clients.
            // Otherwise an early read tool could find a missing snapshot.
            // Has to run on the UI thread because it touches RhinoDoc.ActiveDoc and subscribes to events.
            try
            {
                RhinoApp.WriteLine("AIBridge: Wiring Rhino document events...");
                if (RhinoApp.InvokeRequired)
                    RhinoApp.InvokeOnUiThread(new Action(() => SceneSnapshotRegistry.Initialize()));
                else
                    SceneSnapshotRegistry.Initialize();
            }
            catch (Exception ex)
            {
                AIBridgeLogger.Log(LogLevel.ERROR, "Server", "Snapshot registry init failed", error: ex.ToString());
            }


            try
            {
                if (RhinoApp.InvokeRequired)
                    RhinoApp.InvokeOnUiThread(new Action(() => ChangeTracker.Initialize()));
                else
                    ChangeTracker.Initialize();
            }
            catch (Exception ctEx)
            {
                AIBridgeLogger.Log(LogLevel.ERROR, "Server", "ChangeTracker init failed", error: ctEx.ToString());
            }
            try
            {
                RhinoApp.WriteLine("AIBridge: Opening local listener...");
                _listener = new TcpListener(IPAddress.Parse("127.0.0.1"), PORT);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoop(_cts.Token));

                RhinoApp.WriteLine("==================================================");
                RhinoApp.WriteLine("  Rhino AI Bridge v4.7.6 (C#)");
                RhinoApp.WriteLine($"  Listening on 127.0.0.1:{PORT}  build:{BuildHash}");
                RhinoApp.WriteLine("  Phase 1: deferred redraw, async I/O, lean responses");
                RhinoApp.WriteLine("  Phase 2: scene snapshot cache + scene_version etag");
                RhinoApp.WriteLine("  Phase 3: atomic batches + reference resolution ($1.object_ids[0])");
                RhinoApp.WriteLine("  Phase 5: architect intelligence (massing, floors, core, facade, schedules)");
                RhinoApp.WriteLine("  Phase 6: consolidated 90-tool MCP surface");
                RhinoApp.WriteLine("  Logs: %APPDATA%\\AIBridge\\logs\\");
                RhinoApp.WriteLine("==================================================");
                AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Started on 127.0.0.1:{PORT} build:{BuildHash}");
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine($"AIBridge: Failed — {e.Message}");
                AIBridgeLogger.Log(LogLevel.ERROR, "Server", "Start failed", error: e.Message);
                Stop();
            }
        }

        // ─── Operating-mode selection (bug 1.2 / v4.7.6 mode picker) ──────────────────────
        // IMPORTANT: the server AUTO-STARTS during Rhino's startup Idle event
        // (AIBridgePlugin.OnIdle). We must NOT pop a modal dialog there — doing so blocks the
        // idle handler and the dialog opens invisibly/un-parented. So startup is silent:
        // Start() picks the mode from the environment, or defaults to Safe. The user is then
        // asked interactively by the AIBridge command (PromptAndApplyMode), which shows a
        // native Rhino dialog that is reliably visible.

        // Returns the env-forced mode, or null when no override is set.
        //   RHINO_AIBRIDGE_MODE = safe | standard | developer   (explicit)
        //   RHINO_AIBRIDGE_SAFE_MODE = 1 / true                 (legacy => Safe)
        private static CommandHandler.BridgeMode? ModeFromEnv()
        {
            var modeEnv = Environment.GetEnvironmentVariable("RHINO_AIBRIDGE_MODE");
            if (!string.IsNullOrWhiteSpace(modeEnv))
            {
                switch (modeEnv.Trim().ToLowerInvariant())
                {
                    case "safe":      return CommandHandler.BridgeMode.Safe;
                    case "standard":  return CommandHandler.BridgeMode.Standard;
                    case "developer":
                    case "dev":       return CommandHandler.BridgeMode.Developer;
                }
            }
            var smEnv = Environment.GetEnvironmentVariable("RHINO_AIBRIDGE_SAFE_MODE");
            if (smEnv == "1" || string.Equals(smEnv, "true", StringComparison.OrdinalIgnoreCase))
                return CommandHandler.BridgeMode.Safe;
            return null;
        }

        // Non-blocking, no UI. Used at auto-start so Rhino launch never stalls.
        private static CommandHandler.BridgeMode ModeFromEnvOrDefault()
            => ModeFromEnv() ?? CommandHandler.BridgeMode.Safe;

        // Called by the AIBridge command. Shows a native, reliably-visible Rhino dialog and
        // applies the chosen mode live (enforcement reads CommandHandler.Mode per dispatch).
        // An env override wins and skips the dialog; scripted runs keep the current mode.
        public static CommandHandler.BridgeMode PromptAndApplyMode(bool interactive)
        {
            var forced = ModeFromEnv();
            if (forced.HasValue) { CommandHandler.Mode = forced.Value; return forced.Value; }
            if (!interactive) return CommandHandler.Mode;

            try
            {
                var items = new System.Collections.Generic.List<string> { "Safe", "Standard", "Developer" };
                object pick = Rhino.UI.Dialogs.ShowComboListBox(
                    "Rhino AI Bridge — Access Mode",
                    "Choose how much access the AI has, then click OK:\r\n\r\n" +
                    "Safe — blocks code + destructive edits (recommended)\r\n" +
                    "Standard — allows delete/boolean, still blocks code\r\n" +
                    "Developer — full access, everything allowed",
                    items);

                if (pick is string s)
                {
                    switch (s)
                    {
                        case "Standard":  CommandHandler.Mode = CommandHandler.BridgeMode.Standard; break;
                        case "Developer": CommandHandler.Mode = CommandHandler.BridgeMode.Developer; break;
                        default:          CommandHandler.Mode = CommandHandler.BridgeMode.Safe; break;
                    }
                }
                // pick == null  => user cancelled; keep the current mode.
            }
            catch (Exception ex)
            {
                AIBridgeLogger.Log(LogLevel.WARN, "Server",
                    "Mode dialog failed; keeping current mode", error: ex.ToString());
            }

            AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Operating mode set to {CommandHandler.Mode}");
            return CommandHandler.Mode;
        }

        // Track active client connections for force-shutdown. Keyed dictionary so each
        // connection is removed on normal disconnect, not leaked until Rhino shutdown. (bug 1.14)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TcpClient> _activeClients
            = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TcpClient>();

        // ─── Auth token plumbing (bug 1.2) ────────────────────────────────
        // The token lives in a per-user, user-scoped location that the MCP server (running as
        // the same OS user) can read. On Unix it's chmod 600; on Windows the per-user
        // LOCALAPPDATA path is already inaccessible to other users.
        private static string TokenPath()
        {
            string baseDir;
            if (OperatingSystem.IsWindows())
                baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                          ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            else
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "AIBridge", "token");
        }

        private void InitAuthToken()
        {
            try
            {
                var bytes = new byte[32];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                    rng.GetBytes(bytes);
                _authToken = Convert.ToHexString(bytes).ToLowerInvariant();

                var path = TokenPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, _authToken);
                if (!OperatingSystem.IsWindows())
                {
                    try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
                }
                _requireAuth = true;
                AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Auth token written to {path}");
            }
            catch (Exception ex)
            {
                // If we can't persist a token the MCP client can't read it, which would brick
                // every connection. Fall back to no-auth with a loud warning rather than lock out.
                _requireAuth = false;
                _authToken = null;
                AIBridgeLogger.Log(LogLevel.WARN, "Server",
                    "Could not establish auth token; running WITHOUT authentication", error: ex.ToString());
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (!_running) return;
                _running = false;
                try { _cts?.Cancel(); } catch { }
                try { _listener?.Stop(); } catch { }
                _listener = null;
            }
            UiDispatcher.Stop();
            try { SceneSnapshotRegistry.Shutdown(); } catch { }
            RhinoApp.WriteLine("AIBridge: Stopped");
            AIBridgeLogger.Log(LogLevel.INFO, "Server", "Stopped");
            AIBridgeLogger.Shutdown();
        }

        /// <summary>
        /// Emergency shutdown — force-close all TCP client connections.
        /// Called during Rhino close to prevent "Server Busy" dialog.
        /// </summary>
        public void ForceRelease()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            // Force-close every tracked client socket
            foreach (var kv in _activeClients)
            {
                try { kv.Value?.Close(); } catch { }
                try { kv.Value?.Dispose(); } catch { }
            }
            _activeClients.Clear();
            AIBridgeLogger.Log(LogLevel.INFO, "Server", "ForceRelease: all connections closed");
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    if (_running) AIBridgeLogger.Log(LogLevel.WARN, "Server", $"Accept error: {e.Message}");
                    continue;
                }

                // Reject excess connections so a flood can't exhaust threads/sockets. (security #4)
                if (_activeClients.Count >= MAX_CLIENTS)
                {
                    AIBridgeLogger.Log(LogLevel.WARN, "Server", $"Connection refused: client cap ({MAX_CLIENTS}) reached");
                    try { client.Close(); } catch { }
                    continue;
                }

                // Fire-and-forget per-client task. Background thread.
                var clientId = Guid.NewGuid();
                _activeClients[clientId] = client;
                _ = Task.Run(() => HandleClient(clientId, client, ct));
            }
        }

        private void HandleClient(Guid clientId, TcpClient client, CancellationToken ct)
        {
            var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
            AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Client connected: {ep}");
            // Drop idle sockets instead of parking a thread forever, and close on shutdown. (bug 1.15)
            try { client.ReceiveTimeout = IDLE_READ_TIMEOUT_MS; } catch { }
            using var ctReg = ct.Register(() => { try { client.Close(); } catch { } });
            bool authed = !_requireAuth;
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var hdr = new byte[4];
                    while (_running && client.Connected && !ct.IsCancellationRequested)
                    {
                        if (ReadExact(stream, hdr, 4) < 4) break;
                        int len = (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
                        if (len <= 0 || len > 50_000_000) break;     // cap: 50MB / frame

                        var buf = new byte[len];
                        if (ReadExact(stream, buf, len) < len) break;

                        var cmd = JObject.Parse(Encoding.UTF8.GetString(buf));

                        // Auth gate: the first frame on every connection must be a valid auth
                        // frame before any command (including ping) is honored. (bug 1.2)
                        if (!authed)
                        {
                            var supplied = cmd["token"]?.ToString();
                            if ((cmd["type"]?.ToString() == "auth") && supplied != null &&
                                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                                    Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_authToken ?? "")))
                            {
                                authed = true;
                                WriteFrame(stream, new JObject { ["status"] = "ok", ["authenticated"] = true });
                                continue;
                            }
                            WriteFrame(stream, new JObject { ["status"] = "error", ["error_code"] = "AUTH_REQUIRED", ["message"] = "authentication required" });
                            AIBridgeLogger.Log(LogLevel.WARN, "Server", $"Auth failed/missing from {ep}; closing");
                            break;
                        }
                        var timer = AIBridgeLogger.StartTimer();
                        string cmdType = cmd["type"]?.ToString() ?? "?";
                        JObject result;

                        try
                        {
                            // Fast path â€” ping is in-band, no UI thread hop.
                            if (cmdType == "ping")
                            {
                                result = HandlePing(cmd["params"] as JObject);
                            }
                            else
                            {
                                // Phase 7: per-command timeout — viewport capture/camera can legitimately
                                // take longer on complex scenes than geometry ops.
                                int timeoutSec = cmdType switch
                                {
                                    "capture_viewport" => 120,
                                    "set_camera"       => 120,
                                    "execute_script"   => 180,
                                    "batch"            => 180,
                                    _                  => 60,
                                };

                                // Hop to UI thread, with deferred-redraw scope wrapping the dispatch.
                                result = UiDispatcher.Invoke(() =>
                                {
                                    using (RedrawScope.Defer())
                                    {
                                        var r = _handler.Dispatch(cmd);
                                        // Stamp the post-command scene version on every response.
                                        // Read tools see the version after any pending events have flushed;
                                        // mutating tools see the version reflecting their own changes.
                                        var snap = SceneSnapshotRegistry.Active;
                                        if (snap != null && r != null && r["scene_version"] == null)
                                            r["scene_version"] = snap.SceneVersion;
                                        return r;
                                    }
                                }, TimeSpan.FromSeconds(timeoutSec));
                            }

                            // Batch commands store their payload in cmd["commands"], not cmd["params"],
                            // so log them separately to get something useful out of the log line.
                            string ps;
                            if (cmdType == "batch")
                            {
                                var cmds = cmd["commands"] as JArray;
                                bool atomic = cmd["atomic"]?.ToObject<bool>() ?? false;
                                int n = cmds?.Count ?? 0;
                                var types = cmds != null
                                    ? string.Join(", ", cmds.Take(6).Select(c => c["type"]?.ToString() ?? "?"))
                                    : "?";
                                ps = $"[{n} cmds, atomic={atomic}] {types}{(n > 6 ? "..." : "")}";
                            }
                            else
                            {
                                var paramStr = cmd["params"]?.ToString(Formatting.None) ?? "{}";
                                ps = paramStr.Length > 200 ? paramStr.Substring(0, 200) + "..." : paramStr;
                            }
                            AIBridgeLogger.LogCommand(cmdType, ps, timer,
                                result?["status"]?.ToString() ?? "?",
                                result?["message"]?.ToString());
                        }
                        catch (Exception e)
                        {
                            result = new JObject { ["status"] = "error", ["message"] = e.Message };
                            AIBridgeLogger.LogCommand(cmdType, "{}", timer, "error", e.ToString());
                        }

                        WriteFrame(stream, result);
                    }
                }
            }
            catch (Exception e)
            {
                if (_running) AIBridgeLogger.Log(LogLevel.WARN, "Server", $"Client error: {e.Message}");
            }
            finally
            {
                _activeClients.TryRemove(clientId, out _);   // remove on disconnect, not just at shutdown (bug 1.14)
                AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Client disconnected: {ep}");
            }
        }

        // Serialize + optional gzip compression. Server → client framing:
        //   [1-byte flag][4-byte big-endian length][payload]
        //   flag 0x00 = raw UTF-8 JSON, 0x01 = gzip-compressed UTF-8 JSON
        // Compress when payload > 10 KB (5-8× on object lists, ~2× on base64 images).
        private static void WriteFrame(NetworkStream stream, JObject result)
        {
            // Current responses are always raw JSON (flag 0x00). Clients retain legacy gzip support.
            var payload = Encoding.UTF8.GetBytes(result.ToString(Formatting.None));
            stream.WriteByte(0x00);
            stream.Write(new byte[]
            {
                (byte)(payload.Length >> 24),
                (byte)(payload.Length >> 16),
                (byte)(payload.Length >> 8),
                (byte)payload.Length
            }, 0, 4);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private JObject HandlePing(JObject p)
        {
            // No UI thread hop; this MUST stay sub-millisecond.
            // The MCP server uses ping to verify the connection is alive and the doc is what it expects.
            // scene_version is the etag â€” Claude can short-circuit re-querying if it hasn't changed.
            var doc = RhinoDoc.ActiveDoc;
            var snap = doc != null ? SceneSnapshotRegistry.Get(doc) : null;
            return new JObject
            {
                ["status"] = "ok",
                ["protocol_version"] = PROTOCOL_VERSION,
                ["build_hash"] = BuildHash,
                ["rhino_version"] = RhinoApp.Version?.ToString() ?? "?",
                ["doc_name"] = doc?.Name ?? "Untitled",
                ["doc_serial"] = doc?.RuntimeSerialNumber ?? 0,
                ["unit_system"] = doc?.ModelUnitSystem.ToString() ?? "?",
                ["tolerance"] = doc?.ModelAbsoluteTolerance ?? 0,
                ["scene_version"] = snap?.SceneVersion ?? 0,
                ["object_count"] = snap?.Count ?? 0,
                ["server_time_utc"] = DateTime.UtcNow.ToString("o"),
                ["capabilities"] = new JArray {
                    "deferred_redraw",
                    "lean_response",
                    "scene_cache",
                    "atomic_batch",
                    "reference_resolution",
                    "architect_intelligence",
                    "consolidated_surface",
                    "auto_thumbnail",
                    "pbr_materials",
                    "run_command",
                    "set_camera", "dry_run",
                    "viewport_metadata", "query_modes", "design_memory", "scene_sync", "semantic_intelligence",
                },
                ["capabilities_resource"] = "rhino://capabilities",
                ["safe_mode"] = CommandHandler.SafeMode,
                ["mode"] = CommandHandler.Mode.ToString().ToLowerInvariant(),
                ["auth_required"] = _requireAuth,
            };
        }

        private static int ReadExact(System.Net.Sockets.NetworkStream s, byte[] buf, int needed)
        {
            int total = 0;
            while (total < needed)
            {
                int n = s.Read(buf, total, needed - total);
                if (n == 0) return total;
                total += n;
            }
            return total;
        }
    }
}
