# ScreenTime — Administrator Guide

ScreenTime is a Windows parental-control tool that tracks daily active screen-time usage and locks
the session when the quota runs out. This guide covers how it works, how to install it, how to
configure it, and what to do when something goes wrong.

## 1. How it works

ScreenTime is two programs working together:

- **ScreenTimeService** — a Windows Service running as `LocalSystem`. It owns the actual quota
  logic: it ticks once a second, tracks active-usage seconds against the configured daily limit,
  and decides when to lock the session. It persists its state to disk so a reboot doesn't reset
  the day's usage.
- **ScreenTimeAgent** — a small WPF app that runs in your own (logged-in) user session. It shows
  the on-screen countdown overlay before time runs out, and is the *primary* mechanism that
  actually locks the screen (it calls Windows' own `LockWorkStation`, the same thing that happens
  when you press Win+L). The service supervises the agent and relaunches it automatically if it's
  not running (within about 15 seconds), and the service has its own fallback lock mechanism in
  case the agent is dead or unresponsive.

The two talk to each other over a local named pipe. The agent sends heartbeats saying "the user is
active right now"; the service ticks its quota accordingly and tells the agent when to show a
countdown or lock.

**On startup**, the service loads its configuration and state from
`C:\ProgramData\ScreenTime\config.json` and `state.json` (creating them with defaults if they
don't exist yet), then starts ticking. The agent is expected to live in a sibling `Agent\` folder
next to the service's own install folder — that's how the service finds and launches it
automatically; see [Installation](#2-installation) below.

**The lock itself is real** — it's the same Windows lock screen you'd get from Win+L, not a custom
overlay. That means:
- It requires the actual Windows account password to clear. There is no software "unlock" button
  and no admin override baked into the app — Windows deliberately doesn't allow that (if it did,
  it would defeat the point of the lock screen as a security boundary).
- Once the day's real quota is exhausted, every time the machine is unlocked and used again, the
  service detects that and re-locks within about 5 seconds. It'll keep doing this for the rest of
  the calendar day.

## 2. Installation

There are two ways to install ScreenTime: a **packaged installer** (recommended for an actual
family/target machine that doesn't have this repo checked out) and the **dev PowerShell scripts**
(useful when you're working in the repo directly). Both end up in the same place — a registered
`ScreenTimeService` and files under `C:\Program Files\ScreenTime`.

### 2.1 Packaged installer (recommended)

`installer\dist\ScreenTimeSetup.exe` is a self-contained setup wizard — it bundles the .NET
runtime, so **nothing needs to be pre-installed** on the target machine, not even the .NET SDK.

**To build it** (from a dev machine with the repo and the .NET SDK):
```powershell
.\installer\build-installer.ps1
```
This requires the Inno Setup 6 compiler (`ISCC.exe`); install it once with
`winget install --id JRSoftware.InnoSetup -e` if it's not already present. The script publishes
both apps as self-contained `win-x64` builds and compiles `installer\ScreenTime.iss` into
`installer\dist\ScreenTimeSetup.exe`.

**To install:** copy `ScreenTimeSetup.exe` to the target machine and double-click it (or run it
from an elevated prompt — it requests admin elevation itself either way, via UAC). It installs to
`C:\Program Files\ScreenTime`, registers and starts `ScreenTimeService`, and the agent auto-launches
within ~15 seconds, same as the script path below. Re-running the installer (e.g. to upgrade)
automatically stops and replaces any existing installation; `config.json`/`state.json` under
`ProgramData` are untouched.

**To uninstall:** use Windows' normal "Add or remove programs" (or `installer\dist`'s generated
uninstaller under `C:\Program Files\ScreenTime\unins000.exe`) — this stops and removes the service
and kills any running agent process. Config/state under `C:\ProgramData\ScreenTime` are
intentionally left in place, same as the script path.

### 2.2 Dev PowerShell scripts

**Prerequisites:**
- Windows 10/11, Administrator access.
- [.NET SDK](https://dotnet.microsoft.com/download) matching the version this repo targets (check
  with `dotnet --version`; this repo currently builds against .NET 10) — this path does a
  framework-dependent publish, so unlike the packaged installer, the SDK/runtime needs to already
  be on the machine.

**Steps:**

1. Open an **elevated** PowerShell session (Run as Administrator).
2. From the repo root, run:
   ```powershell
   .\scripts\install-service.ps1
   ```
   This builds and publishes both `ScreenTimeService` and `ScreenTimeAgent` (Release config by
   default) to `C:\Program Files\ScreenTime\Service` and `...\Agent`, registers
   `ScreenTimeService` as an automatic-start `LocalSystem` service, and starts it.
3. Within ~15 seconds the service will detect no agent is running in the active console session
   and launch it automatically. You should see the ScreenTime overlay appear briefly (or nothing
   at all, if you're well within quota) — that confirms the agent connected.
4. `config.json` and `state.json` are created automatically under `C:\ProgramData\ScreenTime` on
   first start, locked down to Administrators + SYSTEM only. See [Configuration](#3-configuration)
   to customize the defaults.

**Custom install location or service name:**
```powershell
.\scripts\install-service.ps1 -InstallRoot "D:\Apps\ScreenTime" -ServiceName "MyScreenTime"
```

**Uninstalling:**
```powershell
.\scripts\uninstall-service.ps1
```
This stops and removes the service and kills any running agent process. Config/state under
`C:\ProgramData\ScreenTime` are left in place by default (so a reinstall picks up where you left
off) — pass `-RemoveProgramData` to wipe them too.

**Re-installing / upgrading:** just re-run `install-service.ps1` — it stops and removes any
existing service of the same name before republishing and re-registering it. Your `config.json`
and `state.json` are untouched (they live under `ProgramData`, not the install folder).

## 3. Configuration

Everything is in one file: **`C:\ProgramData\ScreenTime\config.json`**. It's locked to
Administrators + SYSTEM only, so you'll need an elevated editor (Notepad "Run as administrator",
or an elevated PowerShell) to view or change it.

**Important: config is only read once, at service startup.** There's no hot-reload. After editing
the file, you must restart the service for changes to take effect:
```powershell
Restart-Service -Name ScreenTimeService
```

### Full field reference

| Field | Default | Meaning |
|---|---|---|
| `DailyLimitMinutes` | `70` | Default daily quota, used on any day not covered by `DailyLimitOverrides`. |
| `DailyLimitOverrides` | `[]` | Per-day-of-week quota overrides — see [3.1](#31-per-day-limits-and-the-checkpoint-lock). |
| `CountdownSeconds` | `60` | How many seconds before a lock the on-screen countdown overlay appears. |
| `TickIntervalSeconds` | `1` | How often the service re-evaluates usage. |
| `PersistIntervalSeconds` | `5` | How often usage state is saved to disk. |
| `ClockRollbackToleranceSeconds` | `120` | How far the wall clock can jump backward before it's treated as tampering rather than a normal NTP correction. |
| `HeartbeatTimeoutSeconds` | `30` | If the agent hasn't reported a heartbeat within this window, the session is treated as idle (usage stops accruing). |
| `AgentExecutablePath` | `""` (empty) | Full path to `ScreenTimeAgent.exe`. Leave empty to use the convention default (sibling `Agent\` folder next to the service). |
| `AgentSupervisionIntervalSeconds` | `15` | How often the service checks whether the agent is running and relaunches it if not. |
| `LockEnabled` | `true` | Testing-only escape hatch. When `false`, exhaustion is still fully tracked and shown (countdown, "time is over" screen) but nothing actually locks. **Leave this `true` for real enforcement** — it defaults to `true` specifically so a fresh install is never silently inert. |
| `ExceptionWindows` | `[]` | Recurring weekly windows where usage isn't counted — see [3.2](#32-exception-windows-usage-not-counted). |

### 3.1 Per-day limits and the checkpoint lock

`DailyLimitOverrides` lets you set a different quota on specific days of the week:

```json
"DailyLimitOverrides": [
  { "DayOfWeek": "Thursday", "LimitMinutes": 180 },
  { "DayOfWeek": "Saturday", "LimitMinutes": 180 }
]
```

Any day of week not listed here falls back to `DailyLimitMinutes`.

**Behavior on an override day that raises the limit above the default** (e.g. Thursday above): the
machine still locks once at the *base* `DailyLimitMinutes` (70 min) — a "checkpoint." That lock can
be cleared the normal way (password) and usage then continues *uninterrupted, with no re-locking*,
all the way to the real override limit (180 min). Once the real limit is hit, the machine locks
again and — as described in section 1 — will keep re-locking every ~5 seconds for the rest of the
day if you keep unlocking and using it.

If an override sets a limit *equal to or below* the base default, there's no checkpoint — it just
locks once at that (lower) limit, exactly like a normal day.

### 3.2 Exception windows (usage not counted)

`ExceptionWindows` defines recurring weekly time ranges during which active usage isn't counted
against the quota, and an already-exhausted quota won't trigger a *new* lock:

```json
"ExceptionWindows": [
  { "DayOfWeek": "Thursday", "Start": "18:00:00", "End": "20:00:00" },
  { "DayOfWeek": "Saturday", "Start": "17:00:00", "End": "20:00:00" }
]
```

**To change or add a window** (e.g. to change Thursday's window to 4–6 PM instead of 6–8 PM):

1. Open an elevated PowerShell.
2. Edit `ExceptionWindows` in `C:\ProgramData\ScreenTime\config.json`, e.g.:
   ```json
   { "DayOfWeek": "Thursday", "Start": "16:00:00", "End": "18:00:00" }
   ```
3. Save, then `Restart-Service -Name ScreenTimeService`.

Notes and limits:
- `Start`/`End` are 24-hour local time, `HH:mm:ss`.
- Windows must be same-day: `End` must be later than `Start`. A window spanning midnight (e.g.
  11 PM–1 AM) isn't supported.
- A window **does not retroactively unlock** a session that's already locked from earlier in the
  day — Windows offers no programmatic unlock (see section 1). It only stops accrual and stops a
  *new* lock from firing while the window is active. If the machine locked at 5:55 PM and your
  window starts at 6:00 PM, you'd still need the password to clear that lock — but once cleared,
  no new lock will fire again until the window ends.
- `DayOfWeek` values are the standard English day names: `Sunday`, `Monday`, `Tuesday`,
  `Wednesday`, `Thursday`, `Friday`, `Saturday` (case-sensitive as written; matches .NET's
  `DayOfWeek` enum names).

### 3.3 Example full config

```json
{
  "DailyLimitMinutes": 70,
  "DailyLimitOverrides": [
    { "DayOfWeek": "Saturday", "LimitMinutes": 180 },
    { "DayOfWeek": "Sunday", "LimitMinutes": 180 }
  ],
  "CountdownSeconds": 60,
  "TickIntervalSeconds": 1,
  "PersistIntervalSeconds": 5,
  "ClockRollbackToleranceSeconds": 120,
  "HeartbeatTimeoutSeconds": 30,
  "AgentExecutablePath": "",
  "AgentSupervisionIntervalSeconds": 15,
  "LockEnabled": true,
  "ExceptionWindows": [
    { "DayOfWeek": "Thursday", "Start": "18:00:00", "End": "20:00:00" }
  ]
}
```

Any field you omit falls back to its default — you don't need to list every field, just the ones
you want to change.

### 3.4 Changing configuration on an already-installed machine, step by step

This is the general procedure for any config change (not just the two examples above), including
the safety and verification steps a quick edit can easily skip:

1. **Open an elevated PowerShell** (Run as Administrator) — required, the file is ACL-locked to
   Administrators + SYSTEM.
2. **Back up the current file** before touching it, so a bad edit is a one-line rollback instead
   of reconstructing your settings from memory:
   ```powershell
   Copy-Item "C:\ProgramData\ScreenTime\config.json" "C:\ProgramData\ScreenTime\config.json.bak"
   ```
3. **Edit the file** — Notepad (elevated) or `notepad C:\ProgramData\ScreenTime\config.json` from
   the elevated PowerShell session both work.
4. **Validate the JSON before restarting the service.** This matters more than it sounds:
   `QuotaConfigStore` catches JSON parse errors internally and **silently falls back to defaults**
   rather than crashing — so a typo (trailing comma, mismatched brace, wrong quoting) doesn't error
   loudly, it just quietly discards your entire edit on next restart. Catch it up front instead:
   ```powershell
   Get-Content "C:\ProgramData\ScreenTime\config.json" -Raw | ConvertFrom-Json | Out-Null
   ```
   If this throws, fix the JSON before proceeding. If it succeeds silently, the syntax is valid
   (this doesn't check *values* — e.g. an unknown `DayOfWeek` string — only that it's well-formed
   JSON; see step 6 for catching those).
5. **Restart the service** — config is only read at startup, there's no hot-reload:
   ```powershell
   Restart-Service -Name ScreenTimeService
   ```
6. **Verify the edit was actually accepted**, in two parts:
   - **Service came up clean:**
     ```powershell
     Get-Service -Name ScreenTimeService
     ```
     should show `Running`.
   - **No silent fallback-to-defaults happened** — check the Application event log for the warning
     `QuotaConfigStore` logs on a parse failure:
     ```powershell
     Get-WinEvent -LogName Application -MaxEvents 20 |
       Where-Object { $_.ProviderName -eq "ScreenTimeService" }
     ```
     If you see a "Failed to read ... falling back to defaults" entry around the restart time, your
     edit was rejected (fix per step 4's validation and repeat — a syntactically valid but
     semantically wrong value, like a misspelled day name, is caught here rather than step 4).
     If there's nothing there, the file parsed and your edit is live.
   - There's currently no explicit "config loaded successfully" log line to positively confirm
     against — absence of the failure warning above is the available signal. For anything
     time-based (an exception window, a per-day override), the most conclusive check is simply
     observing the actual behavior at that day/time.
7. **If something's wrong**, restore the backup and restart:
   ```powershell
   Copy-Item "C:\ProgramData\ScreenTime\config.json.bak" "C:\ProgramData\ScreenTime\config.json" -Force
   Restart-Service -Name ScreenTimeService
   ```

## 4. Troubleshooting

### 4.1 Service won't start / install script fails

- Confirm you're in an **elevated** PowerShell (`install-service.ps1` / `uninstall-service.ps1`
  both refuse to run otherwise).
- Check `dotnet publish` output in the script's console for the actual build error — the script
  aborts immediately on a failed publish.
- If a service with the same name is stuck in a weird state, `Get-Service ScreenTimeService` and
  `sc.exe query ScreenTimeService` will show its current status; `sc.exe delete ScreenTimeService`
  (after stopping it) removes a broken registration so you can re-run the install script cleanly.

### 4.2 Windows blocked the app from running (Smart App Control / antivirus)

On machines with **Smart App Control (SAC)** enabled — a Windows 11 feature that blocks unsigned
or unrecognized apps by default — you may see the published `.exe`s (or `ScreenTimeSetup.exe`
itself) fail to run, or Windows Security show a block notification, especially the first time a
freshly-built binary runs. This project's binaries are not code-signed, so this is expected on a
strict machine, not a bug in the app itself.

**What actually happens, concretely:** Windows logs a `Microsoft-Windows-CodeIntegrity/Operational`
event (IDs 3033/3077, "did not meet the Enterprise signing level requirements") each time this
happens. You can check for these with:
```powershell
Get-WinEvent -LogName "Microsoft-Windows-CodeIntegrity/Operational" -MaxEvents 20
```

**Your options, in order of preference:**

1. **Code-sign the binaries.** This is the actual long-term fix if you're going to distribute
   ScreenTime beyond your own dev machine — an EV code-signing certificate (or your organization's
   internal signing infrastructure) is what SAC and most AV products ultimately trust. Not
   something to set up for a one-off home install, but the right answer if this needs to run on
   multiple family/school machines reliably.
2. **Temporarily turn off Smart App Control, install, then turn it back on.** As of recent Windows
   11 updates, SAC can be switched off and back on again freely from **Windows Security → App &
   browser control → Smart App Control**, without needing to reinstall Windows
   ([source](https://www.windowslatest.com/2025/12/16/microsoft-confirms-you-can-soon-disable-smart-app-control-without-reinstalling-windows-11/)).
   Check your build first — this fix shipped in Windows 11 Insider Preview build 26220.7070 and
   later; if your Settings page shows a normal On/Off toggle (rather than a note saying it can't be
   turned back on), you have the newer, reversible behavior. Turn it off, install/run ScreenTime
   once so it's on disk and registered, then turn SAC back on.
3. **Permanently turn Smart App Control off.** Only do this if you're on an *older* Windows build
   where re-enabling it isn't possible (verify via the check in option 2 first) and you've decided
   the trade-off is acceptable for this machine. **This is one-way on older builds** — Microsoft's
   own documentation previously stated that once turned off, SAC could not be turned back on without
   a full Windows reinstall
   ([source](https://support.microsoft.com/en-us/windows/smart-app-control-frequently-asked-questions-285ea03d-fa88-4d56-882e-6698afdb7003)).
   Confirm your build's actual behavior (option 2) before doing this.

**What does *not* work:** unlike Windows Defender's antivirus, Smart App Control currently has **no
per-app exclusion list or allow-list mechanism** — you can't tell it "trust this one app" the way
you can add a Defender exclusion. Signing or a full on/off toggle are the only levers.

If it's regular **Windows Defender antivirus** (not SAC) flagging the app instead, that one *does*
support exclusions: **Windows Security → Virus & threat protection → Manage settings → Add or
remove exclusions** → add the install folder (e.g. `C:\Program Files\ScreenTime`) or the specific
`.exe`.

### 4.3 Agent doesn't appear / no countdown overlay ever shows

- Give it the full supervision interval (~15s after service start) before assuming something's
  wrong.
- Check `Get-Process ScreenTimeAgent` in the user's own (non-elevated) session — the agent runs as
  the logged-in user, not SYSTEM, so an elevated PowerShell won't necessarily see it if run from a
  different session context.
- If `AgentExecutablePath` was manually set in `config.json` and points to a path that doesn't
  exist, the service can't launch it — clear the field (empty string) to fall back to the
  convention default (sibling `Agent\` folder), or fix the path.

### 4.4 Config changes don't seem to apply

Two usual causes, in order of likelihood:
1. Missing `Restart-Service -Name ScreenTimeService` — there is no hot-reload; the service reads
   `config.json` exactly once, at startup.
2. A JSON syntax error silently reverted the whole file to defaults on that restart.

Walk through [3.4](#34-changing-configuration-on-an-already-installed-machine-step-by-step) — step
6 in particular tells you definitively whether the last restart accepted or rejected your edit.

### 4.5 Can't read or edit `config.json` / `state.json`

Both files are ACL-locked to Administrators + SYSTEM only by design (this is what prevents the
person being tracked from just editing their own quota). Use an elevated editor or elevated
PowerShell — see section 3's opening note.

## 5. Things to keep in mind as an administrator

- **The lock is real and non-negotiable by design.** There is no admin override, no "emergency
  unlock," and no way for the software itself to clear a lock once it's fired. If you need to get
  past a lock, it's the normal Windows account password — same as any other time the screen is
  locked. Setting `LockEnabled: false` disables *all* real enforcement (testing/preview mode only)
  — don't leave that set in a config you intend to actually rely on.
- **Config edits require a service restart to take effect** — there's no file-watcher. Get in the
  habit of `Restart-Service -Name ScreenTimeService` after every edit.
- **Exception windows and checkpoint overrides never unlock an already-locked session** — they
  only prevent a *new* lock while active. Time the windows with this in mind (e.g. a window
  starting a few minutes after the daily limit is likely to still be hit).
- **The relock-nagging behavior (~5s) is intentional**, not a bug — it's what makes the real
  exhaustion lock actually effective rather than a one-time speed bump. If you see the screen
  flash unlocked-then-locked repeatedly near the end of the day, that's this working as designed.
- **`DailyLimitOverrides` above the base default create an implicit two-stage lock** (checkpoint at
  the base limit, then the real limit) — there's no way to configure an override day to skip the
  checkpoint and go straight to a single hard lock at the higher number, other than setting the
  base `DailyLimitMinutes` itself to that same higher number (which then applies to every
  non-overridden day too).
- **This isn't a kiosk-mode / tamper-proof lockdown.** The agent can, in principle, be killed by a
  sufficiently technical user before it reports a lock — the service's independent fallback lock
  mitigates this, but there's no low-level input hook or process-protection hardening in this
  build. Treat it as a normal-user deterrent, not a security boundary against someone with local
  admin rights or comparable technical skill.
- **Binaries are unsigned.** Expect Smart App Control / antivirus friction on any machine with
  strict settings, especially on first run after a rebuild — see section 4.2. Plan for
  code-signing before distributing this beyond machines you personally control.
