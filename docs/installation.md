# Installation

This page covers a fresh install. If you already have Webhook Server installed, see [Upgrading](upgrading.md). To remove it, see [Uninstalling](uninstalling.md).

## Requirements

- Windows 10, Windows 11, or Windows Server 2019 / 2022 / 2025
- Administrator rights to install the service and to run the GUI
- (Optional, only if you publish from source) .NET 8 SDK

The installer is **x64 only**. There is no x86 build.

## 1. Download

Grab the latest installer from the GitHub Releases page:

> https://github.com/recklessop/webhook-server/releases/latest

Look for the asset named `WebhookServer-Setup-X.Y.Z.exe`.

## 2. Run the installer

Double-click the `.exe`. UAC will prompt — accept. The wizard:

- Copies the binaries to `C:\Program Files\WebhookServer\`
- Creates a Start Menu folder named **Webhook Server** with a GUI shortcut + Uninstall shortcut
- Optionally creates a desktop shortcut (checkbox; off by default)
- **Registers the Windows Service** named `WebhookServer`, runs it as `LocalSystem`, sets it to start automatically at boot, and configures it to restart on failure
- Starts the service
- Offers to launch the GUI when finished — leave the checkbox ticked

The first time the GUI opens, you'll see UAC prompt again because the GUI requires elevation (it talks to the service over a named pipe restricted to `SYSTEM` and the `Administrators` group). Accept it.

If the GUI's status bar shows a green dot and `Connected — HTTP 8080`, you're done.

> **Default ports**: HTTP on `8080`, HTTPS off. Both can be changed under **Server → Settings**. Port `8080` is rarely in use on a fresh server but conflicts with some other tools — if you see `Connection refused` later, this is the first thing to check.

## 3. Add your first endpoint

In the GUI:

1. **File → New endpoint**
2. Slug: `ping`
3. Auth → Mode: **None**
4. Executor → Type: **Windows PowerShell**
5. Executor → Inline command: `Write-Output 'pong'`
6. Click **Save**

The endpoint appears in the grid. Right-click it → **Copy URL**, paste into a browser. You should get back something like:

```json
{ "runId": "...", "exitCode": 0, "durationMs": 134, "stdout": "pong\r\n", ... }
```

That's it. Real-world recipes start with [Zerto pre/post scripts → AD / DNS update](recipes/zerto-pre-post-scripts.md).

## Silent / unattended install

For deploying to many machines via Group Policy, SCCM, Intune, Ansible, etc. — the installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) and supports its standard silent-mode flags:

```powershell
WebhookServer-Setup-0.1.1.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Useful flags:

| Flag | What it does |
|---|---|
| `/SILENT` | Show progress, no questions |
| `/VERYSILENT` | No UI at all |
| `/SUPPRESSMSGBOXES` | Suppress info / error popups (use with `/SILENT` or `/VERYSILENT`) |
| `/NORESTART` | Don't restart automatically — there's nothing here that needs it, but pair with `/SUPPRESSMSGBOXES` for total quiet |
| `/DIR="C:\Tools\WebhookServer"` | Override the install location |
| `/LOG="C:\Temp\install.log"` | Write a verbose installer log |
| `/TASKS="desktopicon"` | Pre-tick the optional desktop-icon task |

The post-install service install runs the same `install-service.ps1` script regardless of silent flags.

## Manual install from source (if you don't want to trust the prebuilt installer)

```powershell
# clone (or your fork)
git clone https://github.com/recklessop/webhook-server.git
cd webhook-server

# from an elevated PowerShell:
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1
```

`deploy.ps1` publishes both projects, copies the binaries to `C:\Program Files\WebhookServer\`, registers the service, and starts it. Re-run after a `git pull` to upgrade.

To run the service under a non-default account (e.g. a gMSA for AD operations), pass `-ServiceAccount`:

```powershell
.\scripts\deploy.ps1 -ServiceAccount 'CONTOSO\svc-webhookserver$'
```

See [Service account & Active Directory](service-account-and-ad.md) for the full picture.

## Where things live after install

| Path | What |
|---|---|
| `C:\Program Files\WebhookServer\` | Binaries (`WebhookServer.Service.exe`, `WebhookServer.Gui.exe`, the icon, install/uninstall scripts) |
| `C:\ProgramData\WebhookServer\config.json` | The configuration. Backups in `backups\`, daily-rolling logs in `logs\`. **Don't edit by hand** — secrets are DPAPI-encrypted and the service won't pick up your changes without a reload. Use the GUI. |
| `\\.\pipe\WebhookServerAdmin` | The named pipe the GUI uses to talk to the service. ACL'd to `SYSTEM` + `Administrators` only. |

The installer never touches `C:\ProgramData\WebhookServer\`. Uninstalling preserves your config and logs by default; see [Uninstalling](uninstalling.md) for how to wipe them too.
