<#
.SYNOPSIS
    Zerto pre/post script (ZVMA / Linux scripts-service edition). Reads the
    Zerto-injected environment variables and POSTs them to a Webhook Server
    endpoint as a structured JSON payload.

.DESCRIPTION
    Drop into a VPG's Recovery Scripts in the ZVM UI:
        VPG settings -> Recovery -> Scripts -> Pre / Post Recovery Script
        Path:       /app/scripts-files/zerto-zvma-send.ps1
        Parameters: -Phase pre   (or -Phase post on the post-recovery slot)

    Configure $WebhookUrl + $Bearer (or use the -WebhookUrl / -Bearer params
    so one script file can serve multiple VPGs / endpoints).

    Async by default - the call returns 202 in milliseconds and the actual
    work runs in the webhook server's background, so the VPG sequence is
    never blocked by slow downstream actions (DNS, notifications, etc.).

.NOTES
    The scripts-service container has pwsh 7 and curl available. This script
    uses Invoke-RestMethod to keep things native to PowerShell.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('pre', 'post')]
    [string] $Phase,

    [string] $WebhookUrl = 'http://192.168.50.250:8080/hook/zerto-{phase}',
    [string] $Bearer     = '',
    [int]    $TimeoutSec = 10
)

$ErrorActionPreference = 'Stop'

# Resolve {phase} placeholder so one URL template can route to /hook/zerto-pre
# and /hook/zerto-post. Plain URLs without the token work too.
$url = $WebhookUrl.Replace('{phase}', $Phase)

$payload = [ordered]@{
    phase      = $Phase
    capturedAt = (Get-Date).ToUniversalTime().ToString('o')
    host       = $env:HOSTNAME       # scripts-service pod name
    zerto      = [ordered]@{
        vpgName               = $env:ZertoVPGName
        internalVpgName       = $env:ZertoInternalVpgName
        operation             = $env:ZertoOperation        # Test / Failover / Move / ...
        force                 = $env:ZertoForce            # only meaningful pre
        vmDisplayNames        = $env:VmDisplayNames        # comma-separated for multi-VM VPGs
        hypervisorManagerIP   = $env:ZertoHypervisorManagerIP
        hypervisorManagerPort = $env:ZertoHypervisorManagerPort
        outputDir             = $env:ZertoOutputDir
        workingDir            = $env:ZertoWorkingDir
    }
}

$body = $payload | ConvertTo-Json -Depth 4 -Compress

$headers = @{ 'Content-Type' = 'application/json' }
if ($Bearer) { $headers['Authorization'] = "Bearer $Bearer" }

try {
    $resp = Invoke-RestMethod -Method Post -Uri $url -Headers $headers `
        -Body $body -TimeoutSec $TimeoutSec
    Write-Host "[$Phase] webhook accepted: $($resp | ConvertTo-Json -Compress)"
}
catch {
    # Pre/post failures should not block the VPG operation. Log loudly and exit 0
    # so Zerto's recovery sequence continues. Flip to `exit 1` if you want a
    # webhook outage to fail the failover.
    Write-Warning "[$Phase] webhook call failed: $($_.Exception.Message)"
}
