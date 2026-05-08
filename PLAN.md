# Windows Webhook Server — Implementation Plan

## Context

Greenfield project at `/Users/justin/GitHub/webhook-server` (currently empty). Goal: a Windows-native webhook server that receives HTTP requests and runs PowerShell, PowerShell Core, cmd/.bat, or arbitrary executables in response. Each webhook is configured in a desktop GUI; the actual server runs as a Windows Service so it survives reboots and works without anyone logged in. Auth is per-endpoint (HMAC, bearer, or none) so it can sit behind a CI system, a smart-home hub, GitHub webhooks, etc.

> **Note:** Build/test machine must be Windows. The current dev host is macOS — final verification needs a Windows VM or box.

## Architecture summary

Two processes, one config:

```
┌──────────────────┐  named pipe   ┌──────────────────────────────┐
│   WPF GUI app    │ ◄──────────► │  Windows Service              │
│  (config/monitor)│               │  ├─ Kestrel: webhook listener│
└──────────────────┘               │  ├─ Named-pipe admin server  │
                                   │  ├─ Executor pool            │
                                   │  └─ Serilog file logging     │
                                   └──────────────────────────────┘
                                            ▲
                            C:\ProgramData\WebhookServer\
                            ├─ config.json   (DPAPI-encrypted secrets)
                            └─ logs\*.log
```

- **`WebhookServer.Service`** — .NET 8 Worker Service. Hosts an embedded Kestrel `WebApplication` for webhook traffic, plus a named-pipe server for the GUI. Single source of truth.
- **`WebhookServer.Gui`** — WPF (.NET 8) MVVM app. Thin client over the named pipe. Edits endpoints, shows live status/logs, can start/stop the service.
- **`WebhookServer.Core`** — class library shared by both. Config schema, executor abstraction, auth verifiers, IPC contracts, DPAPI helpers.

## Tech choices (locked from clarifying Qs)

| Concern | Choice |
|---|---|
| Stack | C# / .NET 8 + WPF |
| Endpoints | Multiple, each at its own URL slug |
| Auth | Per-endpoint pick: HMAC / bearer token / none |
| Run mode | Service-first; GUI is config/monitor only |
| Script types | Windows PowerShell, PowerShell Core, cmd/.bat, arbitrary exe |
| Data passing | Any combination of: JSON body → stdin, query/headers → env vars, arg template `{{...}}` |
| Response | Per-endpoint sync (exit code + stdout/stderr) or async (202 immediate) |
| GUI ↔ service | Named pipe `\\.\pipe\WebhookServerAdmin` (JSON over line-delimited frames) |
| HTTPS | HTTP default; GUI can bind a cert (.pfx path or cert-store thumbprint) |
| Secrets | DPAPI `LocalMachine` scope — encrypted at rest in `config.json` |
| Concurrency | Parallel by default; per-endpoint "serialize" flag forces a queue |
| IP allowlist | Per-endpoint list of IPs and CIDR subnets; empty = all allowed; checked **before** auth |
| Outbound callbacks | Optional per-endpoint callback URL; service POSTs run result after async runs (and optionally after sync). Pre-configured only — no caller-supplied URLs. |

## Solution layout

