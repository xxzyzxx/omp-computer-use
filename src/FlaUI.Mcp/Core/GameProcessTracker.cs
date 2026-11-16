using System.Diagnostics;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Remembers game processes the server has seen so it can report how they ended. A process handle is opened
/// while the game is alive, which keeps its exit code readable after the window is gone — the difference
/// between "the test finished" and "the game crashed" when a run ends.
/// </summary>
public sealed class GameProcessTracker : IDisposable
{
    private const int MaxTracked = 16;

    private sealed class Entry
    {
        public required uint Pid { get; init; }
        public required string Profile { get; init; }
        public required string? ProcessName { get; init; }
        public required DateTime FirstSeen { get; init; }
        public string Title { get; set; } = "";
        public Process? Process { get; init; }
    }

    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();

    public void Observe(WindowInfo window, GameProfile profile)
    {
        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(e => e.Pid == window.Pid);
            if (existing != null)
            {
                existing.Title = window.Title;
                return;
            }

            Process? process = null;
            try
            {
                process = Process.GetProcessById((int)window.Pid);
                _ = process.HasExited; // opens and caches the handle now, while the process is alive
            }
            catch
            {
                // Access denied (elevated game) or already gone: still track the pid, without an exit code.
                process?.Dispose();
                process = null;
            }

            _entries.Add(new Entry
            {
                Pid = window.Pid,
                Profile = profile.DisplayName,
                ProcessName = window.ProcessName,
                FirstSeen = DateTime.UtcNow,
                Title = window.Title,
                Process = process
            });
            while (_entries.Count > MaxTracked)
            {
                _entries[0].Process?.Dispose();
                _entries.RemoveAt(0);
            }
        }
    }

    /// <summary>One JSON-ready record per tracked process, newest first.</summary>
    public List<object> Report()
    {
        lock (_lock)
        {
            var report = new List<object>();
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                bool? running = null;
                int? exitCode = null;
                DateTime? exitedAt = null;
                if (e.Process != null)
                {
                    try
                    {
                        running = !e.Process.HasExited;
                        if (running == false)
                        {
                            exitCode = e.Process.ExitCode;
                            exitedAt = e.Process.ExitTime.ToUniversalTime();
                        }
                    }
                    catch
                    {
                        running = null;
                    }
                }
                report.Add(new
                {
                    pid = e.Pid,
                    profile = e.Profile,
                    process = e.ProcessName,
                    title = e.Title,
                    firstSeen = e.FirstSeen,
                    running,
                    exitCode,
                    exitedAt
                });
            }
            return report;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var e in _entries) e.Process?.Dispose();
            _entries.Clear();
        }
    }
}
