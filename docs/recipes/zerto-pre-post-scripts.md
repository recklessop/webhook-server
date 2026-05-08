# Recipe: Zerto pre/post scripts → AD / DNS update

This is the canonical reason Webhook Server exists. Zerto's failover, move, and clone operations support pre- and post-scripts — but those scripts run on the Zerto Virtual Manager (ZVM), not on the destination domain controller or DNS server. To touch AD or DNS during a failover you need either:

- A bastion / utility host with the right modules and credentials installed (and you accept the maintenance burden of keeping its scripts in sync)
- **A webhook on a Windows host** — Zerto's pre/post calls a single URL, and the webhook server runs the right PowerShell on the right machine with the right identity. This page is about that.

## What we're building

A Zerto pre/post script POSTs to `http://webhooks.contoso.local:8080/hook/dr-failover-prep` with a JSON body identifying the VPG and target VMs. The webhook server, running on a domain-joined utility host as a gMSA with delegated AD rights, runs PowerShell that:

1. Updates AD computer object descriptions to indicate they're now at the DR site
2. Updates DNS A records to point `app01.contoso.local` and friends at the new (DR) IPs
3. Posts a result line to a Teams channel
4. Returns 200 with the summary so it shows up in Zerto's pre/post script log

It's about ~30 lines of PowerShell on the server side and 3 lines of script in Zerto.

## Prerequisites

On the webhook host:

- Webhook Server installed (see [Installation](../installation.md))
- The host is domain-joined
- The service account has the **AD permissions** it needs. We'll configure this two ways below — the simple way (LocalSystem + delegated rights to the machine account) and the production way (gMSA).
- DNS PowerShell module installed if you'll modify DNS: `Install-WindowsFeature RSAT-DNS-Server` (Server) or RSAT installed (Win 10/11).
- AD PowerShell module: `Install-WindowsFeature RSAT-AD-PowerShell` (Server).

On the Zerto side:

- ZVM 8.x or 9.x (this works with both)
- A Virtual Protection Group (VPG) you want to wire up

## 1. Plan the script and the inputs

What does the script need to know? At minimum:

- **VPG name** — Zerto exposes this as a parameter to the pre/post script
- **VM names** — likewise
- **Target IPs** — depending on your failover topology, these may be static (DR network has known IPs) or known after Zerto reconfigures the IP

Decide what travels in the request body and what's hardcoded. A pragmatic split:

- Hardcoded (in the PowerShell script on the webhook host): zone name, AD OU, Teams webhook URL, mapping table from VM hostname → target IP
- Sent in the body: VPG name, list of VM names, an "operation" field (`failover`, `move`, `failback`, etc.)

Example body the Zerto script will send:

```json
{
  "operation": "failover",
  "vpg": "App-Production",
  "vms": ["app01", "app02", "db01"]
}
```

## 2. Write the PowerShell script on the webhook host

Save this as `C:\Scripts\dr-failover-prep.ps1` on the webhook host:

```powershell
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Read the body from stdin (the webhook server pipes the JSON in for us when
# StdinJson is enabled).
$body = $input | ConvertFrom-Json

# Hardcoded site config - edit for your environment.
$dnsServer    = 'dc01.contoso.local'
$forwardZone  = 'contoso.local'
$adOu         = 'OU=Servers,DC=contoso,DC=local'
$teamsWebhook = 'https://contoso.webhook.office.com/...'   # one-way, no secret to leak
$drIpMap      = @{
    'app01' = '10.42.10.11'
    'app02' = '10.42.10.12'
    'db01'  = '10.42.10.21'
}

$summary = @()

foreach ($vm in $body.vms) {
    if (-not $drIpMap.ContainsKey($vm)) {
        $summary += "skip $vm - no DR IP mapping"
        continue
    }
    $newIp = $drIpMap[$vm]

    # 1. Update DNS A record (delete + recreate is the simplest reliable path)
    $existing = Get-DnsServerResourceRecord -ZoneName $forwardZone -Name $vm `
                  -RRType A -ComputerName $dnsServer -ErrorAction SilentlyContinue
    if ($existing) {
        Remove-DnsServerResourceRecord -ZoneName $forwardZone -Name $vm `
            -RRType A -RecordData $existing.RecordData.IPv4Address `
            -ComputerName $dnsServer -Force
    }
    Add-DnsServerResourceRecordA -ZoneName $forwardZone -Name $vm `
        -IPv4Address $newIp -ComputerName $dnsServer -TimeToLive 00:05:00

    # 2. Update AD computer description so on-call can see at a glance
    Set-ADComputer -Identity $vm -Description "[DR-$($body.operation)] $(Get-Date -Format s)"

    $summary += "ok   $vm -> $newIp"
}

# 3. Notify Teams
$msg = @{
    text = "Webhook DR prep for VPG **$($body.vpg)** ($($body.operation)):`n" +
           ($summary -join "`n")
} | ConvertTo-Json
Invoke-RestMethod -Uri $teamsWebhook -Method POST -ContentType 'application/json' -Body $msg | Out-Null

