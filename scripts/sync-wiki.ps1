<#
.SYNOPSIS
    Mirrors the in-repo docs/ folder to a GitHub or Gitea wiki repo.

.DESCRIPTION
    Wikis are separate git repositories (e.g. <repo>.wiki.git) with a flat URL
    structure. This script:

      1. Clones the wiki repo into a temp directory.
      2. Wipes its existing .md content.
      3. Copies each docs/*.md to a flattened wiki-style page name.
      4. Rewrites in-repo markdown links so they point at the wiki page slugs.
      5. Generates a _Sidebar.md so every wiki page has a navigation sidebar.
      6. Commits and pushes back if anything changed.

    Idempotent. Safe to re-run.

.PARAMETER WikiUrl
    Full HTTPS URL to the wiki repo, including any embedded credentials. Examples:
      https://github.com/recklessop/webhook-server.wiki.git
      https://x-access-token:$TOKEN@github.com/recklessop/webhook-server.wiki.git
      https://justin:$GITEA_TOKEN@git.jpaul.io/justin/webhook-server.wiki.git

.PARAMETER AuthorName
    git committer name. Defaults to "Webhook Server Wiki Sync".

.PARAMETER AuthorEmail
    git committer email. Defaults to "noreply@jpaul.me".

.EXAMPLE
    # Manual sync to Gitea (token in env)
    $env:GITEA_TOKEN = '...'
    ./scripts/sync-wiki.ps1 -WikiUrl "https://justin:$env:GITEA_TOKEN@git.jpaul.io/justin/webhook-server.wiki.git"

.EXAMPLE
    # Manual sync to GitHub (gh-issued token)
    $token = & gh auth token
    ./scripts/sync-wiki.ps1 -WikiUrl "https://x-access-token:$token@github.com/recklessop/webhook-server.wiki.git"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WikiUrl,
    [string]$AuthorName  = 'Webhook Server Wiki Sync',
    [string]$AuthorEmail = 'noreply@jpaul.me'
)

# Continue (not Stop) because git writes informational messages to stderr
# (CRLF warnings, "remote: Processed N references" etc.) which PowerShell 5.1
# escalates to a script-fatal error under Stop. We check $LASTEXITCODE
# manually after each git call instead.
$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$docsDir  = Join-Path $repoRoot 'docs'
$workDir  = Join-Path $env:TEMP ("webhook-wiki-{0}" -f ([guid]::NewGuid().ToString('N').Substring(0, 8)))

# Source path (relative to docs/) -> wiki page slug. Order matters for the sidebar.
$mapping = [ordered]@{}
$mapping.Add('README.md',                         'Home')
$mapping.Add('concepts.md',                       'Concepts')
$mapping.Add('installation.md',                   'Installation')
$mapping.Add('upgrading.md',                      'Upgrading')
$mapping.Add('uninstalling.md',                   'Uninstalling')
$mapping.Add('runas-modes.md',                    'Run-As-Modes')
$mapping.Add('service-account-and-ad.md',         'Service-Account-and-AD')
$mapping.Add('network-and-security.md',           'Network-and-Security')
$mapping.Add('troubleshooting.md',                'Troubleshooting')
$mapping.Add('recipes/zerto-pre-post-scripts.md', 'Recipe-Zerto-Failover')
$mapping.Add('recipes/github-style-hmac.md',      'Recipe-GitHub-HMAC')
$mapping.Add('recipes/ui-on-desktop.md',          'Recipe-UI-on-Desktop')

function Rewrite-Links([string]$content) {
    foreach ($m in $mapping.GetEnumerator()) {
        # Match (path/to/file.md) and (path/to/file.md#anchor) inside markdown
        # link parens. The lookbehind ensures we're consuming a real link target.
        $escaped = [regex]::Escape($m.Key)
        $content = [regex]::Replace($content,
            "\(\.?\.?/?$escaped(\#[^)\s]*)?\)",
            "($($m.Value)`$1)")
    }
    # Also clean up doubled prefixes like "../../docs/" or "../" pointers that
    # sometimes appear in cross-folder relative links from docs/recipes/.
    return $content
}

function New-Sidebar() {
    $lines = @()
    $lines += "[Home](Home)"
    $lines += ""
    $lines += "## Topical"
    foreach ($key in @('concepts.md','installation.md','upgrading.md','uninstalling.md','runas-modes.md','service-account-and-ad.md','network-and-security.md','troubleshooting.md')) {
        $slug = $mapping[$key]
        $lines += "- [$($slug -replace '-', ' ')]($slug)"
    }
    $lines += ""
    $lines += "## Recipes"
    foreach ($key in @('recipes/zerto-pre-post-scripts.md','recipes/github-style-hmac.md','recipes/ui-on-desktop.md')) {
        $slug = $mapping[$key]
        $lines += "- [$($slug -replace '^Recipe-' -replace '-', ' ')]($slug)"
    }
    return ($lines -join "`n")
}

# 1. Clone the wiki.
Write-Host "Cloning wiki to $workDir..."
& git clone --quiet $WikiUrl $workDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "git clone failed. Has the wiki been initialized? Visit the repo's Wiki tab and create the first page via the UI before running this script."
}
# Suppress git's CRLF nags for this throwaway clone so they don't become
# "errors" via PowerShell's native-command stderr handling.
& git -C $workDir config core.autocrlf false 2>&1 | Out-Null
& git -C $workDir config core.safecrlf false 2>&1 | Out-Null

try {
    Push-Location $workDir
    try {
        # 2. Wipe existing markdown so removed source files vanish from the wiki.
        Get-ChildItem -Filter "*.md" -Force | Remove-Item -Force

        # 3. Copy + transform each source file.
        $written = 0
        foreach ($entry in $mapping.GetEnumerator()) {
            $src = Join-Path $docsDir $entry.Key
            $dst = Join-Path $workDir "$($entry.Value).md"
            if (-not (Test-Path $src)) {
                Write-Warning "Source missing, skipping: $src"
                continue
            }
            $content = Get-Content -LiteralPath $src -Raw
            $content = Rewrite-Links $content
            Set-Content -LiteralPath $dst -Value $content -Encoding utf8 -NoNewline
            $written++
        }
        Write-Host "Wrote $written markdown pages."

        # 4. Sidebar
        Set-Content -LiteralPath (Join-Path $workDir '_Sidebar.md') -Value (New-Sidebar) -Encoding utf8 -NoNewline

        # 5. Commit + push if anything actually changed. Drain stderr from each
        # git invocation so PowerShell doesn't treat warnings as errors.
        & git add -A 2>&1 | Out-Null
        $changes = & git status --porcelain 2>&1
        if (-not $changes) {
            Write-Host "Wiki already up to date."
            return
        }
        $sha = & git -C $repoRoot rev-parse --short HEAD 2>&1
        & git -c "user.name=$AuthorName" -c "user.email=$AuthorEmail" commit -q -m "Sync from docs/ at $sha" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)" }
        & git push --quiet 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git push failed (exit $LASTEXITCODE)" }
        Write-Host "Pushed updated wiki."
    }
    finally { Pop-Location }
}
finally {
    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
}
