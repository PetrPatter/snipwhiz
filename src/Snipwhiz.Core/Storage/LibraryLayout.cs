using System.Globalization;

namespace Snipwhiz.Core.Storage;

public abstract record LibraryRow;

public sealed record DayHeaderRow(string Label) : LibraryRow;

public sealed record TileRow(IReadOnlyList<CaptureRecord> Captures) : LibraryRow;

/// <summary>
/// Turns a flat page of captures into the row list the grid renders.
///
/// This exists so the grid can virtualize without a custom panel. WPF ships
/// <c>VirtualizingStackPanel</c>, which does not wrap; rather than write a
/// wrapping virtualizing panel — a measure/arrange implementation that is subtly
/// wrong for months — the tiles are chunked into fixed-width rows here and the
/// stock panel virtualizes those.
/// </summary>
public static class LibraryLayout
{
    /// <param name="now">
    /// Injected rather than read from the clock, so "Today" and "Yesterday" can be
    /// tested against a fixed instant. A test that derives its expected label from
    /// the same <c>Now</c> the code reads proves nothing.
    /// </param>
    public static IReadOnlyList<LibraryRow> Build(
        IEnumerable<CaptureRecord> records, int columns, DateTimeOffset now)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));

        var rows = new List<LibraryRow>();
        DateTime? currentDay = null;
        var pending = new List<CaptureRecord>(columns);

        void FlushPending()
        {
            if (pending.Count == 0) return;
            rows.Add(new TileRow(pending.ToArray()));
            pending.Clear();
        }

        foreach (var record in records)
        {
            var day = record.CreatedUtc.ToLocalTime().Date;
            if (currentDay != day)
            {
                // A day boundary ends the current row even if it is half full —
                // otherwise a header would appear in the middle of a row of tiles.
                FlushPending();
                rows.Add(new DayHeaderRow(DescribeDay(day, now)));
                currentDay = day;
            }

            pending.Add(record);
            if (pending.Count == columns) FlushPending();
        }

        FlushPending();
        return rows;
    }

    public static string DescribeDay(DateTime day, DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";

        return day.Year == today.Year
            ? day.ToString("dddd, d MMMM", CultureInfo.CurrentCulture)
            : day.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    }
}
