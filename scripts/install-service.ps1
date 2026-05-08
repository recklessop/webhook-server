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
    (DOMAIN\svc-name$ - note the trailing $). Domain users require -Password.
    Never pass LocalService - it has no network identity and cannot reach a DC.

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

# sc.exe argv format: "key= value" - space AFTER equals, none before.
$obj = $ServiceAccount
# Get-Service returns $null when the service doesn't exist; sc.exe query is unreliable
# because it writes a FAILED line to stdout that makes truthy checks pass.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existing) {
    Write-Host "Service '$ServiceName' already exists; updating binPath and account."
    $configArgs = @(
        'config', $ServiceName,
        'binPath=', "`"$BinaryPath`"",
        'obj=', $obj
    )
    if ($Password) { $configArgs += @('password=', $Password) }
    sc.exe @configArgs
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed with exit code $LASTEXITCODE" }
} else {
    Write-Host "Creating service '$ServiceName'..."
    $createArgs = @(
        'create', $ServiceName,
        'binPath=', "`"$BinaryPath`"",
        'DisplayName=', "`"$DisplayName`"",
        'start=', 'auto',
        'obj=', $obj
    )
    if ($Password) { $createArgs += @('password=', $Password) }
    sc.exe @createArgs
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE" }
}

# Configure failure recovery: restart the service on first/second failure, reset count after a day.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000
if ($LASTEXITCODE -ne 0) { Write-Warning "sc.exe failure returned $LASTEXITCODE (recovery actions may not be set)" }

Write-Host "Starting service '$ServiceName'..."
sc.exe start $ServiceName
if ($LASTEXITCODE -ne 0) { throw "sc.exe start failed with exit code $LASTEXITCODE - check Event Viewer (Windows Logs > Application) for details" }

Start-Sleep -Seconds 1
sc.exe query $ServiceName
