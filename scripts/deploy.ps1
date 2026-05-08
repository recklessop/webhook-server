<#
.SYNOPSIS
    Builds, publishes, copies, installs, and starts WebhookServer as a Windows Service
    running under LocalSystem.

.DESCRIPTION
    Idempotent — safe to re-run after code changes. Stops the service first so binaries
    aren't locked, copies the latest published output to InstallRoot, then re-creates or
    re-configures the service and starts it.

    Must be run from an elevated PowerShell.

.PARAMETER InstallRoot
    Where the binaries get copied. Defaults to "C:\Program Files\WebhookServer".

.PARAMETER ServiceAccount
    Service identity. Defaults to LocalSystem. For AD-aware hooks pass a domain user
    or gMSA — see the Service account section in README.md.

.PARAMETER SkipBuild
    Skip the dotnet publish step (use the existing publish\ output as-is).

.EXAMPLE
    # First-time install (and after any code change)
    .\deploy.ps1

.EXAMPLE
    # Run service under a gMSA
    .\deploy.ps1 -ServiceAccount 'CONTOSO\svc-webhookserver$'
#>
[CmdletBinding()]
param(
    [string]$InstallRoot   = 'C:\Program Files\WebhookServer',
    [string]$ServiceName   = 'WebhookServer',
    [string]$ServiceAccount = 'LocalSystem',
    [string]$Password,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'deploy.ps1 must be run from an elevated PowerShell.'
}

$repoRoot     = Split-Path -Parent $PSScriptRoot
$publishSvc   = Join-Path $repoRoot 'publish\service'
$publishGui   = Join-Path $repoRoot 'publish\gui'

# 1. Stop the service if it's already installed so its binaries aren't locked.
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    Write-Host "Stopping existing service '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force
    $svc.WaitForStatus('Stopped', '00:00:30')
}

# Belt-and-braces: kill any orphan dev-launch processes still holding the binaries.
Get-Process -Name 'WebhookServer.Service','WebhookServer.Gui' -ErrorAction SilentlyContinue |
    ForEach-Object { try { $_ | Stop-Process -Force } catch { } }

# 2. Publish (unless told to skip).
if (-not $SkipBuild) {
    Write-Host 'Publishing service + GUI...'
    & dotnet publish (Join-Path $repoRoot 'src\WebhookServer.Service\WebhookServer.Service.csproj') `
        -c Release -r win-x64 --self-contained false -o $publishSvc | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'service publish failed' }

    & dotnet publish (Join-Path $repoRoot 'src\WebhookServer.Gui\WebhookServer.Gui.csproj') `
        -c Release -r win-x64 --self-contained false -o $publishGui | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed' }
}

# 3. Copy binaries into InstallRoot.
Write-Host "Copying binaries to $InstallRoot..."
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Copy-Item -Path (Join-Path $publishSvc '*') -Destination $InstallRoot -Recurse -Force
Copy-Item -Path (Join-Path $publishGui '*') -Destination $InstallRoot -Recurse -Force

$serviceExe = Join-Path $InstallRoot 'WebhookServer.Service.exe'
$guiExe     = Join-Path $InstallRoot 'WebhookServer.Gui.exe'

# 4. Create or update the Windows Service via install-service.ps1.
$installArgs = @{
    BinaryPath     = $serviceExe
    ServiceName    = $ServiceName
    ServiceAccount = $ServiceAccount
}
if ($PSBoundParameters.ContainsKey('Password')) { $installArgs.Password = $Password }
& (Join-Path $PSScriptRoot 'install-service.ps1') @installArgs

# 5. Show how to launch the GUI.
Write-Host ''
Write-Host '=== Deployed ===' -ForegroundColor Green
Write-Host "  Service exe : $serviceExe"
Write-Host "  GUI exe     : $guiExe"
Write-Host "  Config      : $env:ProgramData\WebhookServer\config.json"
Write-Host "  Logs        : $env:ProgramData\WebhookServer\logs"
Write-Host ''
Write-Host 'Launch the GUI (must stay elevated to talk to the admin pipe):'
Write-Host "  Start-Process -FilePath '$guiExe' -Verb RunAs"
