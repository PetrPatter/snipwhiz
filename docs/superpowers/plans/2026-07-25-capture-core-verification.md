# Capture Core — Verification Results

**Branch:** `feat/capture-core` · **Commits:** 32 · **Tests:** 60/60 · **Build:** 0 warnings

**Hardware used during implementation:**

| Display | Bounds | Scale | Note |
|---|---|---|---|
| `\\.\DISPLAY1` (primary) | `1920×1080 @ (0,0)` | **1.25** | laptop panel |
| `\\.\DISPLAY2` | `1920×1200 @ (-1920,-177)` | **1.0** | **negative virtual origin** |

Hybrid graphics: NVIDIA Quadro P3200 + Intel UHD 630. The mismatched scale factors and
negative origin mean most of the dangerous cases were exercised continuously rather than
as a one-off pass.

---

## Already verified during implementation

Each row was verified while the relevant task was built, and independently re-checked by
the controller. Evidence is cited so it can be re-audited rather than taken on trust.

| # | Check | Result | Evidence |
|---|-------|--------|----------|
| 3 | Region on primary matches selection pixel-for-pixel | **PASS** | Task 9: `coordMatch` via `GetCursorPos` — an independent path that never touches `ToVirtualPixels` |
| 4 | Region on a mismatched-DPI secondary, no scaling drift | **PASS** | Task 9, plus a **fractional-DIP** case at `(137,213)→(614,509)` whose expected rect `(137,213,477×296)` was hand-derived from monitor geometry *before* the run and matched exactly |
| 5 | Region dragged across two monitors of different DPI | **PASS** | Task 9 seam-straddle across `x=0`, 125%↔100% |
| 6 | Fullscreen captures the monitor under the cursor | **PASS** | Task 7 (`1920×1200`) and the final fix wave (`1920×1080`), both with correct PNG headers and DB rows |
| 7 | Grab median under 120 ms | **PASS — 79.6 ms** | Dual-display, 3840×1257, 4.8 MP. See §4.5. **66% of budget** — see *Residual risks* |
| 8 | Loupe hex matches an independent colour picker | **PASS** | Task 10 vs. raw Win32 `GetPixel` on a screen DC — independent of both `SampleAt` and `BitBltGrabber`. Both monitors. Negative control at +50px correctly read a different colour |
| 11 | Hotkeys register; PrintScreen prompt, both answers | **PASS** | Task 11, driven live against the real registry. Greenshot (installed here) genuinely held `VK_SNAPSHOT`, exercising the "another app holds the key" fallback for real |
| 14 | Display change while the overlay is open → clean abort | **PASS** | Final fix wave. Real `WM_DISPLAYCHANGE` against the running app: overlays 2 → 0, app alive, `Ctrl+Shift+1` worked afterwards |
| 16 | Second instance does not start a second tray icon | **PASS** | Task 7. Note: it **exits silently** rather than focusing the first — correct for a tray app with no main window |
| — | 1:1 physical-pixel overlay rendering | **PASS** | Checkerboard harness, `0/1600` mismatched on both monitors, with a **demonstrated negative control at 71.88%** |

### Re-running the overlay check yourself

```powershell
$env:SNIPWHIZ_VERIFY_OVERLAY = "1"
dotnet run --project src/Snipwhiz.App
# press Ctrl+Shift+1 — result written to %TEMP%\snipwhiz-overlay-verify.txt

# negative control — must report a LARGE mismatch and sessionSelfAborted=True
$env:SNIPWHIZ_VERIFY_BREAK_SCALE = "1"
```

---

## Needs a human at the machine

These are the ones I could not do. Fill in the result column.

| # | Check | How | Result |
|---|-------|-----|--------|
| 1 | Single monitor, 100%, laptop panel — full loop works | Unplug the external display, set the laptop to 100%, capture a region | |
| 2 | Same panel with **HDR on** — note washed-out or desaturated output | Settings → System → Display → HDR on, then capture | |
| 9 | Menu/tooltip **shadows** appear in the capture | Open a right-click menu, capture a region including its drop shadow. Proves `CAPTUREBLT` (§4.4) | |
| 10 | **Paste into Word, Paint, Chrome and Slack** — no black or blue backgrounds | Capture, then `Ctrl+V` into each. This is the defect the hand-built 3-format clipboard payload exists to prevent (§4.10) | |
| 13 | Overlay opened over an **elevated** foreground window → aborts cleanly | Focus an admin PowerShell, press `Ctrl+Shift+1` | |
| 15 | **Autostart survives a reboot** | Tray → "Start with Windows", reboot, confirm the icon returns | |
| 17 | **Soak:** repeated captures, GDI handle count *and* managed working set stay flat | Task Manager → Details → add "GDI objects" and "Memory". Capture repeatedly over a session | |

### Known limitations to confirm rather than fix

| Limitation | Confirmed? |
|------------|-----------|
| **DRM content (Netflix) captures black.** No user-mode API can capture it — Snipping Tool has the same limitation (§4.4) | |
| A **playing YouTube video** in a browser *does* capture correctly (DWM-composited, unlike DRM) | |
| The **UAC prompt itself cannot be captured** — it lives on a separate desktop | |
| **Fullscreen-exclusive games**: black frame plus a forced mode switch | |

---

## Residual risks carried into spec 2

1. **Grab latency headroom is thinner than the raw number suggests.** 79.6 ms of a 120 ms
   budget on two displays. The cost driver is **display count, not pixel count** — going
   1.3 → 2.1 MP on one display cost 4%, while adding a second display cost 140% (hybrid
   graphics forces a cross-adapter composite). A third display could plausibly exceed the
   budget. `IDesktopGrabber` is the seam; DXGI Desktop Duplication is the pre-agreed
   escalation (§4.4/§4.5). **Do not extrapolate this by pixel count** — that was tried
   twice during planning and was wrong in both directions.
2. **The `WM_DPICHANGED` fix depends on undocumented WPF timing.** Verified empirically,
   not by reasoning. The permanent post-show invariant (window rect *and* rendered image
   DIP size, cross-checked against `VisualTreeHelper.GetDpi` rather than the field it
   validates) is the backstop, and it **aborts** rather than shipping a mis-scaled overlay.
3. **Desktop-switch (UAC) is explicitly not handled.** The overlay survives the switch;
   Esc and right-click remain the exit. Recorded in §8 rather than claimed as implemented.

---

## A note on verification standards used here

Five checks written during this project turned out to be **circular or blind** — the
expected value derived from the code under test, or a fixture that made the target bug
invisible. All five were caught and replaced.

The standard adopted, and applied to every "PASS" above: **a guard must be observed
failing before it is trusted.** Where a row cites a negative control, that control was
actually run and actually failed.

Examples of the failure mode, kept as a reference for spec 2:

- comparing a re-grabbed static desktop against the frozen buffer — identical whether the
  overlay is pixel-perfect *or absent*
- appending `"disk"` to an ordering log *after* the call under test returned
- sampling a pixel fixture only where `x == y`, in a fixture encoding red as `x` and green
  as `y` — blind to precisely an R/G channel swap
