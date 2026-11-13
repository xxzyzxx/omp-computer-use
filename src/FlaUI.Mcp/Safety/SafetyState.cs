using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Safety;

public enum AccessTier
{
    None = 0,
    View = 1,
    Control = 2
}

public sealed class Grant
{
    [JsonPropertyName("app")] public string App { get; set; } = "";
    [JsonPropertyName("tier")] public AccessTier Tier { get; set; }
    [JsonPropertyName("persistent")] public bool Persistent { get; set; }
    [JsonPropertyName("grantedAt")] public DateTime GrantedAt { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "session";

    public object ToJson() => new { app = App, tier = Tier.ToString().ToLowerInvariant(), persistent = Persistent, grantedAt = GrantedAt, reason = Reason, source = Source };
}

/// <summary>
/// Mutable safety state shared by every tool and by the kill switch / input monitor threads:
/// grants, the active-control lock, the abort token and the last-user-input clock.
/// </summary>
public sealed class SafetyState
{
    private static readonly JsonSerializerOptions GrantFileOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly ServerConfig _config;
    private readonly ConcurrentDictionary<string, Grant> _sessionGrants = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Grant> _persistentGrants = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _abortLock = new();
    private CancellationTokenSource _abortCts = new();
    private long _lastUserInputTicks = long.MinValue;
    private long _resumeAfterTicks = long.MinValue;
    private long _abortedAtTicks;

    public SafetyState(ServerConfig config)
    {
        _config = config;
        LoadPersistentGrants();
    }

    public ServerConfig Config => _config;

    // ----- grants -----

    public static string NormalizeApp(string app)
    {
        app = app.Trim();
        var name = Path.GetFileName(app);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return name.ToLowerInvariant();
    }

    public AccessTier TierFor(string? processName)
    {
        if (_config.Yolo) return AccessTier.Control;
        if (string.IsNullOrEmpty(processName)) return AccessTier.None;
        var key = NormalizeApp(processName);
        var tier = AccessTier.None;
        if (_config.Allowlist.TryGetValue(key, out var cfgTier) && Enum.TryParse<AccessTier>(cfgTier, true, out var t1)) tier = Max(tier, t1);
        if (_persistentGrants.TryGetValue(key, out var pg)) tier = Max(tier, pg.Tier);
        if (_sessionGrants.TryGetValue(key, out var sg)) tier = Max(tier, sg.Tier);
        return tier;
    }

    private static AccessTier Max(AccessTier a, AccessTier b) => a >= b ? a : b;

    public void AddGrant(string app, AccessTier tier, bool persistent, string? reason, string source)
    {
        var grant = new Grant { App = NormalizeApp(app), Tier = tier, Persistent = persistent, GrantedAt = DateTime.UtcNow, Reason = reason, Source = source };
        if (persistent)
        {
            _persistentGrants[grant.App] = grant;
            SavePersistentGrants();
        }
        else
        {
            _sessionGrants[grant.App] = grant;
        }
    }

    /// <summary>Kill switch: every control grant (session and persistent) drops to view.</summary>
    public int RevokeControlGrants()
    {
        var count = 0;
        foreach (var dict in new[] { _sessionGrants, _persistentGrants })
        {
            foreach (var kv in dict)
            {
                if (kv.Value.Tier == AccessTier.Control)
                {
                    kv.Value.Tier = AccessTier.View;
                    count++;
                }
            }
        }
        if (count > 0) SavePersistentGrants();
        return count;
    }

    public bool RemoveGrant(string app)
    {
        var key = NormalizeApp(app);
        var removed = _sessionGrants.TryRemove(key, out _);
        if (_persistentGrants.TryRemove(key, out _))
        {
            removed = true;
            SavePersistentGrants();
        }
        return removed;
    }

    public IEnumerable<Grant> Grants
    {
        get
        {
            foreach (var kv in _config.Allowlist)
            {
                if (Enum.TryParse<AccessTier>(kv.Value, true, out var t))
                {
                    yield return new Grant { App = NormalizeApp(kv.Key), Tier = t, Persistent = true, Source = "config.allowlist" };
                }
            }
            foreach (var g in _persistentGrants.Values) yield return g;
            foreach (var g in _sessionGrants.Values) yield return g;
        }
    }

