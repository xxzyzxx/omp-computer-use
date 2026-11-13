using System.Drawing;
using PlaywrightWindows.Mcp.Core;
using Xunit;

namespace PlaywrightWindows.Mcp.Tests;

/// <summary>Pure-logic tests for snapshot pruning, formatting, budgets and diffing (no desktop).</summary>
public class SnapshotTests
{
    private static SnapNode N(string role, string? name = null, string? automationId = null,
        Rectangle? bounds = null, bool offscreen = false, string? value = null,
        SnapToggleState? toggle = null, bool togglePattern = false, bool selected = false,
        SnapExpandState? expand = null, bool expandPattern = false, bool invoke = false,
        bool valuePattern = false, bool readOnly = false, int[]? runtimeId = null,
        bool keyboardFocusable = false, params SnapNode[] children)
    {
        var node = new SnapNode
        {
            Role = role,
            Name = name,
            AutomationId = automationId,
            Bounds = bounds ?? new Rectangle(10, 10, 50, 20),
            Offscreen = offscreen,
            ValueText = value,
            ToggleStateV = toggle,
            Toggle = togglePattern,
            Selected = selected,
            ExpandState = expand,
            ExpandCollapse = expandPattern,
            Invoke = invoke,
            Value = valuePattern,
            ReadOnly = readOnly,
            KeyboardFocusable = keyboardFocusable,
            RuntimeId = runtimeId
        };
        node.Children.AddRange(children);
        return node;
    }

    private static SnapshotOutput Emit(SnapNode root, SnapshotOptions? options = null)
    {
        var counter = 0;
        return SnapshotBuilder.Emit(new[] { root }, options ?? new SnapshotOptions(), _ => $"w1e{++counter}");
    }

    private static string[] Lines(SnapshotOutput output) => output.Nodes.Select(n => n.Line).ToArray();

    // ----- pruning / flattening -----

    [Fact]
    public void UnnamedContainersAreFlattened()
    {
        var root = N("window", "Main", children:
            N("group", children: N("button", "OK", runtimeId: new[] { 42, 5, 1 })));
        var lines = Lines(Emit(root));
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("- window \"Main\"", lines[0]);
        // The unnamed group is flattened: the button sits at kept depth 1 (2-space indent).
        Assert.Equal("  - button \"OK\" [ref=w1e2] @10,10,50x20", lines[1]);
    }

