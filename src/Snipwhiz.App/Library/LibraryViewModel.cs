using System.Collections.ObjectModel;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Library;

/// <summary>
/// Owns the grid's rows: pages captures out of the store, chunks them into rows
/// via <see cref="LibraryLayout"/>, and keeps one tile view model per capture so
/// a decoded thumbnail survives re-chunking and recycling.
/// </summary>
public sealed class LibraryViewModel(CaptureStore store, ThumbnailCache cache)
{
    private const int PageSize = 200;

    private readonly List<CaptureRecord> _records = [];
    private readonly Dictionary<Guid, CaptureTileViewModel> _tiles = [];

    private int _columns = 4;
    private bool _fetching;
    private bool _exhausted;

    public ObservableCollection<object> Rows { get; } = [];

    public int Count => _records.Count;

    /// <summary>
    /// How many tile view models are still holding a decoded bitmap. Diagnostic
    /// only: virtualization bounds the <i>containers</i>, and this is what says
    /// whether it also bounds the pixels they were bound to.
    /// </summary>
    public int RetainedThumbnails => _tiles.Values.Count(t => t.Thumbnail is not null);

    public void SetColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns == _columns) return;
        _columns = columns;
        Rebuild();
    }

    private string _query = string.Empty;

    public bool IsSearching => _query.Length > 0;

    public void Reload()
    {
        _records.Clear();
        _tiles.Clear();
        _exhausted = false;
        Rows.Clear();
        LoadNextPage();
    }

    /// <summary>
    /// Replaces the paged view with search results, or restores paging when the
    /// query is cleared. Results are not paged: the limit is the cap, and a query
    /// that matches more than that wants refining, not scrolling.
    /// </summary>
    public void Search(string query)
    {
        query = query.Trim();
        if (query == _query) return;
        _query = query;

        _records.Clear();
        _tiles.Clear();
        Rows.Clear();

        if (query.Length == 0)
        {
            _exhausted = false;
            LoadNextPage();
            return;
        }

        // Nothing more to fetch on scroll — results are one shot.
        _exhausted = true;
        _records.AddRange(store.Search(query, SearchLimit));
        Rebuild();
    }

    private const int SearchLimit = 500;

    /// <summary>
    /// Fetches the next keyset page. Re-entrancy guarded: a fast scroll fires
    /// ScrollChanged repeatedly and would otherwise start several overlapping
    /// fetches for the same page.
    /// </summary>
    public void LoadNextPage()
    {
        if (_fetching || _exhausted) return;

        _fetching = true;
        try
        {
            var page = store.Page(_records.Count == 0 ? null : _records[^1], PageSize);
            if (page.Count == 0)
            {
                _exhausted = true;
                return;
            }

            _records.AddRange(page);
            Rebuild();
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>
    /// Inserts a capture taken while the window was open. Ignored during a search:
    /// dropping an unrelated capture into a filtered view would be a lie about
    /// what the query matched.
    /// </summary>
    public void InsertNewest(CaptureRecord record)
    {
        if (IsSearching) return;
        if (_records.Any(r => r.Id == record.Id)) return;
        _records.Insert(0, record);
        Rebuild();
    }

    /// <summary>
    /// Swaps in a capture that has just been saved from the editor.
    ///
    /// <para>Both places that hold it have to change: <c>_records</c>, because it
    /// carries the keyset paging cursor and would otherwise hand back a row whose
    /// columns are out of date, and the tile, because it decides between the render
    /// and the original from the record it holds.</para>
    /// </summary>
    public void Replace(CaptureRecord updated)
    {
        var index = _records.FindIndex(r => r.Id == updated.Id);
        if (index < 0) return;
        _records[index] = updated;

        if (_tiles.TryGetValue(updated.Id, out var tile)) tile.Refresh(updated);
    }

    public void Remove(Guid id)
    {
        _records.RemoveAll(r => r.Id == id);
        _tiles.Remove(id);
        Rebuild();
    }

    /// <summary>
    /// Puts a record back where it belongs rather than at the top — an undone
    /// delete should restore the grid, not reorder it.
    /// </summary>
    public void Restore(CaptureRecord record)
    {
        if (_records.Any(r => r.Id == record.Id)) return;

        var index = _records.FindIndex(r =>
            r.CreatedUtc < record.CreatedUtc ||
            (r.CreatedUtc == record.CreatedUtc && string.CompareOrdinal(
                r.Id.ToString("D"), record.Id.ToString("D")) < 0));

        _records.Insert(index < 0 ? _records.Count : index, record);
        Rebuild();
    }

    private void Rebuild()
    {
        var target = LibraryLayout.Build(_records, _columns, DateTimeOffset.Now)
            .Select(ToViewRow)
            .ToList();

        // Replace only the tail. Clearing and refilling would reset the scroll
        // position on every page fetch, which is precisely when the user is
        // scrolled down and least wants that.
        var common = 0;
        while (common < Rows.Count && common < target.Count && SameRow(Rows[common], target[common]))
            common++;

        while (Rows.Count > common) Rows.RemoveAt(Rows.Count - 1);
        for (var i = common; i < target.Count; i++) Rows.Add(target[i]);
    }

    private object ToViewRow(LibraryRow row) => row switch
    {
        DayHeaderRow header => header,
        TileRow tiles => new TileRowViewModel(tiles.Captures.Select(TileFor).ToArray()),
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    private CaptureTileViewModel TileFor(CaptureRecord record)
    {
        if (_tiles.TryGetValue(record.Id, out var existing)) return existing;
        return _tiles[record.Id] = new CaptureTileViewModel(record, cache);
    }

    /// <summary>
    /// Structural comparison. The record types cannot do this themselves —
    /// <see cref="TileRow"/> holds a list, and a record's synthesized equality
    /// compares lists by reference, so two builds of identical content would
    /// never match and the tail-only update would degrade to a full replace.
    /// </summary>
    private static bool SameRow(object a, object b) => (a, b) switch
    {
        (DayHeaderRow x, DayHeaderRow y) => x.Label == y.Label,
        (TileRowViewModel x, TileRowViewModel y) =>
            x.Tiles.Count == y.Tiles.Count &&
            x.Tiles.Zip(y.Tiles).All(p => p.First.Record.Id == p.Second.Record.Id),
        _ => false,
    };
}
