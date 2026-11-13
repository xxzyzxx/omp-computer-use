using System.Diagnostics;
using System.Text.RegularExpressions;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Safety;

/// <summary>
/// Decides which windows are off-limits even with a grant: configured blocked apps (exe names and title regexes),
/// the harness's own process chain, and this server itself. Also matches risky element names.
/// </summary>
public sealed class AccessPolicy
{
    private readonly ServerConfig _config;
    private readonly HashSet<string> _blockedExes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Regex> _blockedTitles = new();
    private readonly List<Regex> _risky = new();
    private readonly HashSet<uint> _harnessPids = new();

    public AccessPolicy(ServerConfig config)
    {
        _config = config;
        foreach (var entry in config.BlockedApps)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !entry.Any(c => c is '\\' or '(' or '[' or '^' or '$'))
            {
                _blockedExes.Add(entry.Trim());
            }
            else
            {
                TryAdd(_blockedTitles, entry);
            }
        }
        foreach (var pattern in config.RiskyPatterns)
        {
            TryAdd(_risky, pattern);
        }
        if (config.BlockHarnessTerminal)
        {
            DetectHarness();
        }
    }

    public IReadOnlyCollection<uint> HarnessPids => _harnessPids;

    private static void TryAdd(List<Regex> list, string pattern)
    {
        try
        {
            list.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ignoring invalid pattern '{pattern}': {ex.Message}");
        }
    }

    /// <summary>
    /// The harness is whatever launched this server: walk the parent chain (omp → shell → terminal) and stop at
    /// explorer.exe / system processes so the desktop and File Explorer stay automatable.
    /// </summary>
    private void DetectHarness()
    {
        var self = (uint)Environment.ProcessId;
        _harnessPids.Add(self);
        var pid = Native.GetParentProcessId(self);
        for (var depth = 0; depth < 6 && pid > 4; depth++)
        {
            var exe = Native.GetProcessExeName(pid);
            if (exe == null) break;
            if (exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                exe.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) ||
                exe.Equals("services.exe", StringComparison.OrdinalIgnoreCase) ||
                exe.Equals("wininit.exe", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            _harnessPids.Add(pid);
            pid = Native.GetParentProcessId(pid);
        }
    }

    /// <summary>True when the window must not be automated regardless of grants (unless blocked apps are allowed).</summary>
    public bool IsBlocked(WindowInfo window, out string reason)
    {
        reason = "";
        if (_config.IsBlockedAppsAllowed)
        {
            return false;
        }
        if (_harnessPids.Contains(window.Pid))
        {
            reason = $"{window.ProcessName ?? "pid " + window.Pid} is part of the harness that runs this agent";
            return true;
        }
        if (window.ProcessName != null && _blockedExes.Contains(window.ProcessName))
        {
            reason = $"{window.ProcessName} is on the blocked-apps list";
            return true;
        }
        if (IsConsoleOfHarness(window))
        {
            reason = "this terminal window hosts the agent harness";
            return true;
        }
        foreach (var rx in _blockedTitles)
        {
            try
            {
                if (rx.IsMatch(window.Title))
                {
                    reason = $"window title matches blocked pattern /{rx}/";
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Treat a pathological pattern as non-matching.
            }
        }
        return false;
    }

    private bool IsConsoleOfHarness(WindowInfo window)
    {
        if (_harnessPids.Count <= 1) return false;
        var isTerminalHost = window.ClassName.Equals("ConsoleWindowClass", StringComparison.Ordinal) ||
                             string.Equals(window.ProcessName, "WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(window.ProcessName, "conhost.exe", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(window.ProcessName, "OpenConsole.exe", StringComparison.OrdinalIgnoreCase);
        if (!isTerminalHost) return false;
        var title = window.Title;
        if (title.Contains("omp", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var pid in _harnessPids)
        {
            try
            {
                var name = Process.GetProcessById((int)pid).ProcessName;
                if (name.Length > 2 && title.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch
            {
                // Process gone.
            }
        }
        return false;
    }

    /// <summary>Returns the risky pattern an element name matches, or null.</summary>
    public string? MatchRisky(string? elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName)) return null;
        foreach (var rx in _risky)
        {
            try
            {
                if (rx.IsMatch(elementName)) return rx.ToString();
            }
            catch (RegexMatchTimeoutException)
            {
                // ignore
            }
        }
        return null;
    }
}
