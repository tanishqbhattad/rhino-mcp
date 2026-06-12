// RhinoAIBridge v4.8.0 - AIBridgePlugin.cs
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

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
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
            // Avoid Windows' "Server Busy" dialog while Rhino tears down and background
            // MCP client threads may still be winding down.
            OleMessageFilter.Install();
            try
            {
                AIBridgeServerController.StopForRhinoShutdown();
            }
            catch { }
            finally
            {
                OleMessageFilter.Uninstall();
            }
        }
    }

    // =====================================================================
    // OLE Message Filter - suppresses "Server Busy" dialog during shutdown
    // =====================================================================
    internal static class OleMessageFilter
    {
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000016-0000-0000-C000-000000000046")]
        private interface IMessageFilter
        {
            [PreserveSig]
            int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
            [PreserveSig]
            int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
            [PreserveSig]
            int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
        }

        private class ShutdownFilter : IMessageFilter
        {
            // SERVERCALL_ISHANDLED = 0 - accept incoming calls.
            public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => 0;

            // SERVERCALL_RETRYLATER = 2 - return -1 to cancel instead of retrying.
            public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType) => -1;

            // PENDINGMSG_WAITNOPROCESS = 2 - do not process pending messages.
            public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => 2;
        }

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IMessageFilter lpMessageFilter, out IMessageFilter lplpMessageFilter);

        private static IMessageFilter _oldFilter;
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            CoRegisterMessageFilter(new ShutdownFilter(), out _oldFilter);
            _installed = true;
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            CoRegisterMessageFilter(_oldFilter, out _);
            _installed = false;
        }
    }
}
