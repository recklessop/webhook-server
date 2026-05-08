# Recipe: GitHub-style HMAC-signed webhook

GitHub, Stripe, Slack, Shopify, and most SaaS providers sign their outbound webhooks with HMAC. The receiver computes the same HMAC over the request body using a shared secret and rejects the request if the signatures don't match. Webhook Server has this built in — you just point a real GitHub webhook at your endpoint.

## What we're building

A webhook URL that GitHub calls on every push to a repo. The server runs a PowerShell script that pulls the latest commit and triggers a deployment. Authentication is HMAC-SHA256 over the request body, using the secret you configured in GitHub's webhook settings.

## On the GitHub side

In your repo: **Settings → Webhooks → Add webhook**.

| Field | Value |
|---|---|
| Payload URL | `https://hooks.contoso.com/hook/gh-deploy` (yes, HTTPS — GitHub enforces it for public hosts) |
| Content type | `application/json` |
| Secret | Generate a long random string. Copy it for the next step. |
| SSL verification | Enable |
| Events | Just `push` |

Save. GitHub immediately delivers a `ping` event for testing. You'll see it in **Recent Deliveries** with whatever response code your server returns.

## The PowerShell deployment script

`C:\Scripts\gh-deploy.ps1`:

```powershell
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$payload = $input | ConvertFrom-Json

# Verify the event type via the X-GitHub-Event header passed as an env var
$event = $env:WEBHOOK_HEADER_X_GITHUB_EVENT
if ($event -eq 'ping') {
    "got ping from $($payload.repository.full_name)"
    return
}
if ($event -ne 'push') {
    Write-Error "ignoring $event event"
}

$repo   = $payload.repository.full_name
$branch = $payload.ref -replace '^refs/heads/', ''
$sha    = $payload.after

if ($branch -ne 'main') {
    "ignoring push to $branch"
    return
}

$repoDir = "C:\Deploys\$($payload.repository.name)"
if (-not (Test-Path $repoDir)) {
    git clone "https://github.com/$repo.git" $repoDir
}

Push-Location $repoDir
try {
    git fetch --all
    git reset --hard $sha
    # ...your build/deploy steps here...
    & npm ci
    & npm run build
    Restart-Service MyAppService
}
finally {
    Pop-Location
}

"deployed $repo @ $sha"
```

## Configure the endpoint

**File → New endpoint**:

| Section | Setting | Value |
|---|---|---|
| Identity | Slug | `gh-deploy` |
| Auth | Mode | **HMAC** |
| Auth | HMAC secret | paste the GitHub-side secret |
| Auth | HMAC header | `X-Hub-Signature-256` *(GitHub's default)* |
| Allowed clients | | `140.82.112.0/20`, `192.30.252.0/22` *(GitHub's webhook IP ranges; check [docs.github.com](https://api.github.com/meta) for the live list)* |
| Executor | Type | **Windows PowerShell** |
| Executor | Script path | `C:\Scripts\gh-deploy.ps1` |
| Data passing | JSON body to stdin | ✓ |
| Data passing | Headers/query as env vars | ✓ *(needed so `WEBHOOK_HEADER_X_GITHUB_EVENT` is set)* |
| Run as | Identity | **Service** (default) — assumes the deployment is local |
| Response | Mode | **Async** *(GitHub times out fast; don't make it wait for the build)* |
| Response | Timeout (sec) | `600` |

Save.

## What HMAC does for you here

GitHub computes `sha256(body, secret)` and sends it as `sha256=<hex>` in `X-Hub-Signature-256`. Webhook Server computes the same hash, verifies in fixed time, and rejects (401) on mismatch.

This means:

- A request with a tampered body fails the check
- A captured request can be **replayed verbatim** (the signature is valid for that body) — if that matters, GitHub also includes a `X-GitHub-Delivery` ID and timestamp you can deduplicate against
- The secret never travels over the network — only the digest does, so HTTPS is for confidentiality of the body, not the secret

## Adapting for Stripe, Slack, etc.

Same pattern, different headers and signing details. The four HMAC fields in the editor cover all common variants:

| Provider | Header | Prefix | Encoding | Algorithm |
|---|---|---|---|---|
| GitHub | `X-Hub-Signature-256` | `sha256=` | hex | SHA-256 |
| Stripe | `Stripe-Signature` | (none — but Stripe's format is multipart, see below) | hex | SHA-256 |
| Slack | `X-Slack-Signature` | `v0=` | hex | SHA-256 |
| Generic / custom | configurable | configurable | configurable | SHA-1 / SHA-256 / SHA-512 |

**Stripe** is special: their `Stripe-Signature` header has the format `t=<timestamp>,v1=<sig>,v0=<sig>`, where `v1` is HMAC-SHA256 of `<timestamp>.<body>`. Webhook Server's straight HMAC check doesn't match Stripe's signed-with-timestamp scheme. Workarounds:

- Use **Bearer auth** on Stripe webhooks instead, since you already control the secret
- Or do unauthenticated + IP allowlist + a script-side signature check using their official validation library

For everything that's "GitHub-shaped" (signed body, raw HMAC), the built-in HMAC mode is the right pick.
