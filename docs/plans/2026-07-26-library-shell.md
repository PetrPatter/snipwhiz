# Snipwhiz Library Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A hotkey opens a window showing every past capture, newest first, grouped by day. Clicking one opens it full-size; `Ctrl+C` or a button copies it to the clipboard. Search narrows by source app or window title. Delete removes both the row and the file, with a real undo window first.

**Architecture:** Pure WPF over the existing `Snipwhiz.Core`. No WebView2, no IPC — the store already hands back records in-process. The grid virtualizes by chunking tiles into rows in the view model and feeding the stock `VirtualizingStackPanel`, so no custom panel is written. SQLite stays on the UI thread; every decode, encode, clipboard write and directory walk goes to the thread pool.

**Tech Stack:** .NET 10 · WPF + WinForms (`NotifyIcon`) · `Microsoft.Windows.CsWin32` · `Microsoft.Data.Sqlite` · `System.Drawing.Common` · xUnit

**Spec:** `docs/superpowers/specs/2026-07-26-library-shell-design.md` — read §4 before starting.

## Global Constraints

Every task's requirements implicitly include this section.

- **Everything in spec 1's Global Constraints still applies.** Platform floor Windows 11 22H2 / `net10.0-windows10.0.22621.0`; product name `Snipwhiz`; data path `%LOCALAPPDATA%\Snipwhiz\`; balloons not toasts; `TreatWarningsAsErrors`.
- **Zero new third-party dependencies.** Everything here is BCL, `System.Drawing.Common`, or `Microsoft.Data.Sqlite` — all already referenced. If a task appears to need a NuGet package, stop and report it rather than adding one.
- **`Snipwhiz.Core` has no WPF reference and does not gain one.** WPF types (`BitmapFrame`, `ImageSource`, `Dispatcher`) live only in `Snipwhiz.App`.
- **SQLite is touched only from the UI thread.** `LibraryDb` holds one non-thread-safe connection. Any `await` that leads back to a query must resume on the dispatcher.
- **Saved PNGs are never modified or rewritten.** Spec 1 §4.8's immutability guarantee holds. Thumbnails are separate files; deletion is the only operation that touches an original.
- **There is exactly one copy-to-clipboard path in the app**, and it goes through `ClipboardWriter.Write`. Never `System.Windows.Clipboard.SetImage`.
- **A guard must be observed failing before it is trusted.** Spec 1 shipped five checks that were circular or blind. Where a task names a negative control, run it, watch it fail, and put the failing output in the report. A check that has only ever passed is not evidence.
- **No feature flags, no settings, no configurability** beyond what the spec names. Thumbnail size, page size, and toast duration are constants.

## File Structure

```
src/
  Snipwhiz.Core/
    Imaging/
      PngDecoder.cs                  NEW  PNG file -> BGRA CroppedImage
      ThumbnailCache.cs              NEW  lazy 320px JPEG cache
    Storage/
      LibraryDb.cs                   EDIT stepwise Migrate, keyset Page, Search, Delete, Count
      CaptureStore.cs                EDIT ResolvePath, TotalBytes, query pass-through

  Snipwhiz.App/
    App.xaml.cs                      EDIT library wiring, CaptureCompleted event, hide-on-capture
    TrayHost.cs                      EDIT Library menu item, double-click opens library
    Library/
      LibraryWindow.xaml / .cs       NEW  window shell, Mica, keyboard
      LibraryViewModel.cs            NEW  paging, chunking, grouping, commands
      CaptureTile.xaml / .cs         NEW  one thumbnail + hover actions
      PreviewView.xaml / .cs         NEW  full-size overlay, Copy, Esc
      ClipboardCopier.cs             NEW  the single copy path
      UndoToast.xaml / .cs           NEW  delete undo affordance
      Mica.cs                        NEW  DwmSetWindowAttribute best-effort

tests/
  Snipwhiz.Core.Tests/
    Storage/MigrationTests.cs        NEW
    Storage/LibraryQueryTests.cs     NEW
    Imaging/PngDecoderTests.cs       NEW
    Imaging/ThumbnailCacheTests.cs   NEW
    LibrarySeeder.cs                 NEW  test infrastructure, also used by Task 6
```

**Task order is dependency order with risk pulled forward.** Tasks 1–4 are headless Core work, fully testable without UI. Task 5 produces the first runnable window. Task 6 is the riskiest UI work — virtualization — and lands on a proven data layer with a measured gate.

---

### Task 1: Schema v2 and stepwise migration

