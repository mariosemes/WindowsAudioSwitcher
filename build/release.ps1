<#
.SYNOPSIS
    One-shot release builder for Windows Audio Switcher.

.DESCRIPTION
    Publishes a self-contained, single-file win-x64 build of the WPF app, then
    runs the Inno Setup compiler to produce a per-user installer at:

        dist\WindowsAudioSwitcher-<Version>-setup.exe

    Repo layout: two independent remotes. `origin` is Gitea, which keeps the
    full dev history. `github` is a separate orphan-history remote — public,
    one squashed commit per release, no past commits ever visible.

    Publish flow (when -Local is NOT passed):
      1. Bump csproj <Version>, commit to main, push main + tag to origin (Gitea)
      2. Create a fresh orphan branch from main HEAD; force-push it as
         github/main (replacing GitHub's prior single-commit snapshot)
      3. `gh release create` on the GitHub side — creates the tag at the new
         orphan HEAD and uploads the installer as the release asset

    Releases are published via the `gh` CLI. Install it once with
    `winget install GitHub.cli` and authenticate with `gh auth login`.

    Version handling: omit -Version and the script reads the latest v* git tag
    and bumps PATCH (default), MINOR (-Minor), or MAJOR (-Major). A segment
    that hits 10 rolls into the next-higher one — patch 0.4.9 + bump becomes
    0.5.0, and 0.9.9 + bump becomes 1.0.0.

.PARAMETER Version
    Explicit semantic version, e.g. 1.2.3. Stamped into the assembly +
    installer. Overrides auto-bump. Optional — leave it off to auto-bump.

.PARAMETER Minor
    Bump the minor segment (resets patch). Ignored if -Version is given.

.PARAMETER Major
    Bump the major segment (resets minor + patch). Ignored if -Version is given.

.PARAMETER Local
    Skip the publish phase: no git tag, no push, no GitHub release. Just builds
    the installer locally.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipPublish
    Skip the dotnet publish step (useful when iterating on the .iss script).

.PARAMETER SkipInstaller
    Skip the Inno Setup step. Implies you only want the published bits.

.PARAMETER IsccPath
    Override the path to Inno Setup's ISCC.exe.

.PARAMETER GitHubRepo
    GitHub repo for releases, in `owner/name` format. Defaults to
    "mariosemes/WindowsAudioSwitcher".

.PARAMETER ReleaseBody
    Markdown body of the GitHub release. Defaults to an auto-generated note.

.PARAMETER PreRelease
    Mark the GitHub release as a pre-release.

.EXAMPLE
    # Auto-bump patch and publish (the happy path)
    .\build\release.ps1

.EXAMPLE
    # Auto-bump minor instead, still publishes
    .\build\release.ps1 -Minor

.EXAMPLE
    # Local-only build with an explicit version
    .\build\release.ps1 -Version 0.5.0-rc1 -Local
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [switch]$Minor,
    [switch]$Major,
    [switch]$Local,

    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipPublish,
    [switch]$SkipInstaller,
    [string]$IsccPath,

    [string]$GitHubRepo = 'mariosemes/WindowsAudioSwitcher',
    [string]$ReleaseBody,
    [switch]$PreRelease
)

$ErrorActionPreference = 'Stop'

# PS 5.1 wraps native stderr lines as ErrorRecord when EAP=Stop, so even
# successful git pushes (which print "remote: ..." to stderr) abort the
# script. Relax just for git calls and trust $LASTEXITCODE.
function Invoke-Git {
    $oldEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & git @args 2>&1 | Out-Null }
    finally { $ErrorActionPreference = $oldEAP }
    return $LASTEXITCODE
}

# --- Resolve target version (auto-bump unless -Version was explicit) ---------
function Get-LatestSemverTag {
    # `git tag --sort=-v:refname` orders semver-aware. We require a clean
    # vMAJOR.MINOR.PATCH (no pre-release suffix) so the bumper has integers
    # to work with. Pre-release-tagged versions are ignored as a starting point.
    $tags = git tag --list 'v*' --sort=-v:refname 2>$null
    if (-not $tags) { return $null }
    foreach ($t in $tags) {
        if ($t -match '^v(\d+)\.(\d+)\.(\d+)$') { return $t }
    }
    return $null
}

