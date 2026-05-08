# Concepts

If you've never used a webhook before, this is where to start. Five minutes, no surprises.

## What is a webhook?

A webhook is just **an HTTP URL that runs something when it gets called.** Some other tool — Zerto, GitHub, your monitoring system, a backup job — does an `HTTP POST` to that URL when an event happens. Whatever's listening on the URL takes that request and does work in response.

Concretely, a Zerto pre-script might do:

```powershell
Invoke-WebRequest -Method POST -Uri http://webhooks.contoso.local:8080/hook/start-failover `
    -Body (@{ vmName = $env:ZertoVPGName } | ConvertTo-Json) `
    -ContentType application/json
```

…and the server at `webhooks.contoso.local:8080` would receive that POST and run a PowerShell script you wrote.

## What does this server give you that you don't already have?

You could write a tiny ASP.NET listener, or run a PowerShell script behind IIS, or hand-craft `HttpListener` plumbing. People do, all the time. The trade-off is that **you then own the listener** — auth, retries, logging, restarts, a service wrapper, secret storage, an admin UI. That's where Webhook Server saves you a weekend.

What you get out of the box:

- A real **Windows Service** that survives reboots and runs without anyone logged in
- Per-endpoint **authentication**: Bearer token, HMAC-signed (GitHub / Stripe / Slack style), or none
- Per-endpoint **IP allowlist** (single IPs or CIDR ranges)
- **Run-as identity**: the service runs as `LocalSystem` by default, but each individual hook can run as a domain account, the logged-in user, or whoever — without needing Task Scheduler in the middle
- **Logging** (Serilog, daily-rolling files) plus a GUI tail
- A WPF **GUI** for adding / editing / testing endpoints. No JSON file editing required.
- **Outbound callbacks**: when a hook finishes, the server can POST the result to another URL, signed with HMAC, with retry-and-backoff
- **HTTPS** via `.pfx` or a cert thumbprint from the local cert store
- **Auto-snapshots** of your config on every save, with point-in-time restore from the GUI

## How the moving parts fit together

```
+------------------+   named pipe   +-------------------------------+
|   GUI (WPF)      | <------------> |  Windows Service              |
|  add / edit /    |  SYSTEM+admin  |  - Kestrel: hook listener     |
|  view logs       |  ACL'd         |  - Admin pipe server          |
+------------------+                |  - Executor (process runner)  |
                                    |  - Callback dispatcher        |
                                    |  - Serilog file logging       |
                                    +-------------------------------+
                                              |
                                  C:\ProgramData\WebhookServer\
                                  - config.json   (DPAPI-encrypted secrets)
                                  - backups\      (auto-snapshots)
                                  - logs\         (daily rolling)
```

- The **Windows Service** does the actual work: listens for HTTP requests, runs your scripts, writes logs.
- The **GUI** is purely a config + monitoring tool. It talks to the service over a named pipe ACL'd to `SYSTEM` and `Administrators`. You can launch and close the GUI as you like; the service keeps running.
- **Config + secrets** live in `C:\ProgramData\WebhookServer\config.json`. Secrets (bearer tokens, HMAC keys, run-as passwords, PFX passwords) are DPAPI-encrypted with the `LocalMachine` scope, so the same machine can decrypt them under any account but they don't travel to other machines.

## What's an "endpoint"?

An endpoint is one URL slug (the part after `/hook/`) plus a configuration: who's allowed to call it, how it's authenticated, what to run when it fires, and what to do with the result. Add as many as you want.

| Field | What it controls |
|---|---|
| **Slug** | The URL path. `deploy` → `http://host:8080/hook/deploy` |
| **Auth** | None / Bearer / HMAC. None means anyone who can reach the URL can fire it. |
| **Allowed clients** | List of IPs or CIDRs allowed to hit this slug. Empty = anyone reachable. |
| **Executor** | What to run: Windows PowerShell 5.1, PowerShell Core (7+), `cmd` / `.bat`, or a path to any `.exe` |
| **Run As** | Who the script runs as. See [Run As modes](runas-modes.md). |
| **Data passing** | How request data reaches the script — JSON to stdin, headers / query as env vars, `{{template}}` arg expansion |
| **Response mode** | Sync (the HTTP caller waits for the script to finish and gets its output) or Async (returns 202 immediately, runs in background) |
| **Callback** | Optional outbound URL the server POSTs to with the run result. Required for async hooks if the original caller wants the result. |

## What it isn't

- **Not an HTTP server for serving static files or pages.** Just hook URLs and a `/healthz`.
- **Not a queue.** No durable persistence of inbound requests; if the service crashes mid-execution that run is lost (the inbound caller will see the connection drop or a timeout).
- **Not multi-tenant.** It's one config, one set of endpoints, one machine. Run multiple instances on different ports / different machines if you need separation.
- **Not an internet-facing public-API server out of the box.** Lock down with HTTPS + auth + IP allowlist + a reverse proxy if you're going to expose it publicly. See [network & security](network-and-security.md).