Spec §5. This is first because it is a prerequisite for every query, and because the existing `Migrate` is quietly broken for any version beyond 1.

**Files:**
- Modify: `src/Snipwhiz.Core/Storage/LibraryDb.cs`
- Test: `tests/Snipwhiz.Core.Tests/Storage/MigrationTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `LibraryDb` reporting `SchemaVersion == 2`, with `idx_captures_created` present.

**The existing problem.** `LibraryDb.cs:33-57` gates on `if (SchemaVersion >= CurrentSchemaVersion) return;` and then runs one script ending in a hard-coded `PRAGMA user_version = 1`. Bump `CurrentSchemaVersion` to 2 and a fresh database gets its table created but is stamped version 1 — so the migration re-runs on every single open, forever. A v1 database re-runs the `CREATE TABLE IF NOT EXISTS` and is also never stamped 2.

- [ ] **Step 1: Write the failing migration tests**

Create `tests/Snipwhiz.Core.Tests/Storage/MigrationTests.cs` with four cases:

1. **Fresh database reports version 2.** Open a `LibraryDb` on a temp path; assert `SchemaVersion == 2`.
2. **Fresh database has the index.** Query `SELECT name FROM sqlite_master WHERE type='index' AND name='idx_captures_created'`; assert one row.
3. **A real v1 database upgrades in place, keeping its rows.** Build a v1 database by hand — `CREATE TABLE captures (...)` exactly as it exists today plus `PRAGMA user_version = 1` — insert two rows, close it, then open it with `LibraryDb`. Assert `SchemaVersion == 2`, the index exists, and both rows are still readable.
4. **Migration is idempotent.** Open, dispose, reopen the same file. Assert `SchemaVersion == 2` and exactly one `idx_captures_created` in `sqlite_master`.

Test 3 is the one that matters — it is the only check that runs against data shaped like the user's actual library.

- [ ] **Step 2: Restructure `Migrate` into per-version steps**

```csharp
private const int CurrentSchemaVersion = 2;

