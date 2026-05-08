<#
.SYNOPSIS
    Dev launcher: starts the service in one window and the GUI in another, both
    pointing at an isolated data root so production %ProgramData% is not touched.

.DESCRIPTION
    MUST be run from an elevated PowerShell — the admin pipe is ACL'd to SYSTEM
    and the Administrators group, and a non-elevated process cannot connect.
#>
[CmdletBinding()]
param(
    [string]$DataRoot = (Join-Path $env:TEMP 'webhook-dev'),
    [int]$HttpPort = 18080
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$servicePath = Join-Path $root 'publish\service\WebhookServer.Service.exe'
$guiPath     = Join-Path $root 'publish\gui\WebhookServer.Gui.exe'

if (-not (Test-Path $servicePath)) { throw "Service not built. Run: dotnet publish src/WebhookServer.Service -c Release -o publish/service" }
if (-not (Test-Path $guiPath))     { throw "GUI not built. Run: dotnet publish src/WebhookServer.Gui -c Release -o publish/gui" }

# Verify the current shell is elevated.
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated PowerShell so the GUI can connect to the SYSTEM/Admins-only admin pipe.'
}

New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $DataRoot 'logs') -Force | Out-Null

$cfgPath = Join-Path $DataRoot 'config.json'
if (-not (Test-Path $cfgPath)) {
    $cfg = @"
{
  "httpPort": $HttpPort,
  "trustedProxies": [],
  "logRetentionDays": 7,
  "endpoints": [
    {
      "id": "11111111-1111-1111-1111-111111111111",
      "slug": "ping",
      "description": "Trivial sync hook",
      "enabled": true,
      "allowedClients": [],
      "authMode": "none",
      "executorType": "windowsPowerShell",
      "inlineCommand": "Write-Output 'pong'",
      "executableArgs": [],
      "dataPassing": { "stdinJson": false, "envVars": false, "argTemplate": false },
      "responseMode": "sync",
      "timeoutSeconds": 30,
      "failOnNonZeroExit": true,
      "serialize": false
    }
  ]
}
"@
    Set-Content -Path $cfgPath -Value $cfg -Encoding utf8
}

Write-Host "Data root  : $DataRoot"
Write-Host "Config     : $cfgPath"
Write-Host "Service exe: $servicePath"
Write-Host "GUI exe    : $guiPath"
Write-Host ""

$serviceArgs = @(
    '-NoExit', '-NoProfile', '-Command',
    "`$env:WEBHOOKSERVER_DATA = '$DataRoot'; & '$servicePath'"
)
Start-Process powershell -ArgumentList $serviceArgs -WindowStyle Normal

Start-Sleep -Seconds 2

# GUI inherits this shell's environment automatically.
$env:WEBHOOKSERVER_DATA = $DataRoot
Start-Process -FilePath $guiPath

Write-Host "Service window opened; GUI launched."
Write-Host "Hit http://localhost:$HttpPort/healthz to confirm Kestrel is up."
Write-Host "Logs: $(Join-Path $DataRoot 'logs')"
