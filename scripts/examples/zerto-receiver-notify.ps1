<#
.SYNOPSIS
    Webhook-server-side receiver: posts a Slack/Teams notification when a VPG
    fires its pre or post recovery script.

.DESCRIPTION
    Reads the JSON body from stdin (the payload sent by zerto-zvma-send.ps1),
    builds a phase-aware message, and posts it to an Incoming Webhook URL.

    The message highlights:
      - VPG name + operation type (Test / Failover / Move / ...)
      - Whether ZertoForce was set (only relevant pre)
      - VM display names included in the run
      - Phase (pre vs post) so you can see the bracketing in chat

    Wire up two endpoints:
        /hook/zerto-pre   -> this script with -Phase pre  (pass via args)
        /hook/zerto-post  -> this script with -Phase post

    Or one endpoint per phase, each pointing at this script. The script reads
    `phase` from the JSON body, so the -Phase param is optional.

.NOTES
    Compatible with:
      - Slack Incoming Webhooks (posts {"text": "..."})
      - Teams legacy connector "Incoming Webhook" (same body shape)
      - Discord webhooks (use ?wait=true for body, but text is "content" not
        "text" - tweak below)

    Endpoint config:
        ExecutorType:   WindowsPowerShell or PowerShell 7
        ScriptPath:     C:\scripts\zerto-receiver-notify.ps1
        DataPassing:    [x] Stdin JSON
        ResponseMode:   async  (we don't need to block the VPG on a chat post)
#>

[CmdletBinding()]
param(
    [string] $NotifyUrl = $env:NOTIFY_URL  # set on the Webhook Server host, or hardcode below
)

$ErrorActionPreference = 'Stop'

if (-not $NotifyUrl) {
    # Fall back to a hardcoded URL if NOTIFY_URL env var isn't set.
    # Replace with your Slack/Teams Incoming Webhook URL.
    $NotifyUrl = 'https://hooks.slack.com/services/REPLACE/ME/HERE'
}

$body = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($body)) {
    Write-Error 'Empty stdin - expected JSON body from the webhook server.'
    exit 2
}
$p = $body | ConvertFrom-Json

$z     = $p.zerto
$phase = if ($p.phase) { $p.phase } else { 'unknown' }
$op    = if ($z.operation) { $z.operation } else { 'unknown' }

# Pick an icon based on operation. Test is benign; Failover/Move are real.
$icon = switch ($op) {
    'Test'     { ':test_tube:' }
    'Failover' { ':rotating_light:' }
    'Move'     { ':truck:' }
    default    { ':information_source:' }
}

$forceTag = if ($phase -eq 'pre' -and $z.force -eq 'Yes') { '  *(FORCE)*' } else { '' }

$lines = @(
    "$icon  *Zerto $op* - phase: ``$phase``$forceTag"
    "VPG: ``$($z.vpgName)``"
    "VMs: ``$($z.vmDisplayNames)``"
    "Hypervisor mgr: ``$($z.hypervisorManagerIP):$($z.hypervisorManagerPort)``"
    "Captured: $($p.capturedAt)  (from $($p.host))"
)
$text = $lines -join "`n"

$payload = @{ text = $text } | ConvertTo-Json -Compress

try {
    Invoke-RestMethod -Method Post -Uri $NotifyUrl `
        -ContentType 'application/json' -Body $payload -TimeoutSec 10 | Out-Null
    Write-Host "[$phase] notified $op for VPG '$($z.vpgName)'"
}
catch {
    Write-Error "Notification post failed: $($_.Exception.Message)"
    exit 1
}
