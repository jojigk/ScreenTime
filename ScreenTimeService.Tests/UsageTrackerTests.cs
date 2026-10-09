using Microsoft.Extensions.Logging.Abstractions;
using ScreenTime.Common;
using ScreenTimeService.Quota;
using ScreenTimeService.State;

namespace ScreenTimeService.Tests;

public class UsageTrackerTests
{
    private static readonly DateTime Start = new(2026, 7, 14, 8, 0, 0, DateTimeKind.Utc);

    private static (UsageTracker Tracker, FakeClock Clock) CreateTracker(QuotaConfig? config = null, UsageState? initialState = null)
    {
        var clock = new FakeClock { UtcNow = Start, LocalNow = Start, MonotonicTicks = 0 };
        config ??= new QuotaConfig { DailyLimitMinutes = 1, CountdownSeconds = 10, HeartbeatTimeoutSeconds = 30, ClockRollbackToleranceSeconds = 120 };
        initialState ??= new UsageState
        {
            Date = DateOnly.FromDateTime(Start),
            UsedSeconds = 0,
            LastObservedUtc = Start,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
        };

        var tracker = new UsageTracker(initialState, config, clock, NullLogger<UsageTracker>.Instance);
        return (tracker, clock);
    }

    [Fact]
    public void AccumulatesActiveSecondsAcrossTicks()
    {
        var (tracker, clock) = CreateTracker();
        tracker.ReportActivity(true);

        for (var i = 0; i < 5; i++)
        {
            clock.AdvanceBothBySeconds(1);
            tracker.Tick();
        }

        Assert.Equal(tracker.DailyLimitSeconds - 5, tracker.RemainingSeconds);
    }

    [Fact]
    public void DoesNotAccumulateWhileInactiveOrWithoutHeartbeat()
    {
        var (tracker, clock) = CreateTracker();
        // No ReportActivity call at all — never marked active.

        for (var i = 0; i < 5; i++)
        {
            clock.AdvanceBothBySeconds(1);
            tracker.Tick();
        }

        Assert.Equal(tracker.DailyLimitSeconds, tracker.RemainingSeconds);
    }

    [Fact]
    public void StopsAccumulatingAfterHeartbeatTimesOut()
    {
        var config = new QuotaConfig { DailyLimitMinutes = 5, CountdownSeconds = 10, HeartbeatTimeoutSeconds = 3, ClockRollbackToleranceSeconds = 120 };
        var (tracker, clock) = CreateTracker(config);
        tracker.ReportActivity(true);

        clock.AdvanceBothBySeconds(1);
        tracker.Tick();
        var remainingAfterFirstTick = tracker.RemainingSeconds;

        // Let the heartbeat go stale well past the timeout, with no further ReportActivity calls.
        clock.AdvanceBothBySeconds(10);
        tracker.Tick();

        Assert.Equal(remainingAfterFirstTick, tracker.RemainingSeconds);
    }

    [Fact]
    public void RollsOverAndResetsOnForwardDateChange()
    {
        var config = new QuotaConfig { DailyLimitMinutes = 1, CountdownSeconds = 10, HeartbeatTimeoutSeconds = 30, ClockRollbackToleranceSeconds = 120 };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(Start),
            UsedSeconds = 55,
            LastObservedUtc = Start,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
        };
        var (tracker, clock) = CreateTracker(config, initialState);

        // Cross midnight.
        var afterMidnight = Start.Date.AddDays(1).AddMinutes(1);
        clock.UtcNow = afterMidnight;
        clock.LocalNow = afterMidnight;
        clock.MonotonicTicks += 60_000;
        tracker.Tick();

