# Countdown duration, non-blocking overlay, and reboot-lock race fix

## Context

Three gaps were raised against the current ScreenTime behavior:

1. The countdown warning currently starts 60 seconds before lock — the owner wants more
   lead time, 5 minutes.
2. The countdown overlay (`CountdownOverlayWindow`) is currently a fullscreen, ~95%-opaque,
   focus-stealing window that fights the user for foreground and blocks Alt+F4 — effectively
   preventing work during the whole countdown. It should become a purely advisory "FYI"
   indicator; only the actual timeout should stop the user from working.
3. The owner wants confidence that locking still works correctly across a system restart.
   Investigation confirmed the core mechanism is sound (per-account state persists to disk;
   the service re-checks and re-locks as soon as the agent reconnects) — but found a real gap:
   after reboot, the agent doesn't launch until the next supervisor poll (default every 15s),
   so an already-exhausted account can be used, fully unlocked, for that whole window after
   login. This should be closed, not just documented.

Design decisions already confirmed with the owner: change only the `CountdownSeconds` code
default (no doc changes); replace the fullscreen overlay with a small topmost corner banner
that never steals focus or blocks input outside its own bounds; fix the reboot race with an
event-driven immediate check on session logon, kept alongside the existing poll as a fallback.

## Change 1 — Countdown default: 60s → 300s

**File:** [ScreenTime.Common/QuotaConfig.cs](ScreenTime.Common/QuotaConfig.cs:19)

Change `public int CountdownSeconds { get; set; } = 60;` to `= 300;`. That's the whole change —
`UsageTracker.Tick()`, the pipe's `ShowCountdownMessage`, and the reconnect catch-up logic in
`PipeServerHostedService` all already read `config.CountdownSeconds` dynamically; nothing else
hardcodes 60. `QuotaConfigStore.LoadOrCreate()` only applies the new default to fresh/corrupt
`config.json` files — existing installs keep whatever value is already on disk.

No test changes needed: every `QuotaConfig` in `ScreenTimeService.Tests/UsageTrackerTests.cs`
sets `CountdownSeconds` explicitly rather than relying on the default.

## Change 2 — Countdown overlay becomes a non-blocking corner banner

**Files:**
[ScreenTimeAgent/Overlay/CountdownOverlayWindow.xaml](ScreenTimeAgent/Overlay/CountdownOverlayWindow.xaml)
and [CountdownOverlayWindow.xaml.cs](ScreenTimeAgent/Overlay/CountdownOverlayWindow.xaml.cs)

**XAML:**
- Drop the fullscreen near-opaque `Background="#F2000000"` for a small, still-legible banner
  fill (e.g. `#E6202020`).
- Keep `WindowStyle="None"`, `ResizeMode="NoResize"`, `Topmost="True"`, `ShowInTaskbar="False"`,
  `WindowStartupLocation="Manual"` — all still correct for a topmost corner indicator.
- Add `ShowActivated="False"` so showing/updating the banner never steals foreground/keyboard
  focus in the first place.
- Shrink the text sizes for a small banner instead of a fullscreen layout (e.g. title ~14px,
  countdown digits ~28px, subtitle ~12px) and reduce the oversized margins.
- Keep the existing `x:Name`s (`TitleText`, `CountdownText`, `SubtitleText`) so the code-behind
  doesn't need to change beyond what's below.

**Code-behind:**
- Replace the fullscreen geometry calc in the constructor with a fixed small size anchored to
  the bottom-right of `SystemParameters.WorkArea` (not `PrimaryScreenWidth/Height`, so it
  respects the taskbar):
  ```csharp
  private const double BannerWidth = 300;
  private const double BannerHeight = 110;
  private const double CornerMargin = 16;

  Width = BannerWidth;
  Height = BannerHeight;
  Left = SystemParameters.WorkArea.Right - BannerWidth - CornerMargin;
  Top = SystemParameters.WorkArea.Bottom - BannerHeight - CornerMargin;
  ```
  Use a fixed size rather than `SizeToContent` — otherwise the window visibly resizes/drifts
  as the digit count shrinks (e.g. "300" → "9"), since `Left`/`Top` are computed once.
- Delete `Closing += (_, e) => e.Cancel = true;` — no more forced-cancel of Alt+F4.
- Delete the entire `Deactivated`/`OnDeactivated` focus-stealing mechanism: remove the
  `Deactivated -= /+=` subscribe and the trailing `Activate();` from `ShowOverlay()` (leave it
  as just `Show();`), remove the unsubscribe line from `HideOverlay()`, delete `OnDeactivated`.
- Update the class doc-comment — it currently describes the old "non-dismissable, resists
  casual dismissal" behavior, which will no longer be true.

**Unchanged, confirmed no edits needed:**
`OverlayController.cs` / `IOverlayController.cs` (same `ShowCountdown`/`UpdateRemaining`/
`Lock`/`ShowExpired` signatures, same dispatcher-marshaling pattern) and
`PipeClientHostedService.cs` (same call sites) — this is purely a visual/behavioral change to
the window. Critically, `OverlayController.Lock()` is untouched: the real hard lock
(`AgentSessionLock.Lock()` → native `LockWorkStation()`) at 0 remaining still fires exactly as
before — only the warning display becomes advisory. No click-through/hit-testing work is
needed either: WPF already scopes input capture to a window's own bounds, so shrinking the
window automatically frees up the rest of the screen.

