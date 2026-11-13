using System.Drawing;
using System.Text.Json;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Safety;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class SafetyTests
{
    // ----- HotkeyParser -----

    [Theory]
    [InlineData("ctrl+alt+pause", 0x13)]
    [InlineData("control+shift+break", 0x13)]
    [InlineData("alt+scrolllock", 0x91)]
    [InlineData("win+printscreen", 0x2C)]
    [InlineData("ctrl+esc", 0x1B)]
    [InlineData("ctrl+home", 0x24)]
    [InlineData("ctrl+end", 0x23)]
    [InlineData("shift+insert", 0x2D)]
    [InlineData("ctrl+delete", 0x2E)]
    [InlineData("alt+pageup", 0x21)]
    [InlineData("alt+pagedown", 0x22)]
    [InlineData("ctrl+space", 0x20)]
    [InlineData("ctrl+q", 0x51)]
    [InlineData("alt+7", 0x37)]
    [InlineData("ctrl+alt+f24", 0x87)]
    public void Parse_KnownKeys(string hotkey, uint vk)
    {
        var (mods, key) = HotkeyParser.Parse(hotkey);
        Assert.Equal(vk, key);
        Assert.Equal(Native.MOD_NOREPEAT, mods & Native.MOD_NOREPEAT);
    }

    [Fact]
    public void Parse_ModifiersCombine()
    {
        var (mods, _) = HotkeyParser.Parse("ctrl+alt+pause");
        Assert.Equal(Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, mods);

        var (winMods, _) = HotkeyParser.Parse("win+meta+q");
        Assert.Equal(Native.MOD_WIN | Native.MOD_NOREPEAT, winMods);
    }

    [Theory]
    [InlineData("ctrl+alt+scroll", "scroll")]
    [InlineData("ctrl+hyper+p", "hyper")]
    [InlineData("ctrl+alt+f25", "f25")]
    [InlineData("ctrl++", "+")] // "+" splits into empty tokens
    public void Parse_BadTokenThrows(string hotkey, string token)
    {
        var ex = Assert.Throws<ArgumentException>(() => HotkeyParser.Parse(hotkey));
        Assert.Contains(token, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ctrl")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_MissingKeyThrows(string hotkey)
    {
        Assert.Throws<ArgumentException>(() => HotkeyParser.Parse(hotkey));
    }

    [Fact]
    public void Parse_TwoKeysThrows()
    {
        var ex = Assert.Throws<ArgumentException>(() => HotkeyParser.Parse("ctrl+alt+pause+q"));
        Assert.Contains("q", ex.Message);
    }

    // ----- AuditLog.Redact -----

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void Redact_NestedKeysAndArrays()
    {
        var args = Json("""
        {
          "text": "typed secret",
          "value": 42,
          "enabled": true,
          "nested": { "password": "hunter2", "keep": "visible" },
          "items": [ "a", { "content": "hidden" }, 5 ]
        }
        """);
        var node = AuditLog.Redact(args, new[] { "keep" });
        Assert.NotNull(node);
        Assert.Equal("<redacted 12 chars>", node!["text"]!.GetValue<string>());
        Assert.Equal(42, node["value"]!.GetValue<int>());          // non-string values are untouched
        Assert.True(node["enabled"]!.GetValue<bool>());
        Assert.Equal("<redacted 7 chars>", node["nested"]!["password"]!.GetValue<string>());
        Assert.Equal("<redacted 7 chars>", node["nested"]!["keep"]!.GetValue<string>()); // custom key from redactedKeys
        var items = node["items"]!.AsArray();
        Assert.Equal("a", items[0]!.GetValue<string>());
        Assert.Equal("<redacted 6 chars>", items[1]!["content"]!.GetValue<string>());
        Assert.Equal(5, items[2]!.GetValue<int>());
    }

    [Fact]
    public void Redact_AlwaysRedactedKeys_AreCaseInsensitive()
    {
        var args = Json("""{ "ClipBoard": "abc", "PASSWORD": "xyz", "other": "fine" }""");
        var node = AuditLog.Redact(args, Array.Empty<string>());
        Assert.Equal("<redacted 3 chars>", node!["ClipBoard"]!.GetValue<string>());
        Assert.Equal("<redacted 3 chars>", node["PASSWORD"]!.GetValue<string>());
        Assert.Equal("fine", node["other"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_NonObjectOrNull_ReturnsNull()
    {
        Assert.Null(AuditLog.Redact(null, Array.Empty<string>()));
        Assert.Null(AuditLog.Redact(Json("[1,2]"), Array.Empty<string>()));
    }

    // ----- AccessPolicy -----

    private static WindowInfo Window(string process, string title, uint pid = 100, string className = "Notepad") => new(
        "w1", new IntPtr(1000), title, pid, process, @"C:\x\" + process,
        new Rectangle(0, 0, 800, 600), 1, false, true, className);

    private static ServerConfig PolicyConfig() => new()
    {
        BlockHarnessTerminal = false, // keep the test process chain out of the picture
        Allowlist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    };

    [Fact]
    public void IsBlocked_BlockedExe()
    {
        var policy = new AccessPolicy(PolicyConfig());
        Assert.True(policy.IsBlocked(Window("KeePass.exe", "Passwords"), out var reason));
        Assert.Contains("blocked-apps list", reason);
        Assert.False(policy.IsBlocked(Window("notepad.exe", "Untitled"), out _));
    }

    [Fact]
    public void IsBlocked_TitleRegex()
    {
        var policy = new AccessPolicy(PolicyConfig());
        Assert.True(policy.IsBlocked(Window("chrome.exe", "My Bank - Sign in"), out var reason));
        Assert.Contains("title matches", reason);
        Assert.False(policy.IsBlocked(Window("chrome.exe", "Documentation - Mozilla Firefox"), out _));
    }

    [Fact]
    public void IsBlocked_HarnessPid()
    {
        var config = PolicyConfig();
        config.BlockHarnessTerminal = true; // harness detection adds this test process
        var policy = new AccessPolicy(config);
        Assert.Contains((uint)Environment.ProcessId, policy.HarnessPids);
        Assert.True(policy.IsBlocked(Window("pwsh.exe", "shell", pid: (uint)Environment.ProcessId), out var reason));
        Assert.Contains("harness", reason);
    }

    [Fact]
    public void IsBlocked_AllowBlockedAppsOverride()
    {
        var config = PolicyConfig();
        config.AllowBlockedApps = true;
        var policy = new AccessPolicy(config);
        Assert.False(policy.IsBlocked(Window("KeePass.exe", "My Bank"), out _));
    }

    [Fact]
    public void MatchRisky()
    {
        var policy = new AccessPolicy(PolicyConfig());
        Assert.Equal(@"\bpay(ment)?\b", policy.MatchRisky("Pay now"));
        Assert.Equal(@"\bpay(ment)?\b", policy.MatchRisky("Payment method"));
        Assert.Equal(@"\bdelete permanently\b", policy.MatchRisky("Delete permanently?"));
        Assert.Null(policy.MatchRisky("Play"));
        Assert.Null(policy.MatchRisky(null));
        Assert.Null(policy.MatchRisky(""));
    }

    // ----- SafetyState -----

    private static (ServerConfig Config, string Dir) TempConfig(double idleResumeSeconds = 60)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wmcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ServerConfig
        {
            Yolo = false,
            IdleResumeSeconds = idleResumeSeconds,
            GrantsPath = Path.Combine(dir, "grants.json"),
            Allowlist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        return (config, dir);
    }

    [Fact]
    public void TierFor_YoloBeatsEverything()
    {
        var (config, dir) = TempConfig();
        try
        {
            config.Yolo = true;
            var state = new SafetyState(config);
            Assert.Equal(AccessTier.Control, state.TierFor("anything.exe"));
            Assert.Equal(AccessTier.Control, state.TierFor(null));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TierFor_TakesTheHighestSource()
    {
        var (config, dir) = TempConfig();
        try
        {
            config.Allowlist["app1.exe"] = "view";
            config.Allowlist["app2.exe"] = "control";
            var state = new SafetyState(config);

            Assert.Equal(AccessTier.View, state.TierFor("app1"));                 // allowlist only
            Assert.Equal(AccessTier.Control, state.TierFor("app2"));              // allowlist control
            state.AddGrant("app1", AccessTier.Control, false, "test", "test");   // session beats allowlist
            Assert.Equal(AccessTier.Control, state.TierFor("app1.exe"));
            state.AddGrant("app2", AccessTier.View, false, "test", "test");      // lower session cannot demote
            Assert.Equal(AccessTier.Control, state.TierFor("app2"));
            state.AddGrant("app3", AccessTier.View, true, "test", "test");       // persistent grants count too
            Assert.Equal(AccessTier.View, state.TierFor("APP3"));
            Assert.Equal(AccessTier.None, state.TierFor("unknown"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RevokeControlGrants_DropsToView()
    {
        var (config, dir) = TempConfig();
        try
        {
            var state = new SafetyState(config);
            state.AddGrant("a.exe", AccessTier.Control, false, "r", "s");
            state.AddGrant("b.exe", AccessTier.Control, true, "r", "s");
            state.AddGrant("c.exe", AccessTier.View, false, "r", "s");

            var revoked = state.RevokeControlGrants();
            Assert.Equal(2, revoked);
            Assert.Equal(AccessTier.View, state.TierFor("a.exe"));
            Assert.Equal(AccessTier.View, state.TierFor("b.exe"));
            Assert.Equal(AccessTier.View, state.TierFor("c.exe"));

            // Revocation of persistent grants is persisted.
            var reloaded = new SafetyState(config);
            Assert.Equal(AccessTier.View, reloaded.TierFor("b.exe"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ClearAbortIfStale_RespectsGrace()
    {
        var (config, dir) = TempConfig();
        try
        {
            var state = new SafetyState(config);
            state.TriggerAbort("test");
            Assert.True(state.IsAborted);

            state.ClearAbortIfStale(1500); // within the grace window the abort stays armed
            Assert.True(state.IsAborted);

            Thread.Sleep(30);
            state.ClearAbortIfStale(1); // past the grace the token is renewed
            Assert.False(state.IsAborted);
            Assert.Equal(1, state.AbortCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IsUserActive_HonoursIdleResumeAndExplicitResume()
    {
        var (config, dir) = TempConfig(idleResumeSeconds: 60);
        try
        {
            var state = new SafetyState(config);
            Assert.False(state.IsUserActive);              // no input seen yet

            state.NoteUserInput("mouse");
            Assert.True(state.IsUserActive);
            Assert.Equal("mouse", state.LastUserInputKind);

            state.ExplicitResume();                        // human said "continue"
            Assert.False(state.IsUserActive);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IsUserActive_ExpiresAfterIdleResumeSeconds()
    {
        var (config, dir) = TempConfig(idleResumeSeconds: 0.01);
        try
        {
            var state = new SafetyState(config);
            state.NoteUserInput("keyboard");
            Assert.True(state.IsUserActive);
            Thread.Sleep(30);
            Assert.False(state.IsUserActive);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IsUserActive_DisabledInYolo()
    {
        var (config, dir) = TempConfig();
        try
        {
            config.Yolo = true;
            var state = new SafetyState(config);
            state.NoteUserInput("mouse");
            Assert.False(state.IsUserActive);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PersistentGrants_SurviveReload()
    {
        var (config, dir) = TempConfig();
        try
        {
            var state = new SafetyState(config);
            state.AddGrant("persist.exe", AccessTier.Control, true, "always", "user");
            state.AddGrant("session.exe", AccessTier.Control, false, "once", "user");

            var reloaded = new SafetyState(config);
            Assert.Equal(AccessTier.Control, reloaded.TierFor("persist.exe"));
            Assert.Equal(AccessTier.None, reloaded.TierFor("session.exe")); // session grants are not persisted
        }
        finally { Directory.Delete(dir, true); }
    }
}
