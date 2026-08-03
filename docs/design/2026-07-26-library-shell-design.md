# Library Shell — Design Spec

**Project:** Snipwhiz — a Snagit-class screenshot tool for Windows
**Spec:** 2a of 6 — Library Shell
**Date:** 2026-07-26 (rev. 2, post-review)
**Status:** Ready to plan from

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
grouped by day; clicking one opens it full-size; `Ctrl+C` or a button copies it;
search narrows by source app or window title; delete removes both row and file.

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
- **Copy to clipboard** via `Ctrl+C` or a button, reusing spec 1's writer (§4.5)
- **Search** over source app and window title (§4.7)
- **Delete**, with a real undo window before anything is destroyed (§4.6)
- **Live insert** — a capture taken while the window is open appears in it (§4.8)
- **Reveal in Explorer** and **Open with default viewer**
- Graceful handling of a row whose file is gone (§4.12)
- Native **Mica** backdrop and a dark theme matching the approved prototype

### Out

- Editing, annotation, `.ssproj` — **spec 2b**
- Pinning, tags, folders, favourites — the prototype showed a pin; it is not
  load-bearing and search covers the real need
- Multi-select and bulk operations — one capture at a time until asked otherwise
- Zoom, pan, and arrow-key navigation in the preview (§4.11)
- Retention policies and auto-cleanup — §4.9 shows disk usage; it deletes nothing
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
│       ├── NEW: Page / Search / Delete / Count           │
│       ├── NEW: ResolvePath, TotalBytes  (§4.13)         │
│       ├── NEW: PngDecoder   → BGRA      (§4.13)         │
│       └── NEW: ThumbnailCache           (§4.2)          │
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
closing it disposes nothing and reopening is instant. Capture hotkeys stay live
while it has focus, subject to §4.14.

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
lossless PNG** — spec 1 §4.8's immutability guarantee is unchanged. JPEG has no
alpha, so decoded pixels are **flattened onto the theme surface colour** before
encoding; every 2a capture is opaque, but the cache outlives 2a and 2b will
produce transparency.

Generation is **bounded and cancellable**: at most `Environment.ProcessorCount / 2`
concurrent generations, and a tile scrolled out of view cancels its pending work.
A fast scroll to the bottom of a large library must not queue thousands of
decodes — that is the failure mode this bound exists to prevent.

### 4.3 Virtualization by row-chunking, not a custom panel

A year of heavy use is plausibly 10,000+ captures, so virtualization is mandatory
rather than an optimization. WPF ships `VirtualizingStackPanel`, which does not
wrap; the obvious move is a custom `VirtualizingWrapPanel`, and rev. 1 of this
spec called that the largest unknown in the whole spec.

**Don't write it.** The view model already computes day grouping from the fetched
page (below); the same view model chunks tiles into fixed-width **rows** and
feeds an `ItemsControl` of row objects to the stock, recycling
`VirtualizingStackPanel`. Cost: re-chunk on width change, debounced. That trades
a bespoke measure/arrange implementation — the kind of code that is subtly wrong
for months — for a list transform that is obviously right.

Day grouping is computed **in the view model from the returned page**, not by a
`GROUP BY`. Grouping in SQL forces page boundaries to fall on day boundaries,
which fights paging for no benefit.

### 4.4 Paging is keyset, not offset

`OFFSET`/`LIMIT` is unstable under insertion: a capture taken between page N and
page N+1 shifts every offset by one, so the last row of page N reappears as the
first row of page N+1. §4.8's live insert does not merely coexist with that bug,
it **causes** it.

So pages are keyset: `WHERE (created_utc, id) < (@lastCreated, @lastId)
ORDER BY created_utc DESC, id DESC LIMIT 200`. The ordering is already stable —
v7 GUIDs are time-ordered — and this is less code than compensating offsets.

A composite index covers exactly that ordering:

```sql
CREATE INDEX IF NOT EXISTS idx_captures_created
    ON captures(created_utc DESC, id DESC);
```

### 4.5 SQLite stays on the UI thread; heavy work does not

`LibraryDb` holds one connection with `Pooling = false` (`LibraryDb.cs:13-18`) and
is not thread-safe. Keyset queries with `LIMIT 200` are sub-millisecond, so they
run on the UI thread and stay there. **`Count()` runs there too** — it is one
indexed aggregate, and moving it off-thread would violate this rule for no gain.

Everything that is not a database call goes to the thread pool: thumbnail decode
and encode (§4.2), full-image decode for the preview (§4.11), the clipboard write
(§4.6), and the disk-usage walk (§4.9). None of them touch database state.

This is a deliberate ceiling, not an oversight. If the DB ever becomes slow enough
to stutter the grid, the fix is a dedicated connection-owning thread, and the seam
for it is that every query already goes through `CaptureStore`.

