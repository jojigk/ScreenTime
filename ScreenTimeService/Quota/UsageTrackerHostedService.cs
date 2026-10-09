using ScreenTime.Common;

namespace ScreenTimeService.Quota;

/// <summary>
/// Drives every account's <see cref="UsageTracker"/> (held by <see cref="UsageTrackerRegistry"/>)
/// on a fixed interval. Persistence and exhaustion-triggered locking live on the registry itself
/// (they need to survive per-tracker, not per-tick) — this class is just the timer loop.
/// </summary>
public sealed class UsageTrackerHostedService(
    UsageTrackerRegistry registry,
    QuotaConfig config,
    ILogger<UsageTrackerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, config.TickIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                TickAll();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            registry.PersistAll();
        }
    }

    private void TickAll()
    {
        foreach (var (sid, tracker) in registry.Snapshot())
        {
            try
            {
                tracker.Tick();
                registry.MaybePersist(sid);
            }
            catch (Exception ex)
            {
                // One account's tracker faulting must not stop ticking for every other account.
                logger.LogError(ex, "Tick failed for {Sid}.", sid);
            }
        }
    }
}
