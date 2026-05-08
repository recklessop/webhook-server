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

# 2. Pre-flight: confirm every source path the .iss references exists, and
#    surface the longest path so MAX_PATH issues are obvious in the log.
function Show-SourcePath($label, $path, [switch]$Recursive) {
    if (-not (Test-Path $path)) { Write-Warning "MISSING $label : $path"; return }
    $items = if ($Recursive) {
        Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue
    } else {
        Get-ChildItem $path -File -ErrorAction SilentlyContinue
    }
    $count   = ($items | Measure-Object).Count
    $longest = ($items | Measure-Object -Maximum -Property { $_.FullName.Length }).Maximum
    Write-Host ("  {0,-30} files={1,-5} longestPath={2,-5} root={3}" -f $label, $count, $longest, $path)
}

Write-Host ""
Write-Host "--- pre-flight: source paths the .iss will read ---" -ForegroundColor Cyan
Show-SourcePath 'publish\service'        $publishSvc                              -Recursive
Show-SourcePath 'publish\gui'            $publishGui                              -Recursive
Show-SourcePath 'scripts'                (Join-Path $repoRoot 'scripts')
Show-SourcePath 'scripts\examples'       (Join-Path $repoRoot 'scripts\examples') -Recursive
Show-SourcePath 'docs'                   (Join-Path $repoRoot 'docs')             -Recursive
Show-SourcePath 'resources'              (Join-Path $repoRoot 'resources')
Show-SourcePath 'README.md (file)'       (Join-Path $repoRoot 'README.md')

$lpe = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' `
            -Name LongPathsEnabled -ErrorAction SilentlyContinue).LongPathsEnabled
Write-Host "  LongPathsEnabled (HKLM): $lpe"
Write-Host ""

# 3. Compile installer.
$iscc = Find-InnoCompiler
$iss = Join-Path $repoRoot 'installer\webhook-server.iss'
$dist = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null

Write-Host "Compiling installer with $iscc"
# Run ISCC from the .iss directory with just the bare filename. When invoked
# with a deeply-nested absolute path on the act-runner host (under
# %SystemRoot%\System32\config\systemprofile\...), ISCC sometimes prints a
# generic "The system cannot find the path specified." before it touches any
# source files. cd-ing first sidesteps it.
$issDir  = Split-Path $iss -Parent
$issName = Split-Path $iss -Leaf

# Extra pre-flight: confirm the specific files our .iss references that a
# trivial test .iss wouldn't (icon, README, scripts) actually exist relative
# to the .iss directory the way ISCC will resolve them (RepoRoot = ..\).
Write-Host "--- pre-flight: paths the .iss references via {#RepoRoot} ---" -ForegroundColor Cyan
$issRefs = @(
    'resources\webhook-server.ico',
    'README.md',
    'scripts\install-service.ps1',
    'scripts\uninstall-service.ps1',
    'publish\service',
    'publish\gui',
    'docs',
    'scripts\examples'
)
foreach ($ref in $issRefs) {
    $abs = Join-Path $repoRoot $ref
    $exists = Test-Path $abs
    Write-Host ("  {0,-40} exists={1}  ({2})" -f $ref, $exists, $abs)
}
Write-Host ""

Push-Location $issDir
try {
    Write-Host "  cwd=$issDir"
    # Capture stdout+stderr together so any error line ISCC emits is visible
    # in the runner log even if the runner's console capture drops one stream.
    $logPath = Join-Path $env:TEMP "iscc-$version.log"
    & $iscc "/DAppVersion=$version" $issName *>&1 | Tee-Object -FilePath $logPath | ForEach-Object { Write-Host $_ }
    $exit = $LASTEXITCODE
    Write-Host "  ISCC exit code: $exit"
    Write-Host "  ISCC log:       $logPath"
} finally {
    Pop-Location
}
if ($exit -ne 0) { throw "Inno Setup compile failed (exit $exit)" }

$out = Get-Item (Join-Path $dist "WebhookServer-Setup-$version.exe")
Write-Host ""
Write-Host ("Built: {0}  ({1:n0} bytes)" -f $out.FullName, $out.Length) -ForegroundColor Green