## Change 3 — Close the post-reboot lock race

**File:** [ScreenTimeService/Agent/AgentSupervisorHostedService.cs](ScreenTimeService/Agent/AgentSupervisorHostedService.cs)

Today this `BackgroundService` only checks/launches the agent on a `PeriodicTimer` tick
(default every `AgentSupervisionIntervalSeconds` = 15s), plus once immediately on service
start. After a reboot, a session logon can land in between ticks, leaving an already-exhausted
account unlocked for up to that whole interval. Fix: trigger an immediate check on the actual
session-logon event, keeping the periodic poll as a fallback (matches the project's existing
"event-driven primary, polling fallback" pattern).

- Add a `SemaphoreSlim _checkGate = new(1, 1)` field and a gated wrapper so the timer loop and
  a session-logon callback (which fires on a different thread) can't run
  `EnsureAgentRunningAsync()` concurrently:
  ```csharp
  private async Task EnsureAgentRunningGatedAsync()
  {
      if (!await _checkGate.WaitAsync(0).ConfigureAwait(false))
      {
          return; // a check is already in flight; safe to skip, it's idempotent
      }
      try { await EnsureAgentRunningAsync().ConfigureAwait(false); }
      finally { _checkGate.Release(); }
  }
  ```
  Call this (instead of `EnsureAgentRunningAsync()` directly) from the existing `do { ... }
  while (...)` loop.
- Override `StartAsync`/`StopAsync` to subscribe/unsubscribe
  `Microsoft.Win32.SystemEvents.SessionSwitch` around the service lifecycle:
  ```csharp
  public override Task StartAsync(CancellationToken cancellationToken)
  {
      SystemEvents.SessionSwitch += OnSessionSwitch;
      return base.StartAsync(cancellationToken);
  }

  public override async Task StopAsync(CancellationToken cancellationToken)
  {
      SystemEvents.SessionSwitch -= OnSessionSwitch;
      await base.StopAsync(cancellationToken).ConfigureAwait(false);
  }
  ```
- Add the handler — only reacts to `SessionSwitchReason.SessionLogon` (not `SessionUnlock`,
  which is an already-supervised session, not a new one), defensively wrapped so an exception
  can't disrupt `SystemEvents` delivery to other subscribers, and hands off via `Task.Run` so a
  slow check never blocks the `SystemEvents` thread:
  ```csharp
  private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
  {
      if (e.Reason != SessionSwitchReason.SessionLogon) return;

      logger.LogInformation("Session logon detected; triggering an immediate agent check.");
      _ = Task.Run(async () =>
      {
          try { await EnsureAgentRunningGatedAsync().ConfigureAwait(false); }
          catch (Exception ex) { logger.LogError(ex, "Session-logon-triggered agent check failed."); }
      });
  }
  ```

**File:** [ScreenTimeService/ScreenTimeService.csproj](ScreenTimeService/ScreenTimeService.csproj)

Add `<PackageReference Include="Microsoft.Win32.SystemEvents" Version="..." />`. This project
targets `net10.0-windows` but sets neither `UseWindowsForms` nor `UseWPF`, so the desktop
shared framework (which would otherwise bring `SystemEvents` in for free) isn't referenced —
the standalone NuGet package is required. Use the latest stable version at implementation time.

**Why this is safe to add to a `AddWindowsService`-hosted service:** `SystemEvents` doesn't
depend on the host pumping messages — the first subscription lazily spins up its own dedicated
thread with its own message-only window and message loop, independent of
`ServiceBase`/`WindowsServiceLifetime`. Running as SYSTEM in Session 0 doesn't block receiving
notifications about *other* sessions (the code already does this today via
`WTSGetActiveConsoleSessionId()`). This can't be unit-tested — it needs the manual verification
below.

**Investigated and intentionally not doing:** a full proactive rescan of every persisted
`state\*.json` file on service startup (for the "service restart while already logged in" case,
as opposed to full reboot). Not needed: the existing immediate first check in
`ExecuteAsync`'s `do/while` already relaunches the agent right away if it's not running, and if
it *is* still running, the agent's own reconnect-and-resend-`Hello` logic
(`PipeClientHostedService`, 5s retry) already gets the service to re-check and re-lock within
seconds. Building an eager rescan would require new session-enumeration P/Invoke machinery to
close a gap that's already covered — not worth it.

## Verification

**Change 1:** Delete/rename `C:\ProgramData\ScreenTime\config.json`, start the service, confirm
the regenerated file has `"CountdownSeconds": 300`. Separately confirm a config.json already
containing `"CountdownSeconds": 60` is left untouched after a restart.

**Change 2:** No agent-side unit test project exists, so this is manual:
- Run with a short test quota; confirm the banner appears bottom-right, doesn't cover the
  screen, and the countdown text updates correctly as digits shrink.
