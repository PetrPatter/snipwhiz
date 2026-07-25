# Capture Core — Design Spec

**Project:** Snipwhiz — a Snagit-class screenshot tool for Windows
**Spec:** 1 of 6 — Capture Core
**Date:** 2026-07-25 (rev. 2, post-review)
**Status:** Ready to plan from

---

## 1. Context

We're building a Windows screenshot tool with full Snagit feature parity and a
materially better-looking UI. The visual direction is settled (approved
prototype); the stack is locked to a **C# core + WebView2 UI hybrid**.

Full parity decomposes into six specs. This is the first, and it deliberately
contains **no UI beyond the capture overlay**. It exists to prove every hard
native assumption — DPI, multi-monitor, capture quality, hotkey registration —
before we invest in the editor and library that sit on top.

**Done when:** press a hotkey, drag a region, and the image is on the clipboard,
on disk, and in the history database. No editor. No library UI.

### Why this scope, in this order

The overlay is the moment the app feels fast or sluggish, and DPI/multi-monitor
coordinate bugs are the single largest time sink in tools of this kind. If any of
it is going to go wrong, it should go wrong now, against ~1,500 lines — not after
the editor is built on top of a broken coordinate space.

---

## 2. Scope

### In

- Screen capture via **GDI `BitBlt`** over the virtual desktop (§4.4)
- Multi-monitor, including **mixed per-monitor DPI**
- Freeze-first overlay with **two modes**: drag a region, or capture the whole
  monitor under the cursor
- Magnifier loupe with pixel grid, hex readout, coordinate and size display
- Mouse cursor state recorded at grab time, rendered by nothing yet (§4.9)
- Global hotkeys with graceful conflict handling
- Tray icon, context menu, balloon notifications, autostart, single-instance
- Copy to clipboard with a correct multi-format payload (§4.10)
- Capture store: immutable PNG on disk + SQLite metadata

### Out (later specs)

Editor and annotations · library UI · thumbnails · video and GIF · scrolling
capture · OCR · cloud sharing · installer, signing and auto-update (spec 3) ·
themes and settings UI.

**Also deferred out of spec 1** (was in rev. 1, cut on review):
window-under-cursor capture — it drags in a whole `WindowEnumerator` plus three
known Win32 traps (§10) — and repeat-last-region, which needs rect persistence
and monitor-topology invalidation.

### Platform floor

**Windows 11 22H2, build 22621.** One number, used everywhere:
TFM `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion 10.0.22621.0`.
21H2 left consumer support in Oct 2023 and nobody we ship to runs it. No Windows
10 conditional paths anywhere.

---

## 3. Architecture

```
Snipwhiz.App  (WPF, net10.0-windows10.0.22621.0)
  TrayHost ─────────── single instance, NotifyIcon, balloon tips, lifecycle
  OverlayWindow ────── one per monitor, opaque, topmost
  SelectionLayer ───── crosshair, dim, selection rect, loupe

              ▲ depends on ▼

Snipwhiz.Core  (no WPF; see note on WinForms below)
  Geometry/   VirtualDesktop · MonitorInfo · coordinate conversion   ← unit tested
  Capture/    DesktopGrabber (BitBlt) → FrozenDesktop
  Hotkeys/    HotkeyService (message-only window)
  Clipboard/  ClipboardWriter (multi-format)
  Storage/    CaptureStore · LibraryDb                               ← unit tested
```

**On the Core/App split:** rev. 1 justified this as "so the math can be tested
without a desktop session." That reason was false — xUnit can reference a
WPF-targeted library and run pure static functions headlessly; only
`Dispatcher`/`Window` instantiation needs a session. The split is kept for
namespace discipline and to keep **WPF** out of the layer later shared with a
WebView2 host, which is a real but much smaller benefit than claimed.

**Core does reference WinForms** (`UseWindowsForms`), discovered during
implementation. `HotkeyService` needs a message-only window to receive
`WM_HOTKEY`, and `System.Windows.Forms.NativeWindow` is the in-box way to get
one with correct `WndProc` marshalling. The alternative is ~40 lines of raw
`CreateWindowEx` plus a manually lifetime-managed `WndProc` delegate — more code
and easier to get wrong, for no benefit on a Windows-only product.

