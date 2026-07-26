using Snipwhiz.Core.Storage;
using Xunit;

namespace Snipwhiz.Core.Tests.Storage;

public class LibraryLayoutTests
{
    // Fixed instants; nothing here reads the clock.
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    private static CaptureRecord At(DateTimeOffset when, int n = 0) =>
        new(Guid.CreateVersion7(), when, 100, 50, "app", $"Window {n}", $"captures/{n}.png");

    /// <summary>Local noon on the given day, so time-zone conversion cannot move it.</summary>
    private static DateTimeOffset Day(int day) =>
        new DateTimeOffset(new DateTime(2026, 7, day, 12, 0, 0, DateTimeKind.Local)).ToUniversalTime();

    [Fact]
    public void Seven_captures_over_two_days_chunk_into_the_expected_rows()
    {
        var records = new[]
        {
            At(Day(26), 0), At(Day(26), 1), At(Day(26), 2), At(Day(26), 3),
            At(Day(25), 4), At(Day(25), 5), At(Day(25), 6),
        };

        var rows = LibraryLayout.Build(records, columns: 3, Now);

        Assert.Collection(rows,
            r => Assert.Equal("Today", Assert.IsType<DayHeaderRow>(r).Label),
            r => Assert.Equal(3, Assert.IsType<TileRow>(r).Captures.Count),
            r => Assert.Equal(1, Assert.IsType<TileRow>(r).Captures.Count),
            r => Assert.Equal("Yesterday", Assert.IsType<DayHeaderRow>(r).Label),
            r => Assert.Equal(3, Assert.IsType<TileRow>(r).Captures.Count));
    }

    [Fact]
    public void A_day_boundary_never_leaves_captures_from_two_days_in_one_row()
    {
        var records = new[]
        {
            At(Day(26), 0), At(Day(26), 1),
            At(Day(25), 2), At(Day(25), 3),
        };

        var rows = LibraryLayout.Build(records, columns: 3, Now);

        foreach (var tileRow in rows.OfType<TileRow>())
        {
            var days = tileRow.Captures.Select(c => c.CreatedUtc.ToLocalTime().Date).Distinct();
            Assert.Single(days);
        }
    }

    [Fact]
    public void Changing_the_column_count_rechunks_the_same_records()
    {
        var records = Enumerable.Range(0, 6).Select(i => At(Day(26), i)).ToArray();

        var wide = LibraryLayout.Build(records, columns: 6, Now);
        var narrow = LibraryLayout.Build(records, columns: 2, Now);

        Assert.Single(wide.OfType<TileRow>());
        Assert.Equal(3, narrow.OfType<TileRow>().Count());
        // Same captures, same order, regardless of how they are chunked.
        Assert.Equal(
            wide.OfType<TileRow>().SelectMany(r => r.Captures).Select(c => c.Id),
            narrow.OfType<TileRow>().SelectMany(r => r.Captures).Select(c => c.Id));
    }

    [Fact]
    public void Older_days_use_an_absolute_label_not_a_relative_one()
    {
        var rows = LibraryLayout.Build([At(Day(20), 0)], columns: 3, Now);

        var label = Assert.IsType<DayHeaderRow>(rows[0]).Label;
        Assert.NotEqual("Today", label);
        Assert.NotEqual("Yesterday", label);
        Assert.Contains("July", label);
    }

    [Fact]
    public void A_day_in_an_earlier_year_includes_the_year()
    {
        var lastYear = new DateTimeOffset(new DateTime(2025, 12, 3, 12, 0, 0, DateTimeKind.Local));

        var label = LibraryLayout.DescribeDay(lastYear.Date, Now);

        Assert.Contains("2025", label);
    }

    [Fact]
    public void An_empty_library_produces_no_rows()
    {
        Assert.Empty(LibraryLayout.Build([], columns: 3, Now));
    }

    [Fact]
    public void A_column_count_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LibraryLayout.Build([At(Day(26))], columns: 0, Now));
    }
}