# 4. Print the summary so Zerto's pre/post script log captures it
$summary -join "`n"
```

A few choices worth calling out:

- **`$input | ConvertFrom-Json`** — Webhook Server pipes the request body into the script via stdin when "JSON body to stdin" is ticked. `$input` is PowerShell's automatic variable for pipeline input.
- **`$ErrorActionPreference = 'Stop'`** — turn cmdlet warnings into terminating errors so the script exits non-zero on real problems. Webhook Server then returns 502 (configurable via "Fail on non-zero exit") and Zerto sees the failure.
- **Two-way Teams notification but one-way return** — the script's stdout becomes the HTTP response. Zerto logs it. The Teams notification is a separate Invoke-RestMethod.

## 3. Configure the endpoint in the GUI

In Webhook Server's GUI, **File → New endpoint**:

| Section | Setting | Value |
|---|---|---|
| Identity | Slug | `dr-failover-prep` |
| Identity | Description | "Zerto pre-script: update AD/DNS during failover" |
| Auth | Mode | **Bearer** |
| Auth | Bearer secret | generate a 32-byte random string; copy it for the Zerto script |
| Allowed clients | (one per line) | `10.0.0.0/8` (your ZVM's network) |
| Executor | Type | **Windows PowerShell** |
| Executor | Script path | `C:\Scripts\dr-failover-prep.ps1` |
| Data passing | JSON body to stdin | ✓ |
| Data passing | Headers/query as env vars | ✗ |
| Run as | Identity | **Service** if the service is running as a gMSA with AD rights, otherwise **SpecificUser** with a delegated account |
| Response | Mode | **Sync** |
| Response | Timeout (sec) | `60` |
| Response | Fail on non-zero exit | ✓ |

Save. Right-click the row → **Copy URL** to grab the full URL, e.g. `http://webhooks.contoso.local:8080/hook/dr-failover-prep`.

> **Why Bearer auth and not None?** Even though the IP allowlist limits who can reach this endpoint, the Bearer token is a defense-in-depth layer. If someone managed to spoof or get on the trusted network, they still need the token. Generate it once, store it in a secrets manager (or in Zerto's encrypted script parameters), and never email it.

## 4. The Zerto pre/post script

Zerto pre/post scripts are PowerShell files placed on the ZVM. The path varies by Zerto version; in 9.x it's typically `C:\Program Files\Zerto\Zerto Virtual Replication\Scripts\`.

Create `dr-failover-prep.ps1` on the ZVM:

```powershell
# Zerto passes context as parameters/environment - exact names vary by version.
# Document yours; this is illustrative.
param(
    [string]$VpgName = $env:ZertoVPGName
)

$webhookUrl = 'http://webhooks.contoso.local:8080/hook/dr-failover-prep'
$bearer     = 'paste-the-bearer-secret-here'  # store via Zerto secret param if available

# Build the body. In a real script, list the VMs by querying Zerto's API or by
# convention from the VPG name.
$body = @{
    operation = 'failover'
    vpg       = $VpgName
    vms       = @('app01','app02','db01')
} | ConvertTo-Json

$response = Invoke-RestMethod -Method POST -Uri $webhookUrl -Body $body `
              -ContentType 'application/json' -TimeoutSec 90 `
              -Headers @{ Authorization = "Bearer $bearer" }

# Print whatever the webhook returned to Zerto's log.
$response.stdout
```

Wire this script into your VPG's **Pre-Recovery** or **Post-Recovery** hook in the Zerto UI.

## 5. Test before going live

In a maintenance window, hit the endpoint manually with a fake VPG name to confirm the wiring works:

```powershell
$body = @{ operation='test'; vpg='SmokeTest'; vms=@('app01') } | ConvertTo-Json
Invoke-RestMethod -Method POST `
    -Uri http://webhooks.contoso.local:8080/hook/dr-failover-prep `
    -Headers @{ Authorization = "Bearer paste-the-secret" } `
    -ContentType application/json -Body $body
```

You should see the summary line(s) come back, AD descriptions update, DNS A records update, and a Teams notification. If anything's off:

- **No response, hang** → check the GUI's log panel. The auto-poll updates every 3 seconds. Look for the run line with the slug + exit code.
- **401 Unauthorized** → bearer mismatch
- **403 Forbidden** → IP allowlist blocking you
- **502 Bad Gateway** → script ran but exited non-zero. The response body has stderr.

After a real failover triggers it, audit by checking the daily log file at `C:\ProgramData\WebhookServer\logs\webhook-YYYYMMDD.log` for the `Run <id> dr-failover-prep ok exit=0` line.

## Variations

### Different actions for failover vs. failback

Pass an `operation` field in the body and branch on it in the PowerShell. The script above already does this — extend the `switch` to handle `failback` (revert DNS to production IPs, clear DR description, etc.).

### Per-VPG endpoints

If you want fine-grained access control per VPG, create one endpoint per VPG and give each its own bearer secret. The GUI's grid handles dozens of endpoints fine.

### Async + callback for long-running work

If your AD/DNS update genuinely takes minutes (e.g., updating thousands of records in a large environment), set the endpoint to **Async** mode. Zerto's pre-script gets `202 Accepted` immediately and continues. Configure the endpoint's **Callback** with a URL that records the result (e.g., another endpoint that logs to a file, or your monitoring system's API).

### Audit trail to a SIEM

Configure each endpoint's **Callback** with your SIEM's HTTP collector URL + an HMAC secret. Every run produces a JSON record with runId, exit code, duration, stdout, and stderr — perfect for compliance audit logs.
