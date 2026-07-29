# Editor — Design Spec

**Project:** Snipwhiz — a Snagit-class screenshot tool for Windows
**Spec:** 2b of 6 — Annotation Editor
**Date:** 2026-07-27 (rev. 2, post-review)
**Status:** Ready to plan from

---

## 1. Context

Spec 1 captures. Spec 2a stores, browses and re-copies. Neither can change a pixel.

This spec builds the other half of the sentence that started the project — *"as
well as edit later"* — and it is the largest single piece of work in the plan.
The user chose **full Snagit tool parity** after being told it was probably a spec
of its own. That is the committed scope and this document is written to it.

**Done when:** a capture opens in an editor; every tool below can be drawn,
selected, restyled, moved, resized, rotated, undone and redone; closing saves
without asking; the library tile, preview and clipboard all show the annotated
result; and re-opening restores every object still editable.

### Decisions taken before writing this

**Full parity, phased delivery.** §8 sequences ~20 tools plus a style system so a
usable editor exists early and each later phase is additive. Delivery order, not
scope reduction.

**Capture flow is opt-in.** A new setting, *"Open editor after capture"*,
defaulting **off**. Spec 1's path — hotkey, drag, on the clipboard — is untouched
unless the user turns this on.

**Saving updates in place, non-destructively.** The captured PNG is immutable
forever. Annotations live in a sidecar project file and a flattened render is what
the rest of the app displays. One capture, one library row, however many edits.

**There is no unsaved state.** Closing the editor, switching documents, or copying
saves first, silently. §4.14 explains why this removes more than it costs.

**Blur and magnify sample the original capture only** (§4.9), accepting that an
annotation underneath a blur stays sharp.

### Two supersessions, recorded rather than done quietly

**Core gains `UseWPF`.** Spec 1 §3 recorded twice that "WPF is still absent from
Core, which is the part that actually matters." That is reversed here.
`Annotation.Render(DrawingContext)` lives in Core so that the on-screen canvas and
the flattener are the *same code* — which is the only thing that makes WYSIWYG a
property rather than a hope (§7). Splitting `Render` into App would force a type
switch over every annotation type in App, exactly the design §4.4 rejects for
styles. The original justification for a WPF-free Core was a WebView2 UI that no
longer exists (spec 2a §1). `Snipwhiz.Core.csproj` currently sets
`UseWindowsForms`; it gains `UseWPF` alongside it, and Core's tests gain an STA
runner — precedent already exists in `ClipboardFormatTests`.

**The project sidecar is `projects/<id>.ssproj`, not `<id>.snipwhiz.json`.** Spec
1 §4.8 promised the latter, beside the original. Projects belong in their own
directory beside `captures/` and `thumbs/` so that a capture directory stays a
directory of captures, and the extension is meaningful because file associations
are a spec 6 feature.

---

## 2. Scope

### In

**Drawing tools** — arrow, line, freehand pen, rectangle, ellipse, polygon,
callout bubble, text, step numbers, highlight

**Pixel tools** — blur, pixelate, magnify, spotlight (dim everything outside a
region)

**Document tools** — crop, cut-out (remove a band and rejoin), resize canvas,
border, edge effects (shadow, torn, faded, page-curl)

**Stamps** — a bitmap sticker placed and scaled on the canvas

**A style system** — stroke colour, fill, width, opacity, arrowhead shape, font,
shadow presets; live-editable on a selection via a contextual floating toolbar;
tool defaults remembered

**Editing model** — selection, multi-select, handles for move/resize/rotate,
z-order, duplicate, delete, full undo/redo, zoom and pan

**Persistence** — `.ssproj` sidecar, schema-versioned from the first write

**Integration** — a single display path so tile, preview, clipboard and export all
show the annotated result; library re-hosted as a pull-up sheet inside the editor
(promised by spec 2a §2)

### Out

- **Video, GIF, OCR, scrolling capture** — specs 4 and 5
- **A portable single-file project format.** Ours references a capture id and is
  only meaningful inside this library. Portability is a share feature, spec 6
- **Templates and multi-capture composition** — document composition, spec 6
- **Editor chrome themes** — dark only, matching the library
- **Multiple documents open at once / tabs** — §4.14
- **Rich text runs, per-character styling** — one style per text object

