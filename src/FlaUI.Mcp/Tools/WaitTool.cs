using System.Text.Json;
using System.Drawing;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Waits for conditions on one window (AND-combined): element appears/disappears (name substring, optionally
/// narrowed by role/automation_id), title regex, UI stability (no StructureChanged events for 300 ms and
/// &lt;0.5% pixel difference between two captures 250 ms apart) or a plain delay. Perception kind — no injection.
/// </summary>
public sealed class WaitTool : ToolBase
{
    private readonly Services _s;

    public WaitTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_wait";
    public override ToolKind Kind => ToolKind.Perception;
    public override TimeSpan? Timeout => TimeSpan.FromSeconds(60);

    public override string Description =>
        "Waits until all given conditions hold on one window (default foreground): element={query, role?, " +
        "automation_id?, state: \"appears\"(default)|\"disappears\"} — a name substring, optionally narrowed by " +
        "role (button, edit, listitem, …) and automation_id; title (regex over the window title); stable=true " +
        "(no UI Automation structure changes for 300 ms and <0.5% pixel difference 250 ms apart); ms (plain " +
        "delay). timeout_ms 100..60000 (default 5000), polling every 150 ms. Returns { satisfied, waited_ms, " +
        "conditions }; on timeout it returns status timeout — call windows_snapshot/windows_screenshot to see " +
        "the current state.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            window = new { type = "string", description = "Window handle (\"w3\"), hwnd, pid:N, title substring or process name (default foreground)" },
            timeout_ms = new { type = "integer", description = "Wait budget, 100..60000 (default 5000)" },
            element = new
            {
                type = "object",
                description = "Wait for an element: name substring query, optional role and automation_id",
                properties = new
                {
                    query = new { type = "string", description = "Name substring (case-insensitive)" },
                    role = new { type = "string", description = "UIA control type, e.g. button, edit, listitem" },
                    automation_id = new { type = "string" },
                    state = new { type = "string", @enum = new[] { "appears", "disappears" } }
                },
                required = new[] { "query" }
            },
            title = new { type = "string", description = "Regex the window title must match" },
            stable = new { type = "boolean", description = "Wait until the window is UI-stable" },
            ms = new { type = "integer", description = "Plain delay in ms" }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var window = _s.Session.ResolveWindow(GetStringArgument(arguments, "window"));
        ctx.Gate.EnsureView(window);
        ctx.TargetApp = window.ProcessName;

        var timeoutMs = ActionArgs.IntInRange(arguments, "timeout_ms", 5000, 100, 60000, "a wait budget of 100..60000 ms");
        var elementCond = GetElementCondition(arguments);
        var titlePattern = GetStringArgument(arguments, "title");
        var wantStable = GetBoolArgument(arguments, "stable");
        var delayMs = ActionArgs.IntInRange(arguments, "ms", 0, 0, 60000, "a plain delay of 0..60000 ms");

        if (elementCond == null && titlePattern == null && !wantStable && delayMs == 0)
        {
            throw new ToolException("bad_request", "Pass at least one condition: element, title, stable or ms.",
                "Combine conditions with AND, e.g. element={\"query\":\"Save\",\"role\":\"button\"} or stable=true.");
        }

        Regex? titleRegex = null;
        if (titlePattern != null)
        {
            try
            {
                titleRegex = new Regex(titlePattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                throw new ToolException("bad_request", $"title is not a valid regex: '{titlePattern}'.",
                    "Pass a plain substring or a valid .NET regex.");
            }
        }

        var windowElement = _s.Session.GetWindow(window);
        FlaUI.Core.EventHandlers.StructureChangedEventHandlerBase? handler = null;
        var lastChange = Environment.TickCount64;
        try
        {
            if (wantStable)
            {
                handler = windowElement.RegisterStructureChangedEvent(TreeScope.Subtree,
                    (_, _, _) => lastChange = Environment.TickCount64);
            }

            var started = Environment.TickCount64;
            var elementOk = false;
            var titleOk = false;
            var stableOk = false;
            var stableRatio = 1.0;
            var msOk = false;
            while (true)
            {
                ctx.ThrowIfCancelled();
                elementOk = elementCond == null || CheckElement(windowElement, elementCond);
                titleOk = titleRegex == null || titleRegex.IsMatch(Native.GetWindowText(window.Hwnd));
                msOk = delayMs == 0 || Environment.TickCount64 - started >= delayMs;

                if (wantStable && !stableOk)
                {
                    (stableOk, stableRatio) = await CheckStableAsync(window, lastChange, ctx);
                }

                if (elementOk && titleOk && (!wantStable || stableOk) && msOk)
                {
                    return JsonResult(new
                    {
                        ok = true,
                        satisfied = true,
                        waited_ms = Environment.TickCount64 - started,
                        conditions = Report(elementCond, titleRegex, wantStable, delayMs, elementOk, titleOk, stableOk, stableRatio, msOk)
                    });
                }

                if (Environment.TickCount64 - started >= timeoutMs)
                {
                    var conditions = Report(elementCond, titleRegex, wantStable, delayMs, elementOk, titleOk, stableOk, stableRatio, msOk);
                    throw new ToolException("timeout",
                        $"Condition(s) not met within {timeoutMs}ms ({conditions}).",
                        "Call windows_snapshot or windows_screenshot to see the current state, then adjust the conditions or act.")
                    {
                        IsError = false,
                        Payload = new { waited_ms = Environment.TickCount64 - started, conditions }
                    };
                }

                await Task.Delay(150, ctx.Cancel);
            }
        }
        finally
        {
            try
            {
                if (handler != null &&
                    windowElement.FrameworkAutomationElement is FlaUI.Core.AutomationElements.Infrastructure.IAutomationElementEventUnsubscriber unsubscriber)
                {
                    unsubscriber.UnregisterStructureChangedEventHandler(handler);
                }
            }
            catch
            {
                // Provider may already be gone; nothing left to clean up.
            }
        }
    }

    private async Task<(bool ok, double ratio)> CheckStableAsync(WindowInfo window, long lastChange, ToolContext ctx)
    {
        if (window.Bounds.IsEmpty)
        {
            return (false, 1.0);
        }
        var quiet = Environment.TickCount64 - lastChange > 300;
        ScreenCapture.Captured first;
        ScreenCapture.Captured second;
        try
        {
            first = ScreenCapture.Window(window);
            await Task.Delay(250, ctx.Cancel);
            second = ScreenCapture.Window(window);
        }
        catch (ToolException)
        {
            return (false, 1.0);
        }
        using var a = first.Bitmap;
        using var b = second.Bitmap;
        var (da, _) = ImageOps.FitLongEdge(a, 256);
        var (db, _) = ImageOps.FitLongEdge(b, 256);
        using var da2 = da;
        using var db2 = db;
        var ratio = ImageOps.DifferenceRatio(da2, db2);
        return (quiet && ratio < 0.005, ratio);
    }

    private static bool CheckElement(Window windowElement, ElementCondition cond)
    {
        ConditionBase? condition = null;
        if (cond.Role != null || cond.AutomationId != null)
        {
            var cf = windowElement.Automation.ConditionFactory;
            if (cond.Role != null && cond.AutomationId != null)
            {
                condition = new AndCondition(cf.ByControlType(cond.Role.Value), cf.ByAutomationId(cond.AutomationId));
            }
            else
            {
                condition = cond.Role != null ? cf.ByControlType(cond.Role.Value) : cf.ByAutomationId(cond.AutomationId!);
            }
        }

        AutomationElement[] candidates;
        try
        {
            candidates = condition == null ? windowElement.FindAllDescendants() : windowElement.FindAllDescendants(condition);
        }
        catch
        {
            return false;
        }

        var wantPresent = cond.State != "disappears";
        foreach (var candidate in candidates)
        {
            try
            {
                if ((candidate.Properties.Name.ValueOrDefault ?? "")
                    .Contains(cond.Query, StringComparison.OrdinalIgnoreCase))
                {
                    return wantPresent;
                }
            }
            catch
            {
                // Element vanished mid-scan; keep looking.
            }
        }
        return !wantPresent;
    }

    private static object Report(ElementCondition? elementCond, Regex? titleRegex, bool wantStable, int delayMs,
        bool elementOk, bool titleOk, bool stableOk, double stableRatio, bool msOk)
    {
        var report = new Dictionary<string, object?>();
        if (elementCond != null)
        {
            report["element"] = elementOk
                ? (elementCond.State == "disappears" ? "absent" : "found")
                : (elementCond.State == "disappears" ? "still_present" : "not_found");
        }
        if (titleRegex != null)
        {
            report["title"] = titleOk;
        }
        if (wantStable)
        {
            report["stable"] = stableOk ? true : Math.Round(stableRatio, 4);
        }
        if (delayMs > 0)
        {
            report["ms"] = msOk;
        }
        return report;
    }

    private sealed record ElementCondition(string Query, ControlType? Role, string? AutomationId, string State);

    private static ElementCondition? GetElementCondition(JsonElement? arguments)
    {
        var obj = GetArgument<JsonElement?>(arguments, "element");
        if (obj is not { ValueKind: JsonValueKind.Object } o)
        {
            return null;
        }

        string? query = null;
        if (o.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
        {
            query = q.GetString();
        }
        if (string.IsNullOrEmpty(query))
        {
            throw new ToolException("bad_request", "element.query is required.",
                "Pass the name substring to wait for, e.g. element={\"query\":\"Save\",\"role\":\"button\"}.");
        }

        ControlType? role = null;
        if (o.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String)
        {
            var roleText = r.GetString() ?? "";
            if (!Enum.TryParse(roleText.Replace(" ", ""), true, out ControlType parsed) || !Enum.IsDefined(parsed))
            {
                throw new ToolException("bad_request", $"Unknown role '{roleText}'.",
                    "Use a UIA control type like button, checkbox, combobox, edit, hyperlink, list, listitem, menu, pane, tab, text, tree, treeitem, window.");
            }
            role = parsed;
        }

        string? automationId = null;
        if (o.TryGetProperty("automation_id", out var aid) && aid.ValueKind == JsonValueKind.String)
        {
            automationId = aid.GetString();
        }

        var state = "appears";
        if (o.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
        {
            state = st.GetString() ?? "appears";
            if (state is not ("appears" or "disappears"))
            {
                throw new ToolException("bad_request", $"element.state must be appears or disappears (got '{state}').",
                    "Use state \"appears\" (default) or \"disappears\".");
            }
        }

        return new ElementCondition(query!, role, automationId, state);
    }
}
