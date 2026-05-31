// RhinoAIBridge v4.5 — AIBridgeLogger.cs
// by tanishqb | https://github.com/tanishqb/rhino-ai-bridge

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rhino;

namespace RhinoAIBridge
{
    public enum LogLevel { DEBUG, INFO, WARN, ERROR }

    /// <summary>
    /// Async logger. v3 wrote to disk synchronously inside a lock on every command,
    /// which serialized commands behind disk I/O. v4 fire-and-forget enqueues to a
    /// bounded queue and a single background task drains it to disk.
    /// </summary>
    public static class AIBridgeLogger
    {
        private static string _logDir;
        private static string _currentLogFile;
        private static DateTime _currentDate;
        private static readonly ConcurrentQueue<LogEntry> _recentEntries = new();
        private const int MAX_RECENT = 200;

        // Bounded channel — if logging falls behind, oldest pending entries get dropped
        // before we ever block a Rhino command. Logging is never on the critical path.
        private static readonly ConcurrentQueue<LogEntry> _pendingEntries = new();
        private static readonly AutoResetEvent _pendingSignal = new AutoResetEvent(false);
        private const int MAX_PENDING = 4096;
        private static Task _writerTask;
        private static CancellationTokenSource _cts;

        public struct LogEntry
        {
            public DateTime Timestamp; public LogLevel Level; public string Category;
            public string Message; public string CommandType; public double? ElapsedMs; public string Error;
            public override string ToString()
            {
                var sb = new StringBuilder(128);
                sb.Append('[').Append(Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] [").Append(Level).Append(']');
                if (!string.IsNullOrEmpty(Category)) sb.Append(" [").Append(Category).Append(']');
                if (!string.IsNullOrEmpty(CommandType)) sb.Append(" CMD:").Append(CommandType);
                sb.Append(' ').Append(Message);
                if (ElapsedMs.HasValue) sb.Append(" (").Append(ElapsedMs.Value.ToString("F1")).Append("ms)");
                if (!string.IsNullOrEmpty(Error)) sb.Append("\n  ERROR: ").Append(Error);
                return sb.ToString();
            }
        }

        public static void Initialize()
        {
            _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIBridge", "logs");
            Directory.CreateDirectory(_logDir);
            RotateLogFile();

            // Clean logs older than 7 days
            try { foreach (var f in Directory.GetFiles(_logDir, "*.log").Where(f => File.GetCreationTime(f) < DateTime.Now.AddDays(-7))) File.Delete(f); } catch { }

            _cts = new CancellationTokenSource();
            _writerTask = Task.Run(() => WriterLoop(_cts.Token));

            Log(LogLevel.INFO, "Logger", $"Initialized — {_logDir}");
        }

        public static void Shutdown()
        {
            try
            {
                _cts?.Cancel();
                _pendingSignal.Set();
                _writerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
        }

        private static void RotateLogFile()
        {
            _currentDate = DateTime.Now.Date;
            _currentLogFile = Path.Combine(_logDir, $"aibridge_{_currentDate:yyyy-MM-dd}.log");
        }

        private static void WriterLoop(CancellationToken ct)
        {
            // Batch entries into a single file write per drain cycle.
            // The reader awaits, so this thread is parked unless work arrives.
            var batch = new StringBuilder(8192);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    _pendingSignal.WaitOne(TimeSpan.FromSeconds(1));
                    if (ct.IsCancellationRequested) break;
                    if (!_pendingEntries.TryDequeue(out var entry)) continue;
                    if (DateTime.Now.Date != _currentDate) RotateLogFile();
                    batch.Clear();
                    batch.AppendLine(entry.ToString());

                    // Drain anything else already queued — coalesce writes.
                    while (_pendingEntries.TryDequeue(out var more))
                    {
                        batch.AppendLine(more.ToString());
                    }

                    try { File.AppendAllText(_currentLogFile, batch.ToString()); } catch { }
                }
            }
            catch { /* writer must never crash the plugin */ }
        }

        public static void Log(LogLevel level, string category, string message,
                               string commandType = null, double? elapsedMs = null, string error = null)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Category = category,
                Message = message,
                CommandType = commandType,
                ElapsedMs = elapsedMs,
                Error = error
            };

            // In-memory ring (for get_log tool). ConcurrentQueue, lock-free.
            _recentEntries.Enqueue(entry);
            while (_recentEntries.Count > MAX_RECENT) _recentEntries.TryDequeue(out _);

            // Disk: fire-and-forget enqueue. Bounded + DropOldest, so this never blocks.
            _pendingEntries.Enqueue(entry);
            while (_pendingEntries.Count > MAX_PENDING) _pendingEntries.TryDequeue(out _);
            _pendingSignal.Set();

            if (level == LogLevel.ERROR)
            {
                try { RhinoApp.WriteLine($"AIBridge ERROR: {message}"); } catch { }
            }
        }

        public static Stopwatch StartTimer() => Stopwatch.StartNew();

        public static void LogCommand(string cmdType, string paramsSummary, Stopwatch timer, string status, string error = null)
        {
            timer.Stop();
            Log(status == "ok" ? LogLevel.INFO : LogLevel.ERROR, "Command",
                $"{status} | params: {paramsSummary}", cmdType, timer.Elapsed.TotalMilliseconds, error);
        }

        public static List<LogEntry> GetRecentEntries(int count = 50, LogLevel? minLevel = null)
        {
            var entries = _recentEntries.ToList();
            if (minLevel.HasValue) entries = entries.Where(e => e.Level >= minLevel.Value).ToList();
            return entries.TakeLast(count).ToList();
        }

        public static string GetLogFilePath() => _currentLogFile;

        public static Dictionary<string, object> GetStats()
        {
            var entries = _recentEntries.ToList();
            var cmds = entries.Where(e => !string.IsNullOrEmpty(e.CommandType)).ToList();
            var errors = entries.Where(e => e.Level == LogLevel.ERROR).ToList();
            return new Dictionary<string, object>
            {
                ["total_commands"] = cmds.Count,
                ["total_errors"] = errors.Count,
                ["avg_response_ms"] = cmds.Where(e => e.ElapsedMs.HasValue).Select(e => e.ElapsedMs.Value).DefaultIfEmpty(0).Average(),
                ["p95_response_ms"] = Percentile(cmds.Where(e => e.ElapsedMs.HasValue).Select(e => e.ElapsedMs.Value).ToList(), 0.95),
                ["slowest_command"] = cmds.OrderByDescending(e => e.ElapsedMs ?? 0).FirstOrDefault().CommandType ?? "none",
                ["log_file"] = _currentLogFile ?? "",
                ["uptime_minutes"] = entries.Any() ? (DateTime.Now - entries.First().Timestamp).TotalMinutes : 0,
            };
        }

        private static double Percentile(List<double> values, double p)
        {
            if (values == null || values.Count == 0) return 0;
            values.Sort();
            int idx = (int)Math.Ceiling(p * values.Count) - 1;
            if (idx < 0) idx = 0;
            if (idx >= values.Count) idx = values.Count - 1;
            return values[idx];
        }
    }
}