---

## 3. Architecture

```
┌─ Snipwhiz.Core  (gains UseWPF — §1) ───────────────────────┐
│  CaptureStore · LibraryDb · PngDecoder · ThumbnailCache    │
│       ├── NEW: Annotations/*   model + Render      (§4.3)  │
│       ├── NEW: SceneDocument   crop, cutouts, z     (§4.10)│
│       ├── NEW: ProjectStore    .ssproj read/write   (§4.11)│
│       ├── NEW: Flattener       scene → PNG          (§4.12)│
│       ├── NEW: CaptureAssets   the display path     (§4.13)│
│       └── NEW: schema v3                            (§5)   │
└───────┬────────────────────────────────────────────────────┘
        │ in-process
┌───────▼────────────────────────────────────────────────────┐
│ Snipwhiz.App                                               │
│   EditorWindow.xaml      chrome, tool rail, sheet host     │
│   CanvasHost.cs          DrawingVisual scene graph  (§4.2) │
│   Tools/*.cs             one class per tool         (§4.4) │
│   SelectionAdorner.cs    handles, rotate, marquee   (§4.6) │
│   StyleBar.xaml          contextual floating toolbar       │
│   UndoStack.cs           command pattern            (§4.7) │
│   LibraryView            control, hosted two ways   (§4.16)│
└────────────────────────────────────────────────────────────┘
```

The object model, `Render`, and the flattener all live in Core: serialized,
flattened and unit-tested without a window (§1). App owns interaction only.

---

## 4. Design decisions

### 4.1 One resolved display path, not three

*This is the change the review forced, and it is the most important section here.*

Today three call sites independently resolve a capture to a file, and all three
reach for the original PNG:

- `ThumbnailCache.Generate` → `store.ResolvePath(record)`
- `ClipboardCopier.CopyAsync` → `store.ResolvePath(record)` (`ClipboardCopier.cs:27`)
- `PreviewView.Open` → `store.ResolvePath(record)` (`PreviewView.xaml.cs:74`)

The first draft of this spec redirected only the thumbnail. That would have
shipped a library whose grid showed the annotated image while its preview and its
clipboard — the highest-traffic path in the product — silently handed back the
un-annotated one.

So Core gains one type that every consumer goes through:

```
CaptureAssets
  Display(record)   → flat/<id>.png if it exists, else the original
  Original(record)  → always the capture, for the editor's source bitmap
  Project(record)   → projects/<id>.ssproj
  All(record)       → every file belonging to this capture   (§4.15)
```

`ThumbnailCache`, `ClipboardCopier`, `PreviewView`, export and drag-out all call
`Display`. Only the editor calls `Original`. **Reveal in Explorer and Open with
default viewer also use `Display`** — what the user is pointing at is the picture
they can see.

`Display` falls back to the original when the flat render is missing, so a failed
or deleted flatten degrades to the un-annotated capture rather than to a broken
tile.

### 4.2 The scene graph is `DrawingVisual`, not elements and not `OnRender`

*One `FrameworkElement` per annotation on a `Canvas`* gives hit-testing and
adorners free but carries layout, styles, templates and routed events per object —
the obvious choice, and wrong at a hundred objects. *One element drawing everything
in `OnRender`* retains nothing, so any change repaints the whole scene and
hit-testing is entirely hand-written.

**`DrawingVisual` inside a `VisualCollection` host is WPF's answer to exactly this
problem.** One visual per annotation; changing an object re-renders that object
only; `VisualTreeHelper.HitTest` walks the collection natively, including
non-axis-aligned geometry, which §4.6 needs for rotation.

The host is a `FrameworkElement` overriding `GetVisualChild` and
`VisualChildrenCount` — roughly 40 lines, and it is the whole "3–5k lines of scene
graph" the original plan feared when comparing against Konva.js.

### 4.3 Coordinate spaces: three, and objects live in the innermost

- **Image pixels** — the capture's own grid. Annotation geometry is stored here.
- **Canvas DIPs** — image space times zoom.
- **Screen pixels** — DIPs times the monitor's DPI scale.

