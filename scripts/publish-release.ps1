<#
.SYNOPSIS
    Uploads the packaged release in Releases\ to GitHub Releases.

.DESCRIPTION
    Separate from release.ps1 on purpose. Packing is something you do a dozen
    times while getting a build right; publishing is something the whole internet
    can see, and folding it into the pack step would make it a side effect of
    iterating. This script is the deliberate act.

    It uploads as a DRAFT by default. A draft is invisible to the update feed, so
    a mistake costs nothing until you look at it on GitHub and press publish. Pass
    -Publish to go straight out.

    Authentication is the gh CLI's existing login, read at run time. No token is
    stored here, passed on a command line, or written anywhere.

.PARAMETER Publish
    Publish immediately instead of leaving a draft. The app's update feed only
    sees published, non-prerelease releases, so nothing updates until this
    happens.
#>
[CmdletBinding()]
param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'

$repo     = Split-Path $PSScriptRoot -Parent
$releases = Join-Path $repo 'Releases'
$repoUrl  = 'https://github.com/PetrPatter/snipwhiz'

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk not found. Install it with: dotnet tool install -g vpk"
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "gh not found. It is used only to read the token you are already logged in with."
}
if (-not (Test-Path $releases)) {
    throw "No Releases\ directory. Run scripts\release.ps1 first."
}

# --- The version being published, from the one place that holds it -----------
$node = Select-Xml -Path (Join-Path $repo 'Directory.Build.props') -XPath '/Project/PropertyGroup/Version'
$version = $node.Node.InnerText.Trim()

# The packages must actually match it. Publishing a Releases\ directory left over
# from a previous version is the quiet way to ship the wrong build, and it looks
# exactly like a successful publish.
$expected = Join-Path $releases "SnipwhizApp-$version-full.nupkg"
if (-not (Test-Path $expected)) {
    throw @"
Releases\ has no package for $version.

Expected: $(Split-Path $expected -Leaf)
Found:    $((Get-ChildItem $releases -Filter '*.nupkg' | ForEach-Object Name) -join ', ')

Run scripts\release.ps1 to build the current version before publishing it.
"@
}

# --- The token, from the login that already exists ---------------------------
$token = (gh auth token 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
    throw "gh is not logged in. Run: gh auth login"
}

$state = if ($Publish) { 'PUBLISHED — visible immediately, and the update feed will serve it' }
         else          { 'draft — invisible to the update feed until you publish it on GitHub' }
Write-Host "Uploading Snipwhiz $version to $repoUrl" -ForegroundColor Cyan
Write-Host "  as: $state" -ForegroundColor Yellow

vpk upload github `
    --outputDir $releases `
    --repoUrl $repoUrl `
    --token $token `
    --tag "v$version" `
    --releaseName "Snipwhiz $version" `
    --publish $Publish.IsPresent

if ($LASTEXITCODE -ne 0) { throw "vpk upload failed." }

Write-Host "`nDone. $repoUrl/releases" -ForegroundColor Green
if (-not $Publish) {
    Write-Host "Still a draft. Nothing updates until it is published." -ForegroundColor Yellow
}
