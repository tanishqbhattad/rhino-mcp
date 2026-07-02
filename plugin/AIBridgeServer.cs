// RhinoAIBridge v4.8 - AIBridgeServer.cs
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
    /// TCP server. Length-prefixed JSON frames.
    ///
    /// Protocol 5 (v4.8):
    ///   - MULTIPLEXED: requests carry a request_id; responses echo it and may return
    ///     out of order. A 180s script no longer blocks snapshot reads or ping.
    ///   - TCP-thread reads: ping / cancel / get_state / scene-diff / tracker /
    ///     snapshot-backed query_scene answer directly from the socket thread - no UI hop.
    ///   - IDEMPOTENT RETRIES: mutating requests are registered in OperationRegistry;
    ///     a re-sent request_id replays the cached result instead of re-executing.
    ///   - CANCELLATION: a "cancel" frame (handled inline) signals the running
    ///     command's CancellationToken; long loops stop at the next checkpoint.
    ///   - BINARY IMAGE FRAMES (flag 0x02): [4B header len][JSON header][raw image bytes]
    ///     - no base64 inflation for viewport captures (clients opt in via "hello").
    ///   - Legacy clients (no request_id) get the old strict request->response order.
    /// </summary>
    public class AIBridgeServer
    {
        private const int PORT = 9544;
        public const string PROTOCOL_VERSION = "5.0";
        public static readonly string[] FEATURES =
            { "multiplex", "idempotent_retry", "cancel", "binary_image", "columnar_query", "wal" };

        // Cap concurrent client connections so a flood of fire-and-forget tasks can't
        // exhaust threads/sockets. (security hardening #4)
        private const int MAX_CLIENTS = 8;

        // Idle read timeout (ms) BEFORE the first valid frame. A client that opens the
        // socket and sends nothing gets dropped instead of parking a thread forever.
        // Lifted after the first authenticated command - the persistent MCP connection
        // legitimately sits idle for minutes between user turns.
        private const int IDLE_READ_TIMEOUT_MS = 60_000;

        // Max concurrently executing multiplexed commands per connection.
        private const int MAX_INFLIGHT_PER_CONN = 16;

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly object _lifecycleLock = new object();
        private bool _running;
        private readonly CommandHandler _handler = new CommandHandler();

        // Per-session shared secret. Required as the first frame from every client so a
        // random local process can't drive Rhino over the loopback socket. (bug 1.2)
        private string _authToken;
        private bool _requireAuth;

        // Build hash captured once at startup - useful when 5 versions of the .rhp are on disk.
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
            // talking raw TCP). Startup is SILENT and non-blocking. (bug 1.2)
            CommandHandler.Mode = ModeFromEnvOrDefault();
            AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Operating mode (startup default): {CommandHandler.Mode}");

            RhinoApp.WriteLine("AIBridge: Preparing local authentication...");
            InitAuthToken();
            UiDispatcher.Start();
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

                var asmVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
                RhinoApp.WriteLine("==================================================");
                RhinoApp.WriteLine($"  Rhino AI Bridge v{asmVer} (C#)  protocol {PROTOCOL_VERSION}");
                RhinoApp.WriteLine($"  Listening on 127.0.0.1:{PORT}  build:{BuildHash}");
                RhinoApp.WriteLine("  Multiplexed protocol + idempotent retries + cancel");
                RhinoApp.WriteLine("  Binary image frames, WAL crash recovery, columnar queries");
                RhinoApp.WriteLine("  Logs: %APPDATA%\\AIBridge\\logs\\   WAL: %APPDATA%\\AIBridge\\wal\\");
                RhinoApp.WriteLine("==================================================");
                AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Started on 127.0.0.1:{PORT} build:{BuildHash} protocol:{PROTOCOL_VERSION}");
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine($"AIBridge: Failed - {e.Message}");
                AIBridgeLogger.Log(LogLevel.ERROR, "Server", "Start failed", error: e.Message);
                Stop();
            }
        }

        // ─── Operating-mode selection ──────────────────────────────────────
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

        private static CommandHandler.BridgeMode ModeFromEnvOrDefault()
            => ModeFromEnv() ?? CommandHandler.BridgeMode.Safe;

        public static CommandHandler.BridgeMode PromptAndApplyMode(bool interactive)
        {
            var forced = ModeFromEnv();
            if (forced.HasValue) { CommandHandler.Mode = forced.Value; return forced.Value; }
            if (!interactive) return CommandHandler.Mode;

            try
            {
                var items = new System.Collections.Generic.List<string> { "Safe", "Standard", "Developer" };
                object pick = Rhino.UI.Dialogs.ShowComboListBox(
                    "Rhino AI Bridge - Access Mode",
                    "Choose how much access the AI has, then click OK:\r\n\r\n" +
                    "Safe - blocks code + destructive edits (recommended)\r\n" +
                    "Standard - allows delete/boolean, still blocks code\r\n" +
                    "Developer - full access, everything allowed",
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
            }
            catch (Exception ex)
            {
                AIBridgeLogger.Log(LogLevel.WARN, "Server",
                    "Mode dialog failed; keeping current mode", error: ex.ToString());
            }

            AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Operating mode set to {CommandHandler.Mode}");
            return CommandHandler.Mode;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TcpClient> _activeClients
            = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TcpClient>();

        // ─── Auth token plumbing (bug 1.2) ────────────────────────────────
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

        public void ForceRelease()
        {
            UiDispatcher.BeginShutdown();
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            foreach (var kv in _activeClients)
            {
                try { kv.Value?.Close(); } catch { }
                try { kv.Value?.Dispose(); } catch { }
            }
            _activeClients.Clear();
            UiDispatcher.WaitForIdle(TimeSpan.FromSeconds(2));
            AIBridgeLogger.Log(LogLevel.INFO, "Server", "ForceRelease: all connections closed");
        }

        public void StopForRhinoShutdown()
        {
            UiDispatcher.BeginShutdown();
            lock (_lifecycleLock)
            {
                _running = false;
                try { _cts?.Cancel(); } catch { }
                try { _listener?.Stop(); } catch { }
                _listener = null;
            }

            foreach (var kv in _activeClients)
            {
                try { kv.Value?.Close(); } catch { }
                try { kv.Value?.Dispose(); } catch { }
            }
            _activeClients.Clear();
            UiDispatcher.WaitForIdle(TimeSpan.FromSeconds(2));

            try { SceneSnapshotRegistry.Shutdown(); } catch { }
            try { AIBridgeLogger.Log(LogLevel.INFO, "Server", "StopForRhinoShutdown: listener and clients closed"); } catch { }
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

                if (_activeClients.Count >= MAX_CLIENTS)
                {
                    AIBridgeLogger.Log(LogLevel.WARN, "Server", $"Connection refused: client cap ({MAX_CLIENTS}) reached");
                    try { client.Close(); } catch { }
                    continue;
                }

                var clientId = Guid.NewGuid();
                _activeClients[clientId] = client;
                _ = Task.Run(() => HandleClient(clientId, client, ct));
            }
        }

        /// <summary>Per-connection state: write lock for interleaved responses + negotiated features.</summary>
        private sealed class ClientConn
        {
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
            public volatile bool BinaryImages;     // negotiated via "hello"
            public readonly SemaphoreSlim InFlight = new SemaphoreSlim(MAX_INFLIGHT_PER_CONN);
        }

        // Commands safe to answer directly on the TCP thread (no UI hop). These touch
        // only lock-protected structures (SceneSnapshot RW-lock, ChangeTracker, session
        // state, WAL) - they stay sub-ms even while a 180s script runs on the UI thread.
        private static readonly HashSet<string> TcpThreadCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "get_tracker_version", "get_scene_diff", "get_change_log", "get_state", "get_recovery_log" };

        private static bool IsTcpSafe(string cmdType, JObject cmd)
        {
            if (TcpThreadCommands.Contains(cmdType)) return true;
            if (string.Equals(cmdType, "query_scene", StringComparison.OrdinalIgnoreCase))
            {
                // Only the snapshot-backed objects scope is safe off the UI thread.
                var scope = cmd["params"]?["scope"]?.ToString();
                bool objectsScope = string.IsNullOrEmpty(scope) ||
                                    string.Equals(scope, "objects", StringComparison.OrdinalIgnoreCase);
                return objectsScope && SceneSnapshotRegistry.Active != null;
            }
            return false;
        }

        private void HandleClient(Guid clientId, TcpClient client, CancellationToken ct)
        {
            var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
            AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Client connected: {ep}");
            try { client.ReceiveTimeout = IDLE_READ_TIMEOUT_MS; } catch { }
            using var ctReg = ct.Register(() => { try { client.Close(); } catch { } });
            bool authed = !_requireAuth;
            bool idleTimeoutLifted = false;
            var conn = new ClientConn();
            try
            {
                using (client)
                {
                    conn.Stream = client.GetStream();
                    var stream = conn.Stream;
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
                                WriteFrameSafe(conn, new JObject
                                {
                                    ["status"] = "ok",
                                    ["authenticated"] = true,
                                    ["protocol_version"] = PROTOCOL_VERSION,
                                    ["features"] = new JArray(FEATURES),
                                });
                                continue;
                            }
                            WriteFrameSafe(conn, new JObject { ["status"] = "error", ["error_code"] = "AUTH_REQUIRED", ["message"] = "authentication required" });
                            AIBridgeLogger.Log(LogLevel.WARN, "Server", $"Auth failed/missing from {ep}; closing");
                            break;
                        }

                        // First real (authenticated) frame: lift the idle timeout so the
                        // persistent MCP connection survives quiet periods between turns.
                        if (!idleTimeoutLifted)
                        {
                            try { client.ReceiveTimeout = 0; } catch { }
                            idleTimeoutLifted = true;
                        }

                        string cmdType = cmd["type"]?.ToString() ?? "?";
                        string requestId = cmd["request_id"]?.ToString();

                        // ── Inline (TCP-thread) handling: handshake, ping, cancel, safe reads ──
                        if (cmdType == "hello")
                        {
                            var feats = cmd["features"]?.ToObject<string[]>() ?? Array.Empty<string>();
                            conn.BinaryImages = feats.Contains("binary_image");
                            var hr = new JObject
                            {
                                ["status"] = "ok",
                                ["protocol_version"] = PROTOCOL_VERSION,
                                ["features"] = new JArray(FEATURES),
                                ["build_hash"] = BuildHash,
                            };
                            if (requestId != null) hr["request_id"] = requestId;
                            WriteFrameSafe(conn, hr);
                            continue;
                        }
                        if (cmdType == "ping")
                        {
                            var pr = HandlePing(cmd["params"] as JObject);
                            if (requestId != null) pr["request_id"] = requestId;
                            WriteFrameSafe(conn, pr);
                            continue;
                        }
                        if (cmdType == "cancel")
                        {
                            var target = cmd["params"]?["request_id"]?.ToString();
                            bool found = OperationRegistry.Cancel(target);
                            var cr = new JObject
                            {
                                ["status"] = "ok",
                                ["cancel_requested"] = found,
                                ["target_request_id"] = target ?? "",
                                ["note"] = found
                                    ? "Cancellation signaled; the command stops at its next checkpoint."
                                    : "No running operation with that request_id (it may have already finished).",
                            };
                            if (requestId != null) cr["request_id"] = requestId;
                            WriteFrameSafe(conn, cr);
                            continue;
                        }
                        if (IsTcpSafe(cmdType, cmd))
                        {
                            JObject r;
                            var t0 = AIBridgeLogger.StartTimer();
                            try
                            {
                                r = _handler.Dispatch(cmd);
                                var snapI = SceneSnapshotRegistry.Active;
                                if (snapI != null && r != null && r["scene_version"] == null)
                                    r["scene_version"] = snapI.SceneVersion;
                            }
                            catch (Exception e)
                            {
                                r = new JObject { ["status"] = "error", ["message"] = e.Message };
                            }
                            if (requestId != null) r["request_id"] = requestId;
                            AIBridgeLogger.LogCommand(cmdType, "(tcp-thread)", t0, r?["status"]?.ToString() ?? "?", null);
                            WriteFrameSafe(conn, r);
                            continue;
                        }

                        // ── UI-thread commands ──
                        if (requestId == null)
                        {
                            // Legacy client: strict in-order request->response.
                            var result = ExecuteOnUi(cmd, cmdType, null);
                            LogDispatch(cmd, cmdType, result);
                            WriteFrameSafe(conn, result);
                        }
                        else
                        {
                            // Multiplexed: hand off so the read loop keeps consuming frames
                            // (pings, cancels, reads) while this command runs.
                            var capturedCmd = cmd;
                            var capturedType = cmdType;
                            var capturedId = requestId;
                            conn.InFlight.Wait(ct);
                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    var result = ExecuteOnUi(capturedCmd, capturedType, capturedId);
                                    result["request_id"] = capturedId;
                                    LogDispatch(capturedCmd, capturedType, result);
                                    WriteFrameSafe(conn, result);
                                }
                                catch (Exception e)
                                {
                                    try
                                    {
                                        WriteFrameSafe(conn, new JObject
                                        {
                                            ["status"] = "error",
                                            ["message"] = e.Message,
                                            ["request_id"] = capturedId,
                                        });
                                    }
                                    catch { /* connection gone */ }
                                }
                                finally { conn.InFlight.Release(); }
                            });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (_running) AIBridgeLogger.Log(LogLevel.WARN, "Server", $"Client error: {e.Message}");
            }
            finally
            {
                _activeClients.TryRemove(clientId, out _);
                AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Client disconnected: {ep}");
            }
        }

        /// <summary>
        /// Run one command on the UI thread with timeout, cancellation and (for mutating
        /// commands with a request_id) idempotency registration + WAL bracketing.
        /// </summary>
        private JObject ExecuteOnUi(JObject cmd, string cmdType, string requestId)
        {
            bool mutating = requestId != null && !CommandHandler.ReadOnlyCommands.Contains(cmdType);
            if (mutating)
            {
                switch (OperationRegistry.Begin(requestId, out var cached, out var joinTask))
                {
                    case OperationRegistry.BeginOutcome.Replay:
                    {
                        var replay = (JObject)cached.DeepClone();
                        replay["replayed"] = true;
                        replay["note"] = "Idempotent replay - this request_id already executed; the original result is returned without re-executing.";
                        return replay;
                    }
                    case OperationRegistry.BeginOutcome.Join:
                    {
                        try
                        {
                            if (joinTask.Wait(TimeSpan.FromSeconds(185)))
                            {
                                var joined = (JObject)joinTask.Result.DeepClone();
                                joined["replayed"] = true;
                                return joined;
                            }
                        }
                        catch { }
                        return new JObject
                        {
                            ["status"] = "error",
                            ["error_code"] = "DUPLICATE_IN_FLIGHT",
                            ["message"] = "This request_id is still executing and did not finish within the join window.",
                        };
                    }
                }
                // WAL: record intent BEFORE execution (crash recovery).
                var snapW = SceneSnapshotRegistry.Active;
                var ps = cmd["params"]?.ToString(Formatting.None) ?? cmd["commands"]?.ToString(Formatting.None) ?? "{}";
                if (ps.Length > 300) ps = ps.Substring(0, 300) + "...";
                WriteAheadLog.Append("begin", requestId, cmdType, ps, snapW?.SceneVersion ?? 0);
            }

            int timeoutSec = cmdType switch
            {
                "capture_viewport" => 120,
                "set_camera"       => 120,
                "execute_script"   => 180,
                "batch"            => 180,
                _                  => 60,
            };

            var token = OperationRegistry.TokenFor(requestId);
            JObject result;
            try
            {
                result = UiDispatcher.Invoke(() =>
                {
                    OperationRegistry.SetCurrent(token);
                    try
                    {
                        JObject r;
                        using (RedrawScope.Defer())
                        {
                            r = _handler.Dispatch(cmd);
                            var snap = SceneSnapshotRegistry.Active;
                            if (snap != null && r != null && r["scene_version"] == null)
                                r["scene_version"] = snap.SceneVersion;
                        }
                        if (mutating)
                        {
                            // Complete on the UI thread so a late-finishing command (whose
                            // waiter already timed out) still lands in the replay cache.
                            OperationRegistry.Complete(requestId, r);
                            var snapA = SceneSnapshotRegistry.Active;
                            WriteAheadLog.Append("end", requestId, cmd["type"]?.ToString() ?? "?", null,
                                snapA?.SceneVersion ?? 0, r?["status"]?.ToString() ?? "?");
                        }
                        return r;
                    }
                    finally { OperationRegistry.ClearCurrent(); }
                }, TimeSpan.FromSeconds(timeoutSec));
            }
            catch (TimeoutException e)
            {
                // Signal cooperative cancel so the still-running command stops at its
                // next checkpoint instead of mutating the doc long after the client gave up.
                if (mutating) OperationRegistry.Cancel(requestId);
                bool started = e.Data.Contains("started") && e.Data["started"] is bool b && b;
                result = new JObject
                {
                    ["status"] = "error",
                    ["error_code"] = "COMMAND_TIMEOUT",
                    ["message"] = e.Message,
                    ["may_still_be_running"] = started,
                    ["hint"] = requestId != null
                        ? (started
                            ? "A cancel was signaled. Re-sending the same request_id replays the eventual result without re-executing."
                            : "The command never started; re-issuing it is safe.")
                        : null,
                };
                // v4.10: a never-started action will NEVER call Complete - finalize the
                // registry entry now so the request_id doesn't leak in _inFlight (and a
                // same-id retry doesn't hang in the join window).
                if (mutating && !started)
                    OperationRegistry.Complete(requestId, result);
            }
            catch (Exception e)
            {
                if (mutating) OperationRegistry.Complete(requestId, new JObject { ["status"] = "error", ["message"] = e.Message });
                result = new JObject { ["status"] = "error", ["message"] = e.Message };
            }
            return result;
        }

        private static void LogDispatch(JObject cmd, string cmdType, JObject result)
        {
            try
            {
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
                AIBridgeLogger.LogCommand(cmdType, ps, AIBridgeLogger.StartTimer(),
                    result?["status"]?.ToString() ?? "?",
                    result?["message"]?.ToString());
            }
            catch { }
        }

        // ── Framing ─────────────────────────────────────────────────────────
        // Server -> client: [1-byte flag][4-byte big-endian length][payload]
        //   0x00 raw UTF-8 JSON
        //   0x01 gzip JSON (legacy, accepted by clients)
        //   0x02 binary image: payload = [4-byte header len][JSON header][raw image bytes]
        // Thread-safe per connection: multiplexed tasks interleave whole frames only.

        private static void WriteFrameSafe(ClientConn conn, JObject result)
        {
            lock (conn.WriteLock)
            {
                var stream = conn.Stream;
                string b64 = null;
                if (conn.BinaryImages)
                {
                    // Only the large primary image goes binary; small thumbnails stay inline.
                    b64 = result["image_base64"]?.Type == JTokenType.String
                        ? result.Value<string>("image_base64")
                        : null;
                    if (b64 != null && b64.Length < 8192) b64 = null;
                }

                if (b64 != null)
                {
                    byte[] img;
                    try { img = Convert.FromBase64String(b64); }
                    catch { img = null; }
                    if (img != null)
                    {
                        result.Remove("image_base64");
                        result["image_binary"] = true;
                        result["image_bytes_length"] = img.Length;
                        var header = SerializeUtf8(result);
                        int total = 4 + header.Length + img.Length;
                        stream.WriteByte(0x02);
                        WriteBigEndian(stream, total);
                        WriteBigEndian(stream, header.Length);
                        stream.Write(header, 0, header.Length);
                        stream.Write(img, 0, img.Length);
                        stream.Flush();
                        return;
                    }
                    // fall through with original payload if base64 was malformed
                }

                var payload = SerializeUtf8(result);
                stream.WriteByte(0x00);
                WriteBigEndian(stream, payload.Length);
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
        }

        /// <summary>Serialize straight JObject -> UTF-8 bytes (no intermediate .NET string).</summary>
        private static byte[] SerializeUtf8(JObject o)
        {
            using var ms = new MemoryStream(4096);
            using (var sw = new StreamWriter(ms, new UTF8Encoding(false), 8192, leaveOpen: true))
            using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.None })
                o.WriteTo(jw);
            return ms.ToArray();
        }

        private static void WriteBigEndian(NetworkStream stream, int value)
        {
            stream.Write(new byte[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            }, 0, 4);
        }

        private JObject HandlePing(JObject p)
        {
            // No UI thread hop; this MUST stay sub-millisecond.
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
                ["features"] = new JArray(FEATURES),
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
                    "multiplex", "idempotent_retry", "cancel", "binary_image", "columnar_query", "wal",
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
