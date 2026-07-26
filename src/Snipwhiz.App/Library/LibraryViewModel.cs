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

    public void SetColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns == _columns) return;
        _columns = columns;
        Rebuild();
    }

    public void Reload()
    {
        _records.Clear();
        _tiles.Clear();
        _exhausted = false;
        Rows.Clear();
        LoadNextPage();
    }

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

    /// <summary>Inserts a capture taken while the window was open. Task 10 wires this up.</summary>
    public void InsertNewest(CaptureRecord record)
    {
        if (_records.Any(r => r.Id == record.Id)) return;
        _records.Insert(0, record);
        Rebuild();
    }

    public void Remove(Guid id)
    {
        _records.RemoveAll(r => r.Id == id);
        _tiles.Remove(id);
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
