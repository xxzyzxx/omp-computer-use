using System.Drawing;
using System.Text.Json;
using FlaUI.Core.WindowsAPI;
using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Tools;
using Xunit;

namespace PlaywrightWindows.Mcp.Tests;

public class ActionTests
{
    // ----- KeyChords -----

    [Fact]
    public void Chord_WithModifiers_ParsesInOrder()
    {
        var chord = KeyChords.ParseChord("ctrl+s");
        Assert.Equal(new[] { VirtualKeyShort.CONTROL }, chord.Modifiers);
        Assert.Equal(VirtualKeyShort.KEY_S, chord.Key);
        Assert.Equal("ctrl+s", chord.Raw);
    }

    [Fact]
    public void Chord_ControlShiftTab_ParsesBothModifiers()
    {
        var chord = KeyChords.ParseChord("control+shift+tab");
        Assert.Equal(new[] { VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT }, chord.Modifiers);
        Assert.Equal(VirtualKeyShort.TAB, chord.Key);
    }

    [Theory]
    [InlineData("enter", VirtualKeyShort.RETURN)]
    [InlineData("esc", VirtualKeyShort.ESCAPE)]
    [InlineData("bksp", VirtualKeyShort.BACK)]
    [InlineData("pgdn", VirtualKeyShort.NEXT)]
    [InlineData("pgup", VirtualKeyShort.PRIOR)]
    [InlineData("printscreen", VirtualKeyShort.SNAPSHOT)]
    [InlineData("capslock", VirtualKeyShort.CAPITAL)]
    [InlineData("apps", VirtualKeyShort.APPS)]
    [InlineData("f12", VirtualKeyShort.F12)]
    [InlineData("7", VirtualKeyShort.KEY_7)]
    [InlineData("win", VirtualKeyShort.LWIN)]
    public void Chord_NamedKeys_MapToVirtualKeys(string token, VirtualKeyShort expected)
    {
        Assert.Equal(expected, KeyChords.ParseChord(token).Key);
    }

    [Fact]
    public void Chord_AltF4_ParsesFunctionKey()
    {
        var chord = KeyChords.ParseChord("alt+f4");
        Assert.Equal(new[] { VirtualKeyShort.ALT }, chord.Modifiers);
        Assert.Equal(VirtualKeyShort.F4, chord.Key);
    }

    [Fact]
    public void Chord_UppercaseLetter_HonoursShiftState()
    {
        var chord = KeyChords.ParseChord("A");
        Assert.Equal(VirtualKeyShort.KEY_A, chord.Key);
        Assert.Contains(VirtualKeyShort.SHIFT, chord.Modifiers);
    }

    [Fact]
    public void Chord_ShiftedCharacter_MatchesLayout()
    {
        // '$' maps through VkKeyScan; the exact key depends on the layout, but shift state must be honoured.
        var chord = KeyChords.ParseChord("$");
        Assert.NotEqual((VirtualKeyShort)0, chord.Key);
        if ((Native.VkKeyScanW('$') >> 8 & 1) != 0)
        {
            Assert.Contains(VirtualKeyShort.SHIFT, chord.Modifiers);
        }
    }

    [Fact]
    public void Chord_ExplicitShiftPlusChar_DoesNotDuplicateShift()
    {
        var chord = KeyChords.ParseChord("shift+A");
        Assert.Equal(1, chord.Modifiers.Count(m => m == VirtualKeyShort.SHIFT));
        Assert.Equal(VirtualKeyShort.KEY_A, chord.Key);
    }

    [Fact]
    public void Chord_WinModifier_MapsToLeftWin()
    {
        var chord = KeyChords.ParseChord("win+r");
        Assert.Equal(new[] { VirtualKeyShort.LWIN }, chord.Modifiers);
        Assert.Equal(VirtualKeyShort.KEY_R, chord.Key);
    }

    [Theory]
    [InlineData("f25")]
    [InlineData("bogus")]
    [InlineData("ctrl+ctrl+s")]
    [InlineData("")]
    [InlineData("ctrl+")]
    public void Chord_Invalid_ThrowsUnsupportedKeyNamingToken(string raw)
    {
        var ex = Assert.Throws<ToolException>(() => KeyChords.ParseChord(raw));
        Assert.Equal("unsupported_key", ex.Code);
        if (raw.Length > 0)
        {
            Assert.Contains(raw.TrimEnd('+').Split('+')[0], ex.Message);
        }
    }

    [Fact]
    public void Sequence_Array_ParsesEachChord()
    {
        var chords = KeyChords.ParseSequence(new[] { "ctrl+s", "enter" });
        Assert.Equal(2, chords.Count);
        Assert.Equal(VirtualKeyShort.KEY_S, chords[0].Key);
        Assert.Equal(VirtualKeyShort.RETURN, chords[1].Key);
    }

    [Fact]
    public void Sequence_CommaSeparated_ParsesBoth()
    {
        var chords = KeyChords.ParseSequence(new[] { "ctrl+a,delete" });
        Assert.Equal(2, chords.Count);
        Assert.Equal(VirtualKeyShort.KEY_A, chords[0].Key);
        Assert.Equal(VirtualKeyShort.DELETE, chords[1].Key);
    }

    // ----- BatchTool step normalisation -----

