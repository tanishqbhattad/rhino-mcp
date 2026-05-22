// RhinoAIBridge v4.7.5 — AIBridgeServerController.cs
// by tanishqb | https://github.com/tanishqb/rhino-ai-bridge

namespace RhinoAIBridge
{
    /// <summary>
    /// Static singleton controller — thin wrapper so plugin/commands don't hold
    /// a direct reference to the AIBridgeServer instance.
    /// </summary>
    public static class AIBridgeServerController
    {
        private static readonly AIBridgeServer _server = new AIBridgeServer();

        public static bool IsRunning => _server.IsRunning;

        public static void StartServer()
        {
            if (!_server.IsRunning) _server.Start();
        }

        public static void StopServer()
        {
            if (_server.IsRunning) _server.Stop();
        }

        public static void ForceKill()
        {
            _server.ForceRelease();
        }
    }
}