Consequences, recorded so this is a decision rather than a surprise:

- Core is not usable headlessly or cross-platform. Neither is required.
- `System.Drawing.Common` no longer needs an explicit `PackageReference` in Core;
  WinForms supplies it. `PngEncoder` is its only consumer.
- **WPF is still absent from Core**, which is the part that actually matters for
  the spec 2 WebView2 host.

**Tray:** WinForms `NotifyIcon` via `<UseWindowsForms>true</UseWindowsForms>`.
In-box, zero dependencies, and its `ShowBalloonTip` solves §6's notification
problem in the same component.

### Capture sequence

```
hotkey fires
  → DesktopGrabber.Grab()         one BitBlt over the virtual desktop, sync
  → FrozenDesktop                 immutable bitmap + monitor geometry + cursor state
  → OverlayWindow per monitor     shows its slice of the frozen bitmap, opaque, 1:1
  → user drags (or presses Space) SelectionController owns one rect in virtual px
  → release
  → crop FrozenDesktop            the pixels shown ARE the pixels kept
  → ClipboardWriter, then CaptureStore
  → overlays close
```

---

## 4. Key technical decisions

### 4.1 Freeze-first, and grab exactly once

Grab the screen the instant the hotkey fires, display those pixels fullscreen,
and draw the selection UI on top of a static image. This is what ShareX,
Greenshot and Snagit all do, and it buys two things:

