# Snipwhiz

A screenshot tool for Windows 11. Press a key, drag a box, it's on your clipboard —
and it's still there tomorrow when you want to find it again.

## Install

Download **Snipwhiz-Setup.exe** from the [latest release](../../releases/latest) and
run it.

No admin rights, no options to choose, nothing to configure. It installs for your
account only and puts an icon in your system tray.

### Windows will try to stop you, and it's wrong

You'll see a blue window saying **"Windows protected your PC"**, with a *Don't run*
button. Click **More info**, then **Run anyway**.

This is not a virus warning. Windows shows it for any program it hasn't seen many
people download before, and getting rid of it means buying a code-signing
certificate — currently about $400 a year plus a hardware token. So it will keep
appearing on every release. If you'd rather not, that's a completely reasonable
place to stop.

## Using it

| | |
|---|---|
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>1</kbd> | Drag a region |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>2</kbd> | The whole screen |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>L</kbd> | Everything you've captured |

Every capture goes to your clipboard immediately and is saved to your library.
Double-click one in the library to annotate it: arrows, boxes, highlighter, text,
callouts, numbered steps, a magnifier, spotlight, blur and pixelate.

**Annotations stay editable forever.** Reopen a capture from a month ago and the
arrow is still an arrow you can move, not pixels you have to paint over.

## Your screenshots are yours

They live in `%LOCALAPPDATA%\Snipwhiz` — captures as ordinary PNG files in dated
folders, plus a small database of what's what.

**Uninstalling does not delete them.** The uninstaller opens that folder on its way
out so you know where they are; deleting them is your call, not the program's.

## Building it

Needs the .NET 10 SDK.

```
dotnet test                       # 327 tests
scripts\release.ps1               # publish + installer into Releases\
scripts\verify-clean-install.ps1  # install it in Windows Sandbox and prove it runs
```

That last one is worth knowing about. It installs the real Setup.exe in a throwaway
Windows Sandbox with no .NET SDK, presses the capture hotkey, checks a PNG appeared,
then uninstalls and checks your screenshots survived. It has already caught one bug
that no test could: an uninstaller that deleted the user's entire library.

## Status

Works, and in daily use. Capture, library and editor are done; video, scrolling
capture and OCR are not built yet.

## Licence

[MIT](LICENSE). Do what you like with it.