if ($Version) {
    if ($Minor -or $Major) {
        Write-Host "Note: -Version given; ignoring -Minor / -Major." -ForegroundColor Yellow
    }
} else {
    if ($Minor -and $Major) { throw "Pass at most one of -Minor / -Major." }

    $latest = Get-LatestSemverTag
    if ($latest) {
        [void]($latest -match '^v(\d+)\.(\d+)\.(\d+)$')
        $maj = [int]$Matches[1]; $min = [int]$Matches[2]; $pat = [int]$Matches[3]
        $baseLabel = $latest
    } else {
        $maj = 0; $min = 0; $pat = 0
        $baseLabel = '(no prior tag — starting from 0.0.0)'
        Write-Host "No v* tag found; first auto-bump will produce v0.0.1 (or 0.1.0 / 1.0.0)." -ForegroundColor Yellow
    }

    if     ($Major) { $maj++; $min = 0; $pat = 0;       $segment = 'major' }
    elseif ($Minor) { $min++; $pat = 0;                 $segment = 'minor' }
    else            { $pat++;                           $segment = 'patch' }

    # Cascade: a segment that hits 10 rolls into the next-higher one.
    if ($pat -ge 10) { $pat = 0; $min++ }
    if ($min -ge 10) { $min = 0; $maj++ }

    $Version = "$maj.$min.$pat"
    Write-Host "Auto-bump ($segment): $baseLabel  →  v$Version" -ForegroundColor Cyan
}

# Publish to GitHub is the default; -Local opts out.
$publish = -not $Local

# --- Paths -------------------------------------------------------------------
$root        = Split-Path -Parent $PSScriptRoot
$projectDir  = Join-Path $root 'src\WindowsAudioSwitcher'
$projectFile = Join-Path $projectDir 'WindowsAudioSwitcher.csproj'
$publishDir  = Join-Path $projectDir "bin\$Configuration\net8.0-windows\win-x64\publish"
$distDir     = Join-Path $root 'dist'
$issFile     = Join-Path $root 'installer\installer.iss'
$tag         = "v$Version"

if (-not (Test-Path $projectFile)) { throw "Project not found: $projectFile" }
if (-not (Test-Path $issFile))     { throw "Installer script not found: $issFile" }

Write-Host "==> Windows Audio Switcher release build" -ForegroundColor Cyan
Write-Host "    Version:       $Version  (tag: $tag)"
Write-Host "    Configuration: $Configuration"
Write-Host "    Project:       $projectFile"
Write-Host "    Output dir:    $distDir"
if ($publish) { Write-Host "    Publishing:    YES (GitHub repo: $GitHubRepo)" -ForegroundColor Green }
else          { Write-Host "    Publishing:    NO  (-Local set; nothing will be tagged/pushed)" -ForegroundColor Yellow }
Write-Host ''

# --- Locate dotnet -----------------------------------------------------------
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $candidate = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $candidate) { $dotnet = $candidate; $env:Path = "$(Split-Path $candidate);$env:Path" }
    else { throw "dotnet CLI not found on PATH and not at $candidate. Install the .NET 8 SDK." }
}

