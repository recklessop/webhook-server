# Recipe: Zerto ZVMA pre/post scripts → notify + VM health check

> This is the **canonical** Zerto recipe. It targets the **ZVMA on
> Kubernetes** — the supported deployment — where pre/post scripts run
> inside the in-cluster `scripts-service` container (Linux + pwsh 7). The
> webhook-server side is a normal Windows service that does the
> Windows-domain work the ZVMA container can't reach directly.

## What we're building

ZVMA's `scripts-service` pod runs your VPG pre/post scripts inside a Linux
container. It exposes a small set of `Zerto*` environment variables, and we
want to:

1. POST those variables to a Webhook Server endpoint at the start (pre) and
   end (post) of every VPG operation, and
2. On the receiving Windows host, do something useful with them — at minimum
   a chat notification, and on `post` a quick health check of the VMs that
   just powered on.

The endpoints are **Async**, so the Zerto VPG sequence is never blocked by
slow downstream actions (notifications, port probes, etc.).

```
Zerto VPG operation starts
   |
   +-- ZVMA scripts-service container runs:
   |     /app/scripts-files/zerto-zvma-send.ps1 -Phase pre
   |       -> POST http://webhook.dr/hook/zerto-pre   (async, returns 202)
   |
   +-- VMs come up at recovery site
   |
   +-- ZVMA scripts-service container runs:
         /app/scripts-files/zerto-zvma-send.ps1 -Phase post
           -> POST http://webhook.dr/hook/zerto-post  (async, returns 202)

(meanwhile, on the webhook server)
   /hook/zerto-pre  -> Slack/Teams notification ("Test failover starting...")
   /hook/zerto-post -> Slack/Teams notification + ping/port probe each VM,
                       write a JSON report to disk, exit non-zero on failure.
```

## What ZVMA exposes

Captured from a real Test failover; same set is present in pre and post:

| Variable | Example | Notes |
|---|---|---|
| `ZertoVPGName` | `ubuntu-2404-local` | The VPG that fired the script |
| `ZertoInternalVpgName` | `ubuntu-2404-local` | Usually identical to `ZertoVPGName` |
| `ZertoOperation` | `Test` | `Test` / `Failover` / `Move` / `FailoverBeforeCommit` / `FailoverDuringCommit` |
| `ZertoForce` | `Yes` (pre) / `No` (post) | Set to `Yes` only during the pre phase when force mode is on; reset to `No` by post |
| `VmDisplayNames` | `ubuntu-2404(1)(1)(1)` | Comma-separated for multi-VM VPGs; Test failovers add `(N)` suffixes |
| `ZertoHypervisorManagerIP` | `192.168.50.20` | The vCenter / Hyper-V manager ZVMA is talking to |
| `ZertoHypervisorManagerPort` | `443` | |
| `ZertoOutputDir` | `/app/scripts-output` | Container-side output dir (written back to ZVMA via PVC) |
| `ZertoWorkingDir` | `/app/scripts-files` | Where script files live in-container |

Branch on `ZertoOperation` to differentiate Test runs from real failovers.
**`ZertoForce` is only meaningful during the pre phase** — capture it there
if you need it later, because by post it's been reset.

## 1. The Zerto-side script (sender)

A ready-to-use script ships in this repo at
[`scripts/examples/zerto-zvma-send.ps1`](../../scripts/examples/zerto-zvma-send.ps1).
Place it where the `scripts-service` pod can read it — typically the
`scripts-service-scripts-files-pvc`, mounted at `/app/scripts-files/` — and
wire it into the VPG twice:

> **VPG settings → Recovery → Scripts → Pre-Recovery Script**
> Path: `/app/scripts-files/zerto-zvma-send.ps1`
> Parameters: `-Phase pre`
>
> **VPG settings → Recovery → Scripts → Post-Recovery Script**
> Path: `/app/scripts-files/zerto-zvma-send.ps1`
> Parameters: `-Phase post`

