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
Source: "{#RepoRoot}scripts\examples\*"; DestDir: "{app}\scripts\examples"; Flags: ignoreversion recursesubdirs createallsubdirs
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
; Post-install GUI launch. The GUI's app.manifest is requireAdministrator,
; so launching with shellexec (ShellExecute) honors the manifest and triggers
; a clean UAC prompt. Using plain CreateProcess via the default Run path
; would skip the manifest and result in an un-elevated GUI that cannot connect
; to the admin pipe.
Filename: "{app}\{#AppExeName}"; \
    Description: "Launch {#AppName}"; \
    Flags: postinstall nowait shellexec skipifsilent

[UninstallRun]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\uninstall-service.ps1"""; \
    Flags: runhidden; \
    RunOnceId: "RemoveWebhookService"

[Code]
const
  // aka.ms redirects to the latest 8.0.x patch. Inno Setup's downloader
  // follows redirects via the Windows HTTP stack.
  AspNetCore8Url  = 'https://aka.ms/dotnet/8.0/aspnetcore-runtime-win-x64.exe';
  WinDesktop8Url  = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  AspNetCore8File = 'aspnetcore-runtime-8.0-win-x64.exe';
  WinDesktop8File = 'windowsdesktop-runtime-8.0-win-x64.exe';

var
  DownloadPage: TDownloadWizardPage;

function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  // sc.exe query returns 0 when the service exists, 1060 when it does not.
  Exec(ExpandConstant('{sys}\sc.exe'), 'query WebhookServer', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

// True if a Microsoft.* shared-framework directory under
// %ProgramFiles%\dotnet\shared contains at least one 8.x.y subfolder.
function HasDotNet8(const RuntimeName: String): Boolean;
var
  rec: TFindRec;
  base: String;
begin
  Result := False;
  base := ExpandConstant('{commonpf}\dotnet\shared\') + RuntimeName;
  if not DirExists(base) then Exit;
  if FindFirst(base + '\8.*', rec) then
  try
    repeat
      if (rec.Name <> '.') and (rec.Name <> '..') and
         DirExists(base + '\' + rec.Name) then begin
        Result := True;
        Exit;
      end;
    until not FindNext(rec);
  finally
    FindClose(rec);
  end;
end;

function NeedsAspNet8(): Boolean;
begin
  Result := not HasDotNet8('Microsoft.AspNetCore.App');
end;

function NeedsWinDesktop8(): Boolean;
begin
  Result := not HasDotNet8('Microsoft.WindowsDesktop.App');
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    'Downloading prerequisites',
    'Webhook Server needs the .NET 8 runtimes. Setup is fetching them now.',
    nil);
end;

// Runs a downloaded runtime installer silently. Treats Microsoft's
// "success but reboot pending" / "newer already installed" exit codes
// as successes so we don't fail the whole install over a benign result.
function RunRuntimeInstaller(const FileName, DisplayName: String): String;
var
  resultCode: Integer;
  fullPath: String;
begin
  Result := '';
  fullPath := ExpandConstant('{tmp}\') + FileName;
  if not Exec(fullPath, '/install /quiet /norestart', '', SW_HIDE,
              ewWaitUntilTerminated, resultCode) then begin
    Result := 'Could not launch the ' + DisplayName + ' installer.';
    Exit;
  end;
  case resultCode of
    0, 1638, 3010, 1641: ;
  else
    Result := DisplayName + ' installer failed (exit code ' +
              IntToStr(resultCode) + ').';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  errMsg: String;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;
  if not (NeedsAspNet8 or NeedsWinDesktop8) then Exit;

  DownloadPage.Clear;
  if NeedsAspNet8 then
    DownloadPage.Add(AspNetCore8Url, AspNetCore8File, '');
  if NeedsWinDesktop8 then
    DownloadPage.Add(WinDesktop8Url, WinDesktop8File, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if MsgBox('Failed to download the .NET 8 runtimes:' + #13#10#13#10 +
                GetExceptionMessage + #13#10#13#10 +
                'Continue installing anyway? Webhook Server will not start ' +
                'until the runtimes are installed manually.',
                mbError, MB_YESNO) = IDNO then
        Result := False;
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  if NeedsAspNet8 then begin
    errMsg := RunRuntimeInstaller(AspNetCore8File, 'ASP.NET Core 8 Runtime');
    if errMsg <> '' then begin
      MsgBox(errMsg, mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
  if NeedsWinDesktop8 then begin
    errMsg := RunRuntimeInstaller(WinDesktop8File, '.NET Desktop Runtime 8');
    if errMsg <> '' then begin
      MsgBox(errMsg, mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
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
