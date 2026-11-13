namespace PlaywrightWindows.Mcp.Safety;

public enum PromptAnswer
{
    Denied,
    AllowSession,
    AllowAlways,
    Timeout,
    Unavailable
}

public sealed record AccessPrompt(IReadOnlyList<string> Apps, AccessTier Tier, string Reason, TimeSpan Timeout);

public sealed record ConfirmPrompt(string Action, string Target, string App, string Pattern, TimeSpan Timeout);

/// <summary>
/// The human-in-the-loop surface. The model can never answer these itself: implementations show a native,
/// always-on-top dialog in the interactive session (<c>prompts: dialog</c>) or deny outright (<c>prompts: deny</c>).
/// </summary>
public interface IUserPrompter
{
    Task<PromptAnswer> AskAccessAsync(AccessPrompt prompt, CancellationToken cancel);
    Task<PromptAnswer> ConfirmAsync(ConfirmPrompt prompt, CancellationToken cancel);
}

/// <summary>Headless/test mode: every request is denied immediately (the model is told to ask the human).</summary>
public sealed class DenyPrompter : IUserPrompter
{
    public Task<PromptAnswer> AskAccessAsync(AccessPrompt prompt, CancellationToken cancel) => Task.FromResult(PromptAnswer.Unavailable);
    public Task<PromptAnswer> ConfirmAsync(ConfirmPrompt prompt, CancellationToken cancel) => Task.FromResult(PromptAnswer.Unavailable);
}

/// <summary>Always-on-top, click-through "agent is driving" indicator.</summary>
public interface IOverlay : IDisposable
{
    bool Visible { get; }
    void Show(string? detail);
    void Hide();
    /// <summary>Hides the overlay for the lifetime of the returned scope (screenshots must not contain it).</summary>
    IDisposable HideScope();
}

public sealed class NullOverlay : IOverlay
{
    public bool Visible => false;
    public void Show(string? detail) { }
    public void Hide() { }
    public IDisposable HideScope() => new NoScope();
    public void Dispose() { }

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
    }
}