- While it's showing, click/type into another window; confirm input goes there, not the
  banner, and the banner never steals focus.
- Launch a fullscreen app; confirm the banner still shows on top without disrupting the
  fullscreen app's input.
- Let it reach 0 with `LockEnabled=true`; confirm the session still hard-locks exactly as
  before. Also check `LockEnabled=false` still shows "Time is over" without locking.

**Change 3:**
- Build check: service starts cleanly with the new package reference; no exception from
  `SystemEvents.SessionSwitch +=` during `StartAsync`.
- Fast user switch (quick to iterate, no reboot needed): with one exhausted account already
  logged in, switch to a second session; confirm the agent launches and gets locked within
  ~1-2s, not the full 15s poll interval. Watch the new `LogInformation` line to confirm
  `OnSessionSwitch` fires with `SessionLogon`.
- **Full reboot test (the scenario that actually matters):** mark a test account exhausted in
  its `state\{sid}.json`, reboot, log in as that account, and time how long it stays usable
  before locking. Should drop from ~15s+ to ~1-2s.
- Concurrency: trigger a logon at nearly the same instant as a periodic tick; confirm via logs
  only one `EnsureAgentRunningAsync` call proceeds (the other is skipped by the gate).
- Disposal: `Stop-Service ScreenTimeService` and confirm no further `OnSessionSwitch` log lines
  appear afterward (unsubscribe took effect) and the process shuts down cleanly.

### Files touched
- `ScreenTime.Common/QuotaConfig.cs`
- `ScreenTimeAgent/Overlay/CountdownOverlayWindow.xaml`
- `ScreenTimeAgent/Overlay/CountdownOverlayWindow.xaml.cs`
- `ScreenTimeService/Agent/AgentSupervisorHostedService.cs`
- `ScreenTimeService/ScreenTimeService.csproj`

## Open issues

### Relock-nagging silently stops working after the first post-reboot unlock

**From the end user's perspective:** the tracked account's daily quota runs out and the machine
locks, as expected. The machine is then restarted. Logging back in re-locks the account almost
immediately, also as expected. But if the user then unlocks it with their own Windows password
and keeps using the machine, **no further lock ever appears for the rest of the day** — one
reboot plus one unlock is enough to get fully unrestricted use of the machine, even though the
quota is still exhausted and stays exhausted.

(Separately, and not a bug: this lock is the OS `LockWorkStation` credential screen, so it was
never going to stop someone who legitimately knows the account's own password from dismissing it
once. What's described here is that the app should still keep re-locking them every few seconds
if they keep using the machine afterward — and that part had silently stopped happening.)

**Root cause:** `UsageTracker` rate-limits relock attempts to once every 5 seconds while the
account is exhausted and active, via a private, non-persisted field
`_lastLockTriggerMonotonic` (`RelockCooldownMs`). It defaulted to `long.MinValue`.
`IClock.MonotonicTicks` is backed by `Environment.TickCount64`, which resets to near zero on
every reboot. Because `_lastLockTriggerMonotonic` isn't part of the persisted `UsageState`, a
fresh `UsageTracker` constructed after any service restart/reboot — while the account is already
exhausted on disk (`ExhaustedNotified: true`) — starts with that field still at `long.MinValue`.
The relock check then computes `nowMonotonic - long.MinValue`, which overflows a signed 64-bit
integer and wraps to a large negative number, permanently failing the `>= RelockCooldownMs`
check for that tracker's entire lifetime. The single lock seen right after reboot comes from an
unrelated, separate connect-time check in `PipeServerHostedService` that doesn't touch this
field — once that one lock is dismissed, `UsageTracker.Tick()`'s recurring relock path never
fires again.

Existing tests didn't catch this because every prior `UsageTrackerTests` case starts with
`ExhaustedNotified = false` and lets exhaustion happen naturally within the same tracker
instance — none constructed a tracker that was *already* exhausted from persisted state, which
is the only path that hits the overflow.

**Fix:** `UsageTracker`'s constructor now seeds `_lastLockTriggerMonotonic` from the injected
clock (`clock.MonotonicTicks - RelockCooldownMs`) instead of `long.MinValue`, so the subtraction
in the relock check is always a small, bounded value and can never overflow. Added a regression
test, `RelocksWhenConstructedAlreadyExhausted`, that constructs a tracker already exhausted with
the clock reset near zero (reproducing the post-reboot condition) and asserts the relock still
fires — it fails against the pre-fix code and passes with the fix. Full suite: 14/14 passing.

**Files touched:**
- `ScreenTimeService.Core/Quota/UsageTracker.cs`
- `ScreenTimeService.Tests/UsageTrackerTests.cs`

**Still needed:** this fix exists only as source changes so far — it has not been rebuilt or
redeployed to any test machine. Rebuild and reinstall via `scripts/install-service.ps1`
(elevated), then re-verify: exhaust the quota, let it lock, reboot, log back in, unlock once,
and confirm continued active use gets re-locked within ~5s instead of staying open.
