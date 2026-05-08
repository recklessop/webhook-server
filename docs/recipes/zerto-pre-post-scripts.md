# Recipe: Zerto failover post-script → DNS update + service checks

This is the canonical reason Webhook Server exists.

When Zerto fails a VM over from production to DR, the VM boots fine — but **the things around it** often need attention: DNS records still point at the production IP, dependent services need to be checked, on-call needs a heads-up. Zerto pre/post scripts run on the **Zerto Virtual Manager**, not on a domain controller and not necessarily with admin rights to the things that need fixing. So you want a single webhook URL that the post-script hits, and a Windows host on the DR side that does the actual work with the right identity.

## What we're building

Zerto's post-recovery script (a one-shot PowerShell file pointing at curl) calls `http://webhook.dr.contoso.local:8080/hook/post-failover` with a JSON body identifying the VPG and operation. The Webhook Server, running on a DR-side Windows host as a gMSA with delegated AD/DNS rights, runs PowerShell that:

1. Updates DNS A records to point the failed-over hostnames at their DR IPs
2. Waits for the failed-over VM to come up (ping + WinRM probe)
3. Connects to the VM via PowerShell remoting and starts/checks critical services
4. Sends a Teams notification with the result

The endpoint is **Async** so the Zerto script returns in milliseconds — no risk of timing out Zerto's failover sequence even if the actions take minutes. The script's full output ends up in the webhook log and (optionally) in an outbound callback.

## Why curl and not Invoke-WebRequest?

Zerto's PowerShell runner is intentionally minimal — many environments run an older Windows on the ZVM and don't have full PowerShell modules installed. `curl.exe` ships with Windows 10 1803+ and Server 2019+ and works without any modules. Plus, calling an HTTP endpoint with `curl.exe` doesn't depend on the version of `Invoke-WebRequest` shipped with the host's PowerShell.

## 1. The Zerto post-script (client side)

A ready-to-use script ships in this repo at [`scripts/examples/zerto-post-failover.ps1`](../../scripts/examples/zerto-post-failover.ps1). Copy it to the ZVM, edit `$WebhookUrl` and the bearer-token path at the top, and wire it into the VPG:

> **VPG settings → Recovery → Scripts → Post-Recovery Script**
> Path: `C:\Scripts\zerto-post-failover.ps1`
> Parameters: *(leave empty)*

The script is ~50 lines and only depends on `curl.exe` + a token file readable by the ZVM service account.

The flow:

```
Zerto VPG failover starts
   |
   +-- VM is brought up at DR site
   |
   +-- Zerto post-script fires:
   |     curl POST http://webhook.dr/hook/post-failover  (async, returns 202 in ~50ms)
   |
   +-- Zerto sees success, finishes the failover and reports done
                                                    |
                       (meanwhile, on the webhook server)
                                                    |
                       running PowerShell for several minutes:
                         - update DNS
                         - wait for VM ready
                         - check services on VM
                         - notify Teams
```

## 2. The server-side script (does the actual work)

Save this on the webhook host as `C:\Scripts\post-failover-handler.ps1`:

```powershell
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$body = $input | ConvertFrom-Json

# ---------- environment specifics; edit for your site ----------
$dnsServer    = 'dc01.contoso.local'
$forwardZone  = 'contoso.local'
$teamsWebhook = 'https://contoso.webhook.office.com/...'
$drIpMap      = @{
    'app01' = '10.42.10.11'
    'app02' = '10.42.10.12'
    'db01'  = '10.42.10.21'
}
$serviceMap   = @{
    'app01' = @('W3SVC','MyAppSvc')
    'app02' = @('W3SVC','MyAppSvc')
    'db01'  = @('MSSQLSERVER','SQLAgent')
}
# ---------------------------------------------------------------

# Default the VM list to "all VMs we know about" if the post-script didn't
# tell us, so the same handler works without having to embed the VM list in
# every Zerto post-script.
$vms = if ($body.vms) { $body.vms } else { $drIpMap.Keys }

$summary = @()

foreach ($vm in $vms) {
    if (-not $drIpMap.ContainsKey($vm)) {
        $summary += "skip  $vm  (no DR IP mapping in handler)"
        continue
    }
    $ip = $drIpMap[$vm]

    # 1. DNS - delete + re-add the A record
    try {
        $existing = Get-DnsServerResourceRecord -ZoneName $forwardZone -Name $vm `
                      -RRType A -ComputerName $dnsServer -ErrorAction SilentlyContinue
        if ($existing) {
            Remove-DnsServerResourceRecord -ZoneName $forwardZone -Name $vm `
                -RRType A -RecordData $existing.RecordData.IPv4Address `
                -ComputerName $dnsServer -Force
        }
        Add-DnsServerResourceRecordA -ZoneName $forwardZone -Name $vm `
            -IPv4Address $ip -ComputerName $dnsServer -TimeToLive 00:05:00
        $summary += "dns   $vm -> $ip"
    } catch {
        $summary += "DNS!  $vm  $($_.Exception.Message)"
        continue
    }

    # 2. Wait for the VM to be reachable (up to 5 minutes)
    $deadline = (Get-Date).AddMinutes(5)
    $reachable = $false
    while ((Get-Date) -lt $deadline) {
        if (Test-Connection -ComputerName $ip -Count 1 -Quiet -ErrorAction SilentlyContinue) {
            try {
                # Quick WinRM probe; succeeds when the VM has finished booting
                Invoke-Command -ComputerName $ip -ScriptBlock { $true } -ErrorAction Stop | Out-Null
                $reachable = $true
                break
            } catch { Start-Sleep -Seconds 10 }
        } else {
            Start-Sleep -Seconds 10
        }
    }
    if (-not $reachable) {
        $summary += "wait! $vm  not reachable after 5 minutes"
        continue
    }

    # 3. Check + start critical services on the VM
    if ($serviceMap.ContainsKey($vm)) {
        $svcReport = Invoke-Command -ComputerName $ip -ArgumentList @(,$serviceMap[$vm]) -ScriptBlock {
            param($services)
            $report = @()
            foreach ($s in $services) {
                $svc = Get-Service -Name $s -ErrorAction SilentlyContinue
                if (-not $svc) { $report += "$s : missing"; continue }
                if ($svc.Status -ne 'Running') {
                    Start-Service $s
                    Start-Sleep -Seconds 2
                    $svc.Refresh()
                }
                $report += "$s : $($svc.Status)"
            }
            return $report
        }
        $summary += "svc   $vm : $($svcReport -join ', ')"
    } else {
        $summary += "svc   $vm  (no services configured)"
    }
}

# 4. Notify Teams
$teamsBody = @{
    text = "Webhook post-failover for VPG **$($body.vpg)**:`n" + ($summary -join "`n")
} | ConvertTo-Json
try {
    Invoke-RestMethod -Uri $teamsWebhook -Method POST -ContentType 'application/json' -Body $teamsBody | Out-Null
} catch {
    $summary += "teams! notification failed: $($_.Exception.Message)"
}

# Return the summary so it shows up in the webhook log + outbound callback
$summary -join "`n"
```

Two things to call out:

- **PowerShell remoting to the VM** uses the gMSA's network identity (or whoever the service runs as). Make sure the gMSA / service account can `Invoke-Command` to the failed-over hosts — usually that means the account is a local admin on the target VMs, or you've configured constrained delegation.
- **WinRM** must be enabled on the failed-over VMs for the remoting calls to work. `Enable-PSRemoting` is the simplest, but most prod environments configure WinRM via Group Policy.

## 3. Configure the endpoint in the GUI

**File → New endpoint:**

| Section | Setting | Value |
|---|---|---|
| Identity | Slug | `post-failover` |
| Identity | Description | "Zerto post-recovery: DNS + service checks" |
| Auth | Mode | **Bearer** |
| Auth | Bearer secret | generate a 32-byte random string; copy it for the Zerto script's token file |
| Allowed clients | (one per line) | `10.0.0.0/8` *(your ZVM's network)* |
| Executor | Type | **Windows PowerShell** |
| Executor | Script path | `C:\Scripts\post-failover-handler.ps1` |
| Data passing | JSON body to stdin | ✓ |
| Run as | Identity | **Service** if the service runs under a gMSA with the right rights, otherwise **SpecificUser** with a delegated account |
| Response | Mode | **Async** ← critical: this is what makes the Zerto script non-blocking |
| Response | Timeout (sec) | `600` *(this is the cap on the long-running handler script, not the Zerto-facing response)* |
| Response | Fail on non-zero exit | unticked *(async hooks have no caller to receive a 502)* |

Save. Right-click the row → **Copy URL** to grab `http://webhook.dr.contoso.local:8080/hook/post-failover` and paste it into `$WebhookUrl` at the top of the Zerto-side script.

> **Why Bearer instead of HMAC?** Both work. Bearer is simpler — drop the token in a file on the ZVM that's readable by the ZVM service account and you're done. HMAC requires the Zerto-side script to compute a signature, which is doable but adds a few lines of code. Pick what fits your environment.

## 4. Wire up the bearer token

Place the bearer token in a file the ZVM service account can read (and nobody else):

```powershell
# on the ZVM, from elevated PowerShell
$token = (New-Guid).ToString('N')   # or paste the value from the GUI
$tokenPath = 'C:\ProgramData\Zerto\webhook-token.txt'
$token | Out-File -LiteralPath $tokenPath -Encoding utf8 -NoNewline
icacls $tokenPath /inheritance:r /grant 'NT SERVICE\Zerto Online Services:R' 'BUILTIN\Administrators:F' /T
```

Adjust the service principal name to whatever Zerto runs as on your version. The script reads from this path automatically; no change needed in the script itself.

## 5. Test before going live

In a maintenance window, fire the webhook by hand:

```powershell
# from any machine that can reach the webhook server
$body = @{
    operation = 'test'
    vpg       = 'SmokeTest'
    timestamp = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json -Compress

curl.exe --silent --show-error --max-time 10 -X POST `
    -H "Authorization: Bearer paste-the-token" `
    -H "Content-Type: application/json" `
    -d $body `
    http://webhook.dr.contoso.local:8080/hook/post-failover
```

You'll get back `{"runId":"…","accepted":true}` immediately. Open the Webhook Server GUI and watch the log panel — within 30 seconds or so you'll see lines for the run. Confirm DNS records updated, services on each VM ended in `Running`, and the Teams notification arrived.

## Variations

### Different actions for failover vs. failback

Pass an `operation` field in the body and branch on it. The Zerto-side script already sends `operation = 'failover'`. Add a separate post-failback script (or detect from `$env:ZertoOperationType`) that sends `operation = 'failback'` and have the handler revert DNS to production IPs.

### Per-VPG endpoints

If you want fine-grained access control or different actions per VPG, create one endpoint per VPG (`post-failover-app`, `post-failover-db`, …) and give each its own bearer token. The GUI handles dozens of endpoints fine.

### Audit trail to a SIEM

Each endpoint can have an outbound **Callback** URL. Configure it with your SIEM's HTTP collector + an HMAC secret, and every run produces a JSON record with runId, exit code, duration, stdout, and stderr — perfect for compliance.