Storing geometry in image space makes zoom, resize, DPI changes and flattening
exact and free: one `ScaleTransform` converts, and the flattener uses scale 1.
Storing DIPs would bake the zoom level at draw time into the saved file — invisible
until someone edits at 150% and exports at 100%.

Handle sizes, hit tolerances and minimum stroke widths are the exception: they are
perceptual, must stay constant on screen, and therefore divide by zoom.

### 4.4 Object model and the style system

```
Annotation (abstract)
  Id · ZIndex · Transform · Style
  Bounds(): Rect                      image space
  HitTest(Point, tolerance): bool     local space, via inverse transform
  Render(DrawingContext)              the ONLY render path (§7)
```

Concrete types: `Arrow`, `Line`, `Pen`, `Rectangle`, `Ellipse`, `Polygon`,
`Callout`, `Text`, `Step`, `Highlight`, `Blur`, `Pixelate`, `Magnify`,
`Spotlight`, `Stamp`.

`Style` is one record covering every visual property any tool uses. It holds
`System.Windows.Media.Color` values and numbers — **not `Brush`** — because
brushes are mutable, thread-affine unless frozen, and awkward to serialize; the
renderer converts to frozen brushes through a small cache.

One record rather than fifteen style types, because the contextual toolbar must
reflect and edit whatever is selected without a type switch per control, and
because multi-select must be able to set "stroke red" across mixed types. The cost
is properties meaningless on some types; the alternative is a visitor over fifteen
style types.

**Tool defaults are remembered per tool** and persisted with settings. `Settings`
is currently three booleans written by a non-atomic `File.WriteAllText`
(`Settings.cs:37`); growing it to hold fifteen style records means it gets the
same temp-file-then-move treatment as everything else that matters.

### 4.5 The tool catalogue

```
ITool
  Cursor
  OnPress / OnDrag / OnRelease  (Point imageSpace, ModifierKeys)
```

A tool produces a command (§4.7); it never mutates the scene directly.

| Group | Tools | Notes |
|---|---|---|
| Draw | arrow, line, pen, rectangle, ellipse, polygon | Shift constrains angle/aspect |
| Speak | callout, text, step number | §4.8 for text entry |
| Emphasis | highlight, spotlight | ~~Multiply~~ translucent fill (see below), and dim-outside |
| Redact | blur, pixelate | §4.9 — samples pixels |
| Zoom | magnify | §4.9 — samples pixels |
| Place | stamp | §4.10 on where assets come from |
| Document | crop, cut-out, resize, border, edge effects | §4.10 — change the canvas |

**Highlight's multiply blend is superseded, during phase B.** This section
originally specified highlight as a *multiply* blend against the pixels beneath it.
It ships as a **translucent filled rectangle** instead.

`DrawingContext` has no blend modes. Getting one means a WPF `Effect`, and an
`Effect` is applied by the visual tree — the flattener composites through
`DrawingContext` and would not apply it. That is a **second render path**, which is
the single failure §1 and the WYSIWYG gate exist to prevent: the highlight would
look right on screen and wrong in the exported PNG, and the user would find out
after sending the image. The trade is a slightly duller yellow over dark pixels
against a guarantee that the export matches the canvas, and the guarantee is worth
more than the yellow.

Verified rather than asserted: the gate now carries a highlight at its shipping
default with an arrow crossing it, and still diffs to **0 of 187,200 pixels**. Had
this been built as an `Effect`, that number is what would have said so.

The same reasoning applies in advance to **spotlight**, which §4.5 lists beside it.
Dimming everything outside a region is four filled rectangles or one geometry with
a hole, both of which `DrawingContext` draws directly — so spotlight keeps its
described behaviour and needs no departure.

The **contextual floating toolbar** is the design direction's headline move: tool
options appear in a pill beside the selection, not in a permanent right-hand panel.
Positioned above the selection bounds, flipped below when that would leave the
viewport, gone on deselect.

### 4.6 Selection, handles and transforms

Eight resize handles, one rotate handle above the top edge, a move region inside
the bounds. Handle hit-testing runs before object hit-testing, in screen space, at
fixed tolerance.