1. **The overlay window is fully opaque.** No `WS_EX_LAYERED`, no transparency —
   which removes the layered-window problem class entirely. (It does *not* solve
   mixed DPI; that's §4.2 and §4.3, and rev. 1 wrongly welded the two together.)
2. **One grab, not two.** The displayed pixels are the kept pixels, so the final
   capture is a crop of the frozen bitmap. There is no second capture that could
   disagree with the first.

### 4.2 One overlay window *per monitor*

Not one window spanning the virtual desktop. A spanning window gets a single DPI
from Windows and is scaled incorrectly on every monitor that doesn't match it.

Cost: a drag crossing monitors must be coordinated. `SelectionController` owns
one rect in virtual-screen physical pixels; each overlay renders only the
intersection with its own bounds. Pointer capture stays on the window where the
drag began, and cursor position comes from `GetCursorPos`, which returns physical
virtual-screen coordinates under PerMonitorV2 awareness.

### 4.3 Coordinate spaces — name them, convert at the edges

Two spaces in the core, one at the boundary:

| Space | Origin | Used by |
|---|---|---|
| **Virtual physical px** | virtual desktop top-left (can be negative) | Core, canonical — the frozen bitmap, all crops |
| **Monitor physical bounds** | virtual space | Placing overlays, fullscreen-mode crop |
| **DIP** | per-window | WPF layout only |

**Rules:**

- Core speaks only virtual physical px. DIP conversion happens at the WPF
  boundary and nowhere else.
- The app manifest declares **PerMonitorV2** DPI awareness.
- Overlay windows are positioned with `SetWindowPos` in **physical pixels** via
  `WindowInteropHelper` — never WPF's `Left`/`Top`, which are DIPs and will place
  windows wrongly on non-primary monitors.
- **The frozen bitmap must render at exactly 1:1 physical pixels.** This is the
  rule rev. 1 omitted, and it is the single most likely thing to get wrong. A
  96-DPI `BitmapSource` in a WPF `Image` on a 150% monitor is drawn at 1.5× —
  blurry, offset, not pixel-exact. Neutralise it explicitly: size the `Image` to
  `physicalPx / scale`, set `RenderOptions.BitmapScalingMode=NearestNeighbor`,
  set `UseLayoutRounding=true`, and make the bitmap's own `DpiX`/`DpiY` metadata
  match what you're claiming. Manual checks 1, 2 and 7 in §7 are *entirely* tests
  of this rule.
- Conversion helpers take the scale factor as an **injected parameter**, never
  reading `GetDpiForWindow` internally. That's what makes them pure and testable,
  and it resolves rev. 1's contradiction between §4.3 and §7.

### 4.4 GDI `BitBlt` for the freeze grab

**This reverses an earlier draft.** WGC looked like the obvious modern choice
until the borderless-capture requirement was checked properly.

WGC draws a **yellow capture indicator**, and suppressing it needs *both*
`GraphicsCaptureAccess.RequestAccessAsync(Borderless)` for consent *and* the
`graphicsCaptureWithoutBorder` capability in a **package manifest**. We ship an
unpackaged `.exe` via Velopack, so there is no manifest. If consent is denied,
setting `IsBorderRequired = false` silently succeeds and is then ignored.

**Precision on that, because rev. 1 overstated it:** the indicator is drawn *on
screen only* — it is never composited into captured frames, so it would not
appear in a saved PNG. The actual objection is narrower: for a freeze-first grab
it flashes on screen for a frame every single time the hotkey fires, which reads
as unpolished in a tool whose entire pitch is polish. That still supports the
decision, but the reason on record must be the real one or the next person to
revisit this will reverse it for a cause that was never true.

`BitBlt` has none of that: no indicator, synchronous, and no D3D11 interop.

**Flags: `SRCCOPY | CAPTUREBLT`.** By default `BitBlt` from the screen DC
excludes layered windows — you lose menu and tooltip shadows, transparent overlay
windows, and some IME candidate windows. ShareX and Greenshot both pass
`CAPTUREBLT` for exactly this reason. It has a known cosmetic cost (brief cursor
flicker or screen blink on some configurations), which we accept: missing content
in a screenshot is a correctness bug, flicker is not.

**Measured, 2026-07-25** (dual display, 3840×1257, 125% + 100%, hybrid
Quadro P3200 + Intel UHD 630), median of 15 after warmup:

| Variant | Median |
|---|---|
| `SRCCOPY \| CAPTUREBLT`, whole desktop | 66.6–67.0 ms |
| `SRCCOPY` alone, whole desktop | 66.6–66.7 ms |
| `SRCCOPY \| CAPTUREBLT`, per-monitor, summed | 66.3–66.9 ms |

**`CAPTUREBLT` costs no measurable time**, so the flag is free correctness rather
than a tradeoff. Splitting the grab per monitor to avoid one cross-adapter span
saves nothing either — the cost is inherent to reading a desktop composited
across two adapters.

**What `BitBlt` cannot capture — two distinct classes, not one:**

| Class | Cause | Fixable? |
|---|---|---|
| **DRM-protected** (Netflix etc.) | `SetWindowDisplayAffinity(WDA_MONITOR / WDA_EXCLUDEFROMCAPTURE)` | **No.** Enforced at composition. **WGC honours this identically** — rev. 1 wrongly claimed WGC was "correct" here. No user-mode API captures it |
| **Fullscreen-exclusive / hardware overlay planes** | bypasses DWM composition | **Yes** — by a different API |

```
// ponytail: BitBlt + CAPTUREBLT. No capture indicator, no D3D11, no MSIX.
// If fullscreen-exclusive or overlay-plane black frames show up in real use,
// the escalation target is DXGI Desktop Duplication (IDXGIOutputDuplication)
// — NOT WGC. DD has no indicator, no consent prompt, no package manifest,
// captures overlay planes, and is faster than BitBlt. Its costs are D3D11
// setup, per-output duplication, and DXGI_ERROR_ACCESS_LOST handling.
// Neither DD nor WGC can capture DRM content. Nothing can.
```

### 4.5 Grab latency — measure it in week one

The overlay must paint in **under ~120 ms** or the app feels sluggish, and that
one moment sets the perceived quality of the product.

Rev. 1 asserted "5–15 ms" and demoted this to an end-of-project sanity check.
That was unsourced and optimistic. `BitBlt` + `GetDIBits` over the whole virtual
desktop is a full GPU→CPU readback of every pixel, on the UI thread, before
anything paints. A three-monitor 4K desktop is ~25 megapixels; published
comparisons put GDI at roughly a third of Desktop Duplication's throughput.

**Restored to a week-one measurement with a pre-agreed answer.** Instrument a
stopwatch from `WM_HOTKEY` receipt to first present, logged, on the worst
monitor configuration we ship to:

- **Under 120 ms** → proceed as designed.
- **Over 120 ms** → switch the freeze path to **DXGI Desktop Duplication**
  (§4.4). That is a different architecture, not a tuning pass, which is why it
  is decided up front rather than discovered late.

**Measured, 2026-07-25 — gate PASSED, with less headroom than assumed:**

| Configuration | Displays | MP | Median |
|---|---|---|---|
| Laptop only (DPI-virtualised, invalid) | 1 | 1.3 | 32.0 ms |
| Laptop only, DPI-aware | 1 | 2.1 | 33.1 ms |
| Laptop + external, 125% + 100% | **2** | 4.8 | **79.6 ms** |

**The cost driver is display count, not pixel count.** Going 1.3 → 2.1 MP on one
display cost 4%; adding a second display cost 140%. The test machine has hybrid
graphics (Quadro P3200 + Intel UHD 630), so spanning the grab forces a
cross-adapter composite.

Consequences to carry forward:

- Two displays consume **66% of the budget**. A third display, or 4K panels,
  could plausibly exceed it. Recheck on real target hardware — §7 item 7.
- **Do not extrapolate this by pixel count.** That was tried twice during
  planning and was wrong in both directions, badly.
- ~13 ms of the 79.6 ms is `Grab()` re-enumerating monitors and reading cursor
  state on every call. Caching enumeration behind a `WM_DISPLAYCHANGE`
  invalidation is the known lever if headroom is ever needed. **Not done now:**
  the gate passes, and stale monitor topology after a hot-plug is a worse bug
  than 13 ms.

### 4.6 No capture library, but GDI handles still need owning

With `BitBlt` the grab is `CreateCompatibleDC` / `CreateCompatibleBitmap` /
`BitBlt` / `GetDIBits`, with signatures generated by **`Microsoft.CsWin32`**.
**Zero new third-party dependencies for capture.**

Rev. 1 claimed CsWin32 means "no hand-written P/Invoke to get wrong." It
generates *signatures*; it does not manage lifetime. In a tray app that runs for
weeks, leaking an `HDC` or `HBITMAP` per capture is a real, slow failure.
Every GDI handle is wrapped in a `SafeHandle` and released in a `finally`, and
§7 has a soak test for it.

### 4.7 Hotkeys — and PrintScreen is not the signal you think

**Defaults, which work out of the box:**

| Chord | Action |
|---|---|
| `Ctrl+Shift+1` | Region select |
| `Ctrl+Shift+2` | Fullscreen (monitor under cursor) |

**Why PrintScreen isn't the default.** Since build 22621.1928 Windows 11 binds
PrintScreen to Snipping Tool, with the toggle shipping **on**.

**The trap rev. 1 fell into:** it keyed the whole fallback off
`RegisterHotKey` returning `ERROR_HOTKEY_ALREADY_REGISTERED`. But the Snipping
Tool binding is shell-level, not a hotkey registration — the consistently
reported symptom in ShareX and Snagit is *"registration succeeds and the key
still opens Snipping Tool."* A fallback gated on the return code therefore never
fires for the people who need it.

**Detect by state, not by return code:** read
`HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled` directly, and
confirm functionally that a `WM_HOTKEY` actually arrives.

**Offer, with consent:**

> *Use PrintScreen for Snipwhiz? This turns off the Snipping Tool shortcut.*

On consent set the value to `0` and re-register. `HKCU`, no elevation. **Only
ever with explicit consent** — silently rebinding a system key is hostile, and
it's the user's registry. Warn that a sign-out may be needed; confirm the real
behaviour during implementation and word the prompt to match. Persist the
user's answer so we ask once.

**PrintScreen is contested by more than Windows** — Dropbox and OneDrive both
register it. There is no API to ask who owns a key, so this is best-effort:
check for those processes and name them if present, otherwise report plainly
that something else holds the key. (Rev. 1 demanded we name the holder
unconditionally, which is not achievable.)

**Not doing: a low-level keyboard hook.** `SetWindowsHookEx(WH_KEYBOARD_LL)`
could swallow `VK_SNAPSHOT` outright, but a global keyboard hook in a binary with
no SmartScreen reputation is exactly the signature AV heuristics flag — a bad
trade when two legitimate paths exist. It's also fragile: interactive session
only, dead against the secure desktop and elevated windows.

**Footnote for later:** on **build 26300+** a `Make Print Screen key yieldable`
Group Policy exists whose default permits third-party interception, making all of
the above unnecessary. 26300 is 26H2, still Insider Experimental as of June 2026,
and it's a *Computer* policy so `gpedit` is Pro-only. Not a path we can rely on;
revisit when it ships broadly.

**Always true** — a trust boundary that does not get simplified away:
registration failure is caught per-chord, the app still starts, a balloon names
the specific conflicting chord, and the tray menu always offers every action by
click. **The app is never unusable because a hotkey was taken.**

### 4.8 The captured PNG is immutable

Spec 1 writes a PNG and never modifies it. When the editor arrives in spec 2,
annotations become a **sidecar** (`<id>.snipwhiz.json`) next to the original.

This costs nothing now and means "edit later" works forever — including
re-editing a months-old capture and changing an annotation without generation
loss. Retrofitting non-destructive editing after flattening has shipped is a
rewrite, which is why it's decided here in the spec that has no editor.

### 4.9 Capture cursor state now, even though nothing renders it

`BitBlt` excludes the cursor, which is what the overlay wants. But "include the
cursor in the capture" is standard Snagit-parity functionality, and freeze-first
makes it **unrecoverable after the fact** — cursor shape, hotspot and position
exist only at the instant the hotkey fired.

So `FrozenDesktop` records `GetCursorInfo` (`HCURSOR`, position, hotspot) at grab
time and nothing renders it in spec 1. Cost now: a few lines. Cost later:
reopening the one component every downstream feature depends on.

This is the same argument as `source_app` in §5 — capture-time-only data gets
recorded even when unused, because it cannot be reconstructed.

### 4.10 Clipboard: one sequence, three formats

`Clipboard.SetImage` publishes essentially `CF_BITMAP`, which pastes **black or
blue backgrounds** into Office, Paint and browsers. That is the single most
visible defect a screenshot tool can ship.

One `OpenClipboard`/`EmptyClipboard` sequence publishing, in order:

1. the registered `"PNG"` format — what modern apps prefer
2. `CF_DIBV5` — carries alpha
3. `CF_DIB` — so Windows doesn't synthesise it wrongly from the others

Alpha is **premultiplied**, documented at the call site. Spec 1's output is
opaque so this doesn't bite yet — but spec 2's editor produces transparency, and
by then every consumer is built on this service.

`OpenClipboard` fails when another process holds the clipboard, which clipboard
managers do constantly: retry a few times with short backoff, then report.

### 4.11 Overlay activation, focus, and always having a way out

Rev. 1 specified none of this. The failure mode is the worst one available: a
fullscreen opaque window that has taken over the display and won't accept a
keystroke.

- **One designated overlay is activated** (the one under the cursor); the rest
  are shown without activation.
- **Esc and right-click cancel are handled on every overlay**, not just the
  focused one, and route to the same cancel path.
- `SetForegroundWindow` succeeds for a hotkey-driven process under the
  "received the last input event" rule, but the docs explicitly warn it can still
  be refused. **If activation fails, the capture aborts and tears down** rather
  than leaving an unfocusable fullscreen window.
- **Unconditional escape hatch:** a watchdog closes all overlays if none has
  received input within 60 s, and the tray menu always offers "Cancel capture".
- Overlays are `WS_EX_TOOLWINDOW` — absent from Alt+Tab and the taskbar.

---

## 5. Data model

### Files

```
%LOCALAPPDATA%\Snipwhiz\
  captures\2026\07\<uuidv7>.png      original, immutable
  library.db                         SQLite
  settings.json
```

**`Guid.CreateVersion7()`**, not ULID. In the BCL since .NET 9, time-ordered —
the only property we wanted — and it deletes both a dependency and a unit test of
someone else's library. (Rev. 1's stated reason, "so the filesystem sorts
chronologically," didn't hold anyway: files are bucketed by month and the DB does
the real sorting.)

