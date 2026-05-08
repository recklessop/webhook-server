<#
.SYNOPSIS
    End-to-end installer build: publish service + GUI, then run Inno Setup
    to produce dist/WebhookServer-Setup-{version}.exe.

.DESCRIPTION
    Reads the version from Directory.Build.props. Requires Inno Setup 6 (ISCC.exe)
    on PATH or in the standard install location. CI runs this same script after
    setup-dotnet + winget install Inno Setup.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$VersionOverride
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-RepoVersion {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    [xml]$props = Get-Content $propsPath
    return $props.Project.PropertyGroup.Version
}

function Find-InnoCompiler {
    $candidates = @(
        'ISCC.exe',  # on PATH
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    foreach ($c in $candidates) {
        $cmd = Get-Command $c -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Path }
        if (Test-Path $c) { return $c }
    }
    throw "Inno Setup compiler not found. Install with: winget install JRSoftware.InnoSetup"
}

$version = if ($VersionOverride) { $VersionOverride } else { Get-RepoVersion }
Write-Host "Building Webhook Server installer v$version" -ForegroundColor Cyan

# 1. Publish both projects.
$publishSvc = Join-Path $repoRoot 'publish\service'
$publishGui = Join-Path $repoRoot 'publish\gui'
Remove-Item -Recurse -Force $publishSvc, $publishGui -ErrorAction SilentlyContinue

& dotnet publish (Join-Path $repoRoot 'src\WebhookServer.Service\WebhookServer.Service.csproj') `
    -c $Configuration -r win-x64 --self-contained false -o $publishSvc | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'service publish failed' }

& dotnet publish (Join-Path $repoRoot 'src\WebhookServer.Gui\WebhookServer.Gui.csproj') `
    -c $Configuration -r win-x64 --self-contained false -o $publishGui | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed' }

# 2. Compile installer.
$iscc = Find-InnoCompiler
$iss = Join-Path $repoRoot 'installer\webhook-server.iss'
$dist = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null

Write-Host "Compiling installer with $iscc"
& $iscc "/DAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed' }

$out = Get-Item (Join-Path $dist "WebhookServer-Setup-$version.exe")
Write-Host ""
Write-Host ("Built: {0}  ({1:n0} bytes)" -f $out.FullName, $out.Length) -ForegroundColor Green