Rotation forces the care here: a rotated object's handles are not axis-aligned, so
hit-testing runs in the object's local space via the inverse transform.
`VisualTreeHelper.HitTest` does this for the object; the adorner's handles need it
by hand, and **the inverse-transform maths is unit-tested in Core against known
points before any UI exists** (risk 4).

Multi-select by marquee or Ctrl-click; group bounds is the union, and a group
transform composes onto each member on commit.

**Shift** constrains resize to aspect and rotate to 15°. **Alt** resizes about the
centre. Conventions every drawing tool shares.

### 4.7 Undo stores commands, never bitmaps

A 4K capture is 33 MB decoded. Twenty snapshots is 660 MB — precisely the failure
the library spent a session fixing.

**Command pattern.** Every mutation is an object with `Do`/`Undo` holding only a
delta: object id, before-value, after-value. Depth is bounded at **50 by count**,
not bytes, because commands are tiny.

Drags coalesce into one command on release. Style edits coalesce on a timer, so
dragging an opacity slider is one undo step, not forty.

Document operations (§4.10) are commands too, which is what makes crop undoable
without keeping the uncropped image — the original is immutable on disk and crop
is a stored rectangle.

**Undo history does not survive closing the editor** (§4.14). Every command type
gets a do/undo/redo round-trip test asserting scene equality.

### 4.8 Text editing borrows a `TextBox` and gives it back

A caret, selection, word-wrap, IME composition, RTL and accessibility is a project.
WPF ships all of it in `TextBox`.

Text renders as `FormattedText` in the scene graph; on double-click a real
`TextBox` is overlaid in place, styled to match, focused. On blur the string is
committed. **Escape cancels the edit and restores the previous string** — the
conventional meaning — and is therefore not itself an undo step.

The seam shows in one place: the overlay must match the rendered text's metrics
closely enough that nothing jumps on entering edit mode. Same font, size, padding,
and a consistent `TextOptions.TextFormattingMode` on both. Called out because it
will look broken until done and is easy to declare finished while still off by a
pixel — so it is checked by **pixel-diffing the rendered text against the overlay**
(§7), not by eye.

### 4.9 Pixel tools sample the original image only

Blur, pixelate and magnify need source pixels, so they are not pure vector objects.
The question is *which* pixels: the original capture, or everything beneath them.

**They sample the original capture.** Sampling the composite would make render
order load-bearing, force re-rendering every pixel tool whenever anything below it
changed, and create genuine cycles when two blurs overlap. Sampling the original is
cheap, order-independent and incrementally renderable.

The consequence is documented behaviour, not a discovery: **an arrow underneath a
blur is not blurred.** For redaction — the reason blur exists — that is correct,
because the thing being hidden is in the capture.

**Caching, corrected from rev. 1.** A blur's output depends on the pixels *under
its current position*, so moving it must recompute — rev. 1 said "recompute on
resize, not on move", which would drag a stale patch across the image and, for a
redaction tool, display the wrong content. The cache is keyed by
`(region, radius)`. During an interactive drag the region renders with a cheap
box-blur approximation; the separable Gaussian is computed on a background thread
and swapped in on release.

### 4.10 Document operations change the canvas, not the file

Crop, cut-out and resize alter what the canvas *is*, and all three are stored as
document properties rather than applied to pixels:

- **Crop** — a rectangle in image space. The cropped-away area is **shown dimmed
  and remains draggable** while the crop tool is active, and hidden otherwise;
  annotations outside it are clipped from the render but retained, so un-cropping
  restores them.
- **Cut-out** — an ordered list of removed bands (axis, offset, width) plus an edge
  style for the join. The flattener draws surviving regions adjacent; annotation
  coordinates map through the same band list. **An annotation straddling a removed
  band is clipped, not split and not forbidden** — its geometry is untouched and
  only its render is masked, so removing the band restores it whole.
- **Resize** — a target size applied at flatten time. Minimum canvas **16×16**,
  maximum **16384** on the long edge.
- **Border / edge effects** — drawn *outside* the image bounds, so the flat output
  is larger than the source. §5 stores the flat dimensions for this reason.

