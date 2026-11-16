using System.Text.Json;
using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class GameModeTests
{
    // ----- profiles -----

    private static GameProfile Minecraft() => new()
    {
        Name = "minecraft",
        Process = "javaw.exe",
        WindowClass = "^GLFW30$"
    };

    [Theory]
    [InlineData("javaw.exe", "Minecraft* 1.20.1", "GLFW30", true)]
    [InlineData("JAVAW.EXE", "Some Modpack Title", "GLFW30", true)]
    [InlineData("javaw", "Minecraft 1.20.1", "GLFW30", true)]
    [InlineData("javaw.exe", "Prism Launcher console", "SunAwtFrame", false)]
    [InlineData("prismlauncher.exe", "Prism Launcher", "Qt650QWindowIcon", false)]
    [InlineData(null, "Minecraft", "GLFW30", false)]
    public void Profile_MatchesProcessAndClass(string? process, string title, string windowClass, bool expected)
    {
        Assert.Equal(expected, Minecraft().Matches(process, title, windowClass));
    }

    [Fact]
    public void Profile_TitleRegexNarrows()
    {
        var profile = new GameProfile { Process = "javaw.exe", Title = "^Minecraft" };
        Assert.True(profile.Matches("javaw.exe", "Minecraft 1.21", "GLFW30"));
        Assert.False(profile.Matches("javaw.exe", "Java Control Panel", "SunAwtFrame"));
    }

    [Fact]
    public void Profile_InvalidEntriesNeverMatch()
    {
        var badRegex = new GameProfile { Process = "javaw.exe", Title = "([" };
        Assert.NotNull(badRegex.Validate());
        Assert.False(badRegex.Matches("javaw.exe", "([", "GLFW30"));

        var noProcess = new GameProfile { Title = "Minecraft" };
        Assert.NotNull(noProcess.Validate());
        Assert.False(noProcess.Matches("javaw.exe", "Minecraft", "GLFW30"));

        var badCapture = new GameProfile { Process = "javaw.exe", Capture = "magic" };
        Assert.NotNull(badCapture.Validate());
    }

    [Fact]
    public void Config_NoProfilesMeansNoGameWindows()
    {
        var config = new ServerConfig();
        Assert.Empty(config.GameProfiles);
        Assert.Null(config.MatchGame("javaw.exe", "Minecraft", "GLFW30"));
        Assert.False(config.IsOverlaySuppressedFor("javaw.exe"));
    }

    [Fact]
    public void Config_ProfilesLoadFromJsonAndSuppressOverlayPerProcess()
    {
        var json = """
            { "gameProfiles": [ { "name": "minecraft", "process": "javaw.exe", "windowClass": "^GLFW30$", "overlay": false } ] }
            """;
        var config = JsonSerializer.Deserialize<ServerConfig>(json)!;
        var match = config.MatchGame("javaw.exe", "Minecraft 1.20.1", "GLFW30");
        Assert.NotNull(match);
        Assert.Equal("minecraft", match!.DisplayName);
        Assert.Equal("screen", match.Capture);
        Assert.True(match.IgnoreMouseMoveYield);
        Assert.True(config.IsOverlaySuppressedFor("javaw.exe"));
        Assert.False(config.IsOverlaySuppressedFor("notepad.exe"));
        Assert.Null(config.MatchGame("notepad.exe", "Untitled - Notepad", "Notepad"));
    }

    // ----- scan codes -----

    [Theory]
    [InlineData("w", 0x11, false)]
    [InlineData("A", 0x1E, false)]
    [InlineData("s", 0x1F, false)]
    [InlineData("d", 0x20, false)]
    [InlineData("e", 0x12, false)]
    [InlineData("q", 0x10, false)]
    [InlineData("m", 0x32, false)]
    [InlineData("1", 0x02, false)]
    [InlineData("9", 0x0A, false)]
    [InlineData("0", 0x0B, false)]
    [InlineData("space", 0x39, false)]
    [InlineData("esc", 0x01, false)]
    [InlineData("enter", 0x1C, false)]
    [InlineData("shift", 0x2A, false)]
    [InlineData("ctrl", 0x1D, false)]
    [InlineData("rctrl", 0x1D, true)]
    [InlineData("f1", 0x3B, false)]
    [InlineData("f3", 0x3D, false)]
    [InlineData("f10", 0x44, false)]
    [InlineData("f11", 0x57, false)]
    [InlineData("f12", 0x58, false)]
    [InlineData("up", 0x48, true)]
    [InlineData("left", 0x4B, true)]
    [InlineData("delete", 0x53, true)]
    [InlineData("slash", 0x35, false)]
    [InlineData("numpadenter", 0x1C, true)]
    public void ScanCodes_KnownKeys(string name, int code, bool extended)
    {
        Assert.True(ScanCodes.TryGet(name, out var key));
        Assert.Equal((ushort)code, key.Code);
        Assert.Equal(extended, key.Extended);
    }

    [Theory]
    [InlineData("win")]
    [InlineData("lwin")]
    [InlineData("")]
    [InlineData("ctrl+s")]
    public void ScanCodes_RejectsUnknownAndWindowsKeys(string name)
    {
        Assert.False(ScanCodes.TryGet(name, out _));
        var ex = Assert.Throws<ToolException>(() => ScanCodes.Require(name));
        Assert.Equal("unsupported_key", ex.Code);
    }

    // ----- sequences -----

    private static IReadOnlyList<GameEvent> Parse(string eventsJson)
    {
        using var doc = JsonDocument.Parse("{\"events\":" + eventsJson + "}");
        return GameSequence.Parse(doc.RootElement.Clone());
    }

    [Fact]
    public void Sequence_ParsesEveryEventKind()
    {
        var events = Parse("""
            [
              {"do":"key_down","key":"w"},
              {"do":"mouse_move","dx":300,"dy":-40,"ms":200},
              {"do":"wait","ms":500},
              {"do":"click","button":"right"},
              {"do":"key","key":"e","hold_ms":80},
              {"do":"mouse_to","x":412.5,"y":230,"image_id":"s3"},
              {"do":"button_down","button":"left"},
              {"do":"button_up","button":"left"},
              {"do":"scroll","amount":-2},
              {"do":"text","text":"/time set day"},
              {"do":"key_up","key":"w"}
            ]
            """);
        Assert.Equal(11, events.Count);
        Assert.Equal(GameEventKind.KeyDown, events[0].Kind);
        Assert.Equal((ushort)0x11, events[0].Key.Code);
        Assert.Equal((300, -40, 200), (events[1].Dx, events[1].Dy, events[1].Ms));
        Assert.Equal(GameEventKind.Wait, events[2].Kind);
        Assert.Equal(GameButton.Right, events[3].Button);
        Assert.Equal(GameSequence.DefaultTapMs, events[3].Ms);
        Assert.Equal(80, events[4].Ms);
        Assert.Equal((412.5, 230.0, "s3"), (events[5].X, events[5].Y, events[5].ImageId));
        Assert.Equal(-2, events[8].Amount);
        Assert.Equal("/time set day", events[9].Text);
        Assert.Equal(GameEventKind.KeyUp, events[10].Kind);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"do\":\"jump\"}]")]
    [InlineData("[{\"key\":\"w\"}]")]
    [InlineData("[{\"do\":\"key\",\"key\":\"win\"}]")]
    [InlineData("[{\"do\":\"mouse_move\",\"dx\":0,\"dy\":0}]")]
    [InlineData("[{\"do\":\"mouse_to\",\"x\":5}]")]
    [InlineData("[{\"do\":\"click\",\"button\":\"x1\"}]")]
    [InlineData("[{\"do\":\"wait\",\"ms\":0}]")]
    [InlineData("[{\"do\":\"wait\",\"ms\":20000}]")]
    [InlineData("[{\"do\":\"scroll\",\"amount\":0}]")]
    [InlineData("[{\"do\":\"text\",\"text\":\"\"}]")]
    [InlineData("[{\"do\":\"key\",\"key\":\"w\",\"hold_ms\":\"long\"}]")]
    [InlineData("[\"w\"]")]
    public void Sequence_RejectsBadEvents(string eventsJson)
    {
        var ex = Assert.Throws<ToolException>(() => Parse(eventsJson));
        Assert.Contains(ex.Code, new[] { "bad_request", "unsupported_key" });
    }

    [Fact]
    public void Sequence_EnforcesTotalBudgetAndCount()
    {
        var fourWaits = "[" + string.Join(",", Enumerable.Repeat("{\"do\":\"wait\",\"ms\":10000}", 4)) + "]";
        Assert.Throws<ToolException>(() => Parse(fourWaits));

        var threeWaits = "[" + string.Join(",", Enumerable.Repeat("{\"do\":\"wait\",\"ms\":10000}", 3)) + "]";
        Assert.Equal(3, Parse(threeWaits).Count);

        var tooMany = "[" + string.Join(",", Enumerable.Repeat("{\"do\":\"scroll\",\"amount\":1}", GameSequence.MaxEvents + 1)) + "]";
        Assert.Throws<ToolException>(() => Parse(tooMany));
    }

    [Fact]
    public void Sequence_RequiresEventsArray()
    {
        using var doc = JsonDocument.Parse("{\"events\":\"w\"}");
        Assert.Throws<ToolException>(() => GameSequence.Parse(doc.RootElement.Clone()));
        Assert.Throws<ToolException>(() => GameSequence.Parse(null));
    }

    [Theory]
    [InlineData(300, -40, 25)]
    [InlineData(1, 0, 10)]
    [InlineData(-7, 3, 4)]
    [InlineData(20000, -20000, 500)]
    [InlineData(5, 5, 1)]
    public void SplitDelta_SumsExactly(int dx, int dy, int steps)
    {
        var parts = GameSequence.SplitDelta(dx, dy, steps);
        Assert.True(parts.Count <= steps);
        Assert.Equal(dx, parts.Sum(p => p.Dx));
        Assert.Equal(dy, parts.Sum(p => p.Dy));
        Assert.DoesNotContain(parts, p => p.Dx == 0 && p.Dy == 0);
    }

    [Theory]
    [InlineData(300, 0, 0, 3)]
    [InlineData(50, 10, 0, 1)]
    [InlineData(300, 0, 200, 25)]
    [InlineData(3, 0, 1000, 3)]
    [InlineData(20000, 0, 10000, 500)]
    public void StepsFor_ChunksByDistanceOrTime(int dx, int dy, int ms, int expected)
    {
        Assert.Equal(expected, GameSequence.StepsFor(dx, dy, ms));
    }
}
