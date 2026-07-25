# Capture Core — Design Spec

**Project:** Snipwhiz — a Snagit-class screenshot tool for Windows
**Spec:** 1 of 6 — Capture Core
**Date:** 2026-07-25
**Status:** Draft for review

---

## 1. Context

We're building a Windows screenshot tool with full Snagit feature parity and a
materially better-looking UI. The visual direction is settled (see the approved
prototype); the stack is locked to a **C# core + WebView2 UI hybrid**.

Full parity decomposes into six specs. This is the first, and it deliberately
contains **no UI beyond the capture overlay**. It exists to prove every hard
native assumption — DPI, multi-monitor, capture quality, hotkey registration —
before we invest in the editor and library that sit on top.

**Done when:** press a hotkey, drag a region, and the image is on the clipboard,
on disk, and in the history database. No editor. No library window.

### Why this scope, in this order

The overlay is the moment the app feels fast or sluggish, and DPI/multi-monitor
coordinate bugs are the single largest time sink in tools of this kind. If any of
it is going to go wrong, it should go wrong now, against 1,500 lines — not after
the editor is built on top of a broken coordinate space.

---

## 2. Scope

### In

- Screen capture via Windows.Graphics.Capture (WGC)
- Multi-monitor, including **mixed per-monitor DPI**
- Freeze-first overlay: region select, window-under-cursor, fullscreen, repeat-last-region
- Magnifier loupe with pixel grid, hex readout, and coordinate display
- Global hotkeys with graceful conflict handling
- Tray icon, context menu, autostart, single-instance
- Copy to clipboard
- Capture store: immutable PNG on disk + SQLite metadata + thumbnail

### Out (later specs)

Editor and annotations · library UI · video and GIF · scrolling capture · OCR ·
cloud sharing · installer, signing and auto-update (spec 3) · themes and settings UI.

Settings exist in this spec only as a JSON file — no UI to edit them.

---

## 3. Architecture

```
Snipwhiz.App  (WPF, net10.0-windows10.0.22621.0, Win11 21H2+)
  TrayHost ─────────── single instance, NotifyIcon, lifecycle
  OverlayWindow ────── one per monitor, opaque, topmost
  SelectionLayer ───── crosshair, dim, selection rect, loupe

              ▲ depends on ▼

Snipwhiz.Core  (no WPF reference)
  Geometry/   VirtualDesktop · MonitorInfo · coordinate conversion   ← unit tested
  Capture/    DesktopGrabber (BitBlt) → FrozenDesktop
  Windows/    WindowEnumerator (window-under-cursor)
  Hotkeys/    HotkeyService (message-only window)
  Storage/    CaptureStore · LibraryDb · Thumbnailer                 ← unit tested
```

**The Core/App split exists for one reason:** the coordinate math is where the
bugs live, and it is pure. Keeping it out of a WPF assembly means it can be
tested without a desktop session.

### Capture sequence

```
hotkey fires
  → DesktopGrabber.Grab()         one BitBlt over the virtual desktop, sync
  → FrozenDesktop                 immutable bitmap + monitor geometry
  → OverlayWindow per monitor     shows its own frozen bitmap, opaque
  → user drags                    SelectionController owns one rect in virtual px
  → release
  → crop FrozenDesktop            the pixels shown ARE the pixels kept
  → ClipboardService + CaptureStore
  → overlays close
```

---

## 4. Key technical decisions

### 4.1 Freeze-first, and grab exactly once

Grab the screen the instant the hotkey fires, display those bitmaps fullscreen,
and draw the selection UI on top of a static image. This is what ShareX,
Flameshot and Snagit all do, and it buys two things:

1. **The overlay window is fully opaque.** No layered windows, no transparency,
   no `WS_EX_LAYERED` — which sidesteps the mixed-DPI transparency problems that
   consume months in tools like this.
2. **One grab, not two.** Because the displayed pixels are the kept pixels, the
   final capture is a crop of the frozen bitmap. There is no second capture and
   therefore no possibility of the two disagreeing.

### 4.2 One overlay window *per monitor*

Not one window spanning the virtual desktop. A spanning window gets a single DPI
from Windows and is scaled incorrectly on every monitor that doesn't match it.

Cost: a drag that crosses monitors must be coordinated. `SelectionController`
owns a single rect in virtual-screen physical pixels; each overlay renders only
the intersection with its own bounds. Pointer capture stays on the window where
the drag began, and cursor position comes from `GetCursorPos`, which returns
physical virtual-screen coordinates under PerMonitorV2 awareness.

### 4.3 Coordinate spaces — name them and convert at the edges

Three spaces, and mixing them is the classic failure:

| Space | Origin | Used by |
|---|---|---|
| **Virtual physical px** | virtual desktop top-left (can be negative) | Core, canonical |
| **Monitor-local physical px** | that monitor's top-left | WGC frames, cropping |
| **DIP** | per-window | WPF layout only |