**Stamps need assets that do not exist yet.** Snagit ships hundreds. This is
content work with licensing exposure — nothing traced from Snagit's set or pulled
from an icon site without checking its licence. The spec commits to the mechanism
plus a small first-party starter set; sourcing a full library is §6 risk 2, with a
cost that is not engineering time.

### 4.11 `.ssproj` is JSON, versioned from the first byte

```jsonc
{
  "schema": 1,
  "captureId": "0198f2c1-…",
  "document": { "crop": {…}, "cutouts": [], "border": null, "edges": null },
  "annotations": [
    { "type": "rectangle", "id": "…", "z": 0, "transform": [1,0,0,1,120,80],
      "style": { "stroke": "#E5484D", "width": 4 },
      "geometry": { "rect": [0,0,180,90] } }
  ]
}
```

JSON, because these files get read by a human the first time something is wrong,
and `System.Text.Json` polymorphism is already in the box.

`"schema"` is present on the first write, before there is anything to migrate. The
library DB shipped versioning from day one and made the v1→v2 upgrade a non-event;
a format that starts unversioned cannot be fixed later without guessing.

**Unknown annotation types are preserved, not dropped**, so a file written by a
newer build and opened by an older one round-trips what it does not understand.
This is *not* free: `System.Text.Json` throws on an unknown type discriminator by
default, so it needs a custom converter that captures the raw `JsonElement` before
type resolution and re-emits it on write. Small, but a real plan task.

Written atomically — temp file, then move — like everything else that matters.

### 4.12 Save, flatten, and threading

Saving does four things:

1. Write `projects/<id>.ssproj` atomically.
2. Render the scene to `flat/<id>.png` via `RenderTargetBitmap` at image scale,
   atomically.
3. Update the DB row (§5) and invalidate the thumbnail (§4.13).
4. Notify the library (§4.13).

If step 2 fails the project is still saved and `Display` falls back to the
original — annotations are never lost because a render failed.

**Threading.** A 4K `RenderTargetBitmap` is tens of milliseconds and would be a
visible hitch on the UI thread, and autosave-on-close (§4.14) makes it happen at
the worst moment. Steps 1 and 2 run on a **dedicated STA background thread**; step
3 marshals back, because spec 2a §4.5 put SQLite on the UI thread deliberately and
that is not being reversed for one caller.

### 4.13 Making the library actually update — and why `Remove` is not enough

Rev. 1 claimed invalidation was "one line: `ThumbnailCache.Remove(id)`". It is not,
and the review was right to call it the spec's own predicted bug:

- `LibraryViewModel` keeps one `CaptureTileViewModel` per capture id forever
  (`LibraryViewModel.cs:17`), so no new view model is built after an edit.
- `CaptureTileViewModel` latches `_loaded` and returns early on every later bind
  (`CaptureTileViewModel.cs:86`).
- `InsertNewest` dedupes by id, so re-raising the capture event is a no-op.
- The decode uses `BitmapImage.UriSource` without `BitmapCreateOptions.IgnoreImageCache`
  (`CaptureTileViewModel.cs:100`), so even a forced re-decode of the same path can
  return WPF's cached stale bitmap.

Deleting the cached JPEG only helps a tile that re-requests. Nothing does. The
result would be "my edits didn't save" — reported as data loss — when they did.

So the save path:

1. calls `ThumbnailCache.Remove(id)`;
2. raises an **`EditSaved(record)`** event, the sibling of the existing
   `OnCaptureCompleted`, which the library handles by calling a new
   `CaptureTileViewModel.Invalidate()`;
3. `Invalidate()` clears `Thumbnail` and `_loaded`, and re-requests **if the view
   model is still bound** — reusing the binding count added when thumbnail
   retention was fixed, rather than inventing a second liveness notion;
4. the reload decodes with `IgnoreImageCache` so WPF's global cache cannot serve a
   stale bitmap for a path whose contents changed.

`ThumbnailCache` generates from `CaptureAssets.Display` (§4.1), so the new
thumbnail is of the annotated image.

This gets its own verification check, with a negative control (§7).

### 4.14 There is no unsaved state

Closing the editor saves. Switching documents saves. Copying saves first. No
prompt, no dialog, no dirty flag anywhere in the codebase.

