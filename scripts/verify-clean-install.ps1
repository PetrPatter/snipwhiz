<#
.SYNOPSIS
    Installs the current release in Windows Sandbox and reports what happened.

.DESCRIPTION
    The four gates in the distribution plan that "need a second machine" need one
    because a developer machine hides the failures they exist to catch - chiefly a
    build that quietly depends on an SDK the recipient does not have.

    Windows Sandbox is that machine, and a stricter one than a spare laptop: it is
    clean on every run rather than clean once, so a gate cannot accidentally pass
    on residue from the run before it.

    Requires the sandbox feature, which needs elevation once:
        Enable-WindowsOptionalFeature -Online -FeatureName "Containers-DisposableClientVM" -All

    Run scripts\release.ps1 first, so there is something in Releases\ to install.
#>
[CmdletBinding()]
param(
    # The sandbox is a GUI VM; this is how long to wait for it to report back.
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path "$env:WINDIR\System32\WindowsSandbox.exe")) {
    throw "Windows Sandbox is not enabled. In an elevated PowerShell: Enable-WindowsOptionalFeature -Online -FeatureName 'Containers-DisposableClientVM' -All"
}

$repo     = Split-Path $PSScriptRoot -Parent
$releases = Join-Path $repo 'Releases'

if (-not (Get-ChildItem "$releases\*Setup.exe" -ErrorAction SilentlyContinue)) {
    throw "No Setup.exe in $releases. Run scripts\release.ps1 first."
}

# Somewhere outside the repo for the sandbox to write its answer back to.
$out = Join-Path ([IO.Path]::GetTempPath()) 'snipwhiz-sandbox'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null

# Generated rather than committed: .wsb holds absolute host paths, which are
# specific to whoever is running it.
$wsb = Join-Path $out 'clean-install.wsb'
@"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$releases</HostFolder>
      <SandboxFolder>C:\rel</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$PSScriptRoot</HostFolder>
      <SandboxFolder>C:\gate</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$out</HostFolder>
      <SandboxFolder>C:\out</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -ExecutionPolicy Bypass -NoExit -File C:\gate\sandbox-gate.ps1</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content $wsb -Encoding utf8

Write-Host "Starting Windows Sandbox. Watch it work - this is the install a recipient gets." -ForegroundColor Cyan
Start-Process $wsb

$result = Join-Path $out 'result.txt'
$waited = 0
while (-not (Test-Path $result) -and $waited -lt $TimeoutSeconds) {
    Start-Sleep -Seconds 5
    $waited += 5
}

if (-not (Test-Path $result)) {
    throw "The sandbox did not report back within ${TimeoutSeconds}s. It is still open; look at the PowerShell window inside it."
}

Write-Host ""
Get-Content $result
Write-Host ""
Write-Host "The sandbox is still open. Close it to discard everything it did." -ForegroundColor Cyan