### `captures` table

| Column | Type | Note |
|---|---|---|
| `id` | TEXT PK | UUIDv7 |
| `created_utc` | INTEGER | Unix ms |
| `width`, `height` | INTEGER | physical px |
| `source_app` | TEXT | process name of the foreground window at grab time |
| `source_title` | TEXT | its window title |
| `file_path` | TEXT | relative to root |

`PRAGMA user_version = 1` from the first write, and WAL mode. Spec 2 adds columns
by design; `ALTER TABLE ADD COLUMN` is free in SQLite, so nothing is pre-built
for it.

**Cut on review** (all served a library UI that isn't in this spec, and all are
free to add later): `pinned`, `deleted_utc`, `thumb_path`, the
`created_utc DESC` index, and the entire `Thumbnailer` component.

**Deliberately kept against review advice:** `source_app` and `source_title`. The
reviewer classed these as library-UI speculation, but they are **capture-time-only
data** — you cannot determine what app was focused after the fact. Same category
as cursor state (§4.9), and the reviewer's own argument for keeping that applies
here.

### `settings.json`

Three fields, no UI: `autostart`, `printScreenPromptAnswered`,
`printScreenTakenOver`. If it needs a fourth before spec 2, that's a signal the
settings UI is overdue.

---

## 6. Error handling and notification

