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

- [x] **Step 1: Copy on the tile**, revealed on hover over the thumbnail, alongside
  Delete. Routed through `ClipboardCopier` — it is documented as the one and only
  path to the clipboard and that must stay true with three call sites, not two.
- [x] **Step 2: Copy in the editor**, in the top bar, and `Ctrl+C`. `Key.C` without
  a modifier is still Crop.
- [x] **Step 3: Confirmation that is not a dialog.** *Deviated:* it goes in the
  footer on the library screen and the status line in the editor, not the tray
  balloon the plan named. A balloon is right for a capture taken while the window
  is not on screen, and wrong for a click two inches from the pointer in a focused
  window.

**Editor Copy had to save first, and that is the whole task.** `ClipboardCopier`
copies a *file*, and the editor has no dirty state by design — it writes on the way
out. Copying the file as it stands would put the previous render on the clipboard
and silently omit the last few annotations, which is worse than no Copy button
because it looks like it worked. So it saves, waits for `SavePipeline` to commit,
and copies what the commit produced. **Every failure path declines to copy rather
than copying something stale** — including a save that succeeds while its render
fails, where `Assets.Display` silently falls back to the un-annotated original.

**A pre-existing flake surfaced and is fixed.** `ClipboardFormatTests` failed about
one solution run in six: the clipboard is machine-global, the collection attribute
only stops these tests racing *each other*, and any application can take ownership
between the write and the read-back. The write-and-read pair now retries. Verified
8 runs clean, and the control — demanding `FileDrop` from a write that publishes
none — still fails in ~500 ms, so the retry tolerates a clobber without masking a
defect.

---

### Task V2: Straight to the editor

**Files:** delete `Library/PreviewView.xaml` and `.xaml.cs`; `LibraryWindow`.

- [x] **Step 1: Tile activation opens the editor.** `EditRequested` directly.
- [x] **Step 2: Delete `PreviewView` entirely** — the control, `PreviewHost`, the
  `_preview` field, and its arm of the Escape chain.
- [x] **Step 3: Escape now backs out one level, not two.**

**Verification:** double-click a tile, land in the editor with the capture loaded.
Escape from the editor returns to the library; Escape again hides the window.
**Negative control:** the type must be gone from the assembly, not merely unused.

---

### Task V3: The library grid

**Files:** `LibraryWindow.xaml(.cs)`, `CaptureTile.xaml`, `LibraryViewModel`.

- [x] **Step 1: Tiles divide the width.** The column count is already computed and
  measured correct; tiles take `available / columns` instead of a fixed 252.
- [x] **Step 2: A light mat behind each thumbnail**, so a dark capture has an edge.
  *Deviated: there is no mat.* It was built as specified, looked like what it was —
  a grey block — and was rejected on sight. The problem is real, but filling the
  empty space was the wrong answer to it; **removing** the empty space is the right
  one. `UniformToFill` plus `ClipToBounds` means the picture *is* the area and its
  own boundary is the tile's, and a hairline underneath separates two dark captures
  side by side. The cost is honest: a very wide capture loses its sides.
- [x] **Step 3: The search field reads as one** — magnifier, placeholder, focus ring.
  The capture count moves up beside it, out of the footer.
- [x] **Step 4: The tab underline sizes itself.** It is currently `Width="46"` with
  `Margin="-46,0,0,0"`, hand-measured to the word "Library".
- [x] **Step 5: The marquee.** Four corner brackets in the icon's four colours,
  scaling in from slightly oversized, on hover and on selection — spec §3.7. Four
  `Border` elements with two sides each, on a `Storyboard`. **This replaces the
  accent border**, which frees orange to mean "action" rather than doubling as
  "selected".

**Verification:** at 1100, 1400 and maximised, the tiles consume the full row width
with no gutter. **Negative control:** restore the fixed width and confirm the
assertion fails. The existing grid virtualization gate re-runs unchanged — row
height is untouched, so it must still pass.

**Done:** 11 assertions in `GridLayoutTests`, six of them observed failing against
the old fixed 252 before the fix went in. Virtualization gate unchanged and passing.

**A defect the tile actions surfaced:** hovering a tile and clicking Copy opened the
editor instead. `Click` bubbles, but the grid listens on `PreviewMouseLeftButtonUp`,
which *tunnels* and therefore fires first — `e.Handled` in the Click handler was
already too late. Fixed by bailing out of `OnGridClick` when the original source
sits inside a `Button`. The same wrong comment was already on `RemoveButton`, where
it had never shown because `OnGridClick` returns early for a missing capture: a bad
pattern copied from what looked like precedent.

---

### Task V4: The editor canvas and rail

**Files:** `Editor/EditorView.xaml`, `Editor/CanvasHost.cs`, `Editor/StylePill`.

- [x] **Step 1: The capture sits on a mat.** Checkered ground, soft shadow, hairline
  border — so the image has a boundary and the crop tool has something to aim at.
  *Deviated: no checker.* The checker means alpha everywhere it is used and a
  screenshot is opaque, so it would say something untrue about the pixels. Shadow and
  hairline only.
- [x] **Step 2: The rail hugs its tools**, grouped by family with separators,
  instead of stretching the window height.
- [x] **Step 3: The style pill gets a legible ground.** Solid translucent, not
  frosted — WPF has no per-element backdrop blur and chasing one is wasted time.
  *The ground was already 95% opaque and fine.* What made the pill look imported from
  another program was the stock WPF `Slider`, whose track halves template as raised
  Aero chrome buttons. That is what got restyled.
- [x] **Step 4: The marquee on a selected annotation**, matching the library tile,
  and reconciled with what the crop tool already draws. Three surfaces currently
  draw a version of this shape and look unrelated; after this they are one motif.
  *Deviated: regions get the marquee, objects do not.* The mark means "an area is
  chosen" — true of a crop and of a library tile, false of a rectangle you drew. An
  annotation already says what it is with eight handles and a rotate arm, and
  brackets over those would be two selection languages on one shape.

**Verification:** open a capture that is almost entirely black and confirm its edges
are visible. **Negative control:** a deliberately white capture, since a mat tuned
only against dark ones is a mat tuned to half the problem.

**The negative control did its job, and the diagnosis was not the obvious one.** The
white capture showed no edge and no shadow. The reason was not that the hairline is
too faint on white — it was that **the shadow had never worked at all.** Black at 55%
over the window's near-black ground is not a shadow, and the dark capture only looked
correct because the hairline was carrying the entire effect on its own.

So the fix is a ground the shadow can darken: the editor canvas is now *lighter* than
the chrome around it, which is what every image editor does and what this one was not
doing. A white capture casts a visible shadow onto it; a black capture contrasts with
it and keeps the hairline.

**Two further defects surfaced by eye during the same pass**, neither of them in this
task's scope and both real:

- **The crop rectangle had corners but no edges.** Four brackets say where the corners
  are and leave the edges between them to be inferred, which on a large crop is a long
  way to infer. A faint continuous edge now joins them, dimmer than the brackets.
- **Zoom existed and was undiscoverable.** `Ctrl`+wheel, `Ctrl+0` and `Ctrl+1` all
  worked; the percentage in the status line was a readout for controls that did not
  exist anywhere. It is now a control — step out, step in, Fit, 1:1 — and `Ctrl+plus`
  / `Ctrl+minus`, which genuinely were missing, are bound on both the number row and
  the numpad.

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
