namespace ScreenTime.Common;

/// <summary>
/// A recurring weekly window (local time) during which active time is not counted against the
/// daily quota and an already-exhausted quota will not trigger a new lock. Same-day only —
/// <see cref="End"/> must be later than <see cref="Start"/>; windows spanning midnight aren't supported.
/// </summary>
public sealed class ScreenTimeExceptionWindow
{
    public DayOfWeek DayOfWeek { get; set; }

    public TimeOnly Start { get; set; }

    public TimeOnly End { get; set; }
}
