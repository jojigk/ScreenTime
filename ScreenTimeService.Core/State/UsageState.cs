namespace ScreenTimeService.State;

/// <summary>
/// Persisted quota-tracking state. <see cref="MonotonicAnchorTicks"/> paired with
/// <see cref="LastObservedUtc"/> is what lets <c>UsageTracker</c> tell a legitimate reboot/date
/// change apart from someone winding the wall clock back to dodge the quota.
/// </summary>
public sealed class UsageState
{
    /// <summary>Local calendar date (yyyy-MM-dd) the quota below applies to.</summary>
    public DateOnly Date { get; set; }

    public int UsedSeconds { get; set; }

    public DateTime LastObservedUtc { get; set; }

    public long MonotonicAnchorTicks { get; set; }

    /// <summary>True once <see cref="UsedSeconds"/> has reached the day's limit, so a restart doesn't re-fire the lock event.</summary>
    public bool ExhaustedNotified { get; set; }

    /// <summary>
    /// True once <see cref="UsedSeconds"/> has reached the base daily-limit checkpoint on a day
    /// whose effective (override) limit is higher, so a restart doesn't re-fire that lock either.
    /// </summary>
    public bool CheckpointNotified { get; set; }
}
