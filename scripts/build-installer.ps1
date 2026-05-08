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

Write-Host "--- runtime context ---" -ForegroundColor Cyan
Write-Host "  identity:      $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
Write-Host "  USERPROFILE:   $env:USERPROFILE"
Write-Host "  APPDATA:       $env:APPDATA"
Write-Host "  LOCALAPPDATA:  $env:LOCALAPPDATA"
Write-Host "  TEMP:          $env:TEMP"
$isccDir = Split-Path $iscc -Parent
Write-Host "  ISCC dir:      $isccDir"
foreach ($f in @('ISCC.exe','ISCmplr.dll','ISPP.dll','Default.isl','Compil32.exe')) {
    $p = Join-Path $isccDir $f
    Write-Host ("    {0,-15} exists={1}" -f $f, (Test-Path $p))
}
Write-Host ""

Write-Host "  PS  location (pre):  $((Get-Location).Path)"
Write-Host "  .NET cwd     (pre):  $([System.IO.Directory]::GetCurrentDirectory())"

Push-Location $issDir
$savedDotNetCwd = [System.IO.Directory]::GetCurrentDirectory()
[System.IO.Directory]::SetCurrentDirectory($issDir)
try {
    Write-Host "  PS  location (post): $((Get-Location).Path)"
    Write-Host "  .NET cwd     (post): $([System.IO.Directory]::GetCurrentDirectory())"

    # Sanity: compile a minimal .iss right next to ours BEFORE attempting the
    # real one. Minimal has no #defines, no [Code], no [Files], no compression
    # tweak - just the absolute floor of what ISCC will accept. If THIS fails
    # under the same SYSTEM context with the same identical exit/error, the
    # problem is environmental, not in our .iss content.
    $minIss = Join-Path $issDir "min-test.iss"
    @"
[Setup]
AppName=MinTest
AppVersion=1.0
AppId={{12345678-1234-1234-1234-123456789ABC}
DefaultDirName={pf}\MinTest
CreateAppDir=no
Uninstallable=no
OutputBaseFilename=mintest
OutputDir=$dist
"@ | Set-Content -Path $minIss -Encoding ascii
    Write-Host ""
    Write-Host "--- bisect step 1: minimal .iss ---" -ForegroundColor Cyan
    & $iscc (Split-Path $minIss -Leaf) *>&1 | ForEach-Object { Write-Host "  $_" }
    $minExit = $LASTEXITCODE
    Write-Host "  minimal exit: $minExit"
    Remove-Item $minIss -ErrorAction SilentlyContinue
    Write-Host ""

    # Bake the version into a temp .iss and override OutputDir to an absolute
    # path so nothing in the build depends on cwd resolution.
    $tempIss = Join-Path $issDir "webhook-server.gen.iss"
    $issBody = Get-Content $issName -Raw
    $pattern = '(?s)#ifndef AppVersion\s+#define AppVersion "[^"]*"\s+#endif'
    if ($issBody -notmatch $pattern) { throw "Could not find #ifndef AppVersion block in $issName" }
    $issBody = $issBody -replace $pattern, "#define AppVersion `"$version`""
    Set-Content -Path $tempIss -Value $issBody -Encoding ascii
    Write-Host "  using $tempIss"

    # Capture stdout+stderr together so any error line ISCC emits is visible
    # in the runner log even if the runner's console capture drops one stream.
    # /O<absolute> overrides OutputDir so ..\dist isn't resolved relative to
    # whatever cwd ISCC actually inherits.
    $logPath = Join-Path $env:TEMP "iscc-$version.log"
    & $iscc "/O$dist" (Split-Path $tempIss -Leaf) *>&1 | Tee-Object -FilePath $logPath | ForEach-Object { Write-Host $_ }
    $exit = $LASTEXITCODE
    Write-Host "  ISCC exit code: $exit"
    Write-Host "  ISCC log path:  $logPath"
    if (Test-Path $logPath) {
        Write-Host "  --- iscc log file contents ---"
        Get-Content $logPath | ForEach-Object { Write-Host "    $_" }
        Write-Host "  --- end iscc log ---"
    }
    Remove-Item $tempIss -ErrorAction SilentlyContinue
} finally {
    [System.IO.Directory]::SetCurrentDirectory($savedDotNetCwd)
    Pop-Location
}
if ($exit -ne 0) { throw "Inno Setup compile failed (exit $exit)" }

$out = Get-Item (Join-Path $dist "WebhookServer-Setup-$version.exe")
Write-Host ""
Write-Host ("Built: {0}  ({1:n0} bytes)" -f $out.FullName, $out.Length) -ForegroundColor Green