```
webhook-server/
├─ WebhookServer.sln
├─ src/
│  ├─ WebhookServer.Core/         (netstandard2.0 or net8.0 class lib)
│  │   ├─ Models/
│  │   │   ├─ EndpointConfig.cs
│  │   │   ├─ AuthMode.cs           (None | Bearer | Hmac)
│  │   │   ├─ ExecutorType.cs       (WindowsPowerShell | PwshCore | Cmd | Executable)
│  │   │   ├─ ResponseMode.cs       (Sync | Async)
│  │   │   ├─ DataPassingOptions.cs (StdinJson, EnvVars, ArgTemplate flags + template string)
│  │   │   ├─ AllowedClient.cs      (single IP or CIDR string, validated)
│  │   │   └─ ServerConfig.cs       (HttpPort, HttpsBinding, TrustedProxies[], AdminToken, Endpoints[])
│  │   ├─ Auth/
│  │   │   ├─ IAuthVerifier.cs
│  │   │   ├─ BearerVerifier.cs
│  │   │   ├─ HmacVerifier.cs        (configurable header + sha1/sha256/sha512)
│  │   │   └─ IpAllowList.cs         (parse + match IP / CIDR, IPv4 + IPv6)
│  │   ├─ Execution/
│  │   │   ├─ IExecutor.cs           (RunAsync(ctx) → ExecutionResult)
│  │   │   ├─ ProcessExecutor.cs     (single impl, varies argv per ExecutorType)
│  │   │   ├─ ArgTemplateRenderer.cs ({{body.x}}, {{header.X-Foo}}, {{query.bar}})
│  │   │   └─ ConcurrencyGate.cs     (per-endpoint SemaphoreSlim when serialize=true)
│  │   ├─ Callbacks/
│  │   │   ├─ CallbackConfig.cs      (Url, Method, AuthMode, Secret, retry/timeout)
│  │   │   ├─ CallbackDispatcher.cs  (background queue, retry w/ exponential backoff + jitter)
│  │   │   └─ CallbackPayload.cs     (runId, endpoint, exitCode, stdout/stderr, timings)
│  │   ├─ Storage/
│  │   │   ├─ ConfigStore.cs         (load/save JSON, atomic write)
│  │   │   └─ DpapiSecret.cs         (Protect/Unprotect LocalMachine)
│  │   └─ Ipc/
│  │       ├─ AdminProtocol.cs       (request/response DTOs)
│  │       └─ PipeSecurityFactory.cs (SYSTEM + Administrators ACL)
│  ├─ WebhookServer.Service/        (.NET 8 Worker)
│  │   ├─ Program.cs                  (Host.CreateApplicationBuilder + UseWindowsService)
│  │   ├─ WebhookHost.cs              (BackgroundService that owns the WebApplication)
│  │   ├─ WebhookRouter.cs            (maps slug → endpoint, runs auth, builds context, dispatches)
│  │   ├─ AdminPipeServer.cs          (BackgroundService listening on the named pipe)
│  │   └─ appsettings.json            (only logging defaults — no endpoint config here)
│  └─ WebhookServer.Gui/             (WPF, .NET 8)
│      ├─ App.xaml(.cs)
│      ├─ MainWindow.xaml(.cs)        (endpoint list + log tail)
│      ├─ Views/EndpointEditor.xaml   (per-endpoint edit form)
│      ├─ Views/ServerSettings.xaml   (port, HTTPS cert, install/uninstall service)
│      ├─ ViewModels/                  (CommunityToolkit.Mvvm)
│      └─ Services/AdminPipeClient.cs (talks to AdminPipeServer)
└─ scripts/
   ├─ install-service.ps1            (sc.exe create WebhookServer binPath= ... start= auto)
   └─ uninstall-service.ps1
```

## Key NuGet packages

- `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Hosting.WindowsServices`
- `Microsoft.AspNetCore.App` framework reference (Kestrel + minimal API routing)
- `System.Security.Cryptography.ProtectedData` (DPAPI)
- `Serilog.AspNetCore`, `Serilog.Sinks.File`, `Serilog.Sinks.Async`
- `CommunityToolkit.Mvvm` (GUI)

## Webhook request flow

1. Kestrel receives `POST /hook/{slug}`.
2. Router looks up endpoint by slug. 404 if missing or disabled.
3. **IP allowlist check** (before any expensive work):
   - Resolve the effective client IP: `HttpContext.Connection.RemoteIpAddress`, **then** if that IP is in the server-level `TrustedProxies` list, take the leftmost entry from `X-Forwarded-For` instead. Default `TrustedProxies` is empty, so by default `X-Forwarded-For` is ignored.
   - If the endpoint's `AllowedClients` list is non-empty and the effective IP doesn't match any entry → **403** (no body, log the rejection).
   - Empty `AllowedClients` = allow all.
4. **Capture raw body bytes** (needed for HMAC). Buffer with `EnableBuffering()` or read to `byte[]`.
5. Run the endpoint's auth verifier:
   - `None` → pass.
   - `Bearer` → compare `Authorization: Bearer <secret>` (constant-time compare).
   - `Hmac` → compute HMAC of raw body with configured algo + secret, compare against configured header (default `X-Hub-Signature-256`). Constant-time compare.
   - Fail → 401.
