using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>Registers the perception tools of slice A: list windows, snapshot, find, get text.</summary>
public static class SnapshotTools
{
    public static void Register(ToolRegistry registry, Services services)
    {
        registry.RegisterTool(new ListWindowsTool(services));
        registry.RegisterTool(new SnapshotTool(services));
        registry.RegisterTool(new FindTool(services));
        registry.RegisterTool(new GetTextTool(services));
    }
}