**Notifications are `NotifyIcon.ShowBalloonTip`, not toasts.** Toasts from an
**unpackaged** app are *silently dropped* unless a Start Menu shortcut carrying a
matching AppUserModelID is installed — and the installer that would create it is
**spec 3**. Rev. 1 resolved nearly every error path to "notify the user", which
would have failed invisibly for the whole of spec 1 development, leaving no
failure-reporting channel at all. Balloon tips are tied to the tray icon and need
no AUMID.

| Failure | Behaviour |
|---|---|
| Disk write fails | Image is **already on the clipboard**; balloon says saving failed and why |
| Clipboard locked | Retry with backoff, then report (§4.10) |
| Hotkey conflict | Per-chord; app starts, balloon names the chord, tray menu still works (§4.7) |
| Activation refused | Abort and tear down; never leave an unfocusable fullscreen window (§4.11) |
| All-black frame | Say so rather than silently saving a black rectangle — and distinguish the two causes (§4.4): DRM is a permanent limitation, fullscreen-exclusive is not |
| Desktop switch mid-capture | UAC/secure desktop abort (§8) |

**Ordering: clipboard first, then disk.** The clipboard is what the user is about
to paste and must not block on I/O. (Rev. 1 attached this rationale to the wrong
table row.)

Everything else aborts the capture and closes the overlay. Silent failure is not
acceptable — the user pressed a key and must learn whether it worked.