    [Fact]
    public void Normalize_Shorthand_MapsActionToToolAndArgs()
    {
        var steps = BatchTool.NormalizeSteps(
            JsonSerializer.SerializeToElement(new
            {
                steps = new object[] { new { action = "click", @ref = "w1e5", count = 2 } }
            }, McpProtocol.JsonOptions));

        var (tool, args) = Assert.Single(steps);
        Assert.Equal("windows_click", tool);
        Assert.Equal("w1e5", args.GetProperty("ref").GetString());
        Assert.Equal(2, args.GetProperty("count").GetInt32());
        Assert.False(args.TryGetProperty("action", out _));
    }

    [Fact]
    public void Normalize_ExplicitTool_UsesArgsObject()
    {
        var steps = BatchTool.NormalizeSteps(
            JsonSerializer.SerializeToElement(new
            {
                steps = new object[]
                {
                    new { tool = "windows_snapshot", args = new { window = "w1" } }
                }
            }, McpProtocol.JsonOptions));

        var (tool, args) = Assert.Single(steps);
        Assert.Equal("windows_snapshot", tool);
        Assert.Equal("w1", args.GetProperty("window").GetString());
    }

    [Fact]
    public void Normalize_UnknownAction_Throws()
    {
        var ex = Assert.Throws<ToolException>(() => BatchTool.NormalizeSteps(
            JsonSerializer.SerializeToElement(new
            {
                steps = new object[] { new { action = "explode" } }
            }, McpProtocol.JsonOptions)));
        Assert.Equal("bad_request", ex.Code);
    }

    [Fact]
    public void Normalize_MissingSelector_Throws()
    {
        var ex = Assert.Throws<ToolException>(() => BatchTool.NormalizeSteps(
            JsonSerializer.SerializeToElement(new
            {
                steps = new object[] { new { @ref = "w1e5" } }
            }, McpProtocol.JsonOptions)));
        Assert.Equal("bad_request", ex.Code);
    }

    [Fact]
    public void Normalize_OverLimit_Throws()
    {
        var steps = Enumerable.Range(0, BatchTool.MaxSteps + 1)
            .Select(i => (object)new { action = "wait", ms = 1 })
            .ToArray();
        var ex = Assert.Throws<ToolException>(() => BatchTool.NormalizeSteps(
            JsonSerializer.SerializeToElement(new { steps }, McpProtocol.JsonOptions)));
        Assert.Contains("max", ex.Message);
    }

    [Fact]
    public void Normalize_EmptySteps_Throws()
    {
        var ex = Assert.Throws<ToolException>(() => BatchTool.NormalizeSteps(
            JsonSerializer.SerializeToElement(new { steps = Array.Empty<object>() }, McpProtocol.JsonOptions)));
        Assert.Equal("bad_request", ex.Code);
    }

    // ----- TargetResolver coordinate validation -----

    private static FrameStore StoreWithFrame()
    {
        var frames = new FrameStore();
        frames.Add(new ImageFrame(
            "s1", "screenshot",
            new Rectangle(100, 50, 800, 600), // SourcePhysical
            400, 300,                          // image size
            0.5,                               // image px per physical px
            1, "w1", DateTime.Now));
        return frames;
    }

    [Fact]
    public void ResolveImagePoint_MapsToPhysical()
    {
        var (physical, frame) = TargetResolver.ResolveImagePoint(StoreWithFrame(), "s1", 200, 100);
        Assert.Equal(500, physical.X); // 100 + 200/0.5
        Assert.Equal(250, physical.Y); // 50 + 100/0.5
        Assert.Equal("s1", frame.Id);
    }

    [Fact]
    public void ResolveImagePoint_RoundTripsThroughFromPhysical()
    {
        var frames = StoreWithFrame();
        var (physical, _) = TargetResolver.ResolveImagePoint(frames, "s1", 123.5, 77);
        var back = frames.Get("s1")!.FromPhysical(physical.X, physical.Y);
        Assert.InRange(back.X, 123.4, 123.6);
        Assert.InRange(back.Y, 76.9, 77.1);
    }

    [Fact]
    public void ResolveImagePoint_OutsideImage_ThrowsBadCoordinates()
    {
        var ex = Assert.Throws<ToolException>(() => TargetResolver.ResolveImagePoint(StoreWithFrame(), "s1", 400, 300));
        Assert.Equal("bad_coordinates", ex.Code);
    }

    [Fact]
    public void ResolveImagePoint_NegativeCoordinates_ThrowBadCoordinates()
    {
        var ex = Assert.Throws<ToolException>(() => TargetResolver.ResolveImagePoint(StoreWithFrame(), "s1", -1, 10));
        Assert.Equal("bad_coordinates", ex.Code);
    }

    [Fact]
    public void ResolveImagePoint_UnknownImage_ThrowsUnknownImage()
    {
        var ex = Assert.Throws<ToolException>(() => TargetResolver.ResolveImagePoint(StoreWithFrame(), "s9", 10, 10));
        Assert.Equal("unknown_image", ex.Code);
    }

    [Fact]
    public void ResolveImagePoint_NoFrames_ThrowsNoScreenshot()
    {
        var ex = Assert.Throws<ToolException>(() => TargetResolver.ResolveImagePoint(new FrameStore(), null, 10, 10));
        Assert.Equal("no_screenshot", ex.Code);
    }
}
