<#
.SYNOPSIS
    Publishes, verifies and packages a Snipwhiz release.

.DESCRIPTION
    Produces Setup.exe, a portable zip and a versioned .nupkg in Releases\, plus a
    delta package against whatever previous releases are already sitting there.

    The version comes from Directory.Build.props and is never typed here. The one
    check worth its lines is Step 3: a stray <Version> in any .csproj silently
    beats Directory.Build.props, with no warning from MSBuild, which was observed
    rather than guessed at. Packing is the only moment that drift becomes
    permanent, so this refuses to pack through it.

.PARAMETER Sign
    Signing is off unless configured, and configuring it is an environment
    variable rather than an edit to this file, so no certificate detail ever
    reaches the repository:

        SNIPWHIZ_SIGN_TEMPLATE   a command with {{file}} substituted (preferred:
                                 works with Azure Artifact Signing and any
                                 hardware-token tool)
        SNIPWHIZ_SIGN_PARAMS     parameters passed to signtool.exe

    With neither set the build is unsigned and says so. See spec section 4.4 for
    what that costs the person on the other end.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repo     = Split-Path $PSScriptRoot -Parent
$project  = Join-Path $repo 'src\Snipwhiz.App\Snipwhiz.App.csproj'
$publish  = Join-Path $repo 'publish'
$releases = Join-Path $repo 'Releases'

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk not found. Install it with: dotnet tool install -g vpk"
}

# --- 1. The version, from the one place that holds it -----------------------
$node = Select-Xml -Path (Join-Path $repo 'Directory.Build.props') -XPath '/Project/PropertyGroup/Version'
if ($node -isnot [System.Management.Automation.PSCustomObject] -and $node.Count -ne 1) {
    throw "Expected exactly one <Version> in Directory.Build.props, found $($node.Count)."
}
$version = $node.Node.InnerText.Trim()
Write-Host "Snipwhiz $version" -ForegroundColor Cyan

# --- 2. Publish, self-contained ---------------------------------------------
# Removed first: dotnet publish overwrites but never deletes, so a file dropped
# from the project would otherwise be packaged forever.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "Publishing..." -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# --- 3. The version the binary actually carries ------------------------------
# InformationalVersion carries a +commit suffix when the SDK stamps one; the part
# before it is the semver the installer and the feed use.
$exe     = Join-Path $publish 'Snipwhiz.App.exe'
$stamped = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
if ($stamped -ne $version) {
    throw @"
Version drift. Directory.Build.props says '$version' but the published binary says '$stamped'.

A <Version> in a .csproj overrides Directory.Build.props silently. Remove it, so
the installer, the feed and the About text cannot disagree.
"@
}
Write-Host "Version agrees: props and binary both say $version" -ForegroundColor Green

# --- 4. Signing, or an honest lack of it -------------------------------------
$sign = @()
if ($env:SNIPWHIZ_SIGN_TEMPLATE) {
    $sign = @('--signTemplate', $env:SNIPWHIZ_SIGN_TEMPLATE)
    Write-Host "Signing via template." -ForegroundColor Green
}
elseif ($env:SNIPWHIZ_SIGN_PARAMS) {
    $sign = @('--signParams', $env:SNIPWHIZ_SIGN_PARAMS)
    Write-Host "Signing via signtool." -ForegroundColor Green
}
else {
    Write-Warning "Unsigned. Everyone who runs Setup.exe will see 'Windows protected your PC' and must click More info -> Run anyway. Unsigned reputation is per-file, so this happens on every release."
}

# --- 5. Pack -----------------------------------------------------------------
Write-Host "Packing..." -ForegroundColor Cyan
# --packId is not cosmetic: it is the folder name Velopack installs into, under
# %LOCALAPPDATA%, and its uninstaller removes that folder whole. With packId
# 'Snipwhiz' that folder IS the library - captures, database and settings - and a
# real uninstall in Windows Sandbox deleted every screenshot the user had taken.
#
# So the app gets its own directory and the library keeps %LOCALAPPDATA%\Snipwhiz.
# The user-visible name comes from --packTitle and is unaffected.
#
# Changing this after a release would orphan every existing install, since Velopack
# identifies an app by packId. It is settled here, before anything is published.
vpk pack `
    --packId SnipwhizApp `
    --packVersion $version `
    --packDir $publish `
    --packTitle Snipwhiz `
    --packAuthors Snipwhiz `
    --mainExe Snipwhiz.App.exe `
    --icon (Join-Path $repo 'src\Snipwhiz.App\Snipwhiz.ico') `
    --outputDir $releases `
    @sign
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

Write-Host "`nReleases\" -ForegroundColor Cyan
Get-ChildItem $releases -File |
    Sort-Object Length -Descending |
    Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