6. Build `ExecutionContext`: `{ body (string + JsonNode), headers, query, route }`.
7. Acquire concurrency gate (no-op if not serialized).
8. Build `ProcessStartInfo`:
   - `WindowsPowerShell` → `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "<path>"` or `-Command "<inline>"`.
   - `PwshCore` → `pwsh.exe ...` (same flags).
   - `Cmd` → `cmd.exe /c "<command-or-batfile>"`.
   - `Executable` → user-supplied exe path + args.
   - Apply data-passing options (any combination):
     - `StdinJson` → write request body to stdin, close stdin.
     - `EnvVars` → for each query param `WEBHOOK_QUERY_<KEY>=value`; for each header `WEBHOOK_HEADER_<KEY>=value` (sanitize key chars).
     - `ArgTemplate` → render template, append rendered tokens to argv.
9. Dispatch:
   - **Sync mode**: await process exit (with `Timeout` cancellation token). Return `200` (or `502` on non-zero exit, configurable) with body `{ exitCode, stdout, stderr, durationMs }`.
   - **Async mode**: fire-and-forget Task; return `202 Accepted` with `{ runId }`. Output goes to log + visible in GUI.
10. **Outbound callback** (if configured for this endpoint): enqueue a `CallbackPayload` to `CallbackDispatcher`. For async endpoints this is the only way the original caller can learn the result. For sync endpoints it's optional (off by default) and useful for fan-out / audit sinks.
11. Always log: timestamp, slug, effective client IP, IP-allowlist result, auth result, exit code, duration, stdout/stderr (truncated), callback delivery status. Serilog → daily rolling file.

## Argument template syntax

Simple `{{path}}` substitution. Path grammar:
- `{{body.foo.bar}}` — JSON path into body (uses `JsonNode`).
- `{{header.X-GitHub-Event}}` — header by name (case-insensitive).
- `{{query.ref}}` — query param.
- `{{route.slug}}` — route values.

Missing paths render as empty string. Each `{{...}}` becomes one argv token (already-quoted handling done by `ProcessStartInfo.ArgumentList`). No expression evaluation — keep it dumb so it can't be a sandbox escape vector.

## Outbound callbacks

Each endpoint optionally has a `Callback` block. When present, the service POSTs the run result to a pre-configured URL after the script finishes. Required for async endpoints if the caller wants to know what happened; optional for sync endpoints (where the result is already returned in the HTTP response).

`CallbackConfig` fields:

- `Url` — full URL the dispatcher POSTs to.
- `Method` — default `POST` (allow `PUT` for systems that prefer it).
- `AuthMode` — `None` | `Bearer` | `Hmac`. Mirrors the inbound auth design — same code paths reused.
- `Secret` — DPAPI-encrypted (bearer token, or HMAC shared secret).
- `HmacAlgorithm` / `HmacHeaderName` / `HmacPrefix` / `HmacEncoding` — when `AuthMode = Hmac`. Defaults match the inbound HMAC defaults so a sender that accepts GitHub-style signatures works out of the box.
- `TimeoutSeconds` — default `30`.
- `MaxAttempts` — default `5`. Backoff is exponential (1s, 2s, 4s, 8s, 16s) with ±25% jitter.
- `IncludeStdout` / `IncludeStderr` — default `true`. Allow turning off for endpoints whose output is sensitive or huge. Truncation cap (`MaxOutputBytes`, default `64KB`) applies regardless.
- `Trigger` — `OnComplete` (default — fires whether script succeeded or failed) | `OnSuccess` | `OnFailure`.

Payload shape (`application/json; charset=utf-8`):

```json
{
  "runId": "8f4e...",
  "endpoint": "deploy",
  "startedAt": "2026-05-07T18:22:11.103Z",
  "completedAt": "2026-05-07T18:22:13.811Z",
  "durationMs": 2708,
  "exitCode": 0,
  "succeeded": true,
  "stdout": "...",
  "stderr": "",
  "stdoutTruncated": false,
  "stderrTruncated": false
}
```

When `AuthMode = Hmac`, the HMAC is computed over the **raw serialized JSON body bytes** with the configured algorithm and added as the configured header (e.g. `X-Hub-Signature-256: sha256=...`).

`CallbackDispatcher`:

- Single `BackgroundService` with a bounded `Channel<CallbackPayload>` queue (default capacity 1024; overflow drops oldest with a warning log).
- Uses a singleton `HttpClient` (no per-request allocation, follows redirects only on 3xx-with-Location for safety).
- Per-attempt timeout = `TimeoutSeconds`. Total deadline = `MaxAttempts * (TimeoutSeconds + max-backoff)`.
- Retries on network failure, 408, 425, 429, 5xx. Honors `Retry-After` if present, capped at 60s.
- All attempts logged: outbound URL, status code, attempt number, latency, final disposition (`delivered` / `dropped` / `gave-up`).
- GUI surfaces a per-endpoint counter: pending / delivered / failed in last hour.

**No caller-supplied callback URLs.** The endpoint config is the only source. Accepting a `?callback=` parameter from the request would turn the server into an SSRF gadget — easy to point at internal admin endpoints or cloud-metadata services. Callers who need dynamic fan-out can configure multiple endpoints, or run a small dispatcher script themselves.

## IP allowlist details

Each endpoint has an `AllowedClients: string[]` field. Each entry is one of:

- Single IPv4 address — `192.168.50.20`
- Single IPv6 address — `fe80::1`
- IPv4 CIDR — `10.10.1.0/24`
- IPv6 CIDR — `fd00::/8`