    [Fact]
    public void NamedContainersAreKept()
    {
        var root = N("window", "Main", children:
            N("group", "Details", children: N("button", "OK", runtimeId: new[] { 42, 5, 1 })));
        var lines = Lines(Emit(root));
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"Details\"", lines[1]);
        Assert.StartsWith("    - button", lines[2]); // kept depth 2: window + named group
    }

    [Fact]
    public void DecorativeNodesAreDroppedButTheirChildrenSurvive()
    {
        var root = N("window", "Main", children: new[]
        {
            N("separator", children: N("image", children: N("button", "Save", runtimeId: new[] { 1 }))),
            N("scrollbar", children: N("button", "Cancel", runtimeId: new[] { 2 }))
        });
        var lines = Lines(Emit(root));
        Assert.Equal(3, lines.Length);
        Assert.Equal("  - button \"Save\" [ref=w1e2] @10,10,50x20", lines[1]);
        Assert.Equal("  - button \"Cancel\" [ref=w1e3] @10,10,50x20", lines[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlyoutCommandsRemainDiscoverableThroughLayoutOnlyAncestors(bool wrapperOffscreen)
    {
        var root = N("window", "Main", children:
            N("group", "File", children:
                N("menu", bounds: Rectangle.Empty, offscreen: wrapperOffscreen, children: new[]
                {
                    N("menuitem", "Save as", runtimeId: new[] { 9 }),
                    N("menuitem", "Hidden command", bounds: Rectangle.Empty, runtimeId: new[] { 10 })
                })));
        var text = Emit(root, new SnapshotOptions(VisibleOnly: true)).Text;
        Assert.Contains("menuitem \"Save as\"", text);
        Assert.DoesNotContain("Hidden command", text);
    }

    [Fact]
    public void OffscreenWithBoundsDropsItselfButVisitsChildren()
    {
        var root = N("window", "Main", children:
            N("group", offscreen: true, children: N("button", "Pager", runtimeId: new[] { 7 })));
        var lines = Lines(Emit(root));
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"Pager\"", lines[1]);
    }

    [Fact]
    public void NonInteractiveModeEmitsUnnamedContainers()
    {
        var root = N("window", "Main", children: N("group", children: N("text", "hello")));
        var options = new SnapshotOptions(InteractiveOnly: false, MaxNodes: 50);
        var lines = Lines(Emit(root, options));
        Assert.Equal(3, lines.Length);
        Assert.Contains("- group [ref=", lines[1]);
    }

    // ----- filter -----

    [Fact]
    public void FilterKeepsMatchesAndTheirKeptAncestors()
    {
        var root = N("window", "Main", children: new[]
        {
            N("pane", children:
                N("group", "Toolbar", children: new[]
                {
                    N("button", "OK", runtimeId: new[] { 3 }),
                    N("button", "Cancel", runtimeId: new[] { 4 })
                })),
            N("button", "Unrelated", runtimeId: new[] { 5 })
        });
        var options = new SnapshotOptions(Filter: "OK", MaxNodes: 50);
        var lines = Lines(Emit(root, options));
        // "OK" is the only match; the named ancestor "Toolbar" survives through the keep rules;
        // non-matching interactive buttons ("Cancel", "Unrelated") are cut by the filter.
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"Main\"", lines[0]);
        Assert.Contains("\"Toolbar\"", lines[1]);
        Assert.Contains("\"OK\"", lines[2]);
        Assert.DoesNotContain("Cancel", string.Join('\n', lines));
    }


    // ----- interactive / state -----

    [Fact]
    public void NamedStateTextIsKeptButNotMarkedInteractive()
    {
        var root = N("window", "Main", children: N("text", "Display is 9", runtimeId: new[] { 11 }));
        var output = Emit(root);
        Assert.Equal(2, output.Nodes.Count);
        Assert.False(SnapshotBuilder.IsInteractive(output.Nodes[1].Node));
        Assert.False(SnapshotBuilder.IsInteractive(output.Nodes[0].Node));
        Assert.Equal(0, output.InteractiveNodes);
    }

    [Fact]
    public void IsInteractiveFollowsTheControlSet()
    {
        Assert.True(SnapshotBuilder.IsInteractive(N("button")));
        Assert.True(SnapshotBuilder.IsInteractive(N("element", invoke: true)));
        Assert.True(SnapshotBuilder.IsInteractive(N("element", togglePattern: true)));
        Assert.True(SnapshotBuilder.IsInteractive(N("element", keyboardFocusable: true)));
        Assert.False(SnapshotBuilder.IsInteractive(N("group")));
        Assert.True(SnapshotBuilder.IsInteractive(N("group", valuePattern: true)));
        Assert.False(SnapshotBuilder.IsInteractive(N("group", valuePattern: true, readOnly: true)));
    }

    [Fact]
    public void LineCarriesStatesInContractOrder()
    {
        var node = new SnapNode
        {
            Role = "button",
            Name = "X",
            Enabled = false,
            HasKeyboardFocus = true,
            Bounds = new Rectangle(0, 0, 5, 5),
            Password = true
        };
        var line = SnapshotBuilder.FormatLine(node, "w1e1", 0, includeBounds: true);
        Assert.Equal("- button \"X\" [ref=w1e1] @0,0,5x5 [disabled] [focused] [password]", line);
    }

    [Fact]
    public void UncheckedAndIndeterminateAndCollapsedStates()
    {
        var off = N("checkbox", "A", togglePattern: true, toggle: SnapToggleState.Off, runtimeId: new[] { 1 });
        var ind = N("checkbox", "B", togglePattern: true, toggle: SnapToggleState.Indeterminate, runtimeId: new[] { 2 });
        var tree = N("treeitem", "C", expandPattern: true, expand: SnapExpandState.Collapsed, runtimeId: new[] { 3 });
        var lines = Lines(Emit(N("window", "W", children: new[] { off, ind, tree }))).Skip(1).ToArray();
        Assert.Contains("[unchecked]", lines[0]);
        Assert.Contains("[indeterminate]", lines[1]);
        Assert.Contains("[collapsed]", lines[2]);
        // collapsed treeitem does not repeat {exp}: implied by the role
        Assert.DoesNotContain("{exp", lines[2]);
    }

    // ----- line format -----

    [Fact]
    public void NamesAreEscaped()
    {
        var node = N("button", "a\"b\\c\nd\re", runtimeId: new[] { 1 });
        var line = SnapshotBuilder.FormatLine(node, "w1e1", 0, true);
        Assert.Contains("\"a\\\"b\\\\c\\nd\\ne\"", line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void LongNamesAreTruncatedTo120()
    {
        var node = N("button", new string('x', 200), runtimeId: new[] { 1 });
        var line = SnapshotBuilder.FormatLine(node, "w1e1", 0, true);
        Assert.Equal("- button \"" + new string('x', 120) + "\" [ref=w1e1] @10,10,50x20", line);
    }

    [Fact]
    public void AutomationIdShownWhenShortAndDifferent()
    {
        var line = SnapshotBuilder.FormatLine(N("button", "OK", automationId: "num1Button", runtimeId: new[] { 1 }), "w1e1", 0, true);
        Assert.Contains("#num1Button", line);

        var same = N("button", "OK", automationId: "OK", runtimeId: new[] { 1 });
        Assert.DoesNotContain("#OK", SnapshotBuilder.FormatLine(same, "w1e1", 0, true));

        var longId = N("button", "OK", automationId: new string('i', 40), runtimeId: new[] { 1 });
        Assert.DoesNotContain("#iiii", SnapshotBuilder.FormatLine(longId, "w1e1", 0, true));
    }

    [Fact]
    public void ValueIsShownEscapedTruncatedAndOmittedWhenEqualToName()
    {
        // Every line-break form survives: rich-edit documents separate lines with a lone CR.
        foreach (var text in new[] { "line1\nline2", "line1\rline2", "line1\r\nline2" })
        {
            var shown = N("textbox", "Search", value: text, valuePattern: true, runtimeId: new[] { 1 });
            var line = SnapshotBuilder.FormatLine(shown, "w1e1", 0, true);
            Assert.Contains("value=\"line1\\nline2\"", line);
            Assert.DoesNotContain("{val", line); // implied by the textbox role
        }

        var equal = N("textbox", "Search", value: "Search", valuePattern: true, runtimeId: new[] { 1 });
        Assert.DoesNotContain("value=", SnapshotBuilder.FormatLine(equal, "w1e1", 0, true));

        var longValue = N("textbox", "Search", value: new string('v', 100), valuePattern: true, runtimeId: new[] { 1 });
        var truncLine = SnapshotBuilder.FormatLine(longValue, "w1e1", 0, true);
        Assert.Contains("value=\"" + new string('v', 80) + "\"", truncLine);
    }

    [Fact]
    public void PatternSuffixOnlyAddsInformation()
    {
        var clickableText = N("text", "Click me", invoke: true, runtimeId: new[] { 1 });
        Assert.Contains("{inv}", SnapshotBuilder.FormatLine(clickableText, "w1e1", 0, true));

        var plainButton = N("button", "OK", invoke: true, runtimeId: new[] { 2 });
        Assert.DoesNotContain("{", SnapshotBuilder.FormatLine(plainButton, "w1e1", 0, true));
    }

    // ----- budgets -----

    [Fact]
    public void MaxNodesTruncationReportsOmittedCount()
    {
        var root = N("window", "Main", children: new[]
        {
            N("button", "A", runtimeId: new[] { 1 }),
            N("button", "B", runtimeId: new[] { 2 }),
            N("button", "C", runtimeId: new[] { 3 })
        });
        var output = Emit(root, new SnapshotOptions(MaxNodes: 2));
        Assert.Equal(2, output.Nodes.Count);
        Assert.True(output.Truncated);
        Assert.Equal(2, output.NodesOmitted); // 4 candidates, 2 emitted
    }

    [Fact]
    public void MaxCharsTruncationStopsAtBudget()
    {
        var root = N("window", "Main", children: new[]
        {
            N("button", "AAAA", runtimeId: new[] { 1 }),
            N("button", "BBBB", runtimeId: new[] { 2 }),
            N("button", "CCCC", runtimeId: new[] { 3 })
        });
        // Window line ~41 chars, each button line ~43: a budget of 90 fits exactly two lines.
        var output = Emit(root, new SnapshotOptions(MaxChars: 90));
        Assert.Equal(2, output.Nodes.Count);
        Assert.True(output.Truncated);
        Assert.Equal(2, output.NodesOmitted);
    }

    // ----- diff -----

    [Fact]
    public void DiffMarksAddedChangedRemoved()
    {
        var previous = new[]
        {
            new PrevNode("1.2.3", "- button \"Old\" [ref=w1e1]"),
            new PrevNode("1.2.4", "- button \"Same\" [ref=w1e2]"),
            new PrevNode("1.2.5", "- button \"Gone\" [ref=w1e3]")
        };
        var current = new List<Emitted>
        {
            new(N("button", "Renamed", runtimeId: new[] { 1, 2, 3 }), 0, "w1e1", "- button \"Renamed\" [ref=w1e1]"),
            new(N("button", "Same", runtimeId: new[] { 1, 2, 4 }), 0, "w1e2", "- button \"Same\" [ref=w1e2]"),
            new(N("button", "New", runtimeId: new[] { 1, 2, 6 }), 0, "w1e7", "- button \"New\" [ref=w1e7]")
        };
        var diff = SnapshotBuilder.Diff(previous, current, allowRemoved: true);
        Assert.Equal(3, diff.Lines.Count);
        Assert.Equal("~ - button \"Renamed\" [ref=w1e1]", diff.Lines[0]);
        Assert.Equal("+ - button \"New\" [ref=w1e7]", diff.Lines[1]);
        Assert.Equal("- - button \"Gone\" [ref=w1e3]", diff.Lines[2]);
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Changed);
        Assert.Equal(1, diff.Removed);
    }

    [Fact]
    public void DiffSuppressesRemovedWhenTruncated()
    {
        var previous = new[] { new PrevNode("1.1", "- button \"A\" [ref=w1e1]") };
        var diff = SnapshotBuilder.Diff(previous, new List<Emitted>(), allowRemoved: false);
        Assert.Empty(diff.Lines);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void DiffWithoutChangeProducesNoLines()
    {
        var previous = new[] { new PrevNode("1.2.3", "- button \"Same\" [ref=w1e2]") };
        var current = new List<Emitted>
        {
            new(N("button", "Same", runtimeId: new[] { 1, 2, 3 }), 0, "w1e2", "- button \"Same\" [ref=w1e2]")
        };
        var diff = SnapshotBuilder.Diff(previous, current, allowRemoved: true);
        Assert.Empty(diff.Lines);
        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Changed);
        Assert.Equal(0, diff.Removed);
    }

    // ----- matching -----

    [Fact]
    public void MatchesSupportsTokensAndFields()
    {
        var node = N("button", "Number pad 9");
        Assert.True(SnapshotBuilder.Matches(node, "pad"));
        Assert.True(SnapshotBuilder.Matches(node, "number 9")); // every token in the name
        Assert.False(SnapshotBuilder.Matches(node, "number missing"));
        Assert.True(SnapshotBuilder.Matches(node, "button"));
        Assert.True(SnapshotBuilder.Matches(N("button", automationId: "num9"), "num9"));
        Assert.True(SnapshotBuilder.Matches(node, null));
    }

    [Fact]
    public void RoleNameCoversTheContractList()
    {
        Assert.Equal("textbox", SnapshotBuilder.RoleName(FlaUI.Core.Definitions.ControlType.Edit));
        Assert.Equal("tablist", SnapshotBuilder.RoleName(FlaUI.Core.Definitions.ControlType.Tab));
        Assert.Equal("tab", SnapshotBuilder.RoleName(FlaUI.Core.Definitions.ControlType.TabItem));
        Assert.Equal("row", SnapshotBuilder.RoleName(FlaUI.Core.Definitions.ControlType.DataItem));
        Assert.Equal("element", SnapshotBuilder.RoleName(null));
    }
}
