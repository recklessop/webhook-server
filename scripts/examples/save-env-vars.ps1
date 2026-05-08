<#
.SYNOPSIS
    Server-side receiver for the env-dump webhook. Reads the JSON body from
    stdin and writes it to a timestamped file on disk.

.DESCRIPTION
    Configure a webhook endpoint like this:
        Executable:        powershell.exe   (or pwsh.exe)
        Arguments:         -NoProfile -ExecutionPolicy Bypass -File C:\path\to\save-env-vars.ps1
        Data passing:      [x] Stdin JSON
        Run As:            Service (or any account that can write to $OutDir)

    Output goes to C:\ProgramData\WebhookServer\env-dumps\<host>-<utcstamp>.json
    by default; override with -OutDir.
#>

[CmdletBinding()]
param(
    [string] $OutDir = 'C:\ProgramData\WebhookServer\env-dumps'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$body = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($body)) {
    Write-Error 'Empty request body on stdin.'
    exit 2
}

# Parse so we can pull the host name for the filename, and to fail fast on
# malformed JSON before writing it.
$parsed = $body | ConvertFrom-Json
$hostName = if ($parsed.host) { $parsed.host } else { 'unknown' }
$safeHost = ($hostName -replace '[^A-Za-z0-9_.-]', '_')
$stamp    = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$path     = Join-Path $OutDir "$safeHost-$stamp.json"

# Persist the original body verbatim - keeps key ordering and avoids any
# round-trip surprises from ConvertTo-Json.
Set-Content -Path $path -Value $body -Encoding utf8

Write-Host "Saved $($body.Length) bytes to $path"
