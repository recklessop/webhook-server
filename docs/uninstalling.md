# Uninstalling

## TL;DR

**Settings → Apps & features → Webhook Server → Uninstall.** Or right-click the **Uninstall Webhook Server** Start Menu shortcut.

Your endpoints, secrets, and logs in `C:\ProgramData\WebhookServer\` are preserved by default. To wipe those too, see [Below](#wiping-config-and-logs-too).

## What the uninstaller does

In order:

1. **Stops the service** (`net stop WebhookServer`).
2. **Removes the service** registration via `uninstall-service.ps1` (which calls `sc.exe delete WebhookServer`).
3. **Deletes** `C:\Program Files\WebhookServer\`.
4. **Removes** the Start Menu and (if created) Desktop shortcuts.
5. **Removes** the Programs and Features entry.

What it **does not** touch:

- `C:\ProgramData\WebhookServer\` (config, secrets, log files, auto-snapshots)
- Any cert in your local cert store you bound HTTPS to
- Domain accounts / gMSAs the service ran under
- Endpoints' deployed scripts, if you stored them outside the install dir

## Wiping config and logs too

After running the uninstaller, also remove the data root:

```powershell
# from elevated PowerShell
Remove-Item -Recurse -Force "$env:ProgramData\WebhookServer"
```

This deletes:

- `config.json` (with all your endpoints, encrypted secrets, settings)
- `backups\` (all auto-snapshots — you can't restore from these once gone)
- `logs\` (history of every webhook hit)

There's no recovery from this. If you might want to reinstall later with the same configuration, copy `config.json` to a safe location first. Note that **secrets in the saved config can only be decrypted on the same machine** (DPAPI LocalMachine scope) — you can move the file but the bearer/HMAC/RunAs passwords inside become unrecoverable on a different host.

## Silent uninstall

The Programs and Features uninstaller is `unins000.exe` in the install directory:

```powershell
# from elevated PowerShell
& "C:\Program Files\WebhookServer\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Same set of preserved/removed paths as the interactive flow.

## Removing only the service, keeping the binaries

If you want to keep the GUI installed but stop running the service (rare, but useful if you're testing):

```powershell
# from elevated PowerShell
sc.exe stop WebhookServer
sc.exe delete WebhookServer
```

The GUI will show **Disconnected** since there's no service to talk to. Re-create the service later by running `install-service.ps1`:

```powershell
& "C:\Program Files\WebhookServer\scripts\install-service.ps1" `
    -BinaryPath "C:\Program Files\WebhookServer\WebhookServer.Service.exe"
```

## Edge cases

### "The service cannot be stopped because it has not been started."

Harmless. The uninstaller proceeds regardless.

### "Cannot delete: file in use"

A GUI window or other process is holding files in `C:\Program Files\WebhookServer\` open. Close everything and re-run the uninstaller. If that fails, reboot and re-run.

### Programs and Features entry remains after files are gone

If you deleted `C:\Program Files\WebhookServer\` manually before running the uninstaller, `unins000.exe` is gone too and Programs and Features can't run it. Remove the orphan entry by deleting its registry key:

```powershell
# from elevated PowerShell - dry run to confirm the key exists
Get-Item 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E3B3C1A-9C20-4F50-B6A8-2B6D6D7E2F11}_is1' -ErrorAction SilentlyContinue
# if it shows up, delete it:
Remove-Item 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E3B3C1A-9C20-4F50-B6A8-2B6D6D7E2F11}_is1' -Recurse
```