### 4.6 Copy reuses the spec 1 writer, off the UI thread

Decode the PNG to BGRA (§4.13), wrap in a `CroppedImage`, hand it to
`ClipboardWriter.Write`. That writer publishes `PNG` + `CF_DIBV5` + `CF_DIB` in
one clipboard session (`ClipboardWriter.cs:44-47`) and is the reason pastes into
Word, Paint, Chrome and Slack don't come out with black or blue backgrounds.
**Re-copy must go through the same path**, or it reintroduces exactly the defect
spec 1 spent its clipboard task preventing.

There is exactly **one** copy path in the app, invoked from both the preview's
button and its `Ctrl+C` handler. Two call sites reaching the writer separately is
how one of them eventually stops matching the other.

**It must not run on the UI thread.** The call decodes a full PNG, re-encodes it
to PNG internally (`ClipboardWriter.cs:27`), and retries `OpenClipboard` up to
8 × 60 ms of `Thread.Sleep` (`ClipboardWriter.cs:17-18,33-37`) when a clipboard
manager is holding it. On a 4K capture that is a visible freeze. Copy runs on the
thread pool with the button in a pending state; `ClipboardUnavailableException`
surfaces as an in-window toast, not a tray balloon — the window has focus, so the
message belongs where the user is looking.

**Alpha:** `ClipboardWriter` documents its BGRA as premultiplied
(`ClipboardWriter.cs:107`); PNG decodes to straight alpha. Every 2a capture is
opaque, so this is a no-op today — but the decoder must premultiply, and 2b's
transparent exports are exactly when a missing conversion would first show up.

### 4.7 Delete: the file outlives the undo window

Rev. 1 said deletion was "permanent and immediate" *and* offered a five-second
undo. Those cannot both be true: undo would restore a row pointing at a PNG that
was already deleted, the tile would reappear, and the pixels would be gone. A
fake undo that silently loses data is worse than no undo.

So the order is:

1. **Immediately:** remove the DB row, animate the tile out, show an undo toast.
2. **On undo:** re-insert the row. The file was never touched.
3. **On toast expiry (5 s) or window close:** delete the PNG and the thumbnail.

Deleting the row first and the file last also means a crash between the two
leaves an **orphan file** — invisible, wasting disk, harmless — rather than an
orphan row, which is a visibly broken tile.

Files pending deletion are held in memory. If the app exits before a toast
expires, the file survives and its row does not: an orphan, per the above.

### 4.8 Search: `LIKE`, not FTS

`WHERE source_app LIKE @q ESCAPE '\' OR source_title LIKE @q ESCAPE '\'`,
debounced 200 ms, with `%` and `_` escaped in the user's input — unescaped, a
typed `%` matches everything.

At 10,000 rows a full scan of two short text columns is roughly a millisecond.
FTS5 means a virtual table, sync triggers, and a migration — real complexity to
speed up an already-imperceptible query. If the library reaches a scale where
this stutters, FTS5 is the documented upgrade.

Search matches the **stored** app name (`chrome`, not `Google Chrome`), because
that is what `ForegroundWindow.Describe` records (`CapturePipeline.cs:19-41`).

### 4.9 Live insert without a refresh button

`CapturePipeline.Complete` returns the new record, so `App.xaml.cs` raises an
event the open window subscribes to and the tile is inserted at the top of
today's group. No polling, no file watcher, no re-query.

`CaptureOutcome.Record` is **nullable** and is null whenever the save failed
(`CapturePipeline.cs:11-16,73-87`). The raise must skip those, or the grid shows a
tile for a capture that has neither a row nor a file.

If the window is closed, nothing is subscribed and nothing happens — the next
open reads from the DB anyway.

### 4.10 Disk usage is shown, never enforced

A footer line: capture count (a DB query, §4.5) and total bytes of the captures
directory (a filesystem walk on the thread pool, §4.13). Computed once per open.
No quota, no auto-purge, no retention setting. A screenshot tool that silently
eats old captures is a bug report waiting to happen.

### 4.11 Preview: a view inside the window, not a second window

Clicking a tile opens the capture large. `Ctrl+C` or a **Copy** button puts it on
the clipboard; `Esc`, a back button, or clicking outside the image returns to the
grid.

It is an overlay **inside the library window**, not a new one. A second window
brings its own DPI handling, its own Mica call, its own placement and z-order
rules, and a second thing to keep in sync with the grid's selection — for a view
that is one image and three controls.

The image is the **full PNG**, decoded on a background thread, never the thumbnail
scaled up: a lossy 320 px preview shown at 1200 px is precisely the blurry look
this product exists to avoid. This is the one decode that does **not** go through
`PngDecoder` — it produces a WPF `BitmapFrame` (`OnLoad`, then `Freeze()`) for
display, because routing a display path through BGRA and back would add a
full-size copy to serve no one. Raw-pixel consumers use `PngDecoder`; the screen
uses WPF's decoder. That split is deliberate and is the whole of it (§4.13).

