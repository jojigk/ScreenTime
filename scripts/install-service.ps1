#Requires -RunAsAdministrator
<#
    Publishes ScreenTimeService and ScreenTimeAgent to sibling folders under -InstallRoot
    (Service/, Agent/) and registers the service as a LocalSystem Windows service. The service's
    agent supervisor looks for the agent at "..\Agent\ScreenTimeAgent.exe" relative to its own
    directory by convention, so this layout is what makes auto-launch work without extra config.
    The service bootstraps C:\ProgramData\ScreenTime (config/state + ACLs) itself on first start,
    and launches the agent automatically within one supervision interval (~15s) -- no separate
    scheduled task needed.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$InstallRoot = "C:\Program Files\ScreenTime",
    [string]$ServiceName = "ScreenTimeService"
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run from an elevated (Administrator) PowerShell session."
}

$serviceProjectPath = Join-Path $PSScriptRoot "..\ScreenTimeService\ScreenTimeService.csproj"
$agentProjectPath = Join-Path $PSScriptRoot "..\ScreenTimeAgent\ScreenTimeAgent.csproj"
$servicePublishDir = Join-Path $InstallRoot "Service"
$agentPublishDir = Join-Path $InstallRoot "Agent"

Write-Host "Publishing ScreenTimeService ($Configuration) to $servicePublishDir ..."
dotnet publish $serviceProjectPath -c $Configuration -o $servicePublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (service) failed with exit code $LASTEXITCODE."
}

Write-Host "Publishing ScreenTimeAgent ($Configuration) to $agentPublishDir ..."
dotnet publish $agentProjectPath -c $Configuration -o $agentPublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (agent) failed with exit code $LASTEXITCODE."
}

$exePath = Join-Path $servicePublishDir "ScreenTimeService.exe"
if (-not (Test-Path $exePath)) {
    throw "Published service executable not found at $exePath"
}

$agentExePath = Join-Path $agentPublishDir "ScreenTimeAgent.exe"
if (-not (Test-Path $agentExePath)) {
    throw "Published agent executable not found at $agentExePath"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing existing '$ServiceName' service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

Write-Host "Registering '$ServiceName' (LocalSystem, auto-start)..."
New-Service -Name $ServiceName `
    -BinaryPathName $exePath `
    -DisplayName "ScreenTime Service" `
    -Description "Tracks daily screen-time quota and locks the session when it's exhausted." `
    -StartupType Automatic | Out-Null

Write-Host "Starting '$ServiceName'..."
Start-Service -Name $ServiceName

Get-Service -Name $ServiceName | Format-Table -AutoSize
Write-Host "Config/state live under C:\ProgramData\ScreenTime (Administrators/SYSTEM only)."
Write-Host "The agent will be launched automatically in the active console session within ~15s."
