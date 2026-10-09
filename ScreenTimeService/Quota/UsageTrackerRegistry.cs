using System.Collections.Concurrent;
using ScreenTime.Common;
using ScreenTimeService.Locking;
using ScreenTimeService.State;
using ScreenTimeService.Time;

namespace ScreenTimeService.Quota;

/// <summary>
/// Owns one <see cref="UsageTracker"/> per Windows account (keyed by SID), replacing the old
/// single machine-wide tracker. Also owns everything that only needs to happen once per tracker
/// for its whole lifetime — persistence and the exhaustion-triggered lock — so both survive an
/// agent disconnecting/reconnecting or dying outright, the same guarantee the old single-tracker
/// setup had.
/// </summary>
public sealed class UsageTrackerRegistry
{
    private readonly UsageStateStore _stateStore;
    private readonly QuotaConfig _config;
    private readonly IClock _clock;
    private readonly SessionLockService _lockService;
    private readonly ILogger<UsageTracker> _trackerLogger;
    private readonly ILogger<UsageTrackerRegistry> _logger;

    private readonly ConcurrentDictionary<string, UsageTracker> _trackers = new();
    private readonly ConcurrentDictionary<string, int> _lastKnownSessionId = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastPersistUtc = new();
    private readonly ConcurrentDictionary<string, int> _lastKnownRemaining = new();

    public UsageTrackerRegistry(
        UsageStateStore stateStore,
        QuotaConfig config,
        IClock clock,
        SessionLockService lockService,
        ILogger<UsageTracker> trackerLogger,
        ILogger<UsageTrackerRegistry> logger)
    {
        _stateStore = stateStore;
        _config = config;
        _clock = clock;
        _lockService = lockService;
        _trackerLogger = trackerLogger;
        _logger = logger;
    }

    /// <summary>Records which session a SID is currently connected from, for the exhaustion-triggered lock.</summary>
    public void NoteSession(string sid, int sessionId) => _lastKnownSessionId[sid] = sessionId;

    public UsageTracker GetOrCreate(string sid) => _trackers.GetOrAdd(sid, CreateTracker);

    public IReadOnlyCollection<KeyValuePair<string, UsageTracker>> Snapshot() => _trackers.ToArray();

    /// <summary>Persists a SID's state if the configured persist interval has elapsed.</summary>
    public void MaybePersist(string sid)
    {
        var lastPersist = _lastPersistUtc.GetValueOrDefault(sid, DateTime.MinValue);
        if ((DateTime.UtcNow - lastPersist).TotalSeconds >= _config.PersistIntervalSeconds)
        {
            Persist(sid);
        }
    }

    public void PersistAll()
    {
        foreach (var sid in _trackers.Keys)
        {
            Persist(sid);
        }
    }

    private void Persist(string sid)
    {
        if (!_trackers.TryGetValue(sid, out var tracker))
        {
            return;
        }

        try
        {
            _stateStore.Save(tracker.SnapshotState(), sid);
            _lastPersistUtc[sid] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist usage state for {Sid}.", sid);
        }
    }

    private UsageTracker CreateTracker(string sid)
    {
        var state = _stateStore.LoadOrCreate(_clock, sid);
        var tracker = new UsageTracker(state, _config, _clock, _trackerLogger);

        tracker.RemainingChanged += remaining => OnRemainingChanged(sid, remaining);
        tracker.QuotaExhausted += () => OnQuotaExhausted(sid);

        return tracker;
    }

    private void OnRemainingChanged(string sid, int remaining)
    {
        // Remaining time only ever decreases while active; an increase means the day rolled
        // over, which is worth capturing immediately rather than waiting out the persist interval.
        var last = _lastKnownRemaining.GetValueOrDefault(sid, -1);
        if (last >= 0 && remaining > last)
        {
            Persist(sid);
        }

        _lastKnownRemaining[sid] = remaining;
    }

    private void OnQuotaExhausted(string sid)
    {
        Persist(sid);

        if (_config.LockEnabled && _lastKnownSessionId.TryGetValue(sid, out var sessionId))
        {
            _ = _lockService.LockSessionAsync(sessionId);
        }
    }
}