The default `$WebhookUrl` includes `{phase}` so one script + one URL config
serves both phases — `http://webhook.dr/hook/zerto-{phase}` becomes
`/hook/zerto-pre` and `/hook/zerto-post` automatically. Override with
`-WebhookUrl` and `-Bearer` if you'd rather pass them per-VPG.

The script POSTs a single JSON object:

```json
{
  "phase": "pre",
  "capturedAt": "2026-05-08T17:45:54Z",
  "host": "scripts-service-f9b6cb7-4xbxq",
  "zerto": {
    "vpgName":               "ubuntu-2404-local",
    "internalVpgName":       "ubuntu-2404-local",
    "operation":             "Test",
    "force":                 "Yes",
    "vmDisplayNames":        "ubuntu-2404(1)(1)(1)",
    "hypervisorManagerIP":   "192.168.50.20",
    "hypervisorManagerPort": "443",
    "outputDir":             "/app/scripts-output",
    "workingDir":            "/app/scripts-files"
  }
}
```

A webhook outage **does not fail the VPG** — the script catches and exits 0.
Comment in the file shows how to flip that to strict mode if you'd rather a
webhook outage abort the failover.

## 2. The webhook-server-side scripts (receivers)

Two examples ship in the repo. Both read the JSON body from stdin (the
webhook server delivers the body to the script's stdin when **JSON body to
stdin** is ticked on the endpoint).

### a. Slack/Teams notification — both phases

[`scripts/examples/zerto-receiver-notify.ps1`](../../scripts/examples/zerto-receiver-notify.ps1)
posts a single-line summary to a Slack or Teams Incoming Webhook URL. It
picks an icon based on `ZertoOperation`:

- `Test` → 🧪 — benign, expected
- `Failover` → 🚨 — real production event
- `Move` → 🚚 — planned migration

…and highlights `ZertoForce=Yes` on the **pre** message so you can see at
a glance whether the operation was force-flagged.

Set the destination via `NOTIFY_URL` env var on the webhook host, or
hardcode at the top of the script.

### b. Post-recovery VM health check — post phase only

[`scripts/examples/zerto-receiver-vm-healthcheck.ps1`](../../scripts/examples/zerto-receiver-vm-healthcheck.ps1)
runs only on `phase=post` for operations that bring VMs up
(`Test`/`Failover`/`Move`/`FailoverBeforeCommit`/`FailoverDuringCommit`).
For each name in `VmDisplayNames` it:

1. Strips the trailing `(1)(1)(1)` suffix Zerto adds on Test failovers, so
   DNS resolution targets the actual hostname.
2. Pings (`Test-Connection`).
3. Probes a configurable TCP port (`-ProbePort`, default `3389` for RDP;
   use `22` for SSH or `443` for the web tier).
4. Writes a JSON report to
   `C:\ProgramData\WebhookServer\zerto-healthchecks\<vpg>-<op>-<utcstamp>.json`.
5. Exits non-zero if any VM failed either probe — which surfaces in the
   webhook server's run history (and outbound callback, if configured).

Bump the endpoint's **Timeout (sec)** to `120` when wiring this in, since
network probes can take a while.

## 3. Configure the endpoints in the GUI

Two endpoints. Identical except for the slug, the script, and (for the
healthcheck) the timeout.

### `zerto-pre`

| Section | Setting | Value |
|---|---|---|
| Identity | Slug | `zerto-pre` |
| Identity | Description | "Zerto pre-recovery: chat notification" |
| Auth | Mode | **Bearer** |
| Auth | Bearer secret | generate a 32-byte random string; reuse for `zerto-post` |
| Allowed clients | (one per line) | the IP of the K8s node running `scripts-service` (e.g. `192.168.50.30`) |
| Executor | Type | **Windows PowerShell** (or PowerShell 7) |
| Executor | Script path | `C:\scripts\zerto-receiver-notify.ps1` |
| Data passing | JSON body to stdin | ✓ |
| Run as | Identity | **Service** |
| Response | Mode | **Async** |
| Response | Timeout (sec) | `30` |
| Response | Fail on non-zero exit | unticked *(async hooks have no caller to receive a 502)* |

