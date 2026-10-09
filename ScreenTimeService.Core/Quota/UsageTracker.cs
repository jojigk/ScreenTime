using Microsoft.Extensions.Logging;
using ScreenTime.Common;
using ScreenTimeService.State;
using ScreenTimeService.Time;

namespace ScreenTimeService.Quota;

/// <summary>
/// The quota state machine. Elapsed active time is measured from the monotonic clock so wall-clock
/// changes cannot manufacture extra time; the wall clock is only consulted to decide whether the
/// calendar day has rolled over, and a backward jump there is treated as tampering, not a reset.
/// </summary>
public sealed class UsageTracker
{
    private const long RelockCooldownMs = 5000;

    private readonly QuotaConfig _config;
    private readonly IClock _clock;
    private readonly ILogger<UsageTracker> _logger;
    private readonly object _lock = new();

    private readonly UsageState _state;
    private bool _lastReportedActive;
    private DateTime _lastHeartbeatUtc;
    private bool _countdownStarted;
    private int _lastRemainingRaised = -1;
    private long _carryMillis;
    private long _lastLockTriggerMonotonic;

    public UsageTracker(UsageState initialState, QuotaConfig config, IClock clock, ILogger<UsageTracker> logger)
    {
        _state = initialState;
        _config = config;
        _clock = clock;
        _logger = logger;
        _lastHeartbeatUtc = clock.UtcNow;
        // Seeded relative to the clock (not long.MinValue) so the relock-cooldown subtraction in
        // Tick() can't overflow. MonotonicTicks resets on every reboot, so a tracker constructed
        // from already-exhausted persisted state (the post-restart case) would otherwise compare
        // a near-zero nowMonotonic against a stale pre-reboot trigger time and wrap around to a
        // huge negative delta — permanently disabling relock-nagging for this tracker's lifetime.
        _lastLockTriggerMonotonic = clock.MonotonicTicks - RelockCooldownMs;
        _countdownStarted = RemainingSeconds > 0 && RemainingSeconds <= config.CountdownSeconds;
    }

    /// <summary>Today's quota — the day-of-week override for <see cref="_state"/>'s date if one is configured, otherwise the default.</summary>
    public int DailyLimitSeconds
    {
        get { lock (_lock) { return DailyLimitSecondsUnlocked; } }
    }

