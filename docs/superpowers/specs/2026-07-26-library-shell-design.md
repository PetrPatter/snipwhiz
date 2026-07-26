# Library Shell — Design Spec

**Project:** Snipwhiz — a Snagit-class screenshot tool for Windows
**Spec:** 2a of 6 — Library Shell
**Date:** 2026-07-26
**Status:** Ready to review

---

## 1. Context

Capture core (spec 1) shipped and is verified on real hardware. Captures land on
the clipboard, on disk as immutable PNGs, and in a SQLite table. Nothing can
*look* at them afterwards — the only way to see an old capture today is to open
`%LOCALAPPDATA%\Snipwhiz\` in Explorer.

This spec builds the half of the product the user actually asked for by name:
*"it stores your previous images to copy to clipboard as well as edit later."*
This is the **copy to clipboard** half. Editing is spec 2b.

**Done when:** a hotkey opens a window showing every past capture newest-first,
grouped by day; clicking one copies it to the clipboard; search narrows by source
app or window title; delete removes both the row and the file.

### Two decisions taken before writing this

**The stack is now pure WPF. WebView2 is dropped.** Spec 1 §1 recorded the stack
as a "C# core + WebView2 UI hybrid"; that was provisional and is now reversed.
The reasoning that justified WebView2 was Konva.js providing an annotation scene
graph for free — a spec 2b benefit. For spec 2a it buys nothing and costs a
second language, a JS toolchain, a runtime dependency, and an IPC boundary that
every multi-megabyte capture buffer would have to cross. Spec 2b will hand-roll
the scene graph in WPF instead. **Spec 1's §1 sentence is stale; this document
supersedes it rather than editing history.**

**Library before editor.** The library is useful the day it lands, and it proves
the WPF shell — window chrome, theming, virtualization, DB access off the capture
path — before any annotation code exists to confound it.

---

## 2. Scope

### In

- A **library window**: virtualized grid of every capture, newest first
- **Day grouping** — "Today", "Yesterday", then absolute dates
- **Thumbnails**, generated lazily and cached on disk (§4.2)
- A **preview view** — click a tile to see the capture at full size (§4.11)
- **Copy to clipboard** via `Ctrl+C` or a button, reusing spec 1's multi-format writer
- **Search** over source app and window title (§4.7)
- **Delete**, with the file and the row both removed (§4.6)
- **Live insert** — a capture taken while the window is open appears in it (§4.8)
- **Reveal in Explorer** and **Open with default viewer**
- Native **Mica** backdrop and a dark theme matching the approved prototype

### Out

- Editing, annotation, `.ssproj` — **spec 2b**
- Pinning, tags, folders, favourites — the prototype showed a pin; it is not
  load-bearing and search covers the real need. Revisit when a library gets big
  enough to demand it
- Multi-select and bulk operations — one capture at a time until asked otherwise
- Retention policies and auto-cleanup — §4.9 shows disk usage; it deletes nothing
  on its own
- Drag-out-to-app — spec 6
- The pull-up sheet gesture. The prototype's library slid up from the editor's
  recents strip; **there is no editor yet to slide it out of.** It ships as a
  standalone window now and becomes an embedded sheet in spec 2b, which is a
  re-host of the same control, not a rewrite

---

## 3. Architecture

```
┌─ Snipwhiz.Core (existing) ─────────────────────────────┐
│  CaptureStore ── LibraryDb ── captures table           │
│       │                                                 │
│       ├── NEW: Page / Search / Delete / TotalBytes      │
│       └── NEW: ThumbnailCache (decode + downscale)      │
└───────┬─────────────────────────────────────────────────┘
        │ in-process, no IPC