---

## 7. Testing

Most of this spec is Win32 interop that can't be unit tested without a desktop
session. So we test where the bugs actually live and verify the rest by hand.

### Unit tested (`Snipwhiz.Core.Tests`, xUnit)

- **Coordinate conversion** — virtual ↔ monitor bounds ↔ DIP, with the scale
  factor injected (§4.3). Across: negative virtual origins, monitors above/left
  of primary, mixed scale factors (100% + 150% + 225%), single-monitor. Highest
  value suite in the spec; write it before the overlay exists.
- **Crop math** — a virtual-space rect against the single virtual-space bitmap.
  Note this is now a subtraction of the virtual origin, *not* the per-monitor
  case rev. 1 described; that was a leftover from the WGC draft.
- **Non-rectangular virtual desktops** — an L-shaped or offset arrangement leaves
  regions inside the bounding rectangle covered by no display. Those pixels are
  undefined. Assert we detect and mark them rather than silently returning black
  bands. This is the hazard the single-pass grab actually introduces.
- **Selection rect** — normalization dragged in all four directions, clamping,
  zero-size and one-pixel rects.
- **Hex sampling** — pure function over a known bitmap; assert it rather than
  eyeballing it.
- **Store round-trip** — temp directory + in-memory SQLite.

### Manual checklist

**First, the configuration most recipients actually run** — rev. 1 mandated
mixed-DPI multi-monitor and never came back to the shipping baseline:

1. **Single monitor, 100%, 1080p or 1440p laptop panel, HDR on** — full loop works
2. Same panel with HDR on: check for washed-out/desaturated output (§8)

