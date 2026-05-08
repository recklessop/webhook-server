# Troubleshooting

This page indexes the most common ways things go wrong, where to look, and what to do.

## Where to look first

| Symptom | First check |
|---|---|
| GUI shows "Disconnected" | Service running? `Get-Service WebhookServer` |
| Hook returns 404 | Slug typo, or endpoint disabled |
| Hook returns 401 | Auth header / signature mismatch |
| Hook returns 403 | IP allowlist denies the caller |
| Hook returns 200 but nothing happens | Response is the script's stdout — check exit code, stderr |
| Hook returns 502 | Script ran and exited non-zero. Body has stderr. |
| Hook returns 500 | Launch error (script not found, invalid path) |
| Hook hangs | Timeout reached, or script is waiting on stdin |
| Calc / UI doesn't appear despite InteractiveUser | See [Run As modes](runas-modes.md) — common pitfalls |

## Where the logs are

`C:\ProgramData\WebhookServer\logs\webhook-YYYYMMDD.log` — daily rolling, 14-day retention by default.

Every webhook run logs:
- `[INF] Run <id> <slug> ok exit=0 dur=<ms>ms stdout=...` on success
- `[WRN] Run <id> <slug> non-zero exit=<n> dur=<ms>ms stdout=... stderr=...` on script failure
- `[WRN] Run <id> <slug> failed to launch: <reason>` on launch failure
- `[WRN] Run <id> <slug> timed out after <s>s; process killed` on timeout

The GUI's bottom panel auto-refreshes the same log file every 3 seconds. Tick the **Auto-scroll** checkbox to keep it pinned to the latest line.

## Common issues

### "Disconnected: Access to the path is denied" right after install

You launched the GUI without elevation. The admin pipe ACL is `SYSTEM` + `Administrators`-full-control; UAC token splitting denies the standard token.

**Fix in v0.1.1+**: nothing — the GUI's manifest is `requireAdministrator` and Start Menu / shortcut launches auto-elevate.

**Fix in v0.1.0**: right-click the Start Menu shortcut → **Run as administrator**, or upgrade.

### "Connection refused" hitting the hook URL

Three possibilities, in order of probability:

1. **Service stopped.** `Get-Service WebhookServer` and `Start-Service WebhookServer` if needed.
2. **Wrong port.** Default is 8080. Check **Server → Settings → HTTP port** in the GUI, or `netstat -an | findstr :8080`.
3. **Bound to a specific NIC and you're calling on another.** Check **Server → Settings → Listen on**. If "Listen on all interfaces" is unchecked and you only ticked LAN IPs, calls to `localhost` may fail. Tick `127.0.0.1` too.

### Hook works from `localhost` but not from another machine on the LAN

Windows Firewall. The installer doesn't add a firewall rule (intentional — you should choose your scope). Add one:

```powershell
# from elevated PowerShell on the webhook host
New-NetFirewallRule -DisplayName "Webhook Server HTTP 8080" -Direction Inbound `
    -Action Allow -Protocol TCP -LocalPort 8080 -Profile Domain,Private
```

Use `-Profile Public` only if you really mean it. Better: front the server with a reverse proxy and don't expose 8080 directly.

### `[WRN] Run … failed to launch: launch error: An error occurred trying to start process 'X'. Access is denied.`

Likely **SpecificUser mode + `psi.UserName`** failure. Should be impossible in v0.1.1+ (we use `LogonUser` + `CreateProcessAsUser` directly). If you see this on v0.1.1, double-check the version: `Get-Item "C:\Program Files\WebhookServer\WebhookServer.Service.exe" | % VersionInfo`.

### `[WRN] Run … failed to launch: LogonUser (DOMAIN\user) failed`

The credentials don't authenticate. Common causes:

- Typo in the password (paste it back into the GUI to verify; the field is plaintext for an admin user)
- Account locked / disabled / expired
- The account is denied the right logon types — check `secpol.msc` → Local Policies → User Rights Assignment → "Deny logon as a batch job" / "Deny logon locally"
- For domain accounts: the host can't reach a DC

### `non-zero exit=-1073741502` (`0xC0000142` STATUS_DLL_INIT_FAILED)

The new process couldn't initialize. With **InteractiveUser** mode this means we tried to open `winsta0\default` and the user's session token doesn't have access (e.g., no one's logged in). With **SpecificUser** this should not occur in v0.1.1+ — we deliberately don't set lpDesktop for that mode.

### Hook returns 502 with empty stdout/stderr

The script's exit was non-zero but it didn't print anything. PowerShell's `$ErrorActionPreference = 'Stop'` is your friend — turn it on at the top of the script and any cmdlet failure becomes terminating with a clear message in stderr.

### "ServiceState: ListenerSettingsChanged" → service restart

After saving Server Settings with a port or HTTPS change, the service stops itself so the SCM restarts it on the new bindings. The GUI briefly shows "Disconnected" then reconnects. If it doesn't reconnect within ~10 seconds:

```powershell
Get-Service WebhookServer | Format-List Status, StartType
```

If the service is in `Stopped`, the SCM didn't restart it (failure-recovery only kicks in on *abnormal* termination, and a clean stop doesn't qualify). Manual:

```powershell
Start-Service WebhookServer
```

### GUI editor changes don't seem to take effect

After saving an endpoint, the service loads the new config in memory immediately — no restart needed. If a hook is mid-run when you save, that run finishes against the OLD config; the new config applies to subsequent runs.

If the GUI's grid still shows old values, hit any other endpoint or wait for the 3-second poll to refresh the display.

### Tray icon doesn't appear

Check whether the GUI is running: `Get-Process WebhookServer.Gui`. If not, the tray icon doesn't exist (it's part of the GUI process). To have a persistent tray independent of the main window, leave the GUI running and minimize it — it'll hide-to-tray rather than truly close.

To run the GUI minimized at login: create a Windows shortcut to `WebhookServer.Gui.exe`, set "Run" to "Minimized" in the shortcut properties, and put it in your user's Startup folder (`shell:startup`). The auto-elevate manifest still takes effect.

## Getting useful logs from a script

Inside your hook scripts, write to stderr for diagnostic info — Webhook Server logs stderr separately from stdout, and stderr is preserved even on success:

```powershell
[Console]::Error.WriteLine("processing item $i of $total")
```

Or use `Write-Error` which produces non-fatal errors:

```powershell
Write-Error "skipping bogus input"   # stderr but doesn't terminate
```

The full stderr appears in the log line for the run, plus in the response body for sync calls.

## Asking for help

If you're stuck, file an issue at:

> https://github.com/recklessop/webhook-server/issues

Include:

- Webhook Server version (Help → About, or the file version of the `.exe`)
- Windows version (`winver`)
- The slug + relevant bits of the endpoint config (NOT the secrets)
- The log lines for the failing run (search for the runId)
- What you expected vs. what happened
