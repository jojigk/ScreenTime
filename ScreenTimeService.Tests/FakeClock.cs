using ScreenTimeService.Time;

namespace ScreenTimeService.Tests;

/// <summary>Fully controllable clock so tests can exercise rollover/rollback scenarios without real delays.</summary>
public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; }

    public DateTime LocalNow { get; set; }

    public long MonotonicTicks { get; set; }

    /// <summary>Advances UTC, local, and monotonic time together by the same amount — the common case.</summary>
    public void AdvanceBothBySeconds(int seconds)
    {
        UtcNow = UtcNow.AddSeconds(seconds);
        LocalNow = LocalNow.AddSeconds(seconds);
        MonotonicTicks += seconds * 1000L;
    }
}
