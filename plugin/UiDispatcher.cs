using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoAIBridge
{
    /// <summary>
    /// UI-thread dispatcher.
    /// 
    /// v3 used per-call ManualResetEventSlim allocation:
    ///     var wait = new ManualResetEventSlim(false);
    ///     RhinoApp.InvokeOnUiThread(() => { ...; wait.Set(); });
    ///     wait.Wait(...);
    /// 
    /// v4 keeps RhinoApp.InvokeOnUiThread (still the only safe primitive Rhino exposes for cross-thread doc access).
    /// Each invocation owns its completion source so a timed-out queued action cannot later write into
    /// a reused slot from a newer request.
    /// </summary>
    public static class UiDispatcher
    {
        private static volatile bool _shuttingDown;
        private static int _activeInvokes;

        private sealed class WorkSlot
        {
            public readonly TaskCompletionSource<JObject> Completion =
                new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            public volatile bool Cancelled;
        }

        public static void Start()
        {
            _shuttingDown = false;
        }

        public static void BeginShutdown()
        {
            _shuttingDown = true;
        }

        public static void Stop()
        {
            BeginShutdown();
            WaitForIdle(TimeSpan.FromSeconds(2));
        }

        public static bool WaitForIdle(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _activeInvokes) > 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(25);
            return Volatile.Read(ref _activeInvokes) == 0;
        }

        /// <summary>
        /// Run <paramref name="func"/> on Rhino's UI thread. Blocks the calling thread (a TCP client thread)
        /// until completion or timeout.
        /// </summary>
        public static JObject Invoke(Func<JObject> func, TimeSpan timeout)
        {
            if (_shuttingDown)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["error_code"] = "AIBRIDGE_SHUTTING_DOWN",
                    ["message"] = "AIBridge is shutting down; command was not started."
                };
            }

            var slot = new WorkSlot();
            try
            {
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    if (slot.Cancelled || _shuttingDown)
                    {
                        slot.Completion.TrySetResult(new JObject
                        {
                            ["status"] = "error",
                            ["error_code"] = slot.Cancelled ? "COMMAND_CANCELLED_BEFORE_START" : "AIBRIDGE_SHUTTING_DOWN",
                            ["message"] = slot.Cancelled
                                ? "Command timed out before Rhino's UI thread started it."
                                : "AIBridge is shutting down; command was not started."
                        });
                        return;
                    }

                    Interlocked.Increment(ref _activeInvokes);
                    try { slot.Completion.TrySetResult(func()); }
                    catch (Exception e) { slot.Completion.TrySetException(e); }
                    finally { Interlocked.Decrement(ref _activeInvokes); }
                }));
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Failed to queue command on Rhino UI thread: {e.Message}", e);
            }

            if (!slot.Completion.Task.Wait(timeout))
            {
                slot.Cancelled = true;
                throw new TimeoutException(
                    $"Command timed out after {timeout.TotalSeconds}s. If Rhino had already started the command, it may still be finishing on the UI thread.");
            }

            return slot.Completion.Task.GetAwaiter().GetResult()
                ?? new JObject { ["status"] = "error", ["message"] = "No result" };
        }
    }
}