**Rules:**
- Core speaks only virtual physical px. Conversion to DIP happens at the WPF boundary, nowhere else.
- The app manifest declares **PerMonitorV2** DPI awareness.
- Overlay windows are positioned with `SetWindowPos` in **physical pixels** via
  `WindowInteropHelper` — never with WPF's `Left`/`Top`, which are DIPs and will
  place windows wrongly on non-primary monitors.
- Every conversion is a pure function in `Snipwhiz.Core.Geometry` with unit tests
  covering negative origins, mixed scale factors, and monitors above/left of primary.

### 4.4 GDI `BitBlt` for the freeze grab — not WGC

**This reverses an earlier draft of this spec.** WGC looked like the obvious
modern choice until the borderless-capture requirement was checked properly.

The problem: WGC draws a **yellow border** around whatever is being captured, and
suppressing it is not a simple property set. Disabling it requires *both*
`GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)`
for user consent *and* the `graphicsCaptureWithoutBorder` capability declared in
a **package manifest**. We ship an unpackaged .exe via Velopack, so we have no
manifest to declare it in. If the user denies access, setting `IsBorderRequired
= false` silently succeeds and is then ignored.

A yellow flash around the screen every time you press the hotkey is not
acceptable in a tool whose entire pitch is polish.

`BitBlt` from the screen DC has none of that:

| | `BitBlt` | WGC |
|---|---|---|
| Border | none | yellow, unless MSIX-packaged + consent |
| Latency | ~5–15 ms, synchronous | ~20–50 ms/monitor, async |
| Code | a few Win32 calls | D3D11 staging textures, `Map`, BGRA copy |
| Hardware-overlay & DRM content | can come back black | correct |
| HDR | no tonemapping | correct |

**Prior art matters here:** ShareX and Greenshot both use `BitBlt` for exactly
this — freeze-first region capture. The gap is also narrower than it first
appears: DWM composites most windowed content, including most hardware-accelerated
video, to the desktop. The real failures are fullscreen-exclusive apps and
DRM-protected content (Netflix and similar come back black).

So: **`BitBlt` for spec 1.** It hits the latency budget trivially, needs no
D3D11, no packaging, and no capability negotiation.

```
// ponytail: BitBlt of the virtual desktop. No yellow border, no D3D11,
// no MSIX capability. Escalate to WGC only if black-frame reports on
// protected/fullscreen-exclusive content actually show up in use —
// and by then spec 3 has solved signing, making a sparse MSIX package
// (for the graphicsCaptureWithoutBorder capability) far cheaper.
```

**Minimum supported OS: Windows 11 21H2 (build 22000).** Confirmed with the
project owner — the tool ships only to Windows 11 machines. TFM
`net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion 10.0.22000.0`. No
Windows 10 conditional paths anywhere.

The cursor is not captured — `BitBlt` excludes it by default, which is what we
want, since the overlay draws its own crosshair.

### 4.5 Latency is no longer the risk it was

With `BitBlt` the whole grab is synchronous and lands around 5–15 ms even across
several monitors, comfortably inside the ~120 ms paint budget. The spike that
previously gated the design is now just a sanity assertion in the manual
checklist rather than a fork in the road.

Monitors are grabbed in one pass over the virtual desktop rectangle.

### 4.6 No capture library needed

The earlier plan to evaluate `ScreenCapture.NET` and `Vortice.Windows` existed
purely to avoid hand-rolling D3D11 staging-texture code for WGC. With `BitBlt`
that whole problem disappears — the grab is `CreateCompatibleDC` /
`CreateCompatibleBitmap` / `BitBlt` / `GetDIBits`, generated by
**`Microsoft.CsWin32`** so there is no hand-written P/Invoke to get wrong.

**Zero new third-party dependencies for capture.** This is the ladder working:
the native platform feature covers it, so nothing gets added.

### 4.7 Hotkeys must fail gracefully

`RegisterHotKey` against a message-only window (`HWND_MESSAGE`), receiving `WM_HOTKEY`.

**PrintScreen cannot be the default.** Since build 22621.1928, Windows 11 binds
PrintScreen to Snipping Tool *by default* — the
`Settings → Accessibility → Keyboard` toggle ships **on**. Because we target
Windows 11 exclusively, `RegisterHotKey(VK_SNAPSHOT)` failing with
`ERROR_HOTKEY_ALREADY_REGISTERED` is not an edge case; it is what happens on
essentially every machine we ship to.

An earlier draft made PrintScreen the default and treated the conflict as an
error path. On a Windows 11-only target that ships a broken default to everyone.

**Defaults that work out of the box:**

