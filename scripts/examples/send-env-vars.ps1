<#
.SYNOPSIS
    Collects env vars from PowerShell and bash, packages them into a single
    JSON object, and POSTs the result to a Webhook Server endpoint.

.DESCRIPTION
    Output JSON shape:
        {
          "host":      "<computername>",
          "capturedAt":"2026-05-08T12:34:56Z",
          "pwsh":      { "VAR": "value", ... },
          "bash":      { "VAR": "value", ... }
        }

    Pair this with `save-env-vars.ps1` on the server side - configure an
    endpoint with StdinJson enabled and that script as the executable.
#>

[CmdletBinding()]
param(
    [string] $WebhookUrl = 'http://localhost:8080/hook/env-dump',
    [string] $Bearer     = '',
    [string] $BashExe    = 'bash'
)

$ErrorActionPreference = 'Stop'

# --- pwsh env vars --------------------------------------------------------
$pwshVars = [ordered]@{}
Get-ChildItem Env: | Sort-Object Name | ForEach-Object {
    $pwshVars[$_.Name] = $_.Value
}

# --- bash env vars --------------------------------------------------------
$bashVars = [ordered]@{}
$bashCmd  = Get-Command $BashExe -ErrorAction SilentlyContinue
if ($null -ne $bashCmd) {
    # `env -0` separates entries with NUL so values containing newlines stay intact.
    $raw = & $bashCmd.Source -c 'env -0' 2>$null
    if ($LASTEXITCODE -eq 0 -and $raw) {
        foreach ($entry in ($raw -split "`0")) {
            if ([string]::IsNullOrEmpty($entry)) { continue }
            $eq = $entry.IndexOf('=')
            if ($eq -lt 1) { continue }
            $bashVars[$entry.Substring(0, $eq)] = $entry.Substring($eq + 1)
        }
    }
} else {
    Write-Warning "bash not found on PATH (looked for '$BashExe'); 'bash' section will be empty."
}

# --- assemble payload -----------------------------------------------------
$payload = [ordered]@{
    host       = $env:COMPUTERNAME
    capturedAt = (Get-Date).ToUniversalTime().ToString('o')
    pwsh       = $pwshVars
    bash       = $bashVars
}

$json = $payload | ConvertTo-Json -Depth 5 -Compress

# --- POST -----------------------------------------------------------------
$headers = @{ 'Content-Type' = 'application/json' }
if ($Bearer) { $headers['Authorization'] = "Bearer $Bearer" }

Write-Host "POST $WebhookUrl  ($($json.Length) bytes; pwsh=$($pwshVars.Count), bash=$($bashVars.Count))"
$response = Invoke-RestMethod -Method Post -Uri $WebhookUrl -Headers $headers -Body $json
$response | ConvertTo-Json -Depth 5
