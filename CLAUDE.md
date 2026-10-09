# ScreenTime

Windows parental screen-time tool (.NET 10): a LocalSystem service tracks each account's daily usage quota and a WPF agent in the user session shows the countdown and locks the workstation.

## Commands

- Build: `dotnet build ScreenTime.slnx`
- Test: `dotnet test ScreenTime.slnx`

## Conventions

1. **Never read the system clock directly in quota logic.** Take an `IClock` (`ScreenTimeService.Core/Time`) and measure elapsed time from `MonotonicTicks`; use `UtcNow`/`LocalNow` only for day-rollover decisions. Tests drive time with `FakeClock`.
2. **Testable logic goes in `ScreenTimeService.Core`.** `ScreenTimeService.Tests` references only `Core` and `ScreenTime.Common`, never the service exe, so keep P/Invoke and hosted-service wiring in `ScreenTimeService` and anything that needs a unit test in `Core`.
