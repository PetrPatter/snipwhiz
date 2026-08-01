# Visual pass — Implementation Plan

Spec: [`2026-08-01-visual-pass-design.md`](../specs/2026-08-01-visual-pass-design.md)
Mockup: [`visual-pass.html`](../mockups/visual-pass.html)

**Goal:** the library and the editor look like they were designed rather than
accumulated, without changing what any of the thirteen tools do.

---

## Task order

**Copy comes first, before anything is deleted.** Removing `PreviewView` is the
change that makes the library faster, and it is also the change that would leave
this app unable to put a stored capture on the clipboard — the loop the whole
product exists for. Building the replacement first means there is never a commit
where that is broken.

**Frameless chrome comes last** because it is the only task that can fail in a way
worse than not doing it. A caption bar that does not drag, maximise or snap is
worse than stock Windows chrome, and if any of those cannot be made right the task
is abandoned rather than shipped.

---

### Task V1: Copy, in both places it now has to live

**Files:** `Library/CaptureTile.xaml`, `Editor/EditorView.xaml`, `Library/ClipboardCopier.cs`.

- [ ] **Step 1: Copy on the tile**, revealed on hover over the thumbnail, alongside
  Delete. Routed through `ClipboardCopier` — it is documented as the one and only
  path to the clipboard and that must stay true with three call sites, not two.
- [ ] **Step 2: Copy in the editor**, in the top bar, and `Ctrl+C`. Note `Key.C`
  without a modifier is Crop and stays that way.
- [ ] **Step 3: Confirmation that is not a dialog.** The tray balloon already says
  "Copied" for a fresh capture; reuse it rather than inventing a toast.

**Verification:** copy from a tile and from the editor, paste into Paint and into a
file picker — the second is what catches a broken `CF_HDROP`. **Negative control:**
delete one of the two call sites and confirm the test fails, because a test that
passes with Copy missing is the exact failure this task exists to prevent.

---

### Task V2: Straight to the editor

**Files:** delete `Library/PreviewView.xaml` and `.xaml.cs`; `LibraryWindow`.

- [ ] **Step 1: Tile activation opens the editor.** `EditRequested` directly.
- [ ] **Step 2: Delete `PreviewView` entirely** — the control, `PreviewHost`, the
  `_preview` field, and its arm of the Escape chain.
- [ ] **Step 3: Escape now backs out one level, not two.**

**Verification:** double-click a tile, land in the editor with the capture loaded.
Escape from the editor returns to the library; Escape again hides the window.
**Negative control:** the type must be gone from the assembly, not merely unused.

---

### Task V3: The library grid

**Files:** `LibraryWindow.xaml(.cs)`, `CaptureTile.xaml`, `LibraryViewModel`.

- [ ] **Step 1: Tiles divide the width.** The column count is already computed and
  measured correct; tiles take `available / columns` instead of a fixed 252.
- [ ] **Step 2: A light mat behind each thumbnail**, so a dark capture has an edge.
- [ ] **Step 3: The search field reads as one** — magnifier, placeholder, focus ring.
  The capture count moves up beside it, out of the footer.
- [ ] **Step 4: The tab underline sizes itself.** It is currently `Width="46"` with
  `Margin="-46,0,0,0"`, hand-measured to the word "Library".
- [ ] **Step 5: The marquee.** Four corner brackets in the icon's four colours,
  scaling in from slightly oversized, on hover and on selection — spec §3.7. Four
  `Border` elements with two sides each, on a `Storyboard`. **This replaces the
  accent border**, which frees orange to mean "action" rather than doubling as
  "selected".

**Verification:** at 1100, 1400 and maximised, the tiles consume the full row width
with no gutter. **Negative control:** restore the fixed width and confirm the
assertion fails. The existing grid virtualization gate re-runs unchanged — row
height is untouched, so it must still pass.

---

### Task V4: The editor canvas and rail

**Files:** `Editor/EditorView.xaml`, `Editor/CanvasHost.cs`, `Editor/StylePill`.

- [ ] **Step 1: The capture sits on a mat.** Checkered ground, soft shadow, hairline
  border — so the image has a boundary and the crop tool has something to aim at.
- [ ] **Step 2: The rail hugs its tools**, grouped by family with separators,
  instead of stretching the window height.
- [ ] **Step 3: The style pill gets a legible ground.** Solid translucent, not
  frosted — WPF has no per-element backdrop blur and chasing one is wasted time.
- [ ] **Step 4: The marquee on a selected annotation**, matching the library tile,
  and reconciled with what the crop tool already draws. Three surfaces currently
  draw a version of this shape and look unrelated; after this they are one motif.

**Verification:** open a capture that is almost entirely black and confirm its edges
are visible. **Negative control:** a deliberately white capture, since a mat tuned
only against dark ones is a mat tuned to half the problem.

---

### Task V5: Frameless chrome

**The only task that can fail worse than not doing it.**

- [ ] **Step 1: `WindowChrome` with a zero-height caption**, app-drawn bar carrying
  the icon, the title and the window controls.
- [ ] **Step 2: Restore what stock chrome gave for free** — drag, double-click to
  maximise, snap layouts on hover, and the right-click system menu.

**Verification:** each of those four by hand, in Sandbox, on a clean install.
**Negative control:** none needed — the failure is visible immediately. **If any of
the four cannot be made right, revert to stock chrome and record why.** A caption
that does not snap is worse than a caption that is not ours.

---

## Verification summary

**Most of this is not gateable and pretending otherwise would be the mistake.**
Whether it looks good is a person looking, which is what has found every defect
that mattered in this project so far.

| Gate | Automatable |
|---|---|
| Tiles consume the full row width at three sizes | yes |
| A copy path exists from the library and the editor | yes |
| `PreviewView` is gone from the assembly | yes |
| Grid virtualization still bounds realized containers | yes, existing gate |
| A near-black capture has visible edges | by eye |
| Drag, maximise, snap, system menu | by hand, in Sandbox |

---

## Known gaps, recorded rather than solved

- **No light theme**, so the app is wrong on a light desktop in exactly one place:
  the tray icon, which is why that one is bodiless and saturated.
- **The style pill will not be frosted.** §3.8 of the spec.
- **The overlay is untouched** and remains the least designed surface in the app.
- **Small text will look slightly cruder than the mockup.** WPF sets 11–12px type
  heavier and less evenly than a browser, and no amount of work changes it.
