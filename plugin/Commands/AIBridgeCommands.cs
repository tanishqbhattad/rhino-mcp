using System;
using Rhino;
using Rhino.Commands;

namespace RhinoAIBridge.Commands
{
    public class AIBridgeCommand : Command
    {
        public static AIBridgeCommand Instance { get; private set; }
        public AIBridgeCommand() { Instance = this; }
        public override string EnglishName => "AIBridge";
        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            try
            {
                AIBridgeServerController.StartServer();
                var chosen = AIBridgeServer.PromptAndApplyMode(mode != RunMode.Scripted);
                RhinoApp.WriteLine($"AIBridge: {chosen} mode active. Listening on 127.0.0.1:9544.");
                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"AIBridge: Command failed - {ex.Message}");
                return Result.Failure;
            }
        }
    }

    public class AIBridgeStopCommand : Command
    {
        public static AIBridgeStopCommand Instance { get; private set; }
        public AIBridgeStopCommand() { Instance = this; }
        public override string EnglishName => "AIBridgeStop";
        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            AIBridgeServerController.StopServer();
            return Result.Success;
        }
    }
}