    private void LoadPersistentGrants()
    {
        try
        {
            if (!File.Exists(_config.GrantsPath)) return;
            var grants = JsonSerializer.Deserialize<List<Grant>>(File.ReadAllText(_config.GrantsPath), GrantFileOptions) ?? new();
            foreach (var g in grants)
            {
                g.Persistent = true;
                g.Source = "persisted";
                _persistentGrants[NormalizeApp(g.App)] = g;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"grants file unreadable: {ex.Message}");
        }
    }

    private void SavePersistentGrants()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_config.GrantsPath)!);
            File.WriteAllText(_config.GrantsPath, JsonSerializer.Serialize(_persistentGrants.Values.ToList(), GrantFileOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"grants file not written: {ex.Message}");
        }
    }

    // ----- control lock -----

    public bool ControlActive { get; private set; }
    public DateTime? ControlSince { get; private set; }
    public string? ControlApp { get; private set; }

    /// <summary>Raised (on the caller's thread) when the control lock is acquired or released.</summary>
    public event Action<bool, string?>? ControlChanged;

    public void AcquireControl(string? app)
    {
        var changed = !ControlActive || !string.Equals(ControlApp, app, StringComparison.OrdinalIgnoreCase);
        if (!ControlActive)
        {
            ControlSince = DateTime.UtcNow;
        }
        ControlActive = true;
        ControlApp = app;
        if (changed) ControlChanged?.Invoke(true, app);
    }

    public void ReleaseControl()
    {
        if (!ControlActive) return;
        ControlActive = false;
        ControlSince = null;
        ControlApp = null;
        ControlChanged?.Invoke(false, null);
    }

    // ----- abort (kill switch) -----

    public CancellationToken AbortToken
    {
        get
        {
            lock (_abortLock) return _abortCts.Token;
        }
    }

    public bool IsAborted
    {
        get
        {
            lock (_abortLock) return _abortCts.IsCancellationRequested;
        }
    }

    public string? AbortReason { get; private set; }
    public DateTime? AbortedAt { get; private set; }
    public int AbortCount { get; private set; }

    public void TriggerAbort(string reason)
    {
        lock (_abortLock)
        {
            AbortReason = reason;
            AbortedAt = DateTime.UtcNow;
            _abortedAtTicks = Environment.TickCount64;
            AbortCount++;
            _abortCts.Cancel();
        }
    }

    /// <summary>
    /// Called before each top-level tool call: an abort older than <paramref name="graceMs"/> has done its job
    /// (the running batch died, control grants were revoked) and must not poison the next user-initiated turn.
    /// </summary>
    public void ClearAbortIfStale(int graceMs = 1500)
    {
        lock (_abortLock)
        {
            if (!_abortCts.IsCancellationRequested) return;
            if (Environment.TickCount64 - _abortedAtTicks < graceMs) return;
            _abortCts.Dispose();
            _abortCts = new CancellationTokenSource();
        }
    }

    // ----- user activity -----

    public string? LastUserInputKind { get; private set; }

    public void NoteUserInput(string kind)
    {
        Interlocked.Exchange(ref _lastUserInputTicks, Environment.TickCount64);
        LastUserInputKind = kind;
    }

    public TimeSpan? SinceUserInput
    {
        get
        {
            var t = Interlocked.Read(ref _lastUserInputTicks);
            return t == long.MinValue ? null : TimeSpan.FromMilliseconds(Environment.TickCount64 - t);
        }
    }

    /// <summary>True while real user input happened less than idleResumeSeconds ago (and no explicit resume since).</summary>
    public bool IsUserActive
    {
        get
        {
            if (_config.Yolo) return false;
            var last = Interlocked.Read(ref _lastUserInputTicks);
            if (last == long.MinValue) return false;
            if (last <= Interlocked.Read(ref _resumeAfterTicks)) return false;
            return Environment.TickCount64 - last < _config.IdleResumeSeconds * 1000;
        }
    }

    /// <summary>windows_resume: the model (on the human's say-so) overrides the idle wait for input seen so far.</summary>
    public void ExplicitResume()
    {
        Interlocked.Exchange(ref _resumeAfterTicks, Environment.TickCount64);
    }

    public object ToJson() => new
    {
        yolo = _config.Yolo,
        controlActive = ControlActive,
        controlApp = ControlApp,
        controlSince = ControlSince,
        userActive = IsUserActive,
        sinceUserInputMs = SinceUserInput?.TotalMilliseconds,
        lastUserInputKind = LastUserInputKind,
        aborted = IsAborted,
        abortReason = AbortReason,
        abortCount = AbortCount,
        grants = Grants.Select(g => g.ToJson()).ToList()
    };
}
