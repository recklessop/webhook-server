; Inno Setup script for Webhook Server.
;
; Build: iscc /DAppVersion=0.1.0 webhook-server.iss
; Output: ..\dist\WebhookServer-Setup-{AppVersion}.exe
;
; The installer copies published binaries to {pf}\WebhookServer, installs the
; Windows Service via install-service.ps1 post-install, and removes the service
; via uninstall-service.ps1 pre-uninstall. Start Menu gets a single GUI shortcut.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppName "Webhook Server"
#define AppPublisher "Justin Paul"
#define AppURL "https://jpaul.me"
#define AppExeName "WebhookServer.Gui.exe"
#define ServiceExeName "WebhookServer.Service.exe"
#define ServiceName "WebhookServer"
#define RepoRoot "..\"

[Setup]
AppId={{6E3B3C1A-9C20-4F50-B6A8-2B6D6D7E2F11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL=https://github.com/recklessop/webhook-server
AppUpdatesURL=https://github.com/recklessop/webhook-server/releases
DefaultDirName={autopf}\WebhookServer
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=WebhookServer-Setup-{#AppVersion}
OutputDir={#RepoRoot}dist
SetupIconFile={#RepoRoot}resources\webhook-server.ico
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#RepoRoot}publish\service\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}publish\gui\*";     DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}scripts\install-service.ps1";   DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "{#RepoRoot}scripts\uninstall-service.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "{#RepoRoot}README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}resources\webhook-server.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\webhook-server.ico"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\webhook-server.ico"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install-service.ps1"" -BinaryPath ""{app}\{#ServiceExeName}"""; \
    StatusMsg: "Installing Windows Service..."; \
    Flags: runhidden
Filename: "{app}\{#AppExeName}"; \
    Description: "Launch {#AppName}"; \
    Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\uninstall-service.ps1"""; \
    Flags: runhidden; \
    RunOnceId: "RemoveWebhookService"