**Scaling.** Fit-to-window when the capture is larger than the view; otherwise
shown at **physical 1:1** — a 200×100 capture occupies 200×100 device pixels, not
200×100 DIPs. On a 150% monitor the DIP reading would render it at 1.5× and blur
it, which is spec 1 §4.3's rule broken by a different route. Reuse that rule's
mechanism: 96-DPI bitmap metadata plus explicit DIP sizing derived from the
monitor's own scale.

Deliberately not here: zoom, pan, and arrow-key navigation between captures. All
three are reasonable and none are needed to copy an image to the clipboard. Arrow
navigation is the one most likely to be missed; add it when it is missed.

### 4.12 A row whose file is missing

Users have been told for a whole spec that the only way to see their captures is
to open the folder in Explorer, so some of them will delete files behind the
database's back. Nothing in rev. 1 said what happens next.

A tile whose file will not open renders a **placeholder** with the capture's date
and size, and its actions collapse to one: **Remove from library**, which deletes
the row. Preview and copy on such a row show the same message rather than
throwing. The check is the decode failing, not a `File.Exists` probe — a file can
vanish between the two, and the error path is needed regardless.

### 4.13 The pieces spec 1 did not leave behind

Three things this spec repeatedly assumes exist, none of which do. All are small;
all must be assigned rather than discovered mid-implementation.

**A PNG decoder.** Spec 1 only ever encodes (`PngEncoder.cs`). `PngDecoder.Decode`
lives beside it in `Snipwhiz.Core.Imaging`, uses `System.Drawing.Bitmap` (already
referenced there), returns BGRA in `CroppedImage`, and premultiplies alpha per
§4.6. Used by re-copy and thumbnail generation. The preview is the documented
exception (§4.11).

**Absolute path resolution.** `CaptureRecord.FilePath` is relative to the store
root (`CaptureStore.cs:36-49`) and `_root` is private with no accessor
(`CaptureStore.cs:9`). Every feature here — decode, preview, Reveal in Explorer,
Open with viewer, file deletion, thumbnail keying — needs an absolute path, and
no shipped API produces one. Add `CaptureStore.ResolvePath(CaptureRecord)`.

**`TotalBytes`.** Rev. 1 listed this as a `LibraryDb` method, but the schema
stores no file sizes and v2 adds no columns — there is nothing to sum. It is a
directory walk, so it belongs on `CaptureStore`, which owns the root.

### 4.14 Hide the library before capturing

The capture hotkeys stay live while the library has focus (§4.1), and the frozen
desktop grab would then contain **the library window itself**, covering whatever
the user meant to capture. Snagit and ShareX hide their own windows first.

So does this: on a capture hotkey the library hides, the capture proceeds, and the
window is restored afterwards — including on cancel. The hide-not-close lifecycle
in §4.1 makes this cheap. Restoration must survive the abort paths spec 1 already
has (Esc, right-click, display change), or a cancelled capture leaves the user
with a window that silently vanished.

### 4.15 Mica, with a flat fallback

Windows 11 22H2 — the platform floor — supports `DWMWA_SYSTEMBACKDROP_TYPE`. It
is not quite the two-line call it appears to be: the WPF window's own background
has to be out of the way over the client area, and `DWMWA_USE_IMMERSIVE_DARK_MODE`
is needed as well or the caption bar stays light over a dark window.

The call is best-effort: if it fails the window is a solid warm-neutral dark
surface and **nothing else changes**. No feature detection branching, no second
code path.

---

## 5. Data model

Schema **v2**. One index, no new columns:

```sql
CREATE INDEX IF NOT EXISTS idx_captures_created
    ON captures(created_utc DESC, id DESC);
PRAGMA user_version = 2;
```

**`Migrate` must be restructured first.** Rev. 1 claimed it "already gates on
`SchemaVersion`", which overstates what is there. `LibraryDb.cs:33-57` is a single
script that creates the table and hard-codes `PRAGMA user_version = 1`; bumping
`CurrentSchemaVersion` to 2 without touching it leaves a fresh database stamped
version 1 and re-running its migration on every open. It becomes per-version
steps — *if < 1, create the table; if < 2, add the index; stamp the current
version once at the end* — before anything else in this spec is built.

Thumbnails are files keyed by capture id, so they need no schema. Existing v1
databases upgrade in place with no data movement.

