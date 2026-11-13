// STUB: replaced by Slice D (Safety/* and Tools/AccessTools). Delete this file when the real types exist.
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Safety;

namespace PlaywrightWindows.Mcp.Safety
{
    public sealed class DialogPrompter : IUserPrompter
    {
        public DialogPrompter(UiThread ui, ServerConfig config) { }
        public Task<PromptAnswer> AskAccessAsync(AccessPrompt prompt, CancellationToken cancel) => Task.FromResult(PromptAnswer.Unavailable);
        public Task<PromptAnswer> ConfirmAsync(ConfirmPrompt prompt, CancellationToken cancel) => Task.FromResult(PromptAnswer.Unavailable);
    }
    public sealed class ControlOverlay : IOverlay
    {
        public ControlOverlay(UiThread ui, ServerConfig config) { }
        public bool Visible => false;
        public void Show(string? detail) { }
        public void Hide() { }
        public IDisposable HideScope() => new NullOverlay().HideScope();
        public void Dispose() { }
    }
    public sealed class KillSwitch : IDisposable
    {
        public KillSwitch(UiThread ui, string hotkey, Action onFire) { }
        public bool Registered => false;
        public void Dispose() { }
    }
    public sealed class InputMonitor : IDisposable
    {
        public InputMonitor(UiThread ui, SafetyState state, ServerConfig config) { }
        public bool Active => false;
        public void Dispose() { }
    }
}

namespace PlaywrightWindows.Mcp.Tools
{
    public static class SafetyTools { public static void Register(ToolRegistry registry, Services services) { } }
}
