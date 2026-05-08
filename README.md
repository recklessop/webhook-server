# Webhook Server

A Windows-native webhook server that runs PowerShell, cmd / `.bat`, or any executable in response to incoming HTTP requests. Endpoints are configured in a desktop GUI; the actual server runs as a Windows Service so it survives reboots and works without anyone logged in.

Designed for sysadmins who want to wire up tools like **Zerto pre/post scripts**, GitHub webhooks, monitoring alerts, or backup jobs to Windows-side automation — without writing a custom listener every time.

## Quickstart

1. **Download** the latest installer: <https://github.com/recklessop/webhook-server/releases/latest>
2. **Run it.** UAC accept → next, next, finish. Adds a Start Menu entry, registers and starts the Windows Service.
3. **Open Webhook Server** from the Start Menu (auto-elevates).
4. **File → New endpoint**, configure a slug + script, save, hit the URL.

Full first-time walkthrough: [docs/installation.md](docs/installation.md)

## Highlights

- **Many endpoints, one service.** Each webhook is a configured URL slug mapped to a script or command.
- **Per-endpoint auth** — HMAC signature (GitHub / Stripe / Slack style), bearer token, or none.
- **Per-endpoint IP allowlist.** Restrict by IP or CIDR. Empty list = open. Checked before auth so blocked IPs get a fast 403.
- **Per-endpoint Run As** — run the hook as the service account (default), the user logged in at the keyboard (for UI hooks), or a named domain/local user via password.
- **Flexible execution.** Windows PowerShell 5.1, PowerShell 7+, cmd / `.bat`, or any `.exe`.
- **Flexible input** — any combination of: JSON body to stdin, query / headers as env vars, `{{body.foo.bar}}` template expansion into argv.
- **Sync or async per endpoint.** Sync returns exit code + stdout / stderr to the caller; async returns 202 immediately.
- **Outbound callbacks.** Optional per-endpoint URL the service POSTs run results to after the script finishes. HMAC-signed, retry-with-backoff. Required for async callers who want to know what happened.
- **Configurable network** — bind to specific NICs, set the URL host shown in the GUI, configure trusted reverse proxies.
- **HTTPS optional.** Bind a `.pfx` or cert-store thumbprint from the GUI.
- **Secrets at rest** — bearer tokens, HMAC keys, RunAs passwords, and PFX passwords are DPAPI-encrypted (LocalMachine scope) in `config.json`.
- **Auto-snapshots.** Every config save writes a Config Checkpoint; restore to any point with one click. Last 30 retained.

## Architecture

```
+------------------+   named pipe    +-------------------------------+
|   GUI (WPF)      | <-------------> |  Windows Service              |
|  add / edit /    |  SYSTEM+admin   |  - Kestrel: hook listener     |
|  view logs       |  ACL'd          |  - Admin pipe server          |
+------------------+                 |  - Executor (process runner)  |
                                     |  - Callback dispatcher        |
                                     |  - Serilog file logging       |
                                     +-------------------------------+
                                                 |
                                     C:\ProgramData\WebhookServer\
                                       - config.json   (DPAPI-encrypted)
                                       - backups\      (auto-snapshots)
                                       - logs\         (daily rolling)
```

## Documentation

Everything you need to operate the server:

- [Concepts](docs/concepts.md) — what a webhook is and how this server uses one
- [Installation](docs/installation.md) — interactive and silent install
- [Upgrading](docs/upgrading.md) — single click; what's preserved
- [Uninstalling](docs/uninstalling.md) — clean removal
- [Run As modes](docs/runas-modes.md) — Service / InteractiveUser / SpecificUser
- [Service account & Active Directory](docs/service-account-and-ad.md) — gMSA + delegated rights
- [Network & security](docs/network-and-security.md) — bind addresses, allowlists, HTTPS, secrets
- [Troubleshooting](docs/troubleshooting.md) — common errors and where to look

Recipes:

- [Zerto pre/post scripts → AD / DNS update](docs/recipes/zerto-pre-post-scripts.md) ← **canonical use case**
- [GitHub-style HMAC-signed webhook](docs/recipes/github-style-hmac.md)
- [AD password reset endpoint](docs/recipes/ad-password-reset.md)
- [Pop UI on the user's desktop](docs/recipes/ui-on-desktop.md)

## Requirements

- Windows 10 / 11 / Server 2019+
- x64
- .NET 8 SDK to build (the released installer includes everything else)

## Building from source

```powershell
git clone https://github.com/recklessop/webhook-server.git
cd webhook-server

# Dev install (publishes + copies to C:\Program Files\WebhookServer + registers service)
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1

# Or build the installer locally (requires Inno Setup 6: winget install JRSoftware.InnoSetup)
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

## License

TBD.
