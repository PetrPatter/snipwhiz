using Snipwhiz.App.Library;
using Xunit;

namespace Snipwhiz.App.Tests;

/// <summary>
/// The grid used to fit as many 252-wide tiles as would go and leave the remainder
/// as dead space down the right edge — up to a whole tile's worth, changing with
/// every resize. That was the single most visible thing wrong with the library.
///
/// <para>These assert the arithmetic rather than the appearance. A row that does
/// not add up to the width it was given is a gutter, and the width it is given is
/// the only input, so this needs no window and no store.</para>
/// </summary>
public class GridLayoutTests
{
    /// <summary>The width a row actually occupies: every tile, plus the gaps between them.</summary>
    private static double RowWidth(int columns, double tileWidth) =>
        columns * tileWidth + LibraryViewModel.Gap * (columns - 1);

    [Theory]
    [InlineData(1030)]    // the default window
    [InlineData(1480)]    // maximised on this machine
    [InlineData(268)]     // exactly one tile plus its gap
    [InlineData(519)]     // a hair under two tiles: must drop to one, not overflow
    [InlineData(2400)]    // an ultrawide
    [InlineData(3820)]
    public void A_row_consumes_the_whole_width(double available)
    {
        var (columns, width) = LibraryViewModel.Layout(available);

        Assert.Equal(available, RowWidth(columns, width), precision: 6);
    }

    [Theory]
    [InlineData(1030)]
    [InlineData(1480)]
    [InlineData(2400)]
    public void Tiles_never_shrink_below_the_minimum(double available)
    {
        var (_, width) = LibraryViewModel.Layout(available);

        // Stretching up is the point; stretching down would mean the column count
        // was too high and the tiles are cramped rather than filling.
        Assert.True(width >= LibraryViewModel.MinTileWidth,
            $"tiles came out {width:F0} wide, under the {LibraryViewModel.MinTileWidth} minimum");
    }

    [Fact]
    public void A_window_narrower_than_one_tile_still_gets_a_column()
    {
        // Zero columns would divide by zero and show nothing at all.
        var (columns, width) = LibraryViewModel.Layout(120);

        Assert.Equal(1, columns);
        Assert.Equal(120, width, precision: 6);
    }

    [Fact]
    public void Widening_the_window_never_reduces_the_column_count()
    {
        var previous = 0;

        for (var available = 260.0; available < 4000; available += 7)
        {
            var (columns, _) = LibraryViewModel.Layout(available);
            Assert.True(columns >= previous, $"columns fell from {previous} to {columns} at {available}");
            previous = columns;
        }
    }
}