┌───────▼─────────────────────────────────────────────────┐
│ Snipwhiz.App                                            │
│   LibraryWindow.xaml     grid, search box, day headers  │
│   LibraryViewModel       paging, filtering, commands    │
│   CaptureTile.xaml       one thumbnail + hover actions  │
│   PreviewView.xaml       full-size overlay, Copy, Esc   │
└─────────────────────────────────────────────────────────┘
```

New query methods go on `LibraryDb` beside `Recent`, and are surfaced through
`CaptureStore` the way `Recent` already is. No new project, no repository
abstraction over a class that is already the repository.

---

## 4. Design decisions

### 4.1 One window, opened three ways

`Ctrl+Shift+L`, the tray menu, and double-clicking the tray icon. Double-click
currently starts a region capture (`TrayHost.cs:56`); that moves to the library,
because a hotkey is the natural way to capture and a double-click is the natural
way to open a window.

The window is **non-modal, single-instance, and hidden rather than closed** —
closing it disposes nothing and reopening is instant. It must never block a
capture: the capture hotkeys stay live while it has focus.

### 4.2 Thumbnails: lazy, disk-cached, never on the capture path

Decoding a 4K PNG per tile is far too slow for a scrolling grid, and pre-encoding
thumbnails at capture time would add work to a path with **40 ms of headroom left
in its 120 ms budget** (spec 1 §4.5). Neither is acceptable.

So: on first display, a background thread decodes the PNG, downscales the long
edge to **320 px**, and writes `thumbs/{id}.jpg` at quality 82. Subsequent views
read the cached JPEG. A missing or corrupt thumbnail regenerates silently; the
cache is disposable and its loss costs only time.

JPEG, not PNG, and only here: thumbnails are lossy-tolerant previews, roughly 5×
smaller than an equivalent PNG. **The original capture remains an untouched,
lossless PNG** — the immutability guarantee in spec 1 §4.8 is unchanged.

### 4.3 Virtualization is mandatory, not an optimization

A year of heavy use is plausibly 10,000+ captures. The grid uses a
`VirtualizingWrapPanel` with container recycling and **paged queries** — 200 rows
at a time by `created_utc DESC, id DESC`, the ordering `Recent` already uses and
which is stable because v7 GUIDs are time-ordered.

Day grouping is computed **in the view model from the returned page**, not by a
`GROUP BY`. Grouping in SQL forces the boundary between pages to be a day
boundary, which fights paging for no benefit.

An index is added: `CREATE INDEX idx_captures_created ON captures(created_utc DESC)`.
Free at spec 1's scale, necessary at spec 2a's.

### 4.4 Threading: SQLite stays on the UI thread

`LibraryDb` holds one connection with `Pooling = false` and is not thread-safe.
Paged queries with `LIMIT 200` are sub-millisecond, so they run on the UI thread
and stay there. **Only thumbnail work — file reads, decode, downscale, encode —
goes to the thread pool**, and it touches no database state.

This is a deliberate ceiling, not an oversight. If the DB ever becomes slow
enough to stutter the grid, the fix is a dedicated connection-owning thread, and
the seam for it is that every query already goes through `CaptureStore`.

### 4.5 Copy to clipboard reuses the spec 1 writer verbatim

Decode `file_path` to BGRA, wrap in a `CroppedImage`, hand it to
`ClipboardWriter.Write`. That writer publishes `PNG` + `CF_DIBV5` + `CF_DIB` in
one clipboard session and is the reason pastes into Word, Paint, Chrome and Slack
don't come out with black or blue backgrounds. **Re-copy from the library must go
through the same path**, or it will reintroduce exactly the defect spec 1 spent
its clipboard task preventing.

There is exactly **one** copy path in the app, invoked from both the preview's
button and its `Ctrl+C` handler. Two call sites reaching the writer separately is
how one of them eventually stops matching the other.

### 4.6 Delete: row first, then file

Deleting the row before the file means a crash between the two leaves an
**orphan file** — invisible, wasting disk, harmless. The reverse order leaves an
**orphan row** — a tile pointing at nothing, visibly broken.

Deletion is permanent and immediate, with an undo affordance rather than a
confirmation dialog: the tile animates out and a toast offers "Undo" for five
seconds, which is a dialog you don't have to read. The row is held in memory
until the toast expires.

Deleting also removes `thumbs/{id}.jpg`, best-effort.

### 4.7 Search: `LIKE`, not FTS

`WHERE source_app LIKE '%q%' OR source_title LIKE '%q%'`, debounced 200 ms.

At 10,000 rows a full scan of two short text columns is roughly a millisecond.
FTS5 means a virtual table, triggers to keep it in sync, and a migration —
real complexity to make an already-imperceptible query faster. If the library
reaches a scale where this stutters, FTS5 is the documented upgrade.

Search matches the **stored** app name (`chrome`, not `Google Chrome`), because
that is what spec 1's `ForegroundWindow.Describe` records. Displaying a
friendlier name is out of scope.

### 4.8 Live insert without a refresh button

`CapturePipeline` already returns the new `CaptureRecord`. `App.xaml.cs` raises
an event the open window subscribes to; the tile is inserted at the top of
today's group. **No polling, no file watcher, no re-query.**

If the window is closed, nothing is subscribed and nothing happens — the next
open reads from the DB anyway.

### 4.9 Disk usage is shown, never enforced

A footer line: capture count and total bytes of the PNG directory, computed once
per open on a background thread. No quota, no auto-purge, no retention setting.
Users delete their own files; a screenshot tool that silently eats old captures
is a bug report waiting to happen.

### 4.10 Mica, with a flat fallback

Windows 11 22H2 — the platform floor — supports `DWMWA_SYSTEMBACKDROP_TYPE`.
That is a two-line `DwmSetWindowAttribute` call for the layered translucency the
prototype achieved with CSS `backdrop-filter`, rendered by the compositor rather
than by us.

The call is best-effort: if it fails the window is a solid warm-neutral dark
surface and **nothing else changes**. No feature detection branching, no second
code path.

### 4.11 Preview: a view inside the window, not a second window

Clicking a tile opens the capture large. `Ctrl+C` or a **Copy** button puts it on
the clipboard; `Esc`, a back button, or clicking outside the image returns to the
grid.

It is an overlay **inside the library window**, not a new one. A second window
brings its own DPI handling, its own Mica call, its own placement and z-order
rules, and a second thing to keep in sync with the grid's selection — for a view
that is one image and three controls.

The image is the **full PNG decoded on a background thread**, not the thumbnail
scaled up. The thumbnail is a lossy 320 px preview; showing it at 1200 px is
exactly the blurry-screenshot-tool look this product exists to avoid. The
thumbnail may be shown for the few hundred milliseconds the decode takes, then
replaced — but only if the swap is invisible, and it is worth a look on a slow
disk before assuming it is.

Displayed **fit-to-window, never upscaled past 100%**. A 200×100 capture shows at
200×100 in the middle of the view, not blown up to fill it.

Deliberately not here: zoom, pan, and arrow-key navigation between captures. All
three are reasonable and none are needed to copy an image to the clipboard. Arrow
navigation is the one most likely to be missed; add it when it is missed.

---

## 5. Data model

Schema **v2**. One index, no new columns:

```sql
CREATE INDEX IF NOT EXISTS idx_captures_created ON captures(created_utc DESC);
PRAGMA user_version = 2;
```

`LibraryDb.Migrate` already gates on `SchemaVersion` and the spec 1 comment
anticipated additive migration. Thumbnails are files keyed by capture id, so they
need no schema. Existing v1 databases upgrade in place with no data movement.

New methods on `LibraryDb`, each mirrored on `CaptureStore`:

| Method | Purpose |
|---|---|
| `Page(int offset, int limit)` | The grid's backing query |
| `Search(string query, int limit)` | §4.7 |
| `Delete(Guid id)` | Row removal; the caller removes files |
| `Count()` / `TotalBytes()` | §4.9 footer |

---

## 6. Risks and open questions

1. **Thumbnail generation on a cold, large library.** First open of a 5,000-capture
   library decodes 5,000 PNGs. Mitigated by decoding only what is scrolled into
   view, but a fast scroll to the bottom queues a lot of work. The plan should
   bound concurrency and cancel work for tiles that scroll back out of view.
2. **`VirtualizingWrapPanel` is not in the box.** WPF ships `VirtualizingStackPanel`;
   a wrapping virtualized panel must be written or vendored. This is the largest
   single unknown in the spec and the plan should treat it as its own task with a
   measured scroll-performance gate, not as incidental work inside the grid task.
3. **Deleting a capture that is currently on the clipboard.** The clipboard holds
   a copy of the bytes, not a file reference, so the paste still works. Worth a
   verification check rather than code.
4. **Clicking a tile opens the preview (§4.11); copying is an explicit act**
   — `Ctrl+C` or the button. Decided, not open. This leaves room for spec 2b to
   put an **Edit** button beside **Copy** in the same view, rather than having to
   redefine what a click means once an editor exists.

---

## 7. Verification

Beyond unit tests on the new queries, the following need observation, and each
names the failure it would catch:

| # | Check | Catches |
|---|---|---|
| 1 | Paste a library re-copy into Word, Paint, Chrome, Slack — via **both** the button and `Ctrl+C` | §4.5 bypassing `ClipboardWriter`, or the two paths diverging |
| 1b | Preview a capture and confirm it is the **full PNG**, not an upscaled thumbnail | §4.11 showing the lossy preview |
| 1c | Preview a capture smaller than the window | §4.11 upscaling past 100% |
| 2 | Scroll a 1,000+ capture library end to end | §4.3 virtualization absent or broken |
| 3 | Capture while the window is open | §4.8 live insert |
| 4 | Delete, then undo; delete, then let the toast expire | §4.6 ordering, orphan rows |
| 5 | Delete a capture, confirm the PNG and thumbnail are gone | §4.6 file cleanup |
| 6 | Open with a v1 database from spec 1 | §5 migration on real data |
| 7 | Corrupt a `thumbs/*.jpg`, reopen | §4.2 silent regeneration |
| 8 | Search across mixed case and partial words | §4.7 |
| 9 | Library open, mixed-DPI monitors, drag between them | WPF window DPI handling |
| 10 | Copy from library, delete it, then paste | §6.3 |

**A guard must be observed failing before it is trusted.** Spec 1 produced five
checks that were circular or blind — each derived its expected value from the
code under test. Every check above that can have a negative control gets one.

---

## 8. Known limitations

- Thumbnails are JPEG and will show artefacts on sharp text at small sizes. The
  original is untouched; the editor in 2b opens the PNG, never the thumbnail.
- No multi-select, so deleting fifty captures means fifty deletes.
- Search does not cover OCR'd image text. That arrives with OCR in spec 4.
- The window is not resizable below a floor that fits three tiles across.
