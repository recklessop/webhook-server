# Network & security

This page covers what's exposed by Webhook Server, how to lock it down, and what's safe to change vs. leave alone.

## What's listening

By default the service binds Kestrel to **all interfaces on TCP 8080**. There are two endpoints relevant to outsiders:

- `GET|POST /hook/<slug>` — fires a configured endpoint
- `GET /healthz` — returns `{"ok": true}` for monitoring
- `GET /favicon.ico` — returns 204 to keep browser logs clean

Plus the admin named pipe `\\.\pipe\WebhookServerAdmin`, which is **only available locally** to processes running as SYSTEM or in the Administrators group.

## Reducing the network exposure

### Bind only to specific NICs

By default the server listens on every IP the host has — useful on a single-NIC desktop, dangerous on a multi-NIC server where one NIC faces the internet.

In the GUI: **Server → Settings → Network**. Untick "Listen on all interfaces" and tick the specific addresses you want. Save. The service restarts automatically and rebinds.

Common patterns:

- **Internal-only**: tick the LAN IP(s), leave loopback ticked too if anything on the box itself calls the hook
- **Loopback-only**: tick `127.0.0.1` and `::1`. Useful when a reverse proxy on the same host fronts the public traffic.
- **One specific IP for hooks**: tick a single IP that you've documented as the webhook endpoint. Helps when you have a multi-homed server and want clear network segmentation.

### Per-endpoint IP allowlist

Each endpoint has an **IP allowlist** field. Empty means anyone reachable can call it. Non-empty means deny-by-default — only the listed IPs / CIDRs are allowed:

```
192.168.1.0/24
10.42.0.5
fd00::/8
```

Mixing IPv4 and IPv6 entries is fine. The check runs **before authentication**, so a blocked IP gets a fast 403 without burning CPU on HMAC validation.

### Trusted proxies (X-Forwarded-For)

If the server sits behind a reverse proxy (nginx / IIS / Caddy / Cloudflare Tunnel), the inbound `RemoteIpAddress` will always be the proxy. To make the IP allowlist evaluate the original client instead, configure **Server → Settings → Trusted proxies** with the IP(s) of the proxy:

```
10.0.0.5
```

When the inbound connection comes from that IP and includes an `X-Forwarded-For` header, the leftmost entry of the header is treated as the effective client IP for the allowlist check.

If `Trusted proxies` is empty (default), `X-Forwarded-For` is **ignored entirely**. This is the safe default — it prevents anyone from spoofing their IP by adding the header themselves.

## Authentication options

| Mode | When to use | What the caller sends |
|---|---|---|
| **None** | Internal-only on a trusted LAN, or a hook that's safe to fire repeatedly with no side effects | Nothing |
| **Bearer** | Simple authentication. Pick a long random secret and treat it as a password. | `Authorization: Bearer <secret>` |
| **HMAC** | Anything where the body matters and you want tamper-evidence: GitHub webhooks, Stripe events, signed callbacks | A header (default `X-Hub-Signature-256`) containing `sha256=<hex digest>` of the request body keyed by your shared secret |

For **None**, lean hard on the IP allowlist — that's your only defense.

For **Bearer**, generate the secret with `[Convert]::ToBase64String((1..32 | %{ Get-Random -Maximum 256 }))` or any password manager. 32+ bytes of entropy. The token sits in `Authorization` headers; HTTPS is **strongly recommended** so it doesn't traverse the network in clear text.

For **HMAC**, the secret never traverses the network — only the digest does. This is what GitHub / Stripe / Slack use, and it's the right pick for inbound webhooks from internet-facing services. Configure the four fields to match the sender:

- **Algorithm**: usually SHA256
- **Header name**: e.g. `X-Hub-Signature-256` (GitHub), `X-Slack-Signature` (Slack), `Stripe-Signature` (Stripe — needs different format)
- **Prefix**: `sha256=` for GitHub-style, none for raw hex
- **Encoding**: hex (most senders) or base64 (some Slack-derived implementations)

## HTTPS

HTTP-only is fine for fully-internal use. For anything reachable beyond a trusted LAN, enable HTTPS.

