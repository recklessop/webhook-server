<#
.SYNOPSIS
    Stops and removes the WebhookServer Windows Service.

.DESCRIPTION
    Leaves C:\ProgramData\WebhookServer (config + logs) untouched. Pass -PurgeData
    to remove that directory as well.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'WebhookServer',
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'

$existing = sc.exe query $ServiceName 2>$null
if (-not $existing) {
    Write-Host "Service '$ServiceName' is not installed."
    return
}

Write-Host "Stopping service '$ServiceName'..."
sc.exe stop $ServiceName 2>$null | Out-Null
Start-Sleep -Seconds 2

Write-Host "Deleting service '$ServiceName'..."
sc.exe delete $ServiceName | Out-Null

if ($PurgeData) {
    $dataRoot = Join-Path $env:ProgramData 'WebhookServer'
    if (Test-Path -LiteralPath $dataRoot) {
        Write-Host "Removing $dataRoot"
        Remove-Item -LiteralPath $dataRoot -Recurse -Force
    }
}

Write-Host 'Done.'