### `zerto-post`

Same as above, except:

| Setting | Value |
|---|---|
| Slug | `zerto-post` |
| Description | "Zerto post-recovery: notify + VM health check" |
| Script path | a **wrapper** that calls both receiver scripts in turn (see below) |
| Timeout (sec) | `120` |

Two receivers on one endpoint is easiest with a tiny wrapper that fans
stdin out to both scripts:

```powershell
# C:\scripts\zerto-post-fanout.ps1
$body = [Console]::In.ReadToEnd()
$body | & 'C:\scripts\zerto-receiver-notify.ps1'
$body | & 'C:\scripts\zerto-receiver-vm-healthcheck.ps1'
```

Or run the two as separate endpoints (`zerto-post-notify` and
`zerto-post-healthcheck`) and have the Zerto-side script POST to both —
either pattern is fine. The fanout wrapper keeps the Zerto config simpler.

## 4. Wire up the bearer token

On the ZVMA / scripts-service side, the easiest place to put the token is
a Kubernetes Secret mounted into the pod, but the simplest approach for
testing is to pass it as a parameter to the Zerto-side script:

> VPG settings → Pre-Recovery Script → Parameters:
> `-Phase pre -Bearer <paste-token>`
>
> VPG settings → Post-Recovery Script → Parameters:
> `-Phase post -Bearer <paste-token>`

For production, mount a Secret at a known path in the pod and have the
sender script read from it (`Get-Content /run/secrets/webhook-token`).

## 5. Test before going live

Run a Test failover on a non-critical VPG. Watch:

- **Slack/Teams**: a `:test_tube: Zerto Test - phase: pre` message arrives,
  followed ~30s–several minutes later by a `:test_tube: Zerto Test - phase:
  post` message.
- **Webhook Server GUI** → run history: two runs for `zerto-pre` /
  `zerto-post`, both green.
- **`C:\ProgramData\WebhookServer\zerto-healthchecks\`**: a fresh JSON
  report named `<vpg>-Test-<utcstamp>.json` containing per-VM ping and port
  probe results.
- **ZVMA**: the VPG operation completes successfully; nothing in the
  pre/post logs blocked on the webhook.

## Variations

### Branch on Test vs. real failover in the receivers

The notifier already styles the message differently. To do something only
on a real failover (e.g. update DNS), guard with:

```powershell
if ($p.zerto.operation -ne 'Test') {
    # do the destructive thing
}
```

A `ZertoOperation` of `Test` means "exercise — don't touch production
dependencies." Always check it before doing anything that mutates real
state.

### Capture `ZertoForce` from pre for use in post

`ZertoForce` is `Yes` only during the **pre** phase when force mode is on
and is reset to `No` by the **post** phase. If your post-side logic needs
to know the operation was force-flagged, save it during pre (e.g. write a
small marker to the shared `ZertoOutputDir`) and read it back during post.

### Per-VPG endpoints

For fine-grained access control or different actions per VPG, create one
endpoint per VPG (`zerto-pre-app01`, `zerto-post-app01`, …) with its own
bearer token. Override `-WebhookUrl` and `-Bearer` on the Zerto side per
VPG.

### Audit trail

Every endpoint can have an outbound **Callback** URL. Configure with your
SIEM's HTTP collector + an HMAC secret, and every run produces a JSON
record with runId, exit code, duration, stdout, and stderr — convenient
for compliance.

## Security note

The ZVMA `scripts-service` pod runs your scripts inside a Linux container
with broad reach into the management cluster — anything your script does
runs with whatever ServiceAccount that pod uses. Treat the script content
as privileged and make sure pre/post script edit rights are restricted to
trusted operators. If you're unfamiliar with the pod's RBAC posture, check
`Get-ChildItem Env:` from inside the container and look at
`/var/run/secrets/kubernetes.io/serviceaccount/` — that token is what your
scripts (and a malicious script) can use to talk to the K8s API.
