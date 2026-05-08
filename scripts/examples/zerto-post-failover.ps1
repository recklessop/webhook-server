<#
.SYNOPSIS
    Zerto post-failover script. Fires the on-prem Webhook Server which does
    the real work (DNS updates, service health checks, notifications).

.DESCRIPTION
    Designed to be dropped into a Zerto VPG's post-recovery script slot. The
    Zerto Virtual Manager's PowerShell runner has a limited module set and
    runs scripts synchronously, so this script:

      - uses curl.exe (ships with Windows 10 1803+ / Server 2019+) instead
        of any module-dependent HTTP client;
      - calls an ASYNC webhook endpoint - the server returns 202 in
        milliseconds and runs the actual work in the background;
      - returns within seconds regardless of how long the post-failover
        actions take, so Zerto's failover sequence is never blocked.

    Wire this into your VPG via the Zerto UI:
        VPG settings -> Recovery -> Scripts -> Post-Recovery Script
        Path: C:\path\to\zerto-post-failover.ps1
        Parameters: leave empty (we read from $env:ZertoVPGName)

.NOTES
    Configure $WebhookUrl and either:
      - paste the bearer token directly into $Bearer (simplest, but the
        token then lives in this file), or
      - point $BearerFile at a file readable only by the ZVM service
        account (better - same threat model as Zerto's own credential
        storage).
#>

$ErrorActionPreference = 'Stop'

# ----------------------------- CONFIGURE ---------------------------------
$WebhookUrl = 'http://webhook.contoso.local:8080/hook/post-failover'
$Bearer     = ''                              # paste here, or use $BearerFile
$BearerFile = 'C:\ProgramData\Zerto\webhook-token.txt'   # one line: the token
# -------------------------------------------------------------------------

if (-not $Bearer -and (Test-Path $BearerFile)) {
    $Bearer = (Get-Content -LiteralPath $BearerFile -TotalCount 1).Trim()
}
if (-not $Bearer) {
    throw "No bearer token. Set `$Bearer in this script or write the token to $BearerFile."
}

# Compose the payload. Zerto exposes a few env vars; fall back gracefully.
$payload = @{
    operation = 'failover'
    vpg       = if ($env:ZertoVPGName) { $env:ZertoVPGName } else { 'unknown' }
    timestamp = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json -Compress

# curl on Windows handles long / quoted JSON better via @file than via -d "...".
$tempBody = Join-Path $env:TEMP ("zerto-webhook-{0}.json" -f ([guid]::NewGuid()))
$payload | Out-File -FilePath $tempBody -Encoding utf8 -NoNewline

try {
    Write-Host "POST $WebhookUrl  (vpg=$($env:ZertoVPGName))"
    & curl.exe `
        --silent --show-error --fail-with-body `
        --max-time 10 `
        -X POST `
        -H "Authorization: Bearer $Bearer" `
        -H "Content-Type: application/json" `
        -d "@$tempBody" `
        "$WebhookUrl"
    if ($LASTEXITCODE -ne 0) {
        # curl prints its own error to stderr; surface a non-zero exit so Zerto's
        # script log records the failure but we don't block the failover.
        Write-Warning "Webhook call failed with curl exit $LASTEXITCODE; continuing."
    } else {
        Write-Host "Webhook accepted (run id is in the response above)."
    }
}
finally {
    Remove-Item $tempBody -ErrorAction SilentlyContinue
}
