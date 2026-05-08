<#
.SYNOPSIS
    Webhook-server-side receiver: post-failover VM health check. Pings each
    VM in the VPG and probes a configurable TCP port; writes a per-run
    report to disk.

.DESCRIPTION
    Intended for the POST-recovery webhook only - on a Test or real Failover,
    once the VMs are powered on at the recovery site, we can spot-check that
    they responded to ICMP and that a known port is listening (RDP, SSH,
    HTTP, etc).

    Skips itself entirely on the pre-recovery phase (nothing's running yet)
    and on $z.operation values that don't bring VMs up.

    Wire up one endpoint:
        /hook/zerto-post -> this script
        DataPassing: [x] Stdin JSON
        ResponseMode: async

.NOTES
    VmDisplayNames is a comma-separated list for multi-VM VPGs; some Zerto
    versions wrap each name in parentheses (e.g. "vm1(1)(1)(1)") to disambig
    after Test failover. We strip the trailing parenthesised suffixes when
    resolving DNS so the recovered hostname is what we ping.

    Endpoint config:
        ExecutorType:   WindowsPowerShell or PowerShell 7
        ScriptPath:     C:\scripts\zerto-receiver-vm-healthcheck.ps1
        DataPassing:    [x] Stdin JSON
        ResponseMode:   async
        TimeoutSeconds: 120  (this script does network I/O - bump from default)
#>

[CmdletBinding()]
param(
    [int]    $ProbePort   = 3389,                                # RDP. Use 22 for Linux, 80/443 for web tier.
    [int]    $PingTimeout = 2000,                                # ms
    [string] $ReportDir   = 'C:\ProgramData\WebhookServer\zerto-healthchecks'
)

$ErrorActionPreference = 'Stop'

# --- read + parse payload -------------------------------------------------
$body = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($body)) {
    Write-Error 'Empty stdin.'
    exit 2
}
$p = $body | ConvertFrom-Json

$z     = $p.zerto
$phase = $p.phase
$op    = $z.operation

# Skip if this isn't a post-phase run for an op that powers VMs on.
if ($phase -ne 'post') {
    Write-Host "Phase '$phase' - nothing to check yet, skipping."
    exit 0
}
if ($op -notin @('Test','Failover','Move','FailoverBeforeCommit','FailoverDuringCommit')) {
    Write-Host "Operation '$op' doesn't bring VMs up; skipping."
    exit 0
}

# --- parse VM list --------------------------------------------------------
function Strip-ZertoSuffix {
    param([string] $name)
    # "ubuntu-2404(1)(1)(1)" -> "ubuntu-2404"
    return ($name -replace '(\([^)]*\))+\s*$','').Trim()
}

$rawNames = ($z.vmDisplayNames -split '[,;]') | ForEach-Object { $_.Trim() } |
            Where-Object { $_ }
if (-not $rawNames) {
    Write-Warning 'No VM display names in payload - nothing to check.'
    exit 0
}

# --- run checks -----------------------------------------------------------
$results = foreach ($raw in $rawNames) {
    $clean = Strip-ZertoSuffix $raw
    $pingOk = $false
    $portOk = $false
    $err    = $null

    try {
        $pingOk = (Test-Connection -ComputerName $clean -Count 1 -Quiet `
                    -TimeoutSeconds ([math]::Max(1, [int]($PingTimeout / 1000))) `
                    -ErrorAction Stop)
    } catch { $err = "ping: $($_.Exception.Message)" }

    try {
        $portOk = (Test-NetConnection -ComputerName $clean -Port $ProbePort `
                    -InformationLevel Quiet -WarningAction SilentlyContinue)
    } catch { $err = ($err, "port: $($_.Exception.Message)") -ne $null -join '; ' }

    [pscustomobject]@{
        DisplayName = $raw
        Resolved    = $clean
        PingOk      = $pingOk
        PortOk      = $portOk
        ProbePort   = $ProbePort
        Error       = $err
    }
}

# --- write report ---------------------------------------------------------
if (-not (Test-Path $ReportDir)) {
    New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null
}

$safeVpg = ($z.vpgName -replace '[^A-Za-z0-9_.-]','_')
$stamp   = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$file    = Join-Path $ReportDir "$safeVpg-$op-$stamp.json"

$report = [ordered]@{
    vpgName      = $z.vpgName
    operation    = $op
    phase        = $phase
    capturedAt   = $p.capturedAt
    completedAt  = (Get-Date).ToUniversalTime().ToString('o')
    probePort    = $ProbePort
    vms          = $results
    summary      = @{
        total        = $results.Count
        pingFailures = ($results | Where-Object { -not $_.PingOk }).Count
        portFailures = ($results | Where-Object { -not $_.PortOk }).Count
    }
}
$report | ConvertTo-Json -Depth 5 | Set-Content -Path $file -Encoding utf8

# Console output goes back via the webhook callback (if configured) so the
# Zerto-side script log shows a quick summary even though the call is async.
$bad = $report.summary.pingFailures + $report.summary.portFailures
Write-Host "[$op/$phase] $($z.vpgName): $($results.Count) VM(s), $bad issue(s). Report: $file"

# Exit non-zero if anything failed, so the webhook server's failOnNonZeroExit
# turns this into a 502 for the caller (and shows up in the run history).
if ($bad -gt 0) { exit 1 }