| Method | Lives on | Purpose |
|---|---|---|
| `Page(created, id, limit)` | `LibraryDb` | Keyset page (§4.4) |
| `Search(query, limit)` | `LibraryDb` | §4.8 |
| `Delete(Guid id)` | `LibraryDb` | Row removal; caller removes files |
| `Insert(CaptureRecord)` | `LibraryDb` | Exists; reused by undo (§4.7) |
| `Count()` | `LibraryDb` | Footer (§4.10) |
| `ResolvePath(CaptureRecord)` | `CaptureStore` | §4.13 |
| `TotalBytes()` | `CaptureStore` | Directory walk (§4.13) |

---

## 6. Risks and open questions

1. **Thumbnail generation on a cold, large library.** First open of a
   5,000-capture library decodes 5,000 PNGs. §4.2 bounds concurrency and cancels
   off-screen work; the plan should verify that bound holds under a fast scroll to
   the bottom, which is the case that breaks naive implementations.
2. **Row-chunked virtualization must be measured, not assumed.** §4.3 avoids a
   custom panel, but a wrong `ItemsControl` template can still realize every row.
   Verification check 2 exists for this and asserts a bounded container count.
3. **Deleting a capture that is currently on the clipboard.** The clipboard holds
   a copy of the bytes, not a file reference, so the paste still works. Worth a
   verification check rather than code.
4. **Clicking a tile opens the preview (§4.11); copying is an explicit act** —
   `Ctrl+C` or the button. Decided, not open. This leaves room for spec 2b to put
   an **Edit** button beside **Copy** in the same view, rather than having to
   redefine what a click means once an editor exists.

---

## 7. Verification

Beyond unit tests on the new queries, the following need observation, and each
names the failure it would catch.

| # | Check | Catches |
|---|-------|---------|
| 1 | After a library re-copy, **enumerate the clipboard formats** and assert `PNG`, `CF_DIBV5` and `CF_DIB` are all present — via **both** the button and `Ctrl+C` | §4.6 bypassed, or the two paths diverging |
| 1b | Paste that re-copy into Word, Paint, Chrome, Slack | End-to-end confirmation of the above |
| 1c | Preview a capture and confirm it is the full PNG, not an upscaled thumbnail | §4.11 showing the lossy preview |
| 1d | Preview a capture smaller than the window, on a 150% monitor | §4.11 DIP-vs-physical scaling |
| 2 | Scroll a seeded 1,000-capture library end to end; assert **realized containers stay bounded** | §4.3 virtualization absent or defeated by the template |
| 3 | Capture while the window is open | §4.9 live insert |
| 4 | Delete, undo, then **preview and re-copy the restored capture** | §4.7 — the file destroyed inside the undo window |
| 5 | Delete, let the toast expire, confirm PNG and thumbnail are gone | §4.7 file cleanup |
| 6 | Open with a real v1 database from spec 1; then confirm a **fresh** database reports version 2 | §5 migration, both directions |
| 7 | Corrupt a `thumbs/*.jpg`, reopen | §4.2 silent regeneration |
| 8 | Search mixed case, partial words, and a query containing `%` | §4.8 escaping |
| 9 | Delete a capture's PNG in Explorer, then use its tile | §4.12 missing-file handling |
| 10 | Library open, mixed-DPI monitors, drag between them | WPF window DPI handling |
| 11 | Copy from library, delete it, then paste | §6.3 |
| 12 | Press a capture hotkey with the library focused | §4.14 self-capture; confirm restore on cancel too |

**A guard must be observed failing before it is trusted.** Spec 1 produced five
checks that were circular or blind — each derived its expected value from the code
under test, or used a fixture that made the target bug invisible. Two rev. 1
checks here had the same defect and are fixed above: check 1 originally only
pasted into apps, which passes even with a bypassed writer because every 2a
capture is opaque and the missing-format defect only shows with alpha; check 4
originally observed the tile returning, which is exactly what a data-losing undo
also does.

Negative controls to actually run: `Clipboard.SetImage` in place of
`ClipboardWriter` (check 1 must fail), a non-virtualizing panel (check 2 must
fail), and forcing the thumbnail into the preview (check 1c must fail).

Check 2 needs a **seeding tool** — insert N rows referencing a handful of real
PNGs — which the plan should treat as test infrastructure, not a product feature.

---

## 8. Known limitations

- Thumbnails are JPEG and will show artefacts on sharp text at small sizes. The
  original is untouched; the editor in 2b opens the PNG, never the thumbnail.
- No multi-select, so deleting fifty captures means fifty deletes.
- SQLite's `LIKE` is case-insensitive for ASCII only. A window title in a
  non-Latin script will match only with exact case.
- Search does not cover OCR'd image text. That arrives with OCR in spec 4.
- The window is not resizable below a floor that fits three tiles across.
- If the app exits with a delete toast still showing, the row is gone and the file
  remains (§4.7). Deliberate: the recoverable direction.