Empty list = allow all (matches the user's default-open requirement). Any non-empty list switches that endpoint to deny-by-default.

Implementation:

- `IpAllowList` parses entries on config load; validation errors are surfaced in the GUI before save.
- Use `System.Net.IPNetwork` (added in .NET 8) for CIDR parse + `Contains(IPAddress)` matching. No third-party lib needed.
- Match IPv4-mapped IPv6 (`::ffff:1.2.3.4`) against IPv4 entries by normalizing with `IPAddress.MapToIPv4()` before the check.
- A server-level `TrustedProxies` list (also IPs/CIDRs) controls whether `X-Forwarded-For` / `X-Real-IP` is honored. If the direct connection comes from a trusted proxy, walk `X-Forwarded-For` from rightmost-trusted leftward and use the first untrusted IP. Otherwise ignore forwarded headers — important so callers can't spoof their IP by adding a header.
- Default `TrustedProxies` is empty (most secure). GUI surfaces it under server settings with a note about reverse-proxy setups.
- Rejections are logged and counted per-endpoint so the GUI can surface "blocked: 47 in last hour" — useful for catching misconfigured callers.

Order in the request pipeline matters: **IP check runs before auth.** That avoids HMAC compute work on blocked IPs and prevents any timing-based information leak about token validity to non-allowed sources.

## Auth details

- **Bearer**: secret is the token itself. Verify `Authorization: Bearer <secret>`. Fixed-time `CryptographicOperations.FixedTimeEquals`.
- **HMAC**: configurable per endpoint:
  - `Algorithm`: SHA1 / SHA256 (default) / SHA512
  - `HeaderName`: default `X-Hub-Signature-256`
  - `Prefix`: default `sha256=` (stripped before compare)
  - `Encoding`: hex (default) or base64
  - Compute HMAC over raw body bytes with secret. Fixed-time compare.

This covers GitHub, Stripe, Slack, generic CI patterns by tweaking the four fields.

## Service account

The service itself runs fine under any account — this section is about which account makes sense for the **scripts** the service launches, since they inherit its identity.

| Account | Network identity | When to use |
|---|---|---|
| `LocalSystem` (default) | Computer account `DOMAIN\MACHINE$` on a domain-joined host; nothing on a workgroup host | Default. Local-only scripts, or read-only AD queries on a domain-joined machine. Most powerful local account — any webhook script effectively runs as SYSTEM. |
| `LocalService` | None — no network credentials | **Don't.** Cannot talk to AD or any other remote resource that requires Windows auth. Listed only to rule it out. |
| `NetworkService` | Computer account, same as LocalSystem | Slightly less local privilege than LocalSystem; same network identity. Rarely worth the switch. |
| Domain user (`DOMAIN\svc-webhookserver`) | That user | Need write/admin operations against AD (password resets, group changes, OU creates). You own password rotation. |
| **gMSA** (`DOMAIN\svc-webhookserver$`) | That gMSA | **Recommended for AD-write workloads.** AD generates and rotates the password automatically. Requires domain functional level 2012+ and `Install-ADServiceAccount` on the host. |

Install commands by account type:

```powershell
# LocalSystem (default)
sc.exe create WebhookServer binPath= "C:\path\WebhookServer.Service.exe" start= auto

# Domain user
sc.exe create WebhookServer binPath= "..." obj= "DOMAIN\svc-webhookserver" password= "..." start= auto

# gMSA — note the trailing $ and no password=
sc.exe create WebhookServer binPath= "..." obj= "DOMAIN\svc-webhookserver$" start= auto
```

`scripts/install-service.ps1` will accept a `-ServiceAccount` parameter that defaults to `LocalSystem` and accepts a domain user or gMSA name. README will document the gMSA setup once for users who need AD writes from their hooks.

The service code itself makes no assumptions about the account — DPAPI uses `LocalMachine` scope so secret decryption works under any local identity.

## Secret storage (DPAPI)

Endpoint `Secret` is stored in JSON as `{ "encrypted": "<base64 of ProtectedData.Protect(utf8(secret), null, LocalMachine)>" }`. Decrypt only inside the service when needed. The GUI submits secrets in plaintext over the named pipe (local-machine, ACL-restricted), service encrypts before writing.

Caveat to call out in the GUI: DPAPI `LocalMachine` ties config to the machine — backing up config to another box won't decrypt. Document `Export Config` in GUI later as a future feature.

## GUI ↔ Service IPC

Named pipe `\\.\pipe\WebhookServerAdmin`, ACL: `NT AUTHORITY\SYSTEM` + `BUILTIN\Administrators` full control, deny everyone else. (GUI must run elevated — note that in the README.)

Protocol: line-delimited JSON request/response.

```jsonc
// request
{ "op": "list-endpoints" }
// response
{ "ok": true, "endpoints": [...] }
```

Ops needed: `get-config`, `update-config`, `list-endpoints`, `create-endpoint`, `update-endpoint`, `delete-endpoint`, `enable/disable-endpoint`, `tail-logs` (streaming), `get-status`, `bind-https`, `restart-listener`.

## HTTPS binding

GUI offers two ways to provide a cert:
1. Path to `.pfx` + password (password DPAPI-encrypted in config).
2. Local cert-store thumbprint (`CurrentUser\My` or `LocalMachine\My`).

Service builds Kestrel with both an HTTP and HTTPS endpoint when bound. Restarting the listener picks up new bindings without restarting the whole service.

## Critical files to be created

- `src/WebhookServer.Core/Models/EndpointConfig.cs` — central data shape
- `src/WebhookServer.Core/Auth/HmacVerifier.cs` — must use raw body + fixed-time compare
- `src/WebhookServer.Core/Auth/IpAllowList.cs` — IPv4/IPv6 + CIDR matching, IPv4-mapped IPv6 normalization, runs before auth
- `src/WebhookServer.Core/Execution/ProcessExecutor.cs` — argv assembly per `ExecutorType`, stdin/env wiring, timeout
- `src/WebhookServer.Core/Storage/DpapiSecret.cs` — `LocalMachine` scope, base64 wire format
- `src/WebhookServer.Core/Ipc/PipeSecurityFactory.cs` — correct ACL or the GUI will fail silently for non-admins
- `src/WebhookServer.Service/WebhookHost.cs` — Kestrel setup with `EnableBuffering()` for body capture
- `src/WebhookServer.Service/WebhookRouter.cs` — auth → context → dispatch pipeline
- `src/WebhookServer.Core/Callbacks/CallbackDispatcher.cs` — bounded queue + retry/backoff; reuses HMAC code from `Auth/HmacVerifier.cs` for outbound signing
- `src/WebhookServer.Gui/Views/EndpointEditor.xaml` — the main UX surface; must make adding a "run this script when called" hook feel obvious

## Verification (end-to-end)

Run on a Windows machine after `dotnet publish -c Release`:

1. **Install service**:
   `sc.exe create WebhookServer binPath= "C:\path\to\WebhookServer.Service.exe" start= auto`
   `sc.exe start WebhookServer`
2. **Launch GUI** (as Administrator). Confirm it connects to the pipe; the status indicator should show "running".
3. **Smoke test (no auth, sync, PowerShell inline)**:
   - Endpoint slug `/hook/ping`, executor PowerShell, command `'pong'`.
   - `curl http://localhost:8080/hook/ping` → body contains `pong`, exit code 0.
4. **Bearer auth**:
   - Set bearer secret `s3cret`.
   - `curl http://localhost:8080/hook/ping` → 401.
   - `curl -H "Authorization: Bearer s3cret" http://localhost:8080/hook/ping` → 200.
5. **HMAC auth (GitHub-style)**:
   - Algo SHA256, header `X-Hub-Signature-256`, prefix `sha256=`, secret `topsecret`.
   - `BODY='{"x":1}'; SIG=$(printf %s "$BODY" | openssl dgst -sha256 -hmac topsecret -hex | awk '{print $2}')`
   - `curl -H "X-Hub-Signature-256: sha256=$SIG" -d "$BODY" http://localhost:8080/hook/foo` → 200.
   - Wrong sig → 401.
6. **Stdin JSON + arg template**:
   - PowerShell script: `$j = $input | ConvertFrom-Json; "got repo=$($args[0]) name=$($j.name)"`
   - Arg template: `{{body.repo}}`. POST `{"repo":"acme","name":"bob"}` → output contains `got repo=acme name=bob`.
7. **Env-var passing**:
   - cmd one-liner: `echo event=%WEBHOOK_HEADER_X_GITHUB_EVENT%`
   - `curl -H "X-GitHub-Event: push" ...` → output contains `event=push`.
8. **Async endpoint**:
   - PowerShell: `Start-Sleep 10; "done"`. Mode = Async.
   - `curl ...` returns 202 immediately. GUI log shows `done` ~10s later.
9. **Concurrency**:
   - Endpoint with serialize=true and a `Start-Sleep 5` script. Fire 3 parallel curls → durations ~5s, ~10s, ~15s.
9a. **IP allowlist**:
   - Set `AllowedClients = ["127.0.0.1"]`. `curl http://localhost:8080/hook/ping` → 200; from another machine on LAN → 403.
   - Set `AllowedClients = ["10.10.1.0/24"]`. Request from `10.10.1.42` → 200; from `10.10.2.5` → 403.
   - Empty list → all IPs allowed (regression check).
   - With `TrustedProxies` empty, send `X-Forwarded-For: 127.0.0.1` from a non-allowed IP → still 403 (header not trusted).
   - With `TrustedProxies = ["10.10.1.5"]` and request coming from that IP carrying `X-Forwarded-For: 192.168.50.20`, allowlist `["192.168.50.20"]` → 200.
9b. **Outbound callback (async + HMAC-signed)**:
   - Stand up a tiny receiver: `python -m http.server 9000` won't verify HMAC, so use a one-off script that prints the body and signature. Or another endpoint on this same server.
   - Configure an async endpoint with callback: `Url = http://localhost:9000/sink`, `AuthMode = Hmac`, secret `cbsecret`.
   - Trigger the webhook. After the script finishes, the receiver should see a POST with body `{ runId, exitCode, stdout, ... }` and `X-Hub-Signature-256: sha256=<hmac>`.
   - Verify HMAC matches by recomputing with the secret — this proves outbound HMAC reuses the inbound code path correctly.
   - Stop the receiver, fire another async run → callback should retry with exponential backoff and eventually log `gave-up` after `MaxAttempts`.
   - Bring receiver back up before `MaxAttempts` exhausts → next retry delivers successfully.

10. **HTTPS**:
    - In GUI, bind a self-signed `.pfx` (`New-SelfSignedCertificate ... -CertStoreLocation Cert:\LocalMachine\My`).
    - `curl -k https://localhost:8443/hook/ping` → 200.
11. **Reboot test**:
    - Reboot. Verify service auto-starts and an existing endpoint still answers without launching the GUI.
12. **Permissions test**:
    - Run GUI as a non-admin user → should fail to connect to the named pipe with a clear error (not a hang).

## Out of scope for v1 (call out in README)

- Importing/exporting config across machines (DPAPI-LocalMachine prevents this).
- Outbound webhook delivery / retry queues.
- Per-endpoint rate limiting.
- Multi-user RBAC for the GUI.
- Auto-update.
