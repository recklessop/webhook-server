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
Source: "{#RepoRoot}docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
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
; No post-install GUI launch: the GUI is requireAdministrator and the launch
; from the installer wizard ends up un-elevated for the post-install user, so
; it would just fail to connect to the admin pipe. The Start Menu shortcut
; handles the elevation correctly via the embedded manifest.

[UninstallRun]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\uninstall-service.ps1"""; \
    Flags: runhidden; \
    RunOnceId: "RemoveWebhookService"

[Code]
function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  // sc.exe query returns 0 when the service exists, 1060 when it does not.
  Exec(ExpandConstant('{sys}\sc.exe'), 'query WebhookServer', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  // 1. If the service exists, stop it so its binaries are unlocked before file
  //    copy. net stop is synchronous (blocks until the service is actually
  //    stopped), unlike sc stop which is fire-and-forget. Non-zero exit -
  //    already stopped, missing, dependency error - we ignore; the file copy
  //    will fail loudly if the binaries are still locked.
  if ServiceExists() then
  begin
    WizardForm.PreparingLabel.Caption := 'Stopping the WebhookServer service...';
    Exec(ExpandConstant('{sys}\net.exe'), 'stop WebhookServer', '', SW_HIDE,
         ewWaitUntilTerminated, ResultCode);
  end;

  // 2. Kill any running GUI / tray instances so their binaries are unlocked too.
  //    /f forces termination, /im matches by image name, "*" wildcard would be
  //    risky so we name them explicitly.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im WebhookServer.Gui.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im WebhookServer.Service.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