        Assert.Equal(tracker.DailyLimitSeconds, tracker.RemainingSeconds);
    }

    [Fact]
    public void BackwardClockJumpDoesNotResetOrGrantExtraTime()
    {
        var config = new QuotaConfig { DailyLimitMinutes = 1, CountdownSeconds = 10, HeartbeatTimeoutSeconds = 30, ClockRollbackToleranceSeconds = 120 };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(Start),
            UsedSeconds = 55,
            LastObservedUtc = Start,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
        };
        var (tracker, clock) = CreateTracker(config, initialState);
        tracker.ReportActivity(true);

        // User winds the wall clock back a full day; monotonic clock still advances normally (2s).
        var wallClockJumpedBack = Start.AddDays(-1);
        clock.UtcNow = wallClockJumpedBack;
        clock.LocalNow = wallClockJumpedBack;
        clock.MonotonicTicks += 2000;
        tracker.Tick();

        // Usage was NOT reset, and the 2 monotonic seconds were still charged against the day.
        Assert.Equal(tracker.DailyLimitSeconds - 57, tracker.RemainingSeconds);
    }

    [Fact]
    public void LargeForwardJumpRollsOverExactlyOnce()
    {
        var config = new QuotaConfig { DailyLimitMinutes = 1, CountdownSeconds = 10, HeartbeatTimeoutSeconds = 30, ClockRollbackToleranceSeconds = 120 };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(Start),
            UsedSeconds = 55,
            LastObservedUtc = Start,
            MonotonicAnchorTicks = 5_000,
            ExhaustedNotified = false,
        };
        var (tracker, clock) = CreateTracker(config, initialState);

        var rolloverCount = 0;
        tracker.RemainingChanged += remaining =>
        {
            if (remaining == tracker.DailyLimitSeconds)
            {
                rolloverCount++;
            }
        };

        // Simulate a reboot two days later: monotonic counter resets to a small value.
        var twoDaysLater = Start.AddDays(2);
        clock.UtcNow = twoDaysLater;
        clock.LocalNow = twoDaysLater;
        clock.MonotonicTicks = 1_000;
        tracker.Tick();
        tracker.Tick();
        tracker.Tick();

        Assert.Equal(1, rolloverCount);
        Assert.Equal(tracker.DailyLimitSeconds, tracker.RemainingSeconds);
    }

    [Fact]
    public void QuotaExhaustedFiresExactlyOnceAtTheLimit()
    {
        var config = new QuotaConfig { DailyLimitMinutes = 0, CountdownSeconds = 10, HeartbeatTimeoutSeconds = 30, ClockRollbackToleranceSeconds = 120 };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(Start),
            UsedSeconds = 0,
            LastObservedUtc = Start,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
        };
        var (tracker, clock) = CreateTracker(config, initialState);
        // Never reports activity, so the exhausted state can't be re-triggered by a re-lock path.

        var exhaustedCount = 0;
        tracker.QuotaExhausted += () => exhaustedCount++;

        for (var i = 0; i < 5; i++)
        {
            clock.AdvanceBothBySeconds(1);
            tracker.Tick();
        }

        Assert.Equal(1, exhaustedCount);
    }

    [Fact]
    public void CountdownStartedFiresOnceWhenCrossingThreshold()
    {
        var config = new QuotaConfig { DailyLimitMinutes = 1, CountdownSeconds = 5, HeartbeatTimeoutSeconds = 30, ClockRollbackToleranceSeconds = 120 };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(Start),
            UsedSeconds = 54,
            LastObservedUtc = Start,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
        };
        var (tracker, clock) = CreateTracker(config, initialState);
        tracker.ReportActivity(true);

        var countdownEvents = new List<int>();
        tracker.CountdownStarted += remaining => countdownEvents.Add(remaining);

        for (var i = 0; i < 4; i++)
        {
            clock.AdvanceBothBySeconds(1);
            tracker.Tick();
        }

        Assert.Single(countdownEvents);
        Assert.Equal(5, countdownEvents[0]);
    }

    [Fact]
    public void ReportingActiveWhileExhaustedTriggersRelock()
    {
        var config = new QuotaConfig { DailyLimitMinutes = 0, CountdownSeconds = 10, HeartbeatTimeoutSeconds = 30, ClockRollbackToleranceSeconds = 120 };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(Start),
            UsedSeconds = 0,
            LastObservedUtc = Start,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
        };
        var (tracker, clock) = CreateTracker(config, initialState);

        var exhaustedCount = 0;
        tracker.QuotaExhausted += () => exhaustedCount++;

        // First tick: already at the (zero-minute) limit, fires once immediately.
        clock.AdvanceBothBySeconds(1);
        tracker.Tick();
        Assert.Equal(1, exhaustedCount);

        // User keeps trying to use the machine; not enough time has passed for the re-lock cooldown yet.
        tracker.ReportActivity(true);
        clock.AdvanceBothBySeconds(1);
        tracker.Tick();
        Assert.Equal(1, exhaustedCount);

        // Cooldown elapses — reporting active again should trigger another lock.
        clock.AdvanceBothBySeconds(10);
        tracker.ReportActivity(true);
        tracker.Tick();
        Assert.Equal(2, exhaustedCount);

        // Usage stays capped at the daily limit rather than resuming accumulation.
        Assert.Equal(0, tracker.RemainingSeconds);
    }

    [Fact]
    public void RelocksWhenConstructedAlreadyExhausted()
    {
        // Simulates a service restart/reboot: a fresh UsageTracker is constructed from persisted
        // state that already recorded exhaustion, with the monotonic clock back near zero
        // (Environment.TickCount64 resets on reboot). Regression test for a bug where the
        // relock-cooldown field defaulted to long.MinValue, causing nowMonotonic - long.MinValue
        // to overflow and permanently suppress relock-nagging for a tracker in this state.
        var config = new QuotaConfig { DailyLimitMinutes = 1, CountdownSeconds = 10, HeartbeatTimeoutSeconds = 30, ClockRollbackToleranceSeconds = 120 };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(Start),
            UsedSeconds = 60,
            LastObservedUtc = Start,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = true,
        };
        var clock = new FakeClock { UtcNow = Start, LocalNow = Start, MonotonicTicks = 0 };
        var tracker = new UsageTracker(initialState, config, clock, NullLogger<UsageTracker>.Instance);

        var exhaustedCount = 0;
        tracker.QuotaExhausted += () => exhaustedCount++;

        tracker.ReportActivity(true);
        clock.AdvanceBothBySeconds(1);
        tracker.Tick();

        Assert.Equal(1, exhaustedCount);
    }

    [Fact]
    public void RelocksOnASubsequentDayAfterRolloverAndAnotherRestart()
    {
        // Day 1: exhaust and lock normally within one running tracker.
        var config = new QuotaConfig { DailyLimitMinutes = 1, CountdownSeconds = 10, HeartbeatTimeoutSeconds = 30, ClockRollbackToleranceSeconds = 120 };
        var day1State = new UsageState
        {
            Date = DateOnly.FromDateTime(Start),
            UsedSeconds = 0,
            LastObservedUtc = Start,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
        };
        var (tracker, clock) = CreateTracker(config, day1State);
        for (var i = 0; i < 60; i++)
        {
            tracker.ReportActivity(true);
            clock.AdvanceBothBySeconds(1);
            tracker.Tick();
        }
        Assert.Equal(0, tracker.RemainingSeconds);

        // Cross midnight in the same running tracker — day 2's quota is fresh.
        var day2 = Start.Date.AddDays(1).AddHours(8);
        clock.UtcNow = day2;
        clock.LocalNow = day2;
        clock.MonotonicTicks += 60_000;
        tracker.Tick();
        Assert.Equal(tracker.DailyLimitSeconds, tracker.RemainingSeconds);

        // Exhaust day 2 as well, then snapshot — this is what would be persisted to disk.
        for (var i = 0; i < 60; i++)
        {
            tracker.ReportActivity(true);
            clock.AdvanceBothBySeconds(1);
            tracker.Tick();
        }
        Assert.Equal(0, tracker.RemainingSeconds);
        var day2ExhaustedState = tracker.SnapshotState();
        Assert.True(day2ExhaustedState.ExhaustedNotified);

        // Now simulate a second restart, this time on day 2: a brand-new tracker is constructed
        // from that already-exhausted day-2 state, with the monotonic clock reset near zero again
        // (another reboot). Relock-nagging must still work — the fix isn't a one-day fluke tied to
        // the first exhaustion; it's purely a function of the monotonic clock, so it must hold for
        // every subsequent day exactly the same way.
        var rebootClock = new FakeClock { UtcNow = day2, LocalNow = day2, MonotonicTicks = 0 };
        var rebootedTracker = new UsageTracker(day2ExhaustedState, config, rebootClock, NullLogger<UsageTracker>.Instance);

        var exhaustedCount = 0;
        rebootedTracker.QuotaExhausted += () => exhaustedCount++;

        rebootedTracker.ReportActivity(true);
        rebootClock.AdvanceBothBySeconds(1);
        rebootedTracker.Tick();

        Assert.Equal(1, exhaustedCount);
    }

    [Fact]
    public void DailyLimitOverrideAppliesOnTheMatchingDayOfWeek()
    {
        // Saturday: a higher weekend limit overrides the 70-minute default.
        var saturday = new DateTime(2026, 7, 18, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);

        var config = new QuotaConfig
        {
            DailyLimitMinutes = 70,
            CountdownSeconds = 10,
            HeartbeatTimeoutSeconds = 30,
            ClockRollbackToleranceSeconds = 120,
            DailyLimitOverrides = [new DailyLimitOverride { DayOfWeek = DayOfWeek.Saturday, LimitMinutes = 180 }],
        };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(saturday),
            UsedSeconds = 0,
            LastObservedUtc = saturday,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
        };
        var clock = new FakeClock { UtcNow = saturday, LocalNow = saturday, MonotonicTicks = 0 };
        var tracker = new UsageTracker(initialState, config, clock, NullLogger<UsageTracker>.Instance);

        // DailyLimitSeconds reports the day's real (override) limit...
        Assert.Equal(180 * 60, tracker.DailyLimitSeconds);
        // ...but RemainingSeconds is checkpoint-aware: the 70-minute base checkpoint comes first.
        Assert.Equal(70 * 60, tracker.RemainingSeconds);
    }

    [Fact]
    public void CheckpointLocksOnceAtTheBaseLimitThenTheRealLimitLocksWithRelockNagging()
    {
        // Thursday: 70-minute base default, overridden to 180 minutes.
        var thursday = new DateTime(2026, 7, 16, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal(DayOfWeek.Thursday, thursday.DayOfWeek);

        var config = new QuotaConfig
        {
            DailyLimitMinutes = 70,
            CountdownSeconds = 10,
            HeartbeatTimeoutSeconds = 30,
            ClockRollbackToleranceSeconds = 120,
            DailyLimitOverrides = [new DailyLimitOverride { DayOfWeek = DayOfWeek.Thursday, LimitMinutes = 180 }],
        };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(thursday),
            UsedSeconds = 0,
            LastObservedUtc = thursday,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
            CheckpointNotified = false,
        };
        var clock = new FakeClock { UtcNow = thursday, LocalNow = thursday, MonotonicTicks = 0 };
        var tracker = new UsageTracker(initialState, config, clock, NullLogger<UsageTracker>.Instance);

        var exhaustedCount = 0;
        var countdownEvents = new List<int>();
        tracker.QuotaExhausted += () => exhaustedCount++;
        tracker.CountdownStarted += remaining => countdownEvents.Add(remaining);
        tracker.ReportActivity(true);

        // Tick up to (but not past) the 70-minute checkpoint, re-reporting activity periodically
        // so the 30s heartbeat timeout never lapses.
        for (var i = 0; i < 4199; i++)
        {
            clock.AdvanceBothBySeconds(1);
            if (i % 10 == 0) tracker.ReportActivity(true);
            tracker.Tick();
        }

        Assert.Equal(0, exhaustedCount);

        // Cross the checkpoint.
        clock.AdvanceBothBySeconds(1);
        tracker.ReportActivity(true);
        tracker.Tick();

        Assert.Equal(1, exhaustedCount);
        Assert.Single(countdownEvents);
        Assert.Equal((180 - 70) * 60, tracker.RemainingSeconds);

        // User unlocks (password) and keeps using the machine, well past the 5s relock cooldown
        // that would apply to a *real* exhaustion state.
        for (var i = 0; i < 60; i++)
        {
            clock.AdvanceBothBySeconds(1);
            tracker.ReportActivity(true);
            tracker.Tick();
        }

        Assert.Equal(1, exhaustedCount);

        // Fast-forward to the real 180-minute limit (180*60 - 4200 - 60 seconds remain).
        for (var i = 0; i < 6540; i++)
        {
            clock.AdvanceBothBySeconds(1);
            if (i % 10 == 0) tracker.ReportActivity(true);
            tracker.Tick();
        }

        Assert.Equal(2, exhaustedCount);
        Assert.Equal(2, countdownEvents.Count);
        Assert.Equal(0, tracker.RemainingSeconds);

        // Relock nagging applies once the real limit is reached.
        clock.AdvanceBothBySeconds(6);
        tracker.ReportActivity(true);
        tracker.Tick();

        Assert.Equal(3, exhaustedCount);
    }

    [Fact]
    public void UsageIsNotCountedDuringAnExceptionWindow()
    {
        // Thursday 17:00-20:00, and the fake clock starts inside that window.
        var windowStart = new DateTime(2026, 7, 16, 17, 0, 0, DateTimeKind.Utc);
        Assert.Equal(DayOfWeek.Thursday, windowStart.DayOfWeek);

        var config = new QuotaConfig
        {
            DailyLimitMinutes = 1,
            CountdownSeconds = 10,
            HeartbeatTimeoutSeconds = 30,
            ClockRollbackToleranceSeconds = 120,
            ExceptionWindows = [new ScreenTimeExceptionWindow { DayOfWeek = DayOfWeek.Thursday, Start = new TimeOnly(17, 0), End = new TimeOnly(20, 0) }],
        };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(windowStart),
            UsedSeconds = 0,
            LastObservedUtc = windowStart,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
        };
        var clock = new FakeClock { UtcNow = windowStart, LocalNow = windowStart, MonotonicTicks = 0 };
        var tracker = new UsageTracker(initialState, config, clock, NullLogger<UsageTracker>.Instance);
        tracker.ReportActivity(true);

        for (var i = 0; i < 5; i++)
        {
            clock.AdvanceBothBySeconds(1);
            tracker.Tick();
        }

        Assert.Equal(tracker.DailyLimitSeconds, tracker.RemainingSeconds);
    }

    [Fact]
    public void ExhaustedQuotaDoesNotRelockDuringAnExceptionWindow()
    {
        // Thursday 17:00-20:00; quota gets exhausted a couple seconds before the window opens.
        var beforeWindow = new DateTime(2026, 7, 16, 16, 59, 58, DateTimeKind.Utc);
        Assert.Equal(DayOfWeek.Thursday, beforeWindow.DayOfWeek);

        var config = new QuotaConfig
        {
            DailyLimitMinutes = 0,
            CountdownSeconds = 10,
            HeartbeatTimeoutSeconds = 30,
            ClockRollbackToleranceSeconds = 120,
            ExceptionWindows = [new ScreenTimeExceptionWindow { DayOfWeek = DayOfWeek.Thursday, Start = new TimeOnly(17, 0), End = new TimeOnly(20, 0) }],
        };
        var initialState = new UsageState
        {
            Date = DateOnly.FromDateTime(beforeWindow),
            UsedSeconds = 0,
            LastObservedUtc = beforeWindow,
            MonotonicAnchorTicks = 0,
            ExhaustedNotified = false,
        };
        var clock = new FakeClock { UtcNow = beforeWindow, LocalNow = beforeWindow, MonotonicTicks = 0 };
        var tracker = new UsageTracker(initialState, config, clock, NullLogger<UsageTracker>.Instance);

        var exhaustedCount = 0;
        tracker.QuotaExhausted += () => exhaustedCount++;

        // Zero-minute limit: exhausted (and locked) on the very first tick, still before the window.
        clock.AdvanceBothBySeconds(1);
        tracker.Tick();
        Assert.Equal(1, exhaustedCount);

        // Clock crosses into the window; user unlocks manually (password) and stays active.
        for (var i = 0; i < 20; i++)
        {
            clock.AdvanceBothBySeconds(1);
            tracker.ReportActivity(true);
            tracker.Tick();
        }

        Assert.Equal(1, exhaustedCount);

        // Once the window ends, the still-exhausted quota resumes triggering the relock path.
        clock.UtcNow = new DateTime(2026, 7, 16, 20, 0, 1, DateTimeKind.Utc);
        clock.LocalNow = clock.UtcNow;
        clock.MonotonicTicks += 1000;
        tracker.ReportActivity(true);
        tracker.Tick();

        Assert.Equal(2, exhaustedCount);
    }
}
