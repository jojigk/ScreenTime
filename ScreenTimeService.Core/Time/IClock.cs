namespace ScreenTimeService.Time;

/// <summary>
/// Abstracts wall-clock and monotonic time so the quota engine can be driven deterministically
/// in tests, and so its clock-rollback defense has a source of time that user-visible clock
/// changes cannot affect.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }

    /// <summary>
    /// Wall-clock time in the machine's configured local time zone, used only to decide which
    /// calendar day usage applies to. Kept separate from <see cref="UtcNow"/> (rather than derived
    /// via <c>UtcNow.ToLocalTime()</c>) so tests can control the local calendar date directly
    /// without depending on the test runner's time zone.
    /// </summary>
    DateTime LocalNow { get; }

    /// <summary>
    /// Monotonically non-decreasing tick count in milliseconds (e.g. <see cref="Environment.TickCount64"/>).
    /// Unaffected by wall-clock changes; resets only on reboot.
    /// </summary>
    long MonotonicTicks { get; }
}