What this removes is larger than what it costs: no Save/Don't-save/Cancel path, no
dirty-state threading through close, copy, document switch and the library sheet,
no "what does Copy do on a dirty document" question — the flat PNG always exists on
disk, which is exactly what `CF_HDROP` needs (§4.17). A second open request simply
saves the current document and switches.

The cost is that undo history ends at close and a mistake is corrected by editing
again rather than by discarding. Given the original is immutable and every
annotation is an editable object, nothing is unrecoverable.

The editor **hides rather than closes**, matching `LibraryWindow`, so reopening is
instant. Only one document is open at a time; tabs are spec 6.

### 4.15 Delete, undo, and the whole file set

An edited capture owns four files. `CommitDelete` currently deletes two
(`LibraryWindow.xaml.cs:165`), so `.ssproj` and `flat/` would be orphaned forever.

`CaptureAssets.All(record)` returns the set — original, thumbnail, flat, project —
and delete commits over all of it. Undo restores the row via `LibraryDb.Insert`,
whose column list and `CaptureRecord` shape both grow with §5's columns; **an
undone delete that loses `project_path` silently severs a capture from its
annotations**, so the round-trip is asserted in a test.

`DropFileReferenceFromClipboard` currently matches only the original path
(`LibraryWindow.xaml.cs:219`). Once editor copy advertises the flat path as
`CF_HDROP`, copy-then-delete of an edited capture pastes a dead file reference —
the exact regression spec 2a's check 11 caught, reintroduced through a new path. It
matches against `CaptureAssets.All` instead.

### 4.16 One window, two screens

**Superseded during phase A.** This section originally specified the library
re-hosted as a **pull-up sheet inside a separate editor window**, inherited from
spec 2a §2 and the original design direction. It is replaced by a **top-level
Library / Edit switch in a single window**, proposed by the user after driving the
first runnable editor.

The sheet existed so that the library would not need a window of its own. A view
switch achieves that more cheaply and deletes the second window entirely: one
taskbar entry, one Mica surface, one Escape chain, one lifetime. Every hazard the
review flagged against the sheet disappears rather than being solved —

- Esc routing has one chain instead of two
- Mica applies to the window, not to a collapsed sheet
- `FlushPendingDeletes` keeps its existing hide trigger, because the window still hides
- the **static** `CaptureTile.RemoveRequested` event keeps its single subscriber

— and the spring-eased drag gesture is not written at all.

**The editor is a `UserControl`, not a window.** The shell holds both screens and
asks the editor first on every key: `EditorView.HandleKey` returns false for keys it
does not want. One place decides who owns the keyboard, so a shortcut cannot mean
two things at once.

There is one library view model, as before, because there is now only one library.

**Cost of taking it during phase A rather than phase F:** none worth recording. The
editor was one task old, and every later phase would have been built against the
two-window assumption.

### 4.17 Clipboard, export, and hiding for capture

Copy publishes the flattened image through spec 1's `ClipboardWriter` (2a added
`CF_HDROP`), using `CaptureAssets.Display`. Export writes PNG or JPEG to a chosen
path. No new format work.

**Capture hotkeys stay live while the editor is focused, and the editor hides for
the grab.** Spec 2a §4.14 solved this for the library; `HideLibraryForCapture`
knows only `_library` (`App.xaml.cs:149`), so an open editor would land in the
frozen screen grab — and with *Open editor after capture* on, each capture would
photograph the editor the previous one opened. Hide-for-capture generalises to all
app windows, with restore on **every** abort path spec 1 §4.11 enumerates: Esc,
right-click, watchdog, display change, focus refusal.

### 4.18 Keyboard, zoom and pan

**Escape unwinds one level at a time, innermost first:** text edit → active drag →
selection → library sheet → hide the window. Each level consumes the key only if it
is active.

**Shortcuts:** `Ctrl+Z`/`Ctrl+Y` undo/redo · `Ctrl+S` save now · `Ctrl+C` copy ·
`Ctrl+D` duplicate · `Delete` · arrows nudge 1 px, `Shift`+arrows 10 px ·
`Ctrl+0` fit · `Ctrl+1` 100% · `[`/`]` send back / bring forward · single letters
select tools. The library sheet gets `Ctrl+Z` only when it has focus, so undo never
crosses between the two.

