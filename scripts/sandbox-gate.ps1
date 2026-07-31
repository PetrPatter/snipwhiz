<#
    Runs INSIDE Windows Sandbox. Not useful on a developer machine - the whole
    point is the machine it runs on has never had the .NET SDK, Visual Studio, or
    Snipwhiz.

    Launched by verify-clean-install.ps1, which maps three folders in and reads
    result.txt back out.

    It proves the app runs rather than merely installs. Installing only proves
    files were copied; pressing the capture hotkey and finding a PNG on disk proves
    WPF started, the hotkey registered, the screen grab worked and SQLite wrote -
    which is the set of things a missing runtime actually breaks.

    The two directories below are the subject of the sharpest assertion here. The
    app installs into one and the library lives in the other, and they are separate
    because the first run of this gate proved what happens when they are not:
    Velopack's uninstaller removes its install directory whole, and it took every
    screenshot with it.
#>
$ErrorActionPreference = 'Stop'
$log = @()
function Say($line) { $script:log += $line; Write-Host $line }

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class K {
    [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@

$app      = "$env:LOCALAPPDATA\SnipwhizApp"     # Velopack's, and Velopack deletes it
$library  = "$env:LOCALAPPDATA\Snipwhiz"        # the user's, and nothing deletes it
$captures = "$library\captures"

try {
    Say "=== clean machine ==="
    $sdk = Get-Command dotnet -ErrorAction SilentlyContinue
    Say "dotnet on PATH:      $(if ($sdk) { "YES - $($sdk.Source) - THIS IS NOT A CLEAN MACHINE" } else { 'no' })"
    Say "app dir present:     $(Test-Path $app)"
    Say "library present:     $(Test-Path $library)"
    if ($sdk) { throw "the .NET SDK is present; this gate proves nothing here" }

    Say ""
    Say "=== install ==="
    $setup = Get-ChildItem 'C:\rel\*Setup.exe' | Select-Object -First 1
    Say "running:             $($setup.Name)"
    Start-Process $setup.FullName

    $exe = "$app\current\Snipwhiz.App.exe"
    for ($i = 0; $i -lt 120; $i++) { Start-Sleep -Seconds 1; if (Test-Path $exe) { break } }
    if (-not (Test-Path $exe)) { throw "install did not produce $exe within 120s" }
    Say "installed to:        $app"
    Say "version on disk:     $((Get-Item $exe).VersionInfo.ProductVersion)"

    # Velopack launches the app itself once the install finishes.
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        $p = Get-Process Snipwhiz.App -ErrorAction SilentlyContinue
        if ($p) { break }
    }
    if (-not $p) { throw "the app did not start after installing" }
    Say "running as pid:      $($p.Id)"

    Say ""
    Say "=== first run ==="
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }
    $firstRun = $p.MainWindowHandle -ne 0
    Say "window appeared:     $firstRun ($($p.MainWindowTitle))"
    if (-not $firstRun) { throw "no first-run window on a machine that has never run this" }
    [K]::PostMessage($p.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 3

    Say ""
    Say "=== capture, via the hotkey a user would press ==="
    # Ctrl+Shift+2 - the whole screen, so nothing has to be dragged.
    [K]::keybd_event(0x11,0,0,[IntPtr]::Zero); [K]::keybd_event(0x10,0,0,[IntPtr]::Zero)
    [K]::keybd_event(0x32,0,0,[IntPtr]::Zero); [K]::keybd_event(0x32,0,2,[IntPtr]::Zero)
    [K]::keybd_event(0x10,0,2,[IntPtr]::Zero); [K]::keybd_event(0x11,0,2,[IntPtr]::Zero)

    $png = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        if (Test-Path $captures) { $png = Get-ChildItem $captures -Recurse -Filter *.png -ErrorAction SilentlyContinue }
        if ($png) { break }
    }
    if (-not $png) { throw "Ctrl+Shift+2 produced no capture - installed but not working" }
    Say "captured:            $($png[0].Name), $([math]::Round($png[0].Length/1KB)) KB"
    Say "library location:    $library"
    Say "database:            $(if (Test-Path "$library\library.db") { 'written' } else { 'MISSING' })"

    $before = Get-ChildItem $captures -Recurse -File | ForEach-Object { "$($_.Name)|$((Get-FileHash $_.FullName).Hash)" } | Sort-Object
    Say "capture files:       $($before.Count)"

    Say ""
    Say "=== branding and shortcuts, before anything is removed ==="
    # Asserted before the uninstall, not only after. "Zero shortcuts remain" is
    # satisfied just as well by never having created any, which is the same
    # blindness that let the library deletion through the first time.
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
    Say "exe carries an icon: $($null -ne $icon)"
    if (-not $icon) { throw "the installed exe has no icon - it will show the generic placeholder everywhere" }

    $made = @(Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu" -Recurse -Filter 'Snipwhiz*.lnk' -EA SilentlyContinue) +
            @(Get-ChildItem "$env:USERPROFILE\Desktop" -Filter 'Snipwhiz*.lnk' -EA SilentlyContinue)
    Say "shortcuts created:   $($made.Count) [$(($made | ForEach-Object { $_.Name }) -join ', ')]"
    if ($made.Count -eq 0) { throw "no shortcuts were created - nothing for anyone to launch" }

    # A shortcut pointing at the wrong target is a shortcut that installs cleanly
    # and does nothing, which no other check here would notice.
    $shell = New-Object -ComObject WScript.Shell
    foreach ($lnk in $made) {
        $target = $shell.CreateShortcut($lnk.FullName).TargetPath
        Say "  -> $($lnk.Name): $(if (Test-Path $target) { 'target exists' } else { "BROKEN: $target" })"
        if (-not (Test-Path $target)) { throw "shortcut '$($lnk.Name)' points at nothing" }
    }

    Say ""
    Say "=== uninstall, the way a user does it ==="
    # Found by display name rather than key name: the key is the packId, and this
    # gate exists partly to catch the packId being wrong.
    $arp = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' |
        ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.DisplayName -like 'Snipwhiz*' } | Select-Object -First 1
    Say "Add/Remove entry:    $(if ($arp) { $arp.DisplayName + ' ' + $arp.DisplayVersion } else { 'MISSING' })"
    if (-not $arp) { throw "no Add/Remove Programs entry - nothing for a user to uninstall" }

    $cmd, $cmdArgs = $arp.UninstallString -split ' ', 2
    Start-Process $cmd.Trim('"') -ArgumentList $cmdArgs -Wait
    Start-Sleep -Seconds 5

    Say "app directory gone:  $(-not (Test-Path $app))"
    Say "ARP entry gone:      $($null -eq (Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | ForEach-Object { Get-ItemProperty $_.PSPath } | Where-Object { $_.DisplayName -like 'Snipwhiz*' }))"
    Say "autostart gone:      $($null -eq (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Snipwhiz -EA SilentlyContinue).Snipwhiz)"
    $shortcuts = @(Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu" -Recurse -Filter 'Snipwhiz*.lnk' -EA SilentlyContinue) +
                 @(Get-ChildItem "$env:USERPROFILE\Desktop" -Filter 'Snipwhiz*.lnk' -EA SilentlyContinue)
    Say "shortcuts left:      $($shortcuts.Count)"

    Say ""
    Say "=== the library, which is the user's and not the app's ==="
    $after = Get-ChildItem $captures -Recurse -File -EA SilentlyContinue | ForEach-Object { "$($_.Name)|$((Get-FileHash $_.FullName).Hash)" } | Sort-Object
    Say "capture files left:  $(@($after).Count)"
    if (-not $after) { throw "FAILED: the uninstaller took the user's screenshots with it" }
    if (Compare-Object $before $after) { throw "FAILED: capture files changed across the uninstall" }
    Say "byte-identical:      yes"
    Say "database survived:   $(Test-Path "$library\library.db")"

    Say ""
    Say "RESULT: PASSED"
}
catch {
    Say ""
    Say "RESULT: FAILED - $($_.Exception.Message)"
}
finally {
    New-Item -ItemType Directory 'C:\out' -Force | Out-Null
    $log | Set-Content 'C:\out\result.txt' -Encoding utf8
}