In **Server → Settings → HTTPS**:

- **PFX file**: path to a `.pfx` and its password. Easiest if you got a cert from your internal CA or generated a self-signed one with `New-SelfSignedCertificate`.
- **Cert store thumbprint**: the SHA-1 thumbprint of a certificate already imported into `LocalMachine\My`. Best for production where IT manages the cert lifecycle (auto-renewal, revocation).

The **HTTPS port** defaults to 8443. Both HTTP and HTTPS can be active simultaneously — change `HTTP port` and `HTTPS port` independently.

After saving HTTPS settings the service restarts and rebinds. There is briefly a "Disconnected" state in the GUI while that happens (1–3 seconds).

### Using Let's Encrypt

The server doesn't speak ACME directly. Two practical options:

1. **Reverse proxy approach** — run nginx / Caddy / IIS in front of Webhook Server. The proxy handles Let's Encrypt; Webhook Server stays HTTP-only on loopback. Configure `Trusted proxies` so allowlists still work on the original client IP.
2. **External cert renewal** — use [`win-acme`](https://www.win-acme.com/) to obtain certs and place them in `LocalMachine\My`. Configure HTTPS by **thumbprint** in the GUI. When `win-acme` rotates the cert it produces a new thumbprint, so you'll need to update the GUI; or have a small scheduled task that calls the admin pipe to update the binding (advanced, undocumented for now).

## Secrets at rest

All secrets — bearer tokens, HMAC keys, PFX passwords, RunAs passwords — are encrypted in `config.json` using **DPAPI with the `LocalMachine` scope**:

- The same machine can decrypt them under any account (so changing the service account doesn't break secret access).
- Copying `config.json` to a different machine **doesn't carry the secrets** — DPAPI LocalMachine binds to the host's machine key. This is by design and protects against config exfiltration.
- The GUI displays decrypted secrets in plaintext for an admin user. This is intentional. Anyone who can connect to the admin pipe is already SYSTEM-equivalent on the host; pretending otherwise just makes secret recovery harder.

For backup-and-restore across machines, you'd need to either:

- Re-enter all secrets on the new host (use the **Export config → manual secret re-entry** flow)
- Bind a custom DPAPI scope (not currently supported — would require a v0.x feature request)

## The admin pipe

`\\.\pipe\WebhookServerAdmin` carries the GUI's commands to the service. Its security descriptor allows full control to:

- `NT AUTHORITY\SYSTEM`
- `BUILTIN\Administrators`

Everyone else gets denied at the OS level — there's no auth layer in the protocol itself because the ACL is the auth layer. UAC token splitting means a non-elevated process owned by an Admin user is **also denied** (because the user's standard token has Admins as deny-only). That's why the GUI exe is manifested with `requireAdministrator` — it auto-elevates so the pipe accepts the connection.

If you ever need to grant pipe access to another local group (e.g., a custom `WebhookOperators` group), edit `src/WebhookServer.Core/Ipc/PipeSecurityFactory.cs` and add an `AddAccessRule` for that group. Currently no GUI configures this.

## Threat model summary

What you're protected against, by default:

- **Random scanners hitting your hooks** — solved by IP allowlists (when configured), auth (when configured), and HTTPS (when configured)
- **Replay of inbound requests** — HMAC signs the body, so a captured request can't be modified, but it CAN be replayed. If that matters, include a timestamp in the body and reject old timestamps in your script.
- **Credential leaks** — secrets at rest are DPAPI-encrypted, machine-bound; they don't travel with `config.json`
- **Privilege escalation via the admin pipe** — pipe ACL excludes non-admins
- **Local user spoofing the source IP** — `X-Forwarded-For` is ignored unless you explicitly trust a proxy

What you're NOT protected against — these are out of scope for this server:

- Compromise of an admin account on the host (game over — they own everything)
- A malicious script you configured (you wrote it; the server just runs it)
- DoS via volume of requests — there's no rate limiting in v0.x
- Memory dump of the running service revealing decrypted secrets — DPAPI protects at-rest only
