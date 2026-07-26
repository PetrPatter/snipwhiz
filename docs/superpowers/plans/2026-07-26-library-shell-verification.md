# Library Shell — Verification Results

**Branch:** `feat/library-shell` · **Tests:** 104/104 · **Build:** 0 warnings

Hardware as spec 1: `\\.\DISPLAY1` 1920×1080 @ **1.25**, `\\.\DISPLAY2` 1920×1200 @ **1.0**
at a negative virtual origin.

---

## Measured

| # | Check | Result | Evidence |
|---|-------|--------|----------|
| 2 | Virtualization holds over a large library | **PASS** | Automated sweep over **1,000 seeded captures**: peak **21** realized tiles. Negative control (plain `StackPanel`) peaked at **1,000**. See below |
| 1 | Clipboard publishes PNG, CF_DIBV5, CF_DIB **and** the file | **PASS** | `ClipboardFormatTests`, enumerating real clipboard formats. Control: `Clipboard.SetImage` publishes none of them |
| 1e | A capture with no saved file advertises no path | **PASS** | Same suite — a path that was never written is not published |
| 6 | v1 database upgrades in place, fresh database reports v2 | **PASS** | `MigrationTests`, against a hand-built v1 database with rows |
| 8 | Search escapes `%` and `_` | **PASS** | `LibraryQueryTests`. Controls run: unescaped search returns the whole library |
| — | Keyset paging is stable under insertion | **PASS** | `LibraryQueryTests`. Control: cursor-ignoring pager fails all three paging tests |
| 7 | A corrupt cached thumbnail regenerates | **PASS** | `ThumbnailCacheTests`. Control: validity check reduced to `File.Exists` fails it |

### The virtualization sweep

```
POSITIVE   capturesLoaded=1000  realizedTilesPeak=21    viewport=560px
NEGATIVE   capturesLoaded=1000  realizedTilesPeak=1000  (plain StackPanel)
```

Reproduce:

```powershell
$env:SNIPWHIZ_ROOT = "$env:TEMP\snipwhiz-verify"   # never the real library
$env:SNIPWHIZ_SEED = "1000"
$env:SNIPWHIZ_VERIFY_GRID = "1"
dotnet run --project src/Snipwhiz.App
# result: %TEMP%\snipwhiz-grid-verify.txt

$env:SNIPWHIZ_VERIFY_BREAK_VIRTUALIZATION = "1"    # must peak at 1000
```

`LibrarySeed` refuses to run without `SNIPWHIZ_ROOT`, so the gate cannot seed a
thousand synthetic captures into a real library.

---

## Confirmed by the user at the machine

| Check | Result |
|-------|--------|
| Delete → undo → open → copy → paste into Word | **PASS** — the restored capture's bytes, not just its tile |
| Capture with the library open: window absent from the PNG | **PASS**, after the compositor fix |
| Paste a capture into Windows Terminal | **PASS**, after `CF_HDROP` |
| Thumbnails render; grid scrolls naturally | **PASS**, after pixel scroll unit and wheel step |
| Hover highlight indicates the target tile | **PASS**, after the corner clip fix |

---

| # | Check | Result |
|---|-------|--------|
| 1b | Paste into **Paint and Chrome** | **PASS** (Slack not tried) |
| 1c | Preview shows the full PNG, not an upscaled thumbnail | **PASS** |
| 1d | Preview a capture smaller than the window, 125% panel | **PASS** — pixel-sharp at its true size |
| 3 | Capture while the library is open → new tile at top of Today | **PASS** |
| 9 | Delete a PNG in Explorer, then use its tile | **PASS** |
| 11 | Copy, delete, let the toast expire, then paste | **FAILED, then fixed** — see below |

### Check 11: a regression the file-publishing fix introduced

Pasting a deleted capture reported *"error occurred while importing this file"*.

Spec 2a §6.3 predicted this would pass, reasoning that the clipboard holds a copy
of the bytes rather than a file reference. That was true when it was written, and
publishing `CF_HDROP` made it false: consumers that prefer the file — Paint among
them — followed the path to a file that had just been deleted, while the pixels
sat unused in the same clipboard.

Committing a deletion now checks whether the clipboard still names that exact
path and, if so, republishes the capture as pixels only before removing the file.
Narrow on purpose: a clipboard the user has since filled with something else is
left alone.

**This is the cost of the ordering change made for `CF_HDROP`, showing up in a
place the spec had already reasoned about and got wrong.** The reasoning was
sound when written; the premise moved underneath it.

## Still outstanding

| # | Check | Why it needs a human |
|---|-------|----------------------|
| 1b | Paste into **Slack** | Paint and Chrome confirmed |
| 11 | Re-run after the fix above | The failing case must be seen passing |
| 17 | Soak: repeated open/scroll/capture/delete; GDI handles and working set flat | Runs for weeks |

---

## Residual risks carried into spec 2b

1. **Spec 1's clipboard ordering is reversed.** The capture is now saved before the
   clipboard is written, because `CF_HDROP` cannot advertise a file that does not
   exist. Confirmed with the user. The paste becomes available after the PNG
   lands rather than before — milliseconds normally, longer on a stalled disk.
   A failed save publishes no path.
2. **Delete commits early when the window closes.** Hiding the library flushes
   pending deletions, because the toast is gone and the offer with it. If the
   process is killed mid-toast the file survives and its row does not — a stranded
   file rather than a broken tile, which is the recoverable direction.
3. **Thumbnail concurrency was a real bug, found late.** Two tiles requesting the
   same thumbnail wrote the same temp file; GDI+ reported only "a generic error".
   Temp names are unique per attempt now. Worth remembering that GDI+ failures say
   nothing useful about their cause.
4. **Delete lives only in the preview.** No tile-level delete and no multi-select,
   so clearing out fifty captures means fifty round trips.
5. **The grid gate measures container count, not smoothness.** A bounded peak
   proves virtualization, not that scrolling feels good on slow hardware.

---

## On verification standards

Spec 1 shipped five checks that were later found circular or blind. Two more were
caught in this spec before they could mislead:

- **The clipboard check.** Pasting into four apps — the spec 1 method — cannot
  fail here: every capture is opaque, and the defect the multi-format payload
  prevents only appears with alpha. `Clipboard.SetImage` would have passed it.
  Replaced with format enumeration plus a control that performs the naive write.
- **The thumbnail cancellation check.** It claimed to prove the temp-file
  discipline, but an already-cancelled token exits before any file is touched, so
  it would have passed with the atomic write entirely absent. Renamed to what it
  actually covers, with a separate assertion for the real property.

Every control listed in the tables above was run and observed failing.