# --- Pre-flight for publishing -----------------------------------------------
if ($publish) {
    # Ensure `gh` is installed and authenticated.
    $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if (-not $gh) {
        throw @"
GitHub CLI not installed. Install with:
    winget install --id GitHub.cli
then authenticate:
    gh auth login
"@
    }

    & gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI is not authenticated. Run ``gh auth login`` and retry."
    }

    $dirty = (git status --porcelain) | Where-Object { $_ -ne '' }
    if ($dirty) {
        throw "Working tree is dirty. Commit or stash before publishing a release:`n$($dirty -join "`n")"
    }

    $existingTag = (git tag --list $tag) | Where-Object { $_ -eq $tag }
    if ($existingTag) {
        throw "Tag $tag already exists locally. Delete it (``git tag -d $tag``) and retry if you really mean to re-cut."
    }
}

# --- Publish -----------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host "==> dotnet publish" -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

    $stripped = ($Version -split '-')[0]
    $fourPart = "$([Version]::Parse($stripped).ToString(3)).0"
    $publishArgs = @(
        'publish', $projectFile,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '/p:PublishProfile=Installer',
        "/p:Version=$Version",
        "/p:AssemblyVersion=$fourPart",
        "/p:FileVersion=$fourPart",
        "/p:InformationalVersion=$Version",
        '--nologo',
        '-v', 'minimal'
    )
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
} else {
    Write-Host "==> Skipping publish (-SkipPublish)" -ForegroundColor Yellow
}

$publishedExe = Join-Path $publishDir 'WindowsAudioSwitcher.exe'
if (-not (Test-Path $publishedExe)) {
    throw "Expected published exe not found: $publishedExe"
}

# --- Installer ---------------------------------------------------------------
$installer = $null
if (-not $SkipInstaller) {
    if (-not $IsccPath) {
        $candidates = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        )
        $IsccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
        throw @"
Inno Setup compiler (ISCC.exe) not found. Install it with:
    winget install --id JRSoftware.InnoSetup
or pass -IsccPath 'C:\path\to\ISCC.exe'.
"@
    }

    if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

    Write-Host "==> ISCC: building installer" -ForegroundColor Cyan
    $iscArgs = @(
        "/DAppVersion=$Version",
        "/DSourceDir=$publishDir",
        "/DOutputDir=$distDir",
        $issFile
    )
    & $IsccPath @iscArgs
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }

    $installer = Get-ChildItem $distDir -Filter "WindowsAudioSwitcher-$Version-setup.exe" | Select-Object -First 1
    if ($null -eq $installer) { throw "Installer was not produced in $distDir" }

    $sha256 = (Get-FileHash $installer.FullName -Algorithm SHA256).Hash
    $sizeMb = '{0:N1}' -f ($installer.Length / 1MB)

    Write-Host ''
    Write-Host "==> Release built successfully" -ForegroundColor Green
    Write-Host "    Installer: $($installer.FullName)"
    Write-Host "    Size:      $sizeMb MB"
    Write-Host "    SHA-256:   $sha256"
}

# --- Publish to Gitea + GitHub -----------------------------------------------
# Repo layout note: `origin` is Gitea (preserves dev history) and `github` is
# a SEPARATE remote with its own orphan history — single commit per release,
# no past commits visible. So we publish in two passes:
#   1. Commit version bump + tag main HEAD + push to Gitea (history grows)
#   2. Build a fresh orphan branch from main HEAD, force-push to github/main,
#      then `gh release create` against that new HEAD (gh creates the tag on
#      GitHub at the orphan commit and uploads the installer asset).
if ($publish) {
    if (-not $installer) {
        throw "Cannot publish a release without an installer. Don't combine -SkipInstaller with publishing."
    }

    if (-not $ReleaseBody) {
        $ReleaseBody = @"
## Windows Audio Switcher $Version

Download **``$($installer.Name)``** below and run it. Per-user install, no admin required.

- SHA-256: ``$sha256``
- Size: $sizeMb MB
"@
    }

    # Defensive: an interrupted previous run can leave a stray orphan branch
    # that would block re-running. Clean it up before we start.
    $stale = (git branch --list github-init) | Where-Object { $_ -match 'github-init' }
    if ($stale) {
        Write-Host "==> Cleaning up stale github-init branch from a prior run" -ForegroundColor Yellow
        # Make sure we're not standing on it before deleting.
        $currentBranch = (git rev-parse --abbrev-ref HEAD).Trim()
        if ($currentBranch -eq 'github-init') {
            $rc = Invoke-Git checkout main
            if ($rc -ne 0) { throw "Could not switch off stale github-init branch." }
        }
        $rc = Invoke-Git branch -D github-init
        if ($rc -ne 0) { throw "Could not delete stale github-init branch." }
    }

    # ---- 1. Sync csproj <Version>, tag main HEAD, push to Gitea -------------
    # csproj bump happens BEFORE the tag so the tagged commit (and the orphan
    # we'll make from it) reflects the released version in the source.
    try {
        [xml]$csprojXml = Get-Content -LiteralPath $projectFile -Raw
        $csprojVersionNode = $csprojXml.SelectSingleNode('//Project/PropertyGroup/Version')
        if ($null -ne $csprojVersionNode -and $csprojVersionNode.InnerText -ne $Version) {
            $previousVersion = $csprojVersionNode.InnerText
            Write-Host "==> Bumping csproj <Version>: $previousVersion → $Version" -ForegroundColor Cyan
            $csprojVersionNode.InnerText = $Version

            $writer = New-Object System.Xml.XmlWriterSettings
            $writer.Indent = $true
            $writer.IndentChars = '  '
            $writer.OmitXmlDeclaration = $true
            $writer.Encoding = New-Object System.Text.UTF8Encoding($false)
            $sw = New-Object System.IO.StreamWriter($projectFile, $false, $writer.Encoding)
            try {
                $xw = [System.Xml.XmlWriter]::Create($sw, $writer)
                try { $csprojXml.Save($xw) } finally { $xw.Dispose() }
            } finally { $sw.Dispose() }

            $rc = Invoke-Git add $projectFile
            if ($rc -ne 0) { throw "git add of csproj failed." }
            $rc = Invoke-Git commit -m "Bump csproj <Version> to $Version after release"
            if ($rc -ne 0) { throw "git commit of csproj bump failed." }
        }
    } catch {
        throw "csproj auto-bump failed: $($_.Exception.Message)"
    }

    Write-Host "==> Creating tag $tag" -ForegroundColor Cyan
    $rc = Invoke-Git tag -a $tag -m "Release $tag"
    if ($rc -ne 0) { throw "git tag failed (exit $rc)" }

    Write-Host "==> Pushing main + tag to origin (Gitea)" -ForegroundColor Cyan
    $rc = Invoke-Git push origin main
    if ($rc -ne 0) { throw "git push origin main failed (exit $rc)" }
    $rc = Invoke-Git push origin $tag
    if ($rc -ne 0) { throw "git push origin $tag failed (exit $rc)" }

    # ---- 2. Re-orphan + force-push to GitHub --------------------------------
    # Each release replaces GitHub's main with a fresh single-commit snapshot
    # of the current source tree. The full dev history stays private on Gitea.
    Write-Host "==> Rebuilding GitHub orphan from main HEAD" -ForegroundColor Cyan
    $rc = Invoke-Git checkout --orphan github-init
    if ($rc -ne 0) { throw "git checkout --orphan failed (exit $rc)" }
    $rc = Invoke-Git add -A
    if ($rc -ne 0) { throw "git add -A on orphan failed (exit $rc)" }
    $rc = Invoke-Git commit -m "Update — Windows Audio Switcher v$Version"
    if ($rc -ne 0) {
        # Recover: switch back to main so we don't strand the user on an
        # uncommitted orphan branch.
        Invoke-Git checkout main | Out-Null
        Invoke-Git branch -D github-init | Out-Null
        throw "git commit on orphan failed (exit $rc)"
    }

    Write-Host "==> Force-pushing orphan to github/main" -ForegroundColor Cyan
    $rc = Invoke-Git push -f github github-init:main
    if ($rc -ne 0) {
        Invoke-Git checkout main | Out-Null
        Invoke-Git branch -D github-init | Out-Null
        throw "git push -f to github failed (exit $rc)"
    }

    # Back to main; clean up the temporary orphan branch.
    $rc = Invoke-Git checkout main
    if ($rc -ne 0) { throw "git checkout main failed (exit $rc)" }
    $rc = Invoke-Git branch -D github-init
    if ($rc -ne 0) { throw "git branch -D github-init failed (exit $rc)" }

    # ---- 3. Create the GitHub release ---------------------------------------
    # gh creates the tag on the GitHub side at github/main HEAD (the new orphan
    # commit) and uploads the installer as a release asset. The Gitea-side tag
    # (pushed above) points at the real commit; both labels coexist independently.
    Write-Host "==> Creating GitHub release on $GitHubRepo" -ForegroundColor Cyan
    $ghArgs = @(
        'release', 'create', $tag,
        $installer.FullName,
        '--repo', $GitHubRepo,
        '--title', $tag,
        '--notes', $ReleaseBody
    )
    if ($PreRelease) { $ghArgs += '--prerelease' }

    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE)" }

    Write-Host ''
    Write-Host "==> Release published" -ForegroundColor Green
    Write-Host "    https://github.com/$GitHubRepo/releases/tag/$tag"
}
