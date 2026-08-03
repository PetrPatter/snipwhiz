# Visual pass — Design Spec

**The library and the editor**, which are the two screens people spend time in.

Mockup: `docs/superpowers/mockups/visual-pass.html` — the direction was settled by
looking at a rendered comparison rather than by argument, which is the same method
that settled the icon and the same method spec 1 used before any app code existed.

---

## 1. Context

Three phases built the editor and the library and neither got a visual pass. Both
work; both look like the sum of the features added to them. The distribution spec
is five tasks of six done, and the last one ships an auto-updater whose job is
pushing the current build onto other people's machines — so the look becomes
permanent for every recipient the moment it runs.

**The trigger was the tray icon.** It was `SystemIcons.Application` and it read as
broken next to nine branded neighbours. Fixing it exposed the same question one
level up: the app has a design system in its head — a warm-neutral dark surface, a
single orange accent, Mica — and the screens do not consistently express it.

---

## 2. Scope

### In

- **The library grid**: tiles that divide the width, thumbnails that separate from
  their tile, a search field that reads as one, day headers, and tile-level actions.
- **The editor**: a canvas the capture is visibly sitting *on*, a tool rail grouped
  by family, and the style pill given somewhere legible to sit.
- **Frameless window chrome** across both, which spec 1 called for and never got.
- **Removing `PreviewView`** — the library goes straight to the editor.
- **Copy**, which has to be rebuilt somewhere because §4.5 explains it currently
  exists nowhere else.

### Out

- **New tools or editing features.** This changes how the existing thirteen look,
  not what they do.
- **Light theme.** The app commits to one visual world; a light mode is a separate
  piece of work with its own contrast decisions.
- **Animation beyond hover and selection feedback.** Spring-physics panel
  transitions are a nice idea and not what makes this app feel unfinished.
- **The capture overlay.** It is opaque, it is fast, and nobody looks at it for
  more than two seconds.

---

## 3. Design decisions

### 3.1 Tiles divide the width; they do not claim a fixed one

The grid fits as many 252-DIP tiles as go and leaves the remainder as dead gutter
down the right edge — 140 DIP at a maximised 1550, and a different amount at every
other width. **The column count logic is correct and was measured** (1100 gives 3,
1550 gives 5); the problem is that tiles do not stretch to consume what is left.

WPF has no `repeat(auto-fill, minmax())`. The column count is already computed, so
this becomes: compute the count, then divide the available width by it and let
tiles take that width.

### 3.2 Thumbnails get a light mat

A capture sits on `#141312` inside a `#26241F` tile. Nearly every capture this app
takes is a dark terminal, editor or browser, so the picture and the chrome bleed
into each other and no tile has a visible subject.

A light mat behind the thumbnail gives every capture an edge regardless of its
content. This is the opposite of the instinct — a dark app wants dark surfaces —
and it is right for the same reason a gallery mats a print rather than mounting it
on the wall colour.

### 3.3 The canvas must not be the same black as the capture

The editor's canvas and a dark screenshot are within a few values of each other,
so the image has no boundary and the crop tool operates on something whose edges
cannot be seen. A checkered mat with a soft shadow under the capture fixes it, and
the checker also distinguishes transparent regions once cut-out exists in phase D.

### 3.4 The rail hugs its tools

Fourteen icons in a column that stretches the full window height, evenly spaced,
with no grouping. Family grouping — select, shapes, marks, effects, crop — with the
container ending where the tools end, gives the canvas the space back and makes the
rail scannable.

### 3.5 Copy has to be rebuilt, and this is the point of the whole change

`ClipboardCopier` documents itself as "the one and only path from a stored capture
to the clipboard", and **both of its call sites are inside `PreviewView`** — the
Copy button and its Ctrl+C handler. The editor has none; `Key.C` is Crop.

So deleting the preview screen, which is otherwise a clean deletion of a whole
view, would leave this app with no way to get a stored capture back onto the
clipboard. That is the headline loop of the product.

Copy therefore lands in two places, and both are fewer clicks than today:

| | Today | After |
|---|---|---|
| Re-copy an old capture | open preview, click Copy, dismiss | hover the tile, click Copy |
| Copy after annotating | not possible without leaving and reopening | Copy in the editor |

### 3.6 Frameless chrome

Stock Windows chrome saying "Snipwhiz Library" sits above a Mica surface and
fights it. `WindowChrome` with a zero-height caption lets the app draw its own bar,
carrying the new icon and the window controls.

**The cost is real and worth stating**: a custom caption means re-implementing
drag, double-click-to-maximise, snap layouts on hover, and the right-click system
menu. Getting any of those wrong is worse than stock chrome, so they are gated
individually.

### 3.7 The signature: selection is a capture marquee

**Everything else in this spec fixes a problem. This is the one part that adds an
idea**, and the first draft of the mockup did not have one — it was a competent
cleanup that could have belonged to any dark utility app.

Selecting something shows **four corner brackets in the icon's four colours**,
scaling in from slightly oversized like a viewfinder locking on. It appears on a
hovered or selected library tile, on a selected annotation in the editor, and it
is already what the crop tool draws.

It earns its place by being the product's own vernacular rather than decoration.
**Dragging a selection rectangle is what Snipwhiz is** — it is the first thing
anyone does with it and the most repeated gesture in the app. Three surfaces
already draw a version of this shape (the icon, the crop handles, the capture
overlay) and they currently look unrelated; one motif unifies them for free.

It also frees the accent. Orange currently means both "this is selected" and "this
is the action", and pulling selection onto the marquee leaves orange to mean one
thing: Copy, and the active tool.

**Boldness is spent here and nowhere else.** Everything around it stays quiet —
that is the point of having a signature rather than a theme.

### 3.8 What the browser was flattering

The mockup is HTML and some of it does not reach WPF. Recorded here so it is not
discovered late and chased:

- **`backdrop-filter`** has no per-element equivalent. Mica is window-level. The
  style pill becomes a solid translucent brush — close, not identical.
- **CSS transitions** are one line each; in WPF each is a Storyboard. They go where
  motion is felt (tile hover, selection) and nowhere else.
- **Text rendering.** WPF sets small type heavier and less evenly than a browser.
  The 11–12px labels will look slightly cruder and no amount of work changes it.

Expect roughly 85% of the perceived gap. The missing 15% is mostly the pill.

---

## 4. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | **Custom chrome breaks window management** | Drag, maximise, snap and the system menu each get their own gate; stock chrome is the fallback if any cannot be made right |
| 2 | **Copy is lost with the preview** | §3.5; a test asserts a copy path exists from both the library and the editor, and it is written before the preview is deleted |
| 3 | **Stretching tiles breaks row virtualization** | Row height stays fixed; only width changes. The existing grid gate re-runs |
| 4 | **The light mat looks wrong on light captures** | Verified against a deliberately white capture, not only dark ones |
| 5 | **A visual pass becomes a rewrite** | Scope is explicit above: no new tools, no light theme, no new interaction models |

---

## 5. Verification

**Most of this is not gateable, and pretending otherwise would be the mistake.**
Whether it looks good is a person looking — which is what three phases of this
project have already established as the thing that finds real defects.

What *is* gateable, and will be:

| Gate | Negative control |
|---|---|
| Tiles consume the full row width at several window sizes | Restore the fixed width; the assertion must fail |
| A copy path is reachable from the library and from the editor | Delete one; the test must fail |
| `PreviewView` no longer exists in the assembly | — |
| Grid virtualization still bounds realized containers | The existing `StackPanel` control |
| The window drags, maximises and snaps with custom chrome | Each checked by hand in Sandbox, since a broken caption is worse than stock |

---

## 6. Known limitations

- **No light theme.** Deliberate, and it means the app is wrong on a light desktop
  in exactly one place: the tray icon, which is why that one is bodiless and
  saturated rather than dark.
- **The style pill will not be frosted.** See §3.7.
- **Nothing here touches the overlay**, which remains the least designed surface in
  the app and the one nobody looks at.