private void Migrate()
{
    using (var wal = _connection.CreateCommand())
    {
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();
    }

    var version = SchemaVersion;
    if (version >= CurrentSchemaVersion) return;

    using var tx = _connection.BeginTransaction();
    using var cmd = _connection.CreateCommand();
    cmd.Transaction = tx;

    if (version < 1)
    {
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS captures (
                id           TEXT    PRIMARY KEY,
                created_utc  INTEGER NOT NULL,
                width        INTEGER NOT NULL,
                height       INTEGER NOT NULL,
                source_app   TEXT    NOT NULL,
                source_title TEXT    NOT NULL,
                file_path    TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    if (version < 2)
    {
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_captures_created
                ON captures(created_utc DESC, id DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    // Not parameterizable — PRAGMA takes a literal. CurrentSchemaVersion is a
    // compile-time constant, so there is no injection surface.
    cmd.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
    cmd.ExecuteNonQuery();

    tx.Commit();
}
```

Each future version adds one `if (version < N)` block and nothing else. The stamp happens once, at the end, inside the transaction — so a failure part-way leaves the old version and the migration retries next open rather than believing it succeeded.

- [ ] **Step 3: Run the tests, confirm green, confirm the suite is still pristine**

`dotnet test`. All spec 1 tests must still pass — `CaptureStoreTests` opens real databases and would catch a broken migration immediately.

---

### Task 2: PNG decoder and path resolution

Spec §4.13. Both of these are assumed by three later tasks and neither exists.

**Files:**
- Create: `src/Snipwhiz.Core/Imaging/PngDecoder.cs`
- Modify: `src/Snipwhiz.Core/Storage/CaptureStore.cs`
- Test: `tests/Snipwhiz.Core.Tests/Imaging/PngDecoderTests.cs`

**Interfaces:**
- Consumes: `PngEncoder`, `CroppedImage`, `CaptureRecord`.
- Produces:
  - `PngDecoder.Decode(string path) -> CroppedImage` — BGRA, premultiplied, top-down
  - `CaptureStore.ResolvePath(CaptureRecord record) -> string` — absolute
  - `CaptureStore.Root { get; }`

- [ ] **Step 1: Write the failing decoder tests (TDD — RED first)**

Create `tests/Snipwhiz.Core.Tests/Imaging/PngDecoderTests.cs`:

1. **Round-trip preserves pixels.** Build a 4×3 BGRA buffer where each pixel encodes its own coordinates in a way that is **asymmetric between channels** — for example `B = x * 10`, `G = y * 10 + 5`, `R = 200`, `A = 255`. Encode with `PngEncoder`, write to a temp file, decode, assert every byte matches.

   The `+ 5` and the distinct `R` are load-bearing. Spec 1's loupe test sampled only points where `x == y` in a fixture encoding `R = x, G = y`, which made it blind to a red/green channel swap — the exact bug it was written to catch. A fixture whose channels can be confused is not a fixture.

2. **Dimensions survive.** Assert `Width == 4`, `Height == 3` — not transposed.

3. **A non-PNG file throws a typed exception.** Write four bytes of garbage to a `.png` path; assert the decoder throws something the caller can catch by type (see Step 2).

4. **A missing file throws.** Assert on a path that does not exist.

- [ ] **Step 2: Implement `PngDecoder`**

```csharp
public sealed class ImageDecodeException(string message, Exception inner)
    : Exception(message, inner);

public static class PngDecoder
{
    public static CroppedImage Decode(string path)
    {
        try
        {
            using var bitmap = new Bitmap(path);
            // Format32bppPArgb: premultiplied, matching what ClipboardWriter's
            // CF_DIBV5 payload declares. GDI+ converts on the LockBits blit, so
            // this is one conversion rather than a manual pass.
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            using var clone = bitmap.Clone(rect, PixelFormat.Format32bppPArgb);
            var data = clone.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var bgra = new byte[bitmap.Width * bitmap.Height * 4];
                // Copy row by row: GDI+ stride is padded, our buffer is not.
                for (var y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride,
                                 bgra, y * bitmap.Width * 4, bitmap.Width * 4);
                return new CroppedImage(bgra, bitmap.Width, bitmap.Height);
            }
            finally { clone.UnlockBits(data); }
        }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or FileNotFoundException)
        {
            // GDI+ reports a corrupt or non-image file as OutOfMemoryException.
            // That is not a real OOM and must not be allowed to look like one.
            throw new ImageDecodeException($"Could not decode '{path}'.", e);
        }
    }
}
```

Match `CroppedImage`'s actual constructor shape — read `src/Snipwhiz.Core/Capture/CroppedImage.cs` first rather than assuming the parameter order above.

The stride loop is not optional. GDI+ pads each row to a 4-byte boundary; for 32bpp that happens to be a no-op, but writing the copy correctly costs one line and removes a landmine for any future non-32bpp path.

- [ ] **Step 3: Add path resolution to `CaptureStore`**

```csharp
public string Root => _root;

/// <summary>
/// CaptureRecord.FilePath is relative to the store root (see Save). Nothing
/// outside this class can reconstruct an absolute path without this.
/// </summary>
public string ResolvePath(CaptureRecord record) => Path.Combine(_root, record.FilePath);
```

Add a test asserting `ResolvePath` on a record returned by `Save` names a file that exists.

- [ ] **Step 4: Green, and report the RED output**

The report must include the failing output from Step 1 before implementation, not just the passing output after.

---

### Task 3: Library queries

Spec §4.4, §4.8, §4.10, §5.

**Files:**
- Modify: `src/Snipwhiz.Core/Storage/LibraryDb.cs`, `src/Snipwhiz.Core/Storage/CaptureStore.cs`
- Create: `tests/Snipwhiz.Core.Tests/LibrarySeeder.cs`
- Test: `tests/Snipwhiz.Core.Tests/Storage/LibraryQueryTests.cs`

**Interfaces:**
- Produces on `LibraryDb`, each mirrored as a pass-through on `CaptureStore`:
  - `Page(CaptureRecord? after, int limit) -> IReadOnlyList<CaptureRecord>`
  - `Search(string query, int limit) -> IReadOnlyList<CaptureRecord>`
  - `Delete(Guid id) -> bool`
  - `Count() -> int`
- Produces on `CaptureStore` only: `TotalBytes() -> long`

- [ ] **Step 1: Build the seeder**

`tests/Snipwhiz.Core.Tests/LibrarySeeder.cs` — insert N rows with controllable timestamps, app names and titles, optionally writing a real PNG per row. Task 6 reuses this to build a 1,000-capture library, so it takes a `writeFiles: bool`.

Deterministic by construction: no `Random`, no `DateTimeOffset.UtcNow`. Timestamps are derived from the index against a fixed base instant passed in by the caller.

- [ ] **Step 2: Write the failing query tests**

1. **Keyset paging returns every row exactly once.** Seed 250 rows, page through with `limit: 100` until empty, assert 250 distinct ids in descending `created_utc` order.
2. **Insertion between pages does not duplicate or skip.** Seed 250, fetch page 1, insert a row *newer than everything*, fetch page 2 using page 1's last record. Assert no id from page 1 reappears. This is the bug offset paging has and keyset does not — it must be observed passing here and would fail against an `OFFSET` implementation.
3. **Ties on `created_utc` are broken deterministically.** Seed 5 rows sharing one timestamp, page with `limit: 2` three times, assert all 5 come back exactly once.
4. **Search matches app and title, case-insensitively.** Seed rows with `chrome`/`Google — Chrome`, `code`/`Program.cs`. Assert `"chr"` finds only the first.
5. **Search escapes `%` and `_`.** Seed a row titled `100% done` and another titled `abc`. Assert searching `"%"` returns **only** the first — an unescaped `LIKE '%%%'` returns both, so this test fails against the naive implementation.
6. **`Delete` removes the row and returns true; deleting a missing id returns false.**
7. **`Count` matches the seeded number.**
8. **`TotalBytes` sums real files.** Seed 3 rows with files, assert it equals the sum of their `FileInfo.Length`, and that it ignores `library.db` itself.

- [ ] **Step 3: Implement the queries**

Keyset page — `after == null` means the first page:

```sql
SELECT id, created_utc, width, height, source_app, source_title, file_path
FROM captures
WHERE (@first = 1)
   OR (created_utc < @created)
   OR (created_utc = @created AND id < @id)
ORDER BY created_utc DESC, id DESC
LIMIT @limit;
```

The `id` tiebreak compares v7 GUIDs in `"D"` format as text. Those are fixed-width lowercase hex, so lexicographic order matches numeric order, and v7 puts the timestamp in the leading bits — the tiebreak is therefore also time-ordered within a millisecond.

Search escapes the user's input before wrapping it:

```csharp
var escaped = query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
// ... WHERE source_app LIKE @q ESCAPE '\' OR source_title LIKE @q ESCAPE '\'
cmd.Parameters.AddWithValue("@q", $"%{escaped}%");
```

Backslash first, or you double-escape the escapes.

`TotalBytes` on `CaptureStore` enumerates `captures/` under the root — **not** the whole root, which contains `library.db`, the WAL, and `thumbs/`:

```csharp
public long TotalBytes()
{
    var dir = Path.Combine(_root, "captures");
    if (!Directory.Exists(dir)) return 0;
    return new DirectoryInfo(dir)
        .EnumerateFiles("*", SearchOption.AllDirectories)
        .Sum(f => f.Length);
}
```

- [ ] **Step 4: Green, full suite, pristine output**

---

### Task 4: Thumbnail cache

Spec §4.2.

**Files:**
- Create: `src/Snipwhiz.Core/Imaging/ThumbnailCache.cs`
- Test: `tests/Snipwhiz.Core.Tests/Imaging/ThumbnailCacheTests.cs`

**Interfaces:**
- Consumes: `PngDecoder`, `CaptureStore.ResolvePath`.
- Produces: `ThumbnailCache(string root)` with
  `Task<string> GetOrCreateAsync(CaptureRecord record, CancellationToken ct)` returning the absolute path of `thumbs/{id}.jpg`.

- [ ] **Step 1: Write the failing tests**

1. **First call creates the file; it is a valid JPEG.** Assert the file exists and its first two bytes are `FF D8`.
2. **Long edge is 320; aspect ratio is preserved.** Seed a 800×400 PNG, assert the thumbnail is 320×160. Seed 400×800, assert 160×320.
3. **A capture smaller than 320 is not upscaled.** Seed 100×50; assert the thumbnail is 100×50.
4. **Second call does not regenerate.** Record the file's `LastWriteTimeUtc`, call again, assert it is unchanged.
5. **A corrupt cached thumbnail regenerates.** Overwrite the `.jpg` with garbage, call again, assert a valid JPEG comes back.
6. **A cancelled token stops work.** Pass an already-cancelled token, assert `OperationCanceledException` and that no partial file is left behind.
7. **A missing original propagates `ImageDecodeException`**, not a partial or zero-byte thumbnail.

- [ ] **Step 2: Implement**

Decode via `PngDecoder`, draw into a new `Bitmap` at the target size with `InterpolationMode.HighQualityBicubic` and `PixelOffsetMode.HighQuality`, then encode JPEG at quality 82 via `EncoderParameters`.

**Flatten before encoding.** JPEG has no alpha; drawing a transparent source onto an uninitialized bitmap yields black. Fill with the theme surface colour first (`#1C1B1A`) and draw over it. Every 2a capture is opaque so this is invisible today — and it is exactly the kind of dormant defect that surfaces the week 2b ships transparent exports.

Write to `{id}.jpg.tmp` then `File.Move(..., overwrite: true)`. A cancellation or crash mid-encode must never leave a truncated file that later reads as a valid-but-wrong thumbnail. Test 6 is what proves this.

Concurrency is bounded by a `SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2))` held only around the decode/encode, not around the cache-hit path.

- [ ] **Step 3: Green, full suite**

---

### Task 5: Library window shell, first runnable

Spec §4.1, §4.14, §4.15. First task with visible output. No grid yet — an empty window that opens, themes correctly, and gets out of the way during a capture.

**Files:**
- Create: `src/Snipwhiz.App/Library/LibraryWindow.xaml` / `.cs`, `src/Snipwhiz.App/Library/Mica.cs`
- Modify: `src/Snipwhiz.App/TrayHost.cs`, `src/Snipwhiz.App/App.xaml.cs`, `src/Snipwhiz.App/NativeMethods.txt`

**Interfaces:**
- Produces: `LibraryWindow` (show/hide singleton), `TrayHost.LibraryRequested` event.

- [ ] **Step 1: Add `DwmSetWindowAttribute` to `NativeMethods.txt` and write `Mica.cs`**

Best-effort, and it returns `bool` so the caller can log rather than assume:

```csharp
internal static class Mica
{
    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int  DWMSBT_MAINWINDOW = 2;

    public static bool TryApply(IntPtr hwnd) { /* both attributes, swallow failures */ }
}
```

Both attributes, not just the backdrop: without `USE_IMMERSIVE_DARK_MODE` the caption bar stays light over a dark window, which looks like a bug rather than a fallback.

- [ ] **Step 2: Build the window shell**

`LibraryWindow.xaml`: dark warm-neutral surface `#1C1B1A`, a header with a title and a search box (inert until Task 10), an empty content area, and a footer placeholder. `MinWidth` sized to fit three 320-px tiles plus gutters.

`Esc` closes. Closing **hides** rather than disposes — handle `Closing`, cancel it, and `Hide()`. Reopening is then instant and the view model's state survives.

- [ ] **Step 3: Wire the three open paths**

In `TrayHost`: add a "Library" menu item raising `LibraryRequested`, and repoint `DoubleClick` from `RegionRequested` to `LibraryRequested` (`TrayHost.cs:56`).

In `App.xaml.cs`: register `Ctrl+Shift+L` alongside the existing hotkeys — add a `HotkeyId` member and follow the registration and conflict-reporting the existing two already use. A conflict must report the same way spec 1's do, not silently do nothing.

Show/activate the single instance; never construct a second window.

- [ ] **Step 4: Hide the library during a capture (§4.14)**

In both `CaptureRegion` and `CaptureFullscreen`, before grabbing: if the library is visible, hide it and remember that it was visible. Restore it after the session completes **or aborts**.

Restoration must survive every exit path spec 1 has — Esc, right-click, the display-change abort, and the watchdog. Put the restore in the same place the session teardown already runs, not in the success path only. A cancelled capture that leaves the user's window silently gone is worse than the self-capture this fixes.

The window is hidden *before* `BitBltGrabber.Grab()`, and WPF `Hide()` is not synchronous with respect to the compositor. Verify by capturing with the library open and inspecting the PNG — if the window is still in the frozen bitmap, a `Dispatcher` yield or a short pump is needed before the grab.

- [ ] **Step 5: Verify manually**

Run the app. Confirm: all three open paths work; `Esc` hides; reopening is instant; Mica is visible (or, if it fails, the flat surface looks deliberate); the window survives a capture and comes back; a cancelled capture also brings it back; `Ctrl+Shift+1` and `Ctrl+Shift+2` still work with the library focused.

**Negative control for Step 4:** comment out the hide, capture with the library open, confirm the window appears in the PNG. Then restore the hide and confirm it does not. Put both PNGs' evidence in the report.

---

### Task 6: Virtualized grid

Spec §4.3, §4.4. The riskiest task. It gets a measured gate, not a visual impression.

**Files:**
- Create: `src/Snipwhiz.App/Library/LibraryViewModel.cs`, `src/Snipwhiz.App/Library/CaptureTile.xaml` / `.cs`
- Modify: `src/Snipwhiz.App/Library/LibraryWindow.xaml` / `.cs`

**Interfaces:**
- Consumes: `CaptureStore.Page`, `ThumbnailCache`, `CaptureStore.ResolvePath`.
- Produces: `LibraryViewModel` exposing `ObservableCollection<object> Rows` — a flat list mixing `DayHeaderRow` and `TileRow(IReadOnlyList<CaptureRecord>)`.

- [ ] **Step 1: Row chunking and day grouping in the view model, unit-tested**

This is pure logic over a list and needs no UI to test. Given a page of records and a column count, produce the flat row list: a `DayHeaderRow` whenever the day changes, then `TileRow`s of at most `columns` records.

Test it directly:
1. 7 records across 2 days with `columns: 3` produces the exact expected sequence of headers and row sizes.
2. A day boundary starts a new row even mid-row — a header never appears inside a `TileRow`.
3. Changing `columns` re-chunks the same records without re-querying.
4. "Today" and "Yesterday" resolve against an **injected** clock, not `DateTimeOffset.Now`. Pass the reference instant in; a test that computes the expected label from the same `Now` the code uses proves nothing.

- [ ] **Step 2: The grid itself**

`ItemsControl` bound to `Rows`, with `VirtualizingStackPanel` as the items panel:

```xml
<ItemsControl.ItemsPanel>
  <ItemsPanelTemplate>
    <VirtualizingStackPanel VirtualizationMode="Recycling" IsVirtualizing="True"/>
  </ItemsPanelTemplate>
</ItemsControl.ItemsPanel>
```

`ItemsControl` does not virtualize by default — it needs `ScrollViewer.CanContentScroll="True"` and an `ItemsPanel` that virtualizes, or it silently realizes everything. This is the single most likely way this task fails while looking fine.

A `DataTemplateSelector` picks the day-header template or the tile-row template.

- [ ] **Step 3: Tiles load thumbnails lazily and cancel on recycle**

`CaptureTile` requests its thumbnail when it binds and **cancels when it unbinds** — recycling means a container is reused for a different record while the old request may still be running. Hold a `CancellationTokenSource` per tile, cancel it in the `DataContext` change handler, and drop late results whose record no longer matches the tile's current one.

Without that check, a fast scroll shows thumbnails landing on the wrong tiles — a bug that is invisible on a fast machine with a warm cache and obvious on a cold one.

- [ ] **Step 4: Incremental paging on scroll**

When the scroll viewer nears the end, fetch the next keyset page from the last loaded record and append. Guard re-entrancy: a fast scroll must not fire three overlapping fetches. One in-flight fetch at a time, tracked by a simple bool on the view model.

- [ ] **Step 5: The measured gate**

Seed 1,000 captures with `LibrarySeeder` (files on disk — reuse a handful of real PNGs under many ids so this stays fast).

Add a diagnostic in the spirit of spec 1's `OverlayVerification`, enabled by `SNIPWHIZ_VERIFY_GRID=1` and inert otherwise: walk the visual tree and count realized `CaptureTile` instances, writing the result to `%TEMP%\snipwhiz-grid-verify.txt`.

- **Positive:** scroll top to bottom over 1,000 captures. Realized tiles must stay **bounded** — a small multiple of what fits on screen, not a number that climbs with scroll position.
- **Negative control:** swap the items panel for a plain `StackPanel`, run again, and watch the count climb to 1,000. Put both numbers in the report.

"It scrolled smoothly" is not evidence — a dev box hides jank that a family laptop will not.

- [ ] **Step 6: Manual pass**

Grid renders, day headers are right, thumbnails appear, scrolling is smooth, resizing re-chunks without a re-query, an empty library shows an empty state rather than a blank void.

---

### Task 7: Preview view

Spec §4.11.

**Files:**
- Create: `src/Snipwhiz.App/Library/PreviewView.xaml` / `.cs`
- Modify: `LibraryWindow.xaml` / `.cs`, `LibraryViewModel.cs`

- [ ] **Step 1: Overlay inside the window**

`PreviewView` sits above the grid in the same window's visual tree, collapsed until a tile is clicked. `Esc`, a back button, or a click outside the image returns to the grid. The grid keeps its scroll position.

- [ ] **Step 2: Decode the full PNG off the UI thread**

```csharp
var frame = await Task.Run(() =>
{
    using var stream = File.OpenRead(path);
    var f = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    f.Freeze();   // required before crossing back to the UI thread
    return f;
});
```

`OnLoad` so the stream can close, `Freeze()` so it is legal on the UI thread. This is the documented exception to `PngDecoder` (§4.11) — it is a display path and routing it through BGRA would add a full-size copy for nothing.

Show the thumbnail underneath while the decode runs, then swap. If the swap is visible as a flash on a slow disk, drop the thumbnail placeholder and show nothing — a flicker is worse than a brief blank.

- [ ] **Step 3: Scaling — fit down, never up past physical 1:1**

Larger than the view: fit, preserving aspect. Smaller: display at **physical 1:1**, meaning DIP size = `physicalPixels / scale` where `scale` comes from `VisualTreeHelper.GetDpi(this).DpiScaleX`.

This is spec 1 §4.3's rule reached by a different route. A 200×100 capture sized to 200×100 DIPs renders at 300×150 physical on a 150% monitor — upscaled and blurry, which is the exact defect this product exists not to have.

**Verify on the 125% laptop panel**, not only the 100% external: measure the on-screen size of a small capture and confirm it equals the capture's pixel dimensions. The negative control is sizing in DIPs and observing it come out 1.25× too large.

- [ ] **Step 4: Manual pass**

Open several captures including a very wide one, a very tall one, and one smaller than the window. Confirm no upscaling, no blur, and that `Esc` returns to the same scroll position.

---

### Task 8: The copy path

Spec §4.6. One path, off the UI thread, verified by format enumeration.

**Files:**
- Create: `src/Snipwhiz.App/Library/ClipboardCopier.cs`
- Modify: `PreviewView.xaml` / `.cs`

- [ ] **Step 1: One method, two callers**

```csharp
internal static class ClipboardCopier
{
    public static async Task<bool> CopyAsync(CaptureStore store, CaptureRecord record)
    {
        var path = store.ResolvePath(record);
        return await Task.Run(() =>
        {
            try
            {
                ClipboardWriter.Write(PngDecoder.Decode(path));
                return true;
            }
            catch (Exception e) when (e is ClipboardUnavailableException or ImageDecodeException)
            {
                return false;
            }
        });
    }
}
```

The **Copy** button and the `Ctrl+C` handler both call this. Neither reaches `ClipboardWriter` on its own.

Off-thread is not optional: the call decodes a full PNG, re-encodes it to PNG inside the writer (`ClipboardWriter.cs:27`), and can sleep up to 8 × 60 ms retrying `OpenClipboard` (`ClipboardWriter.cs:17-18,33-37`).

- [ ] **Step 2: Feedback**

Button shows a pending state while copying and a brief "Copied" confirmation after. Failure shows an in-window message — the window has focus, so a tray balloon would be the wrong place. Distinguish the two failures: a clipboard held by another app is transient and worth retrying; a file that will not decode is Task 10's missing-file path.

- [ ] **Step 3: The format-enumeration check — this is the one that matters**

Spec 1's clipboard verification pasted into four apps. That check **cannot fail here** even with a completely wrong implementation: every 2a capture is opaque, and the black-background defect the three-format payload prevents only manifests with alpha. Pasting an opaque screenshot after `Clipboard.SetImage` looks perfectly fine.

So the check enumerates instead. After a library copy, assert `PNG`, `CF_DIBV5` and `CF_DIB` are **all** present — a few lines of `EnumClipboardFormats`, or PowerShell over the same API.

- **Positive:** copy from the preview via the button, then via `Ctrl+C`. All three formats present both times.
- **Negative control:** temporarily replace the call with `System.Windows.Clipboard.SetImage`, run again, and watch the assertion fail. Put the failing output in the report. **Without the negative control this check is not evidence.**

- [ ] **Step 4: Paste into Word, Paint, Chrome and Slack** — end-to-end confirmation on top of Step 3, not instead of it.

---

### Task 9: Delete with a real undo

Spec §4.7. The file must outlive the undo window.

**Files:**
- Create: `src/Snipwhiz.App/Library/UndoToast.xaml` / `.cs`
- Modify: `LibraryViewModel.cs`, `CaptureTile.xaml`, `PreviewView.xaml`

- [ ] **Step 1: The order, exactly**

1. Delete the DB row, remove the tile, show the toast. **Touch no files.**
2. Undo → re-insert the record via the existing `LibraryDb.Insert`, restore the tile. The file was never gone.
3. Toast expires (5 s) or the window closes → delete the PNG and the thumbnail, best-effort.

Pending deletions live in a list on the view model. On window close **and** on app exit, flush it — expire every pending toast and perform the file deletions.

Rev. 1 of the spec had this backwards and would have destroyed the file immediately, so undo restored a row pointing at nothing. Silent data loss behind an affordance that says the opposite.

- [ ] **Step 2: The toast**

Bottom of the window, "Capture deleted — Undo", auto-dismissing after 5 s. Multiple rapid deletes queue rather than clobbering one another; each undo restores its own record.

- [ ] **Step 3: Verify — and this check must exercise the bytes**

- Delete, undo, then **open the preview and copy the restored capture.** Both must work. Observing the tile return proves nothing: a data-losing implementation returns the tile too. That was the flaw in rev. 1's check.
- Delete, let the toast expire, confirm the PNG *and* `thumbs/{id}.jpg` are gone from disk.
- Delete, close the window before the toast expires, confirm the file is still cleaned up.
- Delete a capture that is currently open in the preview — the preview must close or show the missing-file state, not throw.

---

### Task 10: Search, live insert, footer, missing files

Spec §4.8, §4.9, §4.10, §4.12. Four small pieces that share the view model.

**Files:**
- Modify: `LibraryViewModel.cs`, `LibraryWindow.xaml` / `.cs`, `CaptureTile.xaml` / `.cs`, `App.xaml.cs`

- [ ] **Step 1: Search**

Wire the header box to `CaptureStore.Search`, debounced 200 ms. Searching replaces the paged view; clearing it restores paging from the top. An empty result shows an empty state naming the query.

- [ ] **Step 2: Live insert**

Add a `CaptureCompleted` event on `App` raised after a successful capture. `LibraryWindow` subscribes while visible and unsubscribes when hidden.

**Skip failed saves.** `CaptureOutcome.Record` is null whenever the save failed (`CapturePipeline.cs:11-16,73-87`); raising on those inserts a tile for a capture with no row and no file.

The tile inserts at the top of today's group, which may require a new `DayHeaderRow` if it is the first capture today. Do not re-query — the whole point is that the record is already in hand.

- [ ] **Step 3: Footer**

`Count()` on the UI thread — one indexed aggregate (§4.5). `TotalBytes()` on the thread pool — a directory walk. Format as "1,284 captures · 3.2 GB". Refresh on open, after a delete, and after a live insert.

- [ ] **Step 4: Missing files (§4.12)**

Users have spent a whole spec being told to browse `%LOCALAPPDATA%\Snipwhiz\` in Explorer, so some will delete originals behind the database's back.

A tile whose decode throws `ImageDecodeException` renders a placeholder with the capture's date and dimensions, and its only action is **Remove from library** (delete the row; there is no file to delete). Preview and copy on such a record show the same message rather than throwing.

Detect by catching the decode failure, not by probing `File.Exists` — the file can vanish between the probe and the read, and the error path is needed either way.

- [ ] **Step 5: Verify each**

Search mixed case, partial words, and a literal `%`. Capture with the window open. Delete a PNG in Explorer, then click its tile, preview it, and copy it — three paths, no exceptions, all offering removal.

---

### Task 11: Full manual verification

Spec §7. Work the table end to end and write the results to `docs/superpowers/plans/2026-07-26-library-shell-verification.md`, following spec 1's verification doc: cite evidence for every PASS so it can be re-audited rather than taken on trust.

- [ ] **Step 1: Run every check in spec §7, checks 1 through 12**
- [ ] **Step 2: Run the negative controls and record their failing output**

`Clipboard.SetImage` (check 1 must fail), a non-virtualizing `StackPanel` (check 2 must fail), the thumbnail forced into the preview (check 1c must fail), DIP sizing in the preview (check 1d must fail).

- [ ] **Step 3: Soak** — open the library, scroll a 1,000-capture set repeatedly, capture, delete, preview. GDI handles and managed working set must both stay flat. This app runs for weeks.
- [ ] **Step 4: Record residual risks** carried into spec 2b, in spec 1's format.

---

## Verification summary

| Task | Automated | Observed |
|---|---|---|
| 1 | 4 migration tests incl. a real v1 database | — |
| 2 | Round-trip with channel-asymmetric fixture | — |
| 3 | 8 query tests incl. paging under insertion, `%` escaping | — |
| 4 | 7 thumbnail tests incl. corrupt regeneration, cancellation | — |
| 5 | — | Three open paths; self-capture negative control |
| 6 | Chunking/grouping unit tests | **Bounded container count** + `StackPanel` negative control |
| 7 | — | Physical 1:1 on the 125% panel + DIP negative control |
| 8 | — | **Format enumeration** + `SetImage` negative control |
| 9 | — | Undo then **copy the restored bytes** |
| 10 | — | Search escaping, live insert, missing-file paths |
| 11 | Full suite | Spec §7 table, all negative controls, soak |
