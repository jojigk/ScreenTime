<#
    Runs once, elevated, at the end of setup (Inno Setup already requires admin for the whole
    installer, so no separate UAC prompt is needed here). Registers and starts the Windows
    service; the service bootstraps C:\ProgramData\ScreenTime itself on first start, and its
    own agent supervisor launches ScreenTimeAgent.exe automatically within ~15s.
#>
param(
    [string]$ServiceName = "ScreenTimeService"
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $PSScriptRoot "Service\ScreenTimeService.exe"
if (-not (Test-Path $exePath)) {
    throw "Service executable not found at $exePath"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

New-Service -Name $ServiceName `
    -BinaryPathName $exePath `
    -DisplayName "ScreenTime Service" `
    -Description "Tracks daily screen-time quota and locks the session when it's exhausted." `
    -StartupType Automatic | Out-Null

Start-Service -Name $ServiceName
