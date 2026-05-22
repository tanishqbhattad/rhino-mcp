// RhinoAIBridge v4.7.5 â€” AIBridgeServer.cs
// by tanishqb | https://github.com/tanishqb/rhino-ai-bridge

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly object _lifecycleLock = new object();
        private bool _running;
        private readonly CommandHandler _handler = new CommandHandler();

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
            AIBridgeLogger.Initialize();
            UiDispatcher.Start();
            // SceneSnapshot registry must be initialized BEFORE the listener accepts clients.
            // Otherwise an early read tool could find a missing snapshot.
            // Has to run on the UI thread because it touches RhinoDoc.ActiveDoc and subscribes to events.
            try
            {
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
                _listener = new TcpListener(IPAddress.Parse("127.0.0.1"), PORT);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoop(_cts.Token));

                RhinoApp.WriteLine("==================================================");
                RhinoApp.WriteLine("  Rhino AI Bridge v4.5 (C#)");
                RhinoApp.WriteLine($"  Listening on 127.0.0.1:{PORT}  build:{BuildHash}");
                RhinoApp.WriteLine("  Phase 1: deferred redraw, async I/O, lean responses");
                RhinoApp.WriteLine("  Phase 2: scene snapshot cache + scene_version etag");
                RhinoApp.WriteLine("  Phase 3: atomic batches + reference resolution ($1.object_ids[0])");
                RhinoApp.WriteLine("  Phase 5: architect intelligence (massing, floors, core, facade, schedules)");
                RhinoApp.WriteLine("  Phase 6: consolidated 28-tool MCP surface");
                RhinoApp.WriteLine("  Logs: %APPDATA%\\AIBridge\\logs\\");
                RhinoApp.WriteLine("==================================================");
                AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Started on 127.0.0.1:{PORT} build:{BuildHash}");
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine($"AIBridge: Failed â€” {e.Message}");
                AIBridgeLogger.Log(LogLevel.ERROR, "Server", "Start failed", error: e.Message);
                Stop();
            }
        }

        // Track active client connections for force-shutdown
        private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> _activeClients 
            = new System.Collections.Concurrent.ConcurrentBag<TcpClient>();

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
            while (_activeClients.TryTake(out var client))
            {
                try { client?.Close(); } catch { }
                try { client?.Dispose(); } catch { }
            }
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

                // Fire-and-forget per-client task. Background thread.
                _activeClients.Add(client);
                _ = Task.Run(() => HandleClient(client, ct));
            }
        }

        private void HandleClient(TcpClient client, CancellationToken ct)
        {
            var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
            AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Client connected: {ep}");
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

                        // â”€â”€ Serialize + optional gzip compression â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                        // Protocol (server â†’ client): [1-byte flag][4-byte big-endian length][payload]
                        //   flag 0x00 = raw UTF-8 JSON
                        //   flag 0x01 = gzip-compressed UTF-8 JSON
                        // Compress when payload > 10 KB â€” typical gains: 5-8Ã— on object lists,
                        // ~2Ã— on base64 image data. CompressionLevel.Fastest keeps CPU cost low.
                        const int GzipThreshold = 10_000;
                        var raw = Encoding.UTF8.GetBytes(result.ToString(Formatting.None));
                        byte flag;
                        byte[] payload;
                        if (raw.Length > GzipThreshold)
                        {
                            using var ms = new MemoryStream(raw.Length / 2);
                            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                                gz.Write(raw, 0, raw.Length);
                            payload = ms.ToArray();
                            flag = 0x01;
                        }
                        else
                        {
                            payload = raw;
                            flag = 0x00;
                        }
                        stream.WriteByte(flag);
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
                }
            }
            catch (Exception e)
            {
                if (_running) AIBridgeLogger.Log(LogLevel.WARN, "Server", $"Client error: {e.Message}");
            }
            finally
            {
                AIBridgeLogger.Log(LogLevel.INFO, "Server", $"Client disconnected: {ep}");
            }
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
                    "gzip_compression",
                    "pbr_materials",
                    "run_command",
                    "set_camera", "dry_run",
                    "viewport_metadata", "query_modes", "design_memory", "scene_sync", "semantic_intelligence",
                },
                ["capabilities_resource"] = "rhino://capabilities",
                ["safe_mode"] = false,
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
