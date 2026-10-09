#Requires -RunAsAdministrator
<#
    Stops and removes the ScreenTimeService Windows service. Config/state under
    C:\ProgramData\ScreenTime are left in place unless -RemoveProgramData is passed.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "ScreenTimeService",
    [switch]$RemoveProgramData
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run from an elevated (Administrator) PowerShell session."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' is not installed."
}
else {
    Write-Host "Stopping '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

    Write-Host "Removing '$ServiceName'..."
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service removed."
}

# With the service gone, nothing will relaunch the agent -- stop it too so it doesn't linger.
$agentProcesses = Get-Process -Name "ScreenTimeAgent" -ErrorAction SilentlyContinue
if ($agentProcesses) {
    Write-Host "Stopping running ScreenTimeAgent process(es)..."
    $agentProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
}

$dataDir = Join-Path $env:ProgramData "ScreenTime"
if ($RemoveProgramData) {
    if (Test-Path $dataDir) {
        Remove-Item -Path $dataDir -Recurse -Force -Confirm:$false
        Write-Host "Removed $dataDir."
    }
}
elseif (Test-Path $dataDir) {
    Write-Host "Config/state at $dataDir were left in place. Pass -RemoveProgramData to delete them."
}
