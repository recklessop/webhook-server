<#
.SYNOPSIS
    Installs and starts the WebhookServer Windows Service.

.DESCRIPTION
    Creates the service via sc.exe pointed at the published WebhookServer.Service.exe,
    sets it to start automatically, and starts it. Re-running the script with the same
    BinaryPath updates the binary path of an existing service.

.PARAMETER BinaryPath
    Full path to WebhookServer.Service.exe. Defaults to .\publish\WebhookServer.Service.exe
    relative to the script.

.PARAMETER ServiceAccount
    Account to run the service under. Defaults to LocalSystem.
    For Active-Directory-aware hooks pass a domain user (DOMAIN\user) or a gMSA
    (DOMAIN\svc-name$ — note the trailing $). Domain users require -Password.
    Never pass LocalService — it has no network identity and cannot reach a DC.

.PARAMETER Password
    Password for a domain-user account. Not required for LocalSystem, NetworkService,
    LocalService, or gMSA accounts.

.EXAMPLE
    .\install-service.ps1 -BinaryPath C:\WebhookServer\WebhookServer.Service.exe

.EXAMPLE
    .\install-service.ps1 -BinaryPath C:\WebhookServer\WebhookServer.Service.exe `
        -ServiceAccount 'CONTOSO\svc-webhookserver$'
#>
[CmdletBinding()]
param(
    [string]$BinaryPath = (Join-Path $PSScriptRoot '..\publish\WebhookServer.Service.exe'),
    [string]$ServiceName = 'WebhookServer',
    [string]$DisplayName = 'Webhook Server',
    [string]$ServiceAccount = 'LocalSystem',
    [string]$Password
)

$ErrorActionPreference = 'Stop'

if ($ServiceAccount -ieq 'LocalService') {
    throw 'LocalService has no network identity and cannot talk to a domain controller. Use LocalSystem, a domain user, or a gMSA instead.'
}

$BinaryPath = (Resolve-Path -LiteralPath $BinaryPath).Path
if (-not (Test-Path -LiteralPath $BinaryPath)) {
    throw "Binary not found: $BinaryPath"
}

# sc.exe argv format: "key= value" — space AFTER equals, none before.
$obj = $ServiceAccount
$existing = sc.exe query $ServiceName 2>$null

if ($existing) {
    Write-Host "Service '$ServiceName' already exists; updating binPath and account."
    $configArgs = @(
        'config', $ServiceName,
        'binPath=', "`"$BinaryPath`"",
        'obj=', $obj
    )
    if ($Password) { $configArgs += @('password=', $Password) }
    sc.exe @configArgs | Out-Null
} else {
    $createArgs = @(
        'create', $ServiceName,
        'binPath=', "`"$BinaryPath`"",
        'DisplayName=', "`"$DisplayName`"",
        'start=', 'auto',
        'obj=', $obj
    )
    if ($Password) { $createArgs += @('password=', $Password) }
    sc.exe @createArgs | Out-Null
}

# Configure failure recovery: restart the service on first/second failure, reset count after a day.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host "Starting service '$ServiceName'..."
sc.exe start $ServiceName | Out-Null

Start-Sleep -Seconds 1
sc.exe query $ServiceName
