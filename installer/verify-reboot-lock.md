# Live reboot-lock verification

Confirms the fix in `docs/feature_plan.md` (Open issues → relock-nagging overflow bug) on real
hardware. Needs an **elevated** PowerShell window (right-click PowerShell → Run as administrator).

## Why a service restart is enough — no reboot required

The bug lives entirely in `UsageTracker`'s constructor/`Tick()` logic, keyed off
`Environment.TickCount64` (system uptime, resets only on a real reboot) versus a stale
`long.MinValue` sentinel. The overflow it caused doesn't care whether `TickCount64` is small
(post-reboot) or large (mid-session) — subtracting `long.MinValue` from *any* normal tick value
overflows the same way. So `Restart-Service ScreenTimeService` — which reconstructs the tracker
from persisted state exactly like a reboot does, just without touching `TickCount64` — reproduces
and validates the identical code path a real reboot exercises. No need to actually restart the
machine.

## 1. Install the latest build (includes new diagnostic logging for this test)

```powershell
& "E:\Projects\ScreenTime\installer\dist\ScreenTimeSetup.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

## 2. Configure a fast, safe test run

`LockEnabled = $false` means **nothing will actually lock your screen** during this test — you'll
watch it happen through the event log instead. Replace the SID below with your own if different
(`whoami /user`).

```powershell
Stop-Service ScreenTimeService
$configPath = "C:\ProgramData\ScreenTime\config.json"
$config = Get-Content $configPath | ConvertFrom-Json
$config.DailyLimitMinutes = 1
$config.LockEnabled = $false
$config | ConvertTo-Json -Depth 10 | Set-Content $configPath

$mySid = "S-1-5-21-1832139548-3079979135-3189284955-1001"
$stateDir = "C:\ProgramData\ScreenTime\state"
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
@{
    Date = (Get-Date).ToString("yyyy-MM-dd")
    UsedSeconds = 60
    LastObservedUtc = (Get-Date).ToUniversalTime().ToString("o")
    MonotonicAnchorTicks = 0
    ExhaustedNotified = $true
    CheckpointNotified = $false
} | ConvertTo-Json | Set-Content (Join-Path $stateDir "$mySid.json")

Start-Service ScreenTimeService
```

## 3. Watch it fire once, immediately

```powershell
Start-Sleep -Seconds 8
Get-EventLog -LogName Application -Source ScreenTimeService -Newest 10 |
    Select-Object TimeGenerated, EntryType, Message | Format-List
```

Expect one `"Connection from ... is already exhausted; enforcing immediately"` line.

## 4. Stay at the keyboard for ~30s (ordinary use counts), then check for repeats

```powershell
Start-Sleep -Seconds 30
Get-EventLog -LogName Application -Source ScreenTimeService -Newest 15 |
    Select-Object TimeGenerated, EntryType, Message | Format-List
```

Expect several `"QuotaExhausted fired for ... notifying agent to lock"` lines, roughly 5 seconds
apart, while you were active.

## 5. The actual regression test: restart the service

```powershell
Restart-Service ScreenTimeService
Start-Sleep -Seconds 8
Get-EventLog -LogName Application -Source ScreenTimeService -Newest 10 |
    Select-Object TimeGenerated, EntryType, Message | Format-List
```

Expect the immediate `"...enforcing immediately"` line again right after restart.

## 6. The moment that matters: stay active for another ~30s

```powershell
Start-Sleep -Seconds 30
Get-EventLog -LogName Application -Source ScreenTimeService -Newest 20 |
    Select-Object TimeGenerated, EntryType, Message | Format-List
```

**This is what the bug broke.** Pre-fix, you'd see nothing here — silence for the rest of the day.
Post-fix, you should see repeated `"QuotaExhausted fired..."` lines again, ~5s apart, same as
step 4. That's the proof the relock-nagging survives a tracker being rebuilt from already-exhausted
state — which is exactly what happens on a real reboot too.

## 7. Clean up

```powershell
Stop-Service ScreenTimeService
$config.DailyLimitMinutes = 70
$config.LockEnabled = $true
$config | ConvertTo-Json -Depth 10 | Set-Content $configPath
Remove-Item (Join-Path $stateDir "$mySid.json") -ErrorAction SilentlyContinue
Start-Service ScreenTimeService
```

## Optional: the real full-reboot version

If you want the belt-and-suspenders version with an actual `Restart-Computer` instead of step 5,
swap that one command — everything else is identical. Not required for the fix itself (see above),
but proves the whole chain including `AgentSupervisorHostedService`'s session-logon handling too.
