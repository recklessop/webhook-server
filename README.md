# webhook-server

A Windows-native webhook server that runs PowerShell, PowerShell Core, cmd / `.bat`, or arbitrary executables in response to incoming HTTP requests. Endpoints are configured in a desktop GUI; the actual server runs as a Windows Service so it survives reboots and works without anyone logged in.

**Status:** planning complete, implementation pending. See [PLAN.md](PLAN.md) for the full design.

## Highlights

- **Many endpoints, one service.** Each webhook is a configured URL slug mapped to a script or command.
- **Per-endpoint auth.** Pick HMAC signature (GitHub/Stripe-style), bearer token, or none.
- **Per-endpoint IP allowlist.** Restrict by IP or CIDR (IPv4 + IPv6). Empty list = open. Checked before auth.
- **Flexible execution.** Windows PowerShell 5.1, PowerShell 7+, cmd / `.bat`, or any `.exe`.
- **Flexible input.** Any combination of: JSON body to stdin, query/headers as env vars, `{{template}}` arg expansion.
- **Sync or async per endpoint.** Sync returns exit code + stdout/stderr; async returns 202 immediately.
- **Outbound callbacks.** Optional per-endpoint callback URL — service POSTs the run result back after the script finishes. Required for async callers who want to know what happened. HMAC-signed, retried with backoff. Pre-configured only (no SSRF surface from caller-supplied URLs).
- **Service-first.** Always-on Windows Service. The WPF GUI is a thin config/monitor client over a named pipe.
- **HTTPS optional.** Bind a `.pfx` or cert-store thumbprint from the GUI; HTTP works out of the box.
- **Secrets at rest.** Tokens and HMAC secrets are encrypted via DPAPI (LocalMachine scope) in `config.json`.

## Architecture

```
+------------------+  named pipe   +------------------------------+
|   WPF GUI app    | <----------> |  Windows Service              |
|  (config/monitor)|               |  - Kestrel: webhook listener |
+------------------+               |  - Named-pipe admin server   |
                                   |  - Executor pool             |
                                   |  - Serilog file logging      |
                                   +------------------------------+
                                            ^
                            C:\ProgramData\WebhookServer\
                            - config.json   (DPAPI-encrypted secrets)
                            - logs\*.log
```

## Project layout (planned)

```
WebhookServer.sln
src/
  WebhookServer.Core/      class lib: models, auth, execution, storage, IPC
  WebhookServer.Service/   .NET 8 Worker Service (hosts Kestrel + admin pipe)
  WebhookServer.Gui/       WPF (.NET 8) MVVM config/monitor client
scripts/
  install-service.ps1
  uninstall-service.ps1
```

## Requirements

- Windows 10 / 11 or Windows Server 2019+
- .NET 8 SDK to build, .NET 8 Runtime (or self-contained publish) to run
- Administrator rights to install the service and to run the GUI (the admin named pipe is ACL'd to SYSTEM + Administrators)

## Building (on Windows)

```powershell
dotnet restore
dotnet build -c Release
dotnet publish src/WebhookServer.Service -c Release -r win-x64 --self-contained
dotnet publish src/WebhookServer.Gui     -c Release -r win-x64 --self-contained
```

## Installing the service (on Windows)

```powershell
# from an elevated PowerShell prompt
sc.exe create WebhookServer binPath= "C:\Program Files\WebhookServer\WebhookServer.Service.exe" start= auto
sc.exe start  WebhookServer
```

`scripts/install-service.ps1` will wrap this once implemented and will accept a `-ServiceAccount` parameter.

## Service account & Active Directory

The service runs as `LocalSystem` by default — fine for local-only scripts and read-only AD queries (it authenticates to the domain as the computer account). If your webhook scripts need to **modify** AD (password resets, group changes, etc.), run the service under an account with the right delegated rights:

- **Recommended: gMSA** — Active Directory generates and rotates the password automatically.
  ```powershell
  # on a DC, once
  New-ADServiceAccount -Name svc-webhookserver -DNSHostName host.domain.local `
      -PrincipalsAllowedToRetrieveManagedPassword "DOMAIN\WebhookHosts"
  # on the webhook host
  Install-ADServiceAccount svc-webhookserver
  sc.exe create WebhookServer binPath= "..." obj= "DOMAIN\svc-webhookserver$" start= auto
  ```
  Note the trailing `$` and the absence of `password=`.

- **Plain domain user** — works on older domains, but you own password rotation:
  ```powershell
  sc.exe create WebhookServer binPath= "..." obj= "DOMAIN\svc-webhookserver" password= "..." start= auto
  ```

Don't use `LocalService` — it has no network identity and cannot talk to a domain controller.

> Heads up: any account the service runs under is the account your hook scripts run under. `LocalSystem` is the most powerful local account on the machine — treat webhook script contents as privileged.

## Configuration

The service reads `C:\ProgramData\WebhookServer\config.json`. Edit it through the GUI rather than by hand — the GUI handles DPAPI encryption of secrets and validation of IP allowlist entries.

## Out of scope for v1

- Importing/exporting config across machines (DPAPI LocalMachine scope ties decryption to the host).
- Per-endpoint rate limiting.
- Multi-user RBAC for the GUI.
- Auto-update.

## License

Not yet chosen.
