<#
    Runs before Inno Setup removes files, elevated. Tolerant of "already gone" states since
    uninstall must not hard-fail if the service was already removed or never started.
#>
param(
    [string]$ServiceName = "ScreenTimeService"
)

$ErrorActionPreference = "SilentlyContinue"

Stop-Service -Name $ServiceName -Force
sc.exe delete $ServiceName | Out-Null

Get-Process -Name "ScreenTimeAgent" | Stop-Process -Force

# Config/state under C:\ProgramData\ScreenTime are intentionally left in place, consistent with
# scripts/uninstall-service.ps1 -- uninstalling the app shouldn't silently erase usage history.
