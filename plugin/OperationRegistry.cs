// RhinoAIBridge v4.8 - OperationRegistry.cs
// Protocol 5: idempotency replay cache, cooperative cancellation, write-ahead log.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoAIBridge
{
    /// <summary>
    /// Tracks in-flight and completed operations by client-supplied request_id.
    ///
    /// Why: when a connection drops after a mutating command was delivered, the MCP
    /// client cannot know whether it executed. With this registry the client simply
    /// RE-SENDS the command with the SAME request_id:
    ///   - still running  -> the retry joins the in-flight task and gets its result
    ///   - already done   -> the cached response is replayed (no re-execution)
    ///   - never arrived  -> executes normally
    /// This makes retries safe for every command, not just read-only ones.
    ///
    /// Also owns per-request CancellationTokenSources (cancel frames are handled on
    /// the TCP thread, so a running UI-thread command can be cancelled cooperatively)
    /// and the crash-recovery write-ahead log.
    /// </summary>
    public static class OperationRegistry
    {
        private const int COMPLETED_CACHE_MAX = 64;

        public enum BeginOutcome { New, Replay, Join }

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, JObject> _completed = new Dictionary<string, JObject>();
        private static readonly Queue<string> _completedOrder = new Queue<string>();
        private static readonly Dictionary<string, TaskCompletionSource<JObject>> _inFlight
            = new Dictionary<string, TaskCompletionSource<JObject>>();
        private static readonly Dictionary<string, CancellationTokenSource> _cts
            = new Dictionary<string, CancellationTokenSource>();

        /// <summary>Register a mutating request. Replay/Join outcomes mean: do NOT execute again.</summary>
        public static BeginOutcome Begin(string requestId, out JObject cached, out Task<JObject> joinTask)
        {
            cached = null; joinTask = null;
            lock (_gate)
            {
                if (_completed.TryGetValue(requestId, out cached))
                    return BeginOutcome.Replay;
                if (_inFlight.TryGetValue(requestId, out var tcs))
                {
                    joinTask = tcs.Task;
                    return BeginOutcome.Join;
                }
                _inFlight[requestId] = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                _cts[requestId] = new CancellationTokenSource();
                return BeginOutcome.New;
            }
        }

        /// <summary>Called by the UI-thread lambda when a mutating command finishes (success OR error).</summary>
        public static void Complete(string requestId, JObject result)
        {
            if (string.IsNullOrEmpty(requestId) || result == null) return;
            TaskCompletionSource<JObject> tcs = null;
            lock (_gate)
            {
                if (_inFlight.TryGetValue(requestId, out tcs)) _inFlight.Remove(requestId);
                if (_cts.TryGetValue(requestId, out var c)) { _cts.Remove(requestId); try { c.Dispose(); } catch { } }
                if (!_completed.ContainsKey(requestId))
                {
                    _completed[requestId] = result;
                    _completedOrder.Enqueue(requestId);
                    while (_completedOrder.Count > COMPLETED_CACHE_MAX)
                        _completed.Remove(_completedOrder.Dequeue());
                }
            }
            tcs?.TrySetResult(result);
        }

        /// <summary>
        /// Look up a finished (or still-running) operation by request_id.
        ///
        /// v4.14 (field report A4): when the MCP client gives up before the plugin's
        /// budget, the command keeps running and completes - but its printed output and
        /// results were unreachable, so the agent could not tell whether the work had
        /// happened. The replay cache already held the answer; nothing exposed it.
        /// </summary>
        public static JObject Lookup(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
                return new JObject { ["status"] = "error", ["message"] = "request_id required" };
            lock (_gate)
            {
                if (_completed.TryGetValue(requestId, out var done))
                    return new JObject
                    {
                        ["status"] = "ok",
                        ["state"] = "completed",
                        ["request_id"] = requestId,
                        ["result"] = done.DeepClone(),
                    };
                if (_inFlight.ContainsKey(requestId))
                    return new JObject
                    {
                        ["status"] = "ok",
                        ["state"] = "running",
                        ["request_id"] = requestId,
                        ["note"] = "Still executing in Rhino. Poll again, or cancel_operation to stop it.",
                    };
            }
            return new JObject
            {
                ["status"] = "ok",
                ["state"] = "unknown",
                ["request_id"] = requestId,
                ["note"] = "No record. Either it never arrived, or it finished long enough ago to fall out "
                         + "of the replay cache (last " + COMPLETED_CACHE_MAX + " operations).",
            };
        }

        /// <summary>Ids of operations currently executing, newest last.</summary>
        public static JArray InFlightIds()
        {
            lock (_gate) return new JArray(_inFlight.Keys);
        }

        public static CancellationToken TokenFor(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return CancellationToken.None;
            lock (_gate)
                return _cts.TryGetValue(requestId, out var c) ? c.Token : CancellationToken.None;
        }

        /// <summary>Request cancellation of a running operation. Safe from any thread.</summary>
        public static bool Cancel(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return false;
            lock (_gate)
            {
                if (_cts.TryGetValue(requestId, out var c)) { try { c.Cancel(); } catch { } return true; }
            }
            return false;
        }

        // ── Current-operation token (UI thread only) ─────────────────────────
        // All dispatched commands run serially on Rhino's UI thread, so a simple
        // thread-static is correct: long-running handlers poll CancelRequested
        // between loop iterations.
        [ThreadStatic] private static CancellationToken _currentToken;

        public static void SetCurrent(CancellationToken token) => _currentToken = token;
        public static void ClearCurrent() => _currentToken = CancellationToken.None;
        public static bool CancelRequested => _currentToken.IsCancellationRequested;
    }

    /// <summary>
    /// Crash-safe write-ahead log: every top-level mutating command is appended
    /// (JSONL) BEFORE execution and its status afterwards. After a Rhino crash the
    /// agent can read the tail to recover what it was doing and diff against the scene.
    /// Location: %APPDATA%\AIBridge\wal\
    /// </summary>
    public static class WriteAheadLog
    {
        private static readonly object _gate = new object();
        private static string _path;
        private const int MAX_SESSION_FILES = 20;

        private static string EnsurePath()
        {
            if (_path != null) return _path;
            lock (_gate)
            {
                if (_path != null) return _path;
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIBridge", "wal");
                Directory.CreateDirectory(dir);
                // prune old session files
                try
                {
                    var old = new DirectoryInfo(dir).GetFiles("wal_*.jsonl")
                        .OrderByDescending(f => f.LastWriteTimeUtc).Skip(MAX_SESSION_FILES - 1);
                    foreach (var f in old) { try { f.Delete(); } catch { } }
                }
                catch { }
                _path = Path.Combine(dir, $"wal_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}.jsonl");
                return _path;
            }
        }

        public static void Append(string phase, string requestId, string type, string paramsSummary, long sceneVersion, string status = null)
        {
            try
            {
                var entry = new JObject
                {
                    ["ts"] = DateTime.UtcNow.ToString("o"),
                    ["phase"] = phase,
                    ["request_id"] = requestId,
                    ["type"] = type,
                    ["scene_version"] = sceneVersion,
                };
                if (paramsSummary != null) entry["params"] = paramsSummary;
                if (status != null) entry["status"] = status;
                var line = entry.ToString(Formatting.None) + "\n";
                lock (_gate) File.AppendAllText(EnsurePath(), line, Encoding.UTF8);
            }
            catch { /* WAL must never break a command */ }
        }

        /// <summary>Return the last <paramref name="limit"/> entries of the current session log.</summary>
        public static JArray GetRecent(int limit)
        {
            var arr = new JArray();
            try
            {
                string path;
                lock (_gate) path = _path;
                if (path == null || !File.Exists(path))
                {
                    // No writes this session yet - fall back to the most recent session file.
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIBridge", "wal");
                    if (!Directory.Exists(dir)) return arr;
                    path = new DirectoryInfo(dir).GetFiles("wal_*.jsonl")
                        .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
                    if (path == null) return arr;
                }
                string[] lines;
                lock (_gate) lines = File.ReadAllLines(path);
                foreach (var line in lines.Skip(Math.Max(0, lines.Length - limit)))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { arr.Add(JObject.Parse(line)); } catch { }
                }
            }
            catch { }
            return arr;
        }
    }
}
