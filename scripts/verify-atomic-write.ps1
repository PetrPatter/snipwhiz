<#
.SYNOPSIS
    Kills Snipwhiz mid-settings-write, repeatedly, and checks what is left on disk.

.DESCRIPTION
    The control for the atomic write behind Settings.Save. Each round starts the app
    with SNIPWHIZ_VERIFY_ATOMIC=1, which writes a large probe file in a tight loop,
    waits a random moment, kills it, and then reads the file back. A survivor must be
    complete: parseable JSON ending in the sentinel. Absent is fine on the first
    round; truncated never is.

    A script rather than a unit test because the property is about the process dying.
    See Diagnostics/SettingsWriteVerification.cs for the two test-shaped attempts
    that could not fail.

.PARAMETER Break
    NEGATIVE CONTROL. Writes with File.WriteAllText instead, which truncates the
    target before writing. Rounds should be reported as TRUNCATED.
#>
[CmdletBinding()]
param(
    [int]$Rounds = 25,
    [switch]$Break
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'src\Snipwhiz.App\bin\Debug\net10.0-windows10.0.22621.0\Snipwhiz.App.exe'
if (-not (Test-Path $exe)) { throw "Build first: $exe not found." }

# Never the real library, and never the real settings file.
$root = Join-Path $env:TEMP 'snipwhiz-atomic-verify'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Path $root | Out-Null
$probe = Join-Path $root 'atomic-probe.json'

$env:SNIPWHIZ_ROOT = $root
$env:SNIPWHIZ_VERIFY_ATOMIC = '1'
if ($Break) { $env:SNIPWHIZ_VERIFY_BREAK_ATOMIC = '1' } else { $env:SNIPWHIZ_VERIFY_BREAK_ATOMIC = $null }

$mode = if ($Break) { 'NEGATIVE CONTROL (File.WriteAllText)' } else { 'POSITIVE (AtomicFile)' }
Write-Host "mode=$mode"
Write-Host "root=$root"
Write-Host ''

$complete = 0
$absent = 0
$truncated = 0
$leftovers = 0

for ($i = 1; $i -le $Rounds; $i++) {
    $p = Start-Process -FilePath $exe -PassThru
    # Spread across the write so the kill lands inside one rather than always
    # between two.
    Start-Sleep -Milliseconds (Get-Random -Minimum 120 -Maximum 900)
    try { $p.Kill(); $p.WaitForExit(10000) | Out-Null } catch { }

    if (-not (Test-Path $probe)) {
        $absent++
        continue
    }

    $text = Get-Content $probe -Raw -ErrorAction SilentlyContinue
    $ok = $false
    if ($text) {
        try {
            $null = $text | ConvertFrom-Json
            $ok = $text.Contains('--end-of-file--')
        } catch { $ok = $false }
    }

    if ($ok) { $complete++ } else {
        $truncated++
        $size = (Get-Item $probe).Length
        Write-Host "  round ${i}: TRUNCATED ($size bytes)"
    }

    # Scratch files must not accumulate; a crash mid-write leaves one behind by
    # design, but the count should stay small rather than growing every round.
    $leftovers = (Get-ChildItem $root -Filter '*.tmp' -ErrorAction SilentlyContinue).Count
}

Write-Host ''
Write-Host "rounds=$Rounds  complete=$complete  absent=$absent  truncated=$truncated"
Write-Host "leftoverTempFiles=$leftovers"
Write-Host ''
if ($Break) {
    Write-Host 'Expected: truncated > 0. If this reports zero, the check is not landing'
    Write-Host 'inside the write window and proves nothing about the positive run.'
} else {
    Write-Host 'Expected: truncated=0. Any truncated round means a kill left a settings'
    Write-Host 'file that parses as something other than what was last saved.'
}
