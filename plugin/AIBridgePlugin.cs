// RhinoAIBridge - AIBridgePlugin.cs
// by tanishqb | https://github.com/tanishqbhattad/rhino-mcp

using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;

namespace RhinoAIBridge
{
    public class AIBridgePlugin : PlugIn
    {
        public AIBridgePlugin() { Instance = this; }
        public static AIBridgePlugin Instance { get; private set; }

        // v4.10: load with Rhino instead of on first command use, so the bridge's
        // auto-start (OnIdle below) makes the TCP server available the moment Rhino
        // is open - no manual AIBridge command needed. Mode comes from
        // RHINO_AIBRIDGE_MODE (safe|standard|developer), default Safe.
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // Install the COM message filter on Rhino's main STA thread up front. The
            // "Server Busy" dialog that hung Rhino on close was raised on the MAIN thread
            // during pre-close COM work - which blocks before RhinoApp.Closing fires, so a
            // filter installed in OnClosing was always too late. Installed here, a busy
            // cross-apartment call retries silently (no dialog) instead.
            try { OleMessageFilter.Install(); } catch { }
            RhinoApp.Idle += OnIdle;
            RhinoApp.Closing += OnClosing;
            return LoadReturnCode.Success;
        }

        private bool _started;

        private void OnIdle(object sender, EventArgs e)
        {
            if (_started) return;
            _started = true;
            RhinoApp.Idle -= OnIdle;
            try { AIBridgeServerController.StartServer(); }
            catch (Exception ex) { RhinoApp.WriteLine($"AIBridge: auto-start failed - {ex.Message}"); }
        }

        private void OnClosing(object sender, EventArgs e)
        {
            // Flip the filter to "cancel rejected calls" so the rest of Rhino's COM teardown
            // can never wedge on a busy cross-apartment call from a winding-down worker.
            OleMessageFilter.ShuttingDown = true;
            try { OleMessageFilter.Install(); } catch { }   // idempotent safety net
            try
            {
                AIBridgeServerController.StopForRhinoShutdown();
            }
            catch { }
            // Intentionally NOT uninstalling: the process is exiting and the filter must
            // stay active through the remainder of teardown.
        }
    }

    // =====================================================================
    // OLE Message Filter - prevents the modal "Server Busy" dialog. During
    // normal operation a rejected (busy) call retries silently for up to 30s;
    // during shutdown it is cancelled so teardown never blocks on a dialog.
    // =====================================================================
    internal static class OleMessageFilter
    {
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000016-0000-0000-C000-000000000046")]
        private interface IMessageFilter
        {
            [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
            [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
            [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
        }

        private sealed class Filter : IMessageFilter
        {
            // SERVERCALL_ISHANDLED = 0: accept incoming calls normally.
            public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => 0;

            // Return >= 0 to retry after that many ms; < 0 to cancel the call (no dialog either way).
            public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
            {
                if (dwRejectType != 2 /* SERVERCALL_RETRYLATER */) return -1; // hard-rejected: cancel
                if (ShuttingDown) return -1;                                   // closing: cancel, never block teardown
                if (dwTickCount > 30000) return -1;                            // give up after ~30s (avoid a permanent freeze)
                return 100;                                                    // otherwise retry silently after 100ms
            }

            // PENDINGMSG_WAITDEFPROCESS = 2: keep pumping input while waiting (stay responsive).
            public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => 2;
        }

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IMessageFilter lpMessageFilter, out IMessageFilter lplpMessageFilter);

        private static IMessageFilter _old;
        private static Filter _filter;          // keep a managed reference alongside the COM registration
        private static bool _installed;
        public static volatile bool ShuttingDown;

        public static void Install()
        {
            if (_installed) return;
            _filter = new Filter();
            if (CoRegisterMessageFilter(_filter, out _old) == 0) _installed = true;
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            CoRegisterMessageFilter(_old, out _);
            _installed = false;
            _filter = null;
        }
    }
}
