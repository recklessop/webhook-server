# Upgrading

## TL;DR

Download the new installer from [Releases](https://github.com/recklessop/webhook-server/releases/latest) and run it. That's it. Your config, endpoints, secrets, and logs are preserved.

## What the upgrade does

The Inno Setup installer detects an existing install and runs through these steps automatically:

1. **`net stop WebhookServer`** — synchronously stops the running service so its binaries are unlocked. Blocks until the SCM reports the service is actually stopped.
2. **`taskkill /f /im WebhookServer.Gui.exe`** — closes the GUI if you left it running. Same for any orphan `WebhookServer.Service.exe` from a `deploy.ps1` dev install.
3. **Copies** the new binaries into `C:\Program Files\WebhookServer\`. Files marked `ignoreversion` so newer files always overwrite older ones, even if version metadata happens to match.
4. **Re-registers** the service via `install-service.ps1`, which detects the existing `WebhookServer` service via `Get-Service` and takes the **update** branch (changes the binary path) rather than re-creating it. Your service account choice is preserved.
5. **Starts the service**. The GUI launches if you left the post-install checkbox ticked.

Total downtime for the service: 2–10 seconds depending on disk speed and how long the service takes to flush its log buffer.

## What's preserved

- `C:\ProgramData\WebhookServer\config.json` — the installer never touches this directory
- All endpoints, secrets, callback URLs, allowlists
- Bind addresses, display host, HTTPS binding settings
- Auto-snapshots in `C:\ProgramData\WebhookServer\backups\`
- Log files in `C:\ProgramData\WebhookServer\logs\`
- The Windows Service identity (LocalSystem, gMSA, domain user — whatever you configured)

## What gets replaced

- Everything in `C:\Program Files\WebhookServer\` — the .exe files, .dll files, the icon, `install-service.ps1`, `uninstall-service.ps1`, the bundled `README.md`, the `docs/` folder

## Silent upgrades (Group Policy / SCCM / Intune / Ansible)

Same as the silent install:

```powershell
WebhookServer-Setup-X.Y.Z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

The pre-install `net stop` step still fires; downtime is unchanged.

## Rolling back to a previous version

The installer doesn't support side-by-side versions or downgrade detection. To roll back:

1. Uninstall the current version (Settings → Apps, or `Start Menu → Webhook Server → Uninstall`). This stops + removes the service. Your config in `C:\ProgramData\WebhookServer\` is preserved.
2. Run the older installer.

If a config field changed semantics between versions and you ran on the new version first, the **Config Checkpoints** menu (File → Config Checkpoints) lists snapshots taken before each save. The auto-snapshot from immediately before the upgrade is the closest you'll have to your pre-upgrade config.

## Edge cases

### "Setup cannot continue. Please close the following applications: WebhookServer.Gui.exe"

The taskkill step normally handles this, but if you're running an unusually slow process or if the GUI was elevated by a different user, you may see this. Close the GUI manually and click Retry.

### Service stays in a "Stopping" state forever

`net stop` waits up to 30 seconds for the service to stop. If a hook script hung (e.g. interactive prompt) and the service can't kill it cleanly, the SCM gives up and the install continues, but the service may end up in a bad state. Recovery:

```powershell
# from elevated PowerShell
Stop-Service WebhookServer -Force
# if that fails:
Get-WmiObject Win32_Service -Filter "Name='WebhookServer'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

…then re-run the installer.

### Upgrade from a `deploy.ps1` dev install to an installer-managed install

The first time you run the installer on a machine that previously used `deploy.ps1`, the installer thinks it's doing a fresh install (no `Programs and Features` registry entry). It still detects the existing service and updates it cleanly, so the only visible difference is that **a Programs and Features entry now exists** for "Webhook Server" with `Justin Paul` as publisher. Future upgrades take the proper upgrade path.

### `deploy.ps1` after an installer-managed install

`deploy.ps1` is the dev workflow. It publishes from source and copies binaries to the same install location. Running it on top of an installer-managed install will overwrite the binaries but won't deregister the installer. If you then uninstall via Programs and Features, the uninstaller may leave files behind that `deploy.ps1` introduced. Pick one workflow and stick with it.