**Zoom** 10%–800%, `Ctrl`+wheel around the cursor, fit-on-open for images larger
than the viewport and 100% otherwise. **Pan** with space-drag or middle-drag.

**If the original PNG is missing when a project opens** — spec 2a §4.12's scenario,
the user deleted it in Explorer — the editor refuses to open and the library shows
its existing missing-file affordance. Opening an editor over a blank canvas and
letting someone annotate nothing is worse than refusing.

### 4.19 Memory

The library holds 320 px thumbnails and still reached 708 MB by retaining them all.
The editor holds a 33 MB decoded 4K bitmap plus a cached blur region per pixel tool
plus a `RenderTargetBitmap` at save.

- One decoded source bitmap per open document, frozen and shared.
- One document at a time (§4.14).
- Blur caches dropped when their object is deleted or the document closes.
- Undo holds deltas only (§4.7).
- Closing releases the source bitmap.

Verified by a **counted gate with a named negative control**, not a working-set
eyeball (§7).

---

## 5. Schema changes — `captures` v3

```sql
ALTER TABLE captures ADD COLUMN project_path TEXT;     -- NULL until first edit
ALTER TABLE captures ADD COLUMN flat_path    TEXT;     -- NULL until first save
ALTER TABLE captures ADD COLUMN flat_width   INTEGER;  -- differs from width when
ALTER TABLE captures ADD COLUMN flat_height  INTEGER;  -- a border/effect is applied
ALTER TABLE captures ADD COLUMN edited_utc   INTEGER;
```

New method: `SetEditPaths(id, projectPath, flatPath, flatWidth, flatHeight, editedUtc)`.
`SelectColumns`, `ReadAll`, `Insert` and `CaptureRecord` all grow to match.

Columns rather than probing the filesystem per tile: the grid renders 21 tiles at a
time during a fast scroll, and `File.Exists` per tile is UI-thread work for
information the DB hands over free.

`flat_width`/`flat_height` exist because border and edge effects make the flat image
larger than the capture (§4.10), and two consumers would otherwise be wrong: the
tile caption (`CaptureTileViewModel.cs:43`) and the preview's physical-1:1 maths
(`PreviewView.xaml.cs:136`), which both read `record.Width/Height`.

**`edited_utc` does not affect ordering.** The library stays sorted by
`created_utc` — it is a record of when captures were *taken*, and finding one by
roughly-when should stay reliable.

Migration follows the existing stepwise pattern with `PRAGMA user_version` stamped
once at the end inside the transaction. The v1→v2 upgrade test gets a v2→v3 sibling.

---

## 6. Risks and open items

| # | Risk | Mitigation |
|---|---|---|
| 1 | **Scope.** ~20 tools plus a style system is larger than specs 1 and 2a combined | §8 phases it; every phase independently useful and verifiable |
| 2 | **Stamp assets are content, not code**, with licensing exposure | Mechanism plus a small first-party set; a full library is a separate decision with a non-engineering cost |
| 3 | **Text edit-mode jump** between `FormattedText` and `TextBox` | §4.8; pixel-diffed, not eyeballed |
| 4 | **Rotated hit-testing** on handles is hand-rolled | Inverse-transform maths unit-tested in Core against known points before any UI |
| 5 | **Blur performance** on a large region | Box-blur during drag, Gaussian on release, cached by region and radius |
| 6 | **Cut-out coordinate mapping** is the fiddliest geometry here | Pure function in Core; property-tested for stability under band reordering |
| 7 | **Editor memory** with a 4K source and many pixel tools | §4.19, counted gate with a negative control |
| 8 | **Undo correctness** across document ops and multi-select | Do/undo/redo identity test per command type |
| 9 | **Core gains WPF** — build and test surface changes | §1; Core tests get an STA runner, precedent in `ClipboardFormatTests` |
| 10 | **The re-host is bigger than it looks** | §4.16 enumerates the orphaned behaviour; scheduled as a phase of its own |

---

## 7. Verification approach