    /// <summary>
    /// Seconds until the next unfired lock threshold — the base-limit checkpoint if today has a
    /// higher override and it hasn't fired yet, otherwise the day's real limit.
    /// </summary>
    public int RemainingSeconds
    {
        get { lock (_lock) { return Math.Max(0, ResolveCurrentThreshold().ThresholdSeconds - _state.UsedSeconds); } }
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private int DailyLimitSecondsUnlocked => DailyLimitMinutesFor(_state.Date.DayOfWeek) * 60;

    /// <summary>
    /// The checkpoint lets a day with a higher override still pause for a real (password-gated)
    /// unlock at the ordinary daily limit before continuing on to the override amount. Caller must
    /// hold <see cref="_lock"/>.
    /// </summary>
    private (int ThresholdSeconds, bool IsCheckpoint) ResolveCurrentThreshold()
    {
        var hardLimitSeconds = DailyLimitSecondsUnlocked;
        var checkpointSeconds = _config.DailyLimitMinutes * 60;

        if (checkpointSeconds < hardLimitSeconds && !_state.CheckpointNotified)
        {
            return (checkpointSeconds, true);
        }

        return (hardLimitSeconds, false);
    }

    private int DailyLimitMinutesFor(DayOfWeek dayOfWeek)
    {
        foreach (var entry in _config.DailyLimitOverrides)
        {
            if (entry.DayOfWeek == dayOfWeek)
            {
                return entry.LimitMinutes;
            }
        }

        return _config.DailyLimitMinutes;
    }

    /// <summary>Fires whenever the whole-second remaining-time value changes.</summary>
    public event Action<int>? RemainingChanged;

    /// <summary>Fires once when remaining time first drops to or below the countdown threshold.</summary>
    public event Action<int>? CountdownStarted;

    /// <summary>
    /// Fires when the base-limit checkpoint is reached on an override day (once, no repeats —
    /// unlocking should let usage continue uninterrupted toward the real limit), when the day's
    /// real quota is first exhausted, and again (rate-limited) if the user keeps trying to use the
    /// machine after that — every firing should trigger a session lock.
    /// </summary>
    public event Action? QuotaExhausted;

    public UsageState SnapshotState()
    {
        lock (_lock)
        {
            return new UsageState
            {
                Date = _state.Date,
                UsedSeconds = _state.UsedSeconds,
                LastObservedUtc = _state.LastObservedUtc,
                MonotonicAnchorTicks = _state.MonotonicAnchorTicks,
                ExhaustedNotified = _state.ExhaustedNotified,
                CheckpointNotified = _state.CheckpointNotified,
            };
        }
    }

    public void ReportActivity(bool isActive)
    {
        lock (_lock)
        {
            _lastReportedActive = isActive;
            _lastHeartbeatUtc = _clock.UtcNow;
        }
    }

    public void Tick()
    {
        int remaining;
        bool exhaustedTransition;
        bool relockDue;
        int? countdownToRaise;

        lock (_lock)
        {
            var nowUtc = _clock.UtcNow;
            var nowLocal = _clock.LocalNow;
            var nowMonotonic = _clock.MonotonicTicks;

            ApplyRolloverOrRollbackDefense(nowUtc, nowLocal);

            var active = _lastReportedActive && (nowUtc - _lastHeartbeatUtc).TotalSeconds <= _config.HeartbeatTimeoutSeconds;

            // A negative delta means the monotonic counter reset underneath us (reboot) since the
            // last anchor; treat the gap as zero elapsed active time rather than underflowing.
            var elapsedMs = nowMonotonic >= _state.MonotonicAnchorTicks ? nowMonotonic - _state.MonotonicAnchorTicks : 0;
            _state.MonotonicAnchorTicks = nowMonotonic;
            _state.LastObservedUtc = nowUtc;

            var totalMs = elapsedMs + _carryMillis;
            var wholeSeconds = totalMs / 1000;
            _carryMillis = totalMs % 1000;

            var inExceptionWindow = IsInExceptionWindow(nowLocal);
            var hardLimitSeconds = DailyLimitSecondsUnlocked;

            if (active && wholeSeconds > 0 && !inExceptionWindow)
            {
                _state.UsedSeconds = Math.Min(hardLimitSeconds, _state.UsedSeconds + (int)wholeSeconds);
            }

            var (currentThreshold, checkpointActive) = ResolveCurrentThreshold();
            remaining = Math.Max(0, currentThreshold - _state.UsedSeconds);

            countdownToRaise = null;
            if (!inExceptionWindow && !_countdownStarted && remaining > 0 && remaining <= _config.CountdownSeconds)
            {
                _countdownStarted = true;
                countdownToRaise = remaining;
            }

            // A window doesn't retroactively unlock an already-locked session (Windows offers no
            // programmatic unlock), it only stops the exhausted state from triggering a *new* lock.
            exhaustedTransition = false;
            relockDue = false;
            if (!inExceptionWindow && remaining <= 0)
            {
                if (checkpointActive)
                {
                    // One-time soft lock at the base limit; no relock nagging afterward — once
                    // unlocked, usage keeps accruing uninterrupted toward the day's real limit.
                    _state.CheckpointNotified = true;
                    _countdownStarted = false;
                    exhaustedTransition = true;
                }
                else if (!_state.ExhaustedNotified)
                {
                    _state.ExhaustedNotified = true;
                    _lastLockTriggerMonotonic = nowMonotonic;
                    exhaustedTransition = true;
                }
                else if (active && nowMonotonic - _lastLockTriggerMonotonic >= RelockCooldownMs)
                {
                    _lastLockTriggerMonotonic = nowMonotonic;
                    relockDue = true;
                }
            }
        }

        if (remaining != _lastRemainingRaised)
        {
            _lastRemainingRaised = remaining;
            RemainingChanged?.Invoke(remaining);
        }

        if (countdownToRaise is { } seconds)
        {
            CountdownStarted?.Invoke(seconds);
        }

        if (exhaustedTransition || relockDue)
        {
            QuotaExhausted?.Invoke();
        }
    }

    private bool IsInExceptionWindow(DateTime nowLocal)
    {
        var dayOfWeek = nowLocal.DayOfWeek;
        var timeOfDay = TimeOnly.FromDateTime(nowLocal);

        foreach (var window in _config.ExceptionWindows)
        {
            if (window.DayOfWeek == dayOfWeek && timeOfDay >= window.Start && timeOfDay < window.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void ApplyRolloverOrRollbackDefense(DateTime nowUtc, DateTime nowLocal)
    {
        var today = DateOnly.FromDateTime(nowLocal);
        var backwardJump = nowUtc < _state.LastObservedUtc.AddSeconds(-_config.ClockRollbackToleranceSeconds);

        if (backwardJump)
        {
            _logger.LogWarning(
                "System clock moved backward from {Previous:o} to {Now:o}; ignoring for quota rollover purposes.",
                _state.LastObservedUtc, nowUtc);
            return;
        }

        if (today > _state.Date)
        {
            _logger.LogInformation("Quota day rolled over from {Previous} to {Today}; resetting usage.", _state.Date, today);
            _state.Date = today;
            _state.UsedSeconds = 0;
            _state.ExhaustedNotified = false;
            _state.CheckpointNotified = false;
            _countdownStarted = false;
        }

        // today < _state.Date without tripping the backward-jump threshold: a boundary-case
        // blip, not a real rollback attempt. Leave the stored date exactly as-is either way.
    }
}