**Then the configuration that breaks everything** (multi-monitor, mismatched scaling):

3. Region capture on primary → clipboard matches selection pixel for pixel
4. Region capture on a 150% secondary → no scaling drift, no half-pixel offset
5. Region dragged across two monitors of different DPI → seamless, correct dimensions
6. Fullscreen mode captures the monitor under the cursor, not always the primary
7. Overlay paints in <120 ms — **logged stopwatch, week one** (§4.5)
8. Loupe hex readout matches the actual on-screen pixel, verified against an
   independent colour picker. **This is the pass/fail for the 1:1 rendering rule**
9. Menu/tooltip shadows and transparent overlay windows appear in the capture
   (proves `CAPTUREBLT`, §4.4)

**Then the things that bite in the real world:**

10. Paste into Word, Paint, Chrome and Slack — no black or blue backgrounds (§4.10)
11. Hotkey conflict: confirm defaults register on a stock box; accept the
    PrintScreen prompt and confirm it works; decline it and confirm the registry
    is untouched
12. Trigger a UAC prompt while the overlay is open → clean abort, no orphaned window
13. Deny activation (overlay opened over an elevated foreground window) → aborts
14. Disconnect a monitor while the overlay is open → clean abort, no crash
15. Autostart survives a reboot
16. Second instance launch → focuses the existing one
17. **Soak:** run a week, capture repeatedly, watch GDI handle count in Task
    Manager stay flat (§4.6)

---

## 8. Known limitations — state them, don't discover them

- **DRM-protected content captures black.** No user-mode API can capture it, ours
  or anyone's (§4.4). Snipping Tool has the same limitation.
- **The UAC prompt itself cannot be captured** — it lives on a separate desktop.
  Users will try. If the overlay is open when UAC appears, the desktop switches
  and the overlay is orphaned behind it; detect the desktop/session change and
  abort.
- **Fullscreen-exclusive games:** the hotkey forces a mode switch and minimises
  the game, *and* the frozen bitmap is black. Detect, decline, and say why rather
  than shipping the classic "screenshot tool killed my game" report.
- **HDR:** with HDR enabled, GDI captures come back washed out — severe enough
  that Snipping Tool ships a dedicated colour-corrector toggle. Newer laptops
  (exactly what we're shipping to) default HDR on. Spec 1 documents it and tests
  for it; correction is spec 4 work.
- **Other topmost windows** (Teams call bar, media PiP, game overlays) can sit
  above ours depending on activation order.

---

## 9. Open question

**PrintScreen takeover timing** (§4.7) — determine during implementation whether
clearing `PrintScreenKeyForSnippingEnabled` applies immediately or needs a
sign-out, and word the prompt to match. Affects copy, not design.

**Resolved since rev. 1:** product name is Snipwhiz; the WGC-latency and
capture-library spikes were dissolved by the `BitBlt` decision; the grab-latency
measurement was restored as a real week-one gate with Desktop Duplication as the
pre-agreed fallback.

---

## 10. Not doing, and why

- **WGC capture path** — reversed in §4.4. Note the escalation target is DXGI Desktop Duplication, not WGC
- **Low-level keyboard hook** — AV-heuristic risk on an unsigned-reputation binary (§4.7)
- **Window-under-cursor capture** — deferred; needs `WindowEnumerator` plus three traps: `GetWindowRect` returns the invisible DWM resize border (want `DWMWA_EXTENDED_FRAME_BOUNDS`), cloaked UWP ghost windows need filtering via `DWMWA_CLOAKED`, and the enumeration snapshot must be taken *at grab time* because the desktop is frozen
- **Repeat-last-region** — needs rect persistence and monitor-topology invalidation
- **Thumbnails** — need the library UI to be meaningful; also a latency contributor if generated synchronously
- **Settings UI** — three JSON fields don't need a window
- **Retention/cleanup** — needs the library UI
- **HDR colour correction** — documented as a limitation here, fixed in spec 4
- **Delayed/timed capture** — spec 4
- **Configurable hotkeys** — defaults plus takeover plus graceful conflict handling covers spec 1
