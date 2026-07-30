using System.Windows;
using Snipwhiz.App.Editor;

namespace Snipwhiz.App.Tests;

/// <summary>
/// The view is built from the document's crop, so it has to follow when something
/// other than the crop tool changes it.
///
/// <para><b>Regression.</b> Undo put the old crop back on the document and the
/// screen kept showing the new one: <c>AfterHistory</c> rebuilt the annotation
/// visuals — which is what undo usually needs — but nothing re-ran the view, and
/// the transform and clip stayed sized to the crop that had just been undone. It
/// looked like undo had done nothing. Switching to the crop tool called
/// <c>Fit()</c> on the way in, so the picture snapped to the right size on pressing
/// C, which made it look like a crop-tool bug rather than a repaint that never
/// happened.</para>
///
/// <para><b>These assert on where the picture is drawn, not on what the document
/// says.</b> The first draft of this file checked <c>ContentSize</c> and
/// <c>ContentOrigin</c> and passed with <c>SyncView</c> gutted — both are computed
/// from the document on every read, so they were never the half that went stale.
/// What goes stale is the transform, and <see cref="OnScreen"/> is what can see
/// it.</para>
/// </summary>
public class ViewSyncTests
{
    /// <summary>Where the visible picture actually lands in the viewport.</summary>
    private static Rect OnScreen(CanvasHost canvas)
    {
        var topLeft = canvas.ToElement(canvas.ContentOrigin);
        return new Rect(topLeft, new Size(
            canvas.ContentSize.Width * canvas.Zoom,
            canvas.ContentSize.Height * canvas.Zoom));
    }

    /// <summary>
    /// <see cref="CanvasHost.Fit"/> centres what it fits, so an off-centre picture is
    /// one the view has not been recomputed for.
    /// </summary>
    private static void AssertCentred(CanvasHost canvas)
    {
        var picture = OnScreen(canvas);
        Assert.Equal(picture.Left, canvas.ActualWidth - picture.Right, precision: 6);
        Assert.Equal(picture.Top, canvas.ActualHeight - picture.Bottom, precision: 6);
    }

    [Fact]
    public void The_view_follows_a_crop_applied_behind_its_back()
    {
        Harness.Canvas((canvas, document) =>
        {
            var whole = OnScreen(canvas);

            // Exactly what CropDocument.Do does. Going through the command would
            // test the command; this tests that the canvas notices at all.
            document.Crop = new Rect(10, 10, 40, 30);
            canvas.SyncView();

            var cropped = OnScreen(canvas);
            Assert.NotEqual(whole, cropped);
            AssertCentred(canvas);
        });
    }

    [Fact]
    public void Undoing_a_crop_puts_the_whole_picture_back_on_screen()
    {
        Harness.Canvas((canvas, document) =>
        {
            var whole = OnScreen(canvas);

            document.Crop = new Rect(10, 10, 40, 30);
            canvas.SyncView();
            // Or the assertion below is "nothing changed twice", which is true of a
            // view that never updates at all. Centring is the probe: a stale
            // transform still reports the new ContentSize, because that is read from
            // the document — what it cannot do is put the picture in the middle.
            AssertCentred(canvas);

            document.Crop = null;              // the undo
            canvas.SyncView();

            Assert.Equal(whole, OnScreen(canvas));
        });
    }

    /// <summary>
    /// The guard, and the reason <c>SyncView</c> is not simply "re-fit after every
    /// undo": undoing a nudge would then yank the zoom the user had set.
    /// </summary>
    [Fact]
    public void Syncing_when_the_crop_has_not_changed_leaves_the_view_alone()
    {
        Harness.Canvas((canvas, _) =>
        {
            canvas.ZoomAt(4, new Point(50, 50));
            var zoom = canvas.Zoom;
            var pan = canvas.Pan;

            canvas.SyncView();

            Assert.Equal(zoom, canvas.Zoom);
            Assert.Equal(pan, canvas.Pan);
        });
    }

    /// <summary>
    /// While the crop tool is active the canvas deliberately shows the whole
    /// capture, so an undo arriving then must not re-fit to a crop that is not
    /// being honoured.
    /// </summary>
    [Fact]
    public void A_crop_change_while_showing_the_whole_capture_does_not_move_the_view()
    {
        Harness.Canvas((canvas, document) =>
        {
            canvas.ShowUncropped = true;
            canvas.Fit();
            var whole = OnScreen(canvas);

            document.Crop = new Rect(10, 10, 40, 30);
            canvas.SyncView();

            Assert.Equal(whole, OnScreen(canvas));
            Assert.Equal(new Point(0, 0), canvas.ContentOrigin);
        });
    }
}