Two lessons are inherited explicitly. **A guard must be observed failing before it
is trusted** — a check that only ever passes proves nothing. And **an automated
gate that exercises one axis is blind to the others**: spec 2a's sweep measured
retention while scrolling and passed two UI regressions cleanly because it never
resized.

**Core, unit-tested without a window:** geometry, hit-testing, inverse transforms,
cut-out mapping, serialization round-trip, flattening against known scenes, every
undo command's do/undo/redo identity.

**WYSIWYG is a contract, not a hope.** The canvas and the flattener must call the
same `Annotation.Render` (§1). The check renders a known scene both ways and
**pixel-diffs canvas output against flattened output**. Without this, "export
doesn't match what I drew" is a whole bug class with no gate — this spec's version
of thumbnail-versus-original.

**Named negative controls**, each of which must be seen failing:

| Gate | Negative control |
|---|---|
| Tile refresh after save (§4.13) | Build with the `EditSaved` handler suppressed — tile must keep showing the un-annotated thumbnail |
| Editor memory (§4.19) | Build that retains the source bitmap after close — counter must read nonzero |
| WYSIWYG | Scene with a deliberately divergent flattener path — diff must be nonzero |
| Forward compatibility | Fixture with an unknown annotation type — must survive load/save |
| Clipboard after delete (§4.15) | Copy an edited capture, delete it, paste — must not offer a dead file reference |

**The golden-file test asserts scene equality against known values**, not merely
that loading did not throw — a no-throw assertion passes on a loader that returns
an empty scene.

**Manual checks are enumerated in the plan with expected outcomes**, each written
so it can fail, and include 100%/150% DPI and a resize pass — the axis that caught
the library out.

---

## 8. Delivery phases

Full parity is the commitment; this is the order it lands in.

| Phase | Contents | Usable after? |
|---|---|---|
| **A — Foundation** | `DrawingVisual` host, coordinate spaces, object model, selection and handles, undo stack, `.ssproj`, save/flatten, `CaptureAssets` and the display path, tile invalidation, schema v3, hide-for-capture. **Rectangle only** | Barely — but every claim in it is provable |
| **B — Core tools + minimum style** | Arrow, ellipse, line, text, highlight, crop, **plus a colour and width control** | **Yes — a genuinely useful editor** |
| **C — Redaction and emphasis** | Blur, pixelate, spotlight, magnify, step numbers, callout | Yes |
| **D — Remaining tools** | Pen, polygon, stamps, cut-out, resize, border, edge effects | Yes |
| **E — Full style system** | Complete contextual toolbar, per-tool defaults, shadow presets, fills, arrowhead shapes | Yes |
| **F — Shell polish** | Screen-switch transitions, capture-to-editor setting. **The re-host itself landed in phase A** (§4.16), so this is what is left of it | Yes |

**Rectangle is Phase A's proving tool** (review S2): it exercises create-by-drag,
hit-test, select, resize from eight handles, rotate, undo, serialize, flatten and
library refresh, with no text entry, pixel sampling or geometry subtleties to debug
alongside the foundation.

**A minimum style control moved from E into B** (review S2): tools in B–D with no
way to change colour or width until E would make three phases of "usable" untrue.
B gets colour and width; E gets the rest.

Phases A and B together are where this stops being scaffolding. Everything after is
additive, and any phase boundary is a safe place to stop, reprioritise, or
interleave spec 3.

---

## 9. Settled by review

Rev. 1's open questions, and the review's, are now decided in place: display path
(§4.1), blur sampling (§4.9), undo depth 50 (§4.7), step numbers per document,
crop shown dimmed (§4.10), entry point and single-document behaviour (§4.14),
autosave (§4.14), Escape order and shortcuts (§4.18), zoom/pan (§4.18), canvas
limits (§4.10), missing original (§4.18), edit ordering (§5), straddling
annotations (§4.10), Core/WPF boundary and the sidecar path (§1).

**Remaining, and genuinely open:** whether the stamp starter set is worth authoring
in-house at all, or whether stamps should ship empty in phase D and wait for a
licensed set. That is a content decision, not an engineering one, and it can be
taken as late as phase D.
