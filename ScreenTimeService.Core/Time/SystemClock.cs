namespace ScreenTimeService.Time;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public DateTime LocalNow => DateTime.Now;

    public long MonotonicTicks => Environment.TickCount64;
}