| Chord | Action |
|---|---|
| `Ctrl+Shift+1` | Region select |
| `Ctrl+Shift+2` | Window under cursor |
| `Ctrl+Shift+3` | Fullscreen (monitor under cursor) |
| `Ctrl+Shift+4` | Repeat last region |

**Plus a three-tier PrintScreen takeover.** PrintScreen is still the key people
reach for. The mechanism to claim it changed in 2026, so we try the cheapest
thing first and escalate only on failure:

**Tier 1 — just ask for it.** Attempt `RegisterHotKey(VK_SNAPSHOT)` at startup.
On **build 26300+** Microsoft replaced the old accessibility toggle with a
`Make Print Screen key yieldable` Group Policy
(*Computer Configuration → Administrative Templates → Windows Components →
File Explorer*) whose **default "Not configured" state permits third-party apps
to intercept the key**. On those builds this simply succeeds and there is
nothing else to do.

**Tier 2 — offer the registry flip.** If tier 1 fails (older builds, or the
policy explicitly Disabled), first run offers:

> *Use PrintScreen for Snipwhiz? This turns off the Snipping Tool shortcut.*

On consent, set `HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled`
to `0`, then re-attempt registration. `HKCU`, so no elevation needed. **Only
ever with explicit consent** — silently rebinding a system key is hostile, and
it's the user's registry. Warn that a sign-out may be required; confirm the
actual behaviour during implementation and word the prompt to match.

**Tier 3 — a low-level keyboard hook. We are not doing this.**
`SetWindowsHookEx(WH_KEYBOARD_LL)` can swallow `VK_SNAPSHOT` before the shell
sees it, which is how some tools force the issue. Rejected on purpose:

- A global keyboard hook in a **new binary with no SmartScreen reputation** is
  exactly the behavioural signature AV heuristics flag. We are already fighting
  reputation warnings for distribution (spec 3); adding a keylogger-shaped API
  call to that fight is a bad trade.
- It's fragile regardless — interactive session only, dies against the secure
  desktop and elevated windows, and any other app's hook can win instead.

```
// ponytail: no WH_KEYBOARD_LL. Tiers 1+2 cover every supported build,
// and a global keyboard hook would trip AV heuristics on a binary that
// has no signing reputation yet. Revisit only if tier 2 proves
// insufficient in real use.
```

**PrintScreen is contested by more than Windows.** Dropbox and OneDrive both
register it for screenshot sync. Tier 2's failure message must name *what*
holds the key when we can determine it, not just report a generic failure.

Required behaviour regardless — a trust boundary that does not get simplified
away: registration failure is caught per-chord, the app still starts, a tray
notification names the specific conflicting chord, and the tray menu always
offers every action by click. **The app is never unusable because a hotkey was
taken.** This is why `Ctrl+Shift+1..4` are the defaults and PrintScreen is a
bonus rather than the baseline.

### 4.8 The captured PNG is immutable

Spec 1 writes a PNG and never modifies it. When the editor arrives in spec 2,
annotations become a **sidecar** (`<id>.snipwhiz.json`) next to the original.

This costs nothing now and means "edit later" works forever — including
re-editing a capture from months ago, and changing an annotation without
generation loss. Retrofitting non-destructive editing after flattening has
shipped is a rewrite, which is exactly why it's decided here.

---

## 5. Data model

### Files

```
%LOCALAPPDATA%\Snipwhiz\
  captures\2026\07\<ulid>.png        original, immutable
  thumbs\<ulid>.png                  320px wide
  library.db                         SQLite
  settings.json
```

ULID rather than GUID: lexicographically sortable by creation time, so the
filesystem sorts chronologically for free.

### `captures` table

| Column | Type | Note |
|---|---|---|
| `id` | TEXT PK | ULID |
| `created_utc` | INTEGER | Unix ms |
| `width`, `height` | INTEGER | physical px |
| `source_app` | TEXT | process name under cursor at capture |
| `source_title` | TEXT | window title |
| `monitor_device` | TEXT | which display |
| `file_path`, `thumb_path` | TEXT | relative to root |
| `pinned` | INTEGER | 0/1 |
| `deleted_utc` | INTEGER NULL | soft delete |

Index on `created_utc DESC` — the library queries by recency and groups by day.

`Microsoft.Data.Sqlite`. A JSON index would be simpler, but day-grouped,
searchable, filterable history over thousands of rows is exactly what SQLite is
for, and it's effectively stdlib-tier here.

**Retention:** none in spec 1. Nothing is auto-deleted. Soft-delete column exists
so spec 2's library can offer undo.

---

## 6. Error handling

Only three failures actually matter here, and all three are user-visible:

| Failure | Behaviour |
|---|---|
| Hotkey conflict | Per-chord; app starts, notification names the chord, tray menu still works (§4.7) |
| Disk write fails | Image still goes to the clipboard; notification says saving failed and why. **Never lose the capture the user just took** |
| Clipboard locked by another app | `OpenClipboard` fails when another process holds it. Retry a few times with a short backoff, then notify. Common enough with clipboard managers to be worth handling |
| Black/empty frame | DRM or fullscreen-exclusive content (§4.4). Detect an all-black result and say so plainly rather than silently saving a black rectangle |

That last one is the rule that shapes the ordering: **clipboard first, then disk.**
The clipboard is what the user is about to paste, and it must not be blocked on I/O.

Everything else (a monitor disconnected mid-capture, a WGC frame arriving late)
aborts the capture and closes the overlay. Silent failure is not acceptable —
the user pressed a key and must learn whether it worked.

---

## 7. Testing

Honest split: most of this spec is Win32 interop that cannot be unit tested
without a desktop session. So we test the part where the bugs actually live, and
verify the rest by hand against a checklist.

### Unit tested (`Snipwhiz.Core.Tests`, xUnit)

- **Coordinate conversion** — virtual ↔ monitor-local ↔ DIP, across: negative
  virtual origins, monitors above/left of primary, mixed scale factors
  (100% + 150% + 225%), and single-monitor. This is the highest-value test suite
  in the spec.
- **Selection rect** — normalization when dragged in all four directions,
  clamping to virtual bounds, zero-size and one-pixel rects.
- **Crop math** — a virtual-space rect against a monitor-local frame, including
  selections spanning two monitors.
- **Store round-trip** — write and read back against a temp directory and an
  in-memory SQLite database.
- **ULID** — monotonic ordering.

Written test-first. The coordinate suite in particular should exist before the
overlay does, because it's cheaper to be wrong in a test than on a second monitor.

### Manual verification checklist

Run on a **multi-monitor setup with mismatched scaling** (this is not optional —
it is the configuration that breaks everything):

1. Region capture on primary → clipboard matches selection exactly, pixel for pixel
2. Region capture on a 150% secondary → **no scaling drift**, no half-pixel offset
3. Region dragged **across** two monitors of different DPI → seamless, correct dimensions
4. Window-under-cursor highlights the right window, including a maximized one
5. Fullscreen capture on the monitor under the cursor, not always the primary
6. Overlay paints in <120 ms (measured, per §4.5)
7. Magnifier hex readout matches the actual on-screen pixel — verify against an independent color picker
8. **No yellow capture border ever appears** (the reason for §4.4)
9. Capture a playing YouTube video in a browser → real frame, not black. Then a
   DRM source (Netflix) → confirm the black-frame message fires rather than
   silently saving a black rectangle
10. Hotkey conflict: confirm defaults register on a stock Win11 box with Snipping
    Tool owning PrintScreen; then accept the takeover prompt and confirm PrintScreen works
11. Decline the takeover prompt → registry untouched, defaults still work
12. Disconnect a monitor while the overlay is open → clean abort, no crash
13. Capture with the app on a machine at 225% scaling
14. Second instance launch → focuses the existing one, doesn't start a second

---

## 8. What Windows 11-only buys the later specs

Recorded here so it isn't rediscovered later:

- **WebView2 is preinstalled on Windows 11.** Spec 3's installer needs no
  Evergreen bootstrapper and no first-run download — this materially simplifies
  distribution and was one of the open risks of the hybrid stack.
- **Mica and Acrylic backdrops** via `DWMWA_SYSTEMBACKDROP_TYPE`, and rounded
  corners via `DWMWA_WINDOW_CORNER_PREFERENCE`. The editor's frameless chrome
  gets native depth for two P/Invoke calls instead of faked shadows — directly
  serving the "better looking than Snagit" goal.
- **No Windows 10 branches anywhere** — no capability probing, no version gates,
  no dual code paths.

---

## 9. Open questions

1. **PrintScreen takeover timing** (§4.7) — determine during implementation
   whether clearing `PrintScreenKeyForSnippingEnabled` applies immediately or
   needs a sign-out, and word the prompt to match. Affects copy, not design.

**Resolved:** the product name is **Snipwhiz** (install path
`%LOCALAPPDATA%\Snipwhiz`, root namespace `Snipwhiz`). The two spikes that
previously headed this list — WGC latency and picking a capture library — were
both dissolved by the move to `BitBlt` (§4.4–4.6).

---

## 10. Not doing, and why

- **WGC capture path** — reversed in §4.4; escalate only if black-frame reports appear in real use
- **Settings UI** — JSON file is enough until there's a window to put it in
- **Retention/cleanup** — needs the library UI to be meaningful
- **Capture history limit** — PNGs are small; revisit with real usage data
- **Delayed/timed capture** — spec 4
- **Configurable hotkeys** — defaults plus takeover plus graceful conflict handling covers spec 1; rebinding needs settings UI
