#define MyAppName "ScreenTime"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ScreenTime"

[Setup]
AppId={{4808A12E-F6A3-4E6E-A4CF-693C948D5139}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\ScreenTime
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=dist
OutputBaseFilename=ScreenTimeSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Self-contained publish output already bundles the .NET runtime, so no prerequisite is needed
; on the target machine -- this installer works on a bare Windows install.

[Files]
Source: "publish\Service\*"; DestDir: "{app}\Service"; Flags: recursesubdirs ignoreversion
Source: "publish\Agent\*"; DestDir: "{app}\Agent"; Flags: recursesubdirs ignoreversion
Source: "PostInstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "PreUninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; PrivilegesRequired=admin means the whole installer already runs elevated -- this needs no
; separate UAC prompt, unlike the interactive PowerShell-elevation dance the dev scripts require.
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\PostInstall.ps1"""; \
    StatusMsg: "Registering ScreenTime Service..."; \
    Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\PreUninstall.ps1"""; \
    RunOnceId: "PreUninstall"; \
    Flags: runhidden waituntilterminated
