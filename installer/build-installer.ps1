<#
    Publishes ScreenTimeService and ScreenTimeAgent as self-contained win-x64 builds (bundling
    the .NET runtime, so the resulting installer needs nothing pre-installed on the target
    machine), then compiles installer/ScreenTime.iss into a single ScreenTimeSetup.exe.

    Does NOT need to run elevated -- only ScreenTimeSetup.exe itself (when run later) does.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$IsccPath = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $IsccPath)) {
    throw "Inno Setup compiler not found at $IsccPath. Install it first (winget install JRSoftware.InnoSetup) or pass -IsccPath."
}

$installerDir = $PSScriptRoot
$repoRoot = Split-Path $installerDir -Parent
$servicePublishDir = Join-Path $installerDir "publish\Service"
$agentPublishDir = Join-Path $installerDir "publish\Agent"

Write-Host "Publishing ScreenTimeService (self-contained win-x64) to $servicePublishDir ..."
dotnet publish (Join-Path $repoRoot "ScreenTimeService\ScreenTimeService.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $servicePublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (service) failed with exit code $LASTEXITCODE." }

Write-Host "Publishing ScreenTimeAgent (self-contained win-x64) to $agentPublishDir ..."
dotnet publish (Join-Path $repoRoot "ScreenTimeAgent\ScreenTimeAgent.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $agentPublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (agent) failed with exit code $LASTEXITCODE." }

Write-Host "Compiling installer with Inno Setup..."
& $IsccPath (Join-Path $installerDir "ScreenTime.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE." }

$outputExe = Join-Path $installerDir "dist\ScreenTimeSetup.exe"
if (Test-Path $outputExe) {
    Write-Host "Installer built: $outputExe"
}
else {
    throw "Expected installer output not found at $outputExe"
}
