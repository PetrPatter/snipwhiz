using System.Windows;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Scene;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// Step badges read their position in the document, and store nothing.
///
/// <para>The decision, taken with the user before the phase: deleting step 2 of 5
/// renumbers the rest to close the gap. A screenshot that runs 1, 3, 4, 5 reads as
/// a mistake to whoever receives it, and fixing it by hand would mean retyping every
/// later number.</para>
///
/// <para>The reason it is worth a file of its own is what falls out. <b>Nothing is
/// stored, so undo has no counter to roll back</b>: delete is a delete,
/// <see cref="RemoveAnnotation"/> already restores list position, and the numbers
/// come back correct on their own. The stored-number design would have needed every
/// command that adds, removes or reorders a step to renumber the rest — and the one
/// that forgot would have been found by a user, in a screenshot they had already
/// sent.</para>
/// </summary>
public class StepNumberTests
{
    private static SceneDocument Scene(params Annotation[] annotations) =>
        new() { CaptureId = Guid.Empty, Annotations = [.. annotations] };

    private static StepAnnotation Step(double x = 0)
    {
        var step = new StepAnnotation();
        step.Fit(new Point(x, 0), new Point(x, 0));
        return step;
    }

    private static int[] Numbers(SceneDocument document)
    {
        document.NumberSteps();
        return [.. document.Annotations.OfType<StepAnnotation>().Select(s => s.Number)];
    }

    [Fact]
    public void Badges_are_numbered_from_one_in_document_order()
    {
        var document = Scene(Step(10), Step(20), Step(30));

        Assert.Equal([1, 2, 3], Numbers(document));
    }

    [Fact]
    public void Deleting_the_middle_one_closes_the_gap()
    {
        var steps = new[] { Step(10), Step(20), Step(30), Step(40), Step(50) };
        var document = Scene(steps);

        // Numbered first, because that is what happens: the badges were placed and
        // drawn before anyone deleted one. Without this the test passes against a
        // stored number too — nothing has been assigned yet, so both designs count
        // from scratch and agree. It did, until this line was added.
        Assert.Equal([1, 2, 3, 4, 5], Numbers(document));

        new RemoveAnnotation(steps[1]).Do(document);

        Assert.Equal([1, 2, 3, 4], Numbers(document));
    }

    /// <summary>
    /// And undo opens it again — with no counter to restore, because there is no
    /// counter.
    /// </summary>
    [Fact]
    public void Undoing_the_delete_puts_the_numbers_back()
    {
        var steps = new[] { Step(10), Step(20), Step(30), Step(40), Step(50) };
        var document = Scene(steps);
        var remove = new RemoveAnnotation(steps[1]);

        Assert.Equal([1, 2, 3, 4, 5], Numbers(document));

        remove.Do(document);
        Assert.Equal([1, 2, 3, 4], Numbers(document));

        remove.Undo(document);

        Assert.Equal([1, 2, 3, 4, 5], Numbers(document));
        // Specifically: the one that came back is second again, not appended fifth.
        Assert.Equal(2, steps[1].Number);
    }

    /// <summary>
    /// Other annotations do not take up numbers, however many of them sit between
    /// two badges.
    /// </summary>
    [Fact]
    public void Only_step_badges_are_counted()
    {
        var first = Step(10);
        var second = Step(20);
        var document = Scene(first, new RectangleAnnotation { Size = new Size(10, 10) }, second);

        Assert.Equal([1, 2], Numbers(document));
    }

    /// <summary>
    /// Z-order is how objects overlap, not what order the reader follows. Bringing a
    /// badge to the front must not renumber it.
    /// </summary>
    [Fact]
    public void Reordering_in_z_does_not_renumber()
    {
        var first = Step(10);
        var second = Step(20);
        var document = Scene(first, second);

        first.ZIndex = 99;

        Assert.Equal([1, 2], Numbers(document));
    }

    /// <summary>
    /// The numbering has to have happened before anything draws, which is what
    /// putting it in <see cref="SceneDocument.InPaintOrder"/> buys. Rendering is the
    /// only consumer that matters and it is the only one that must never see a stale
    /// number.
    /// </summary>
    [Fact]
    public void Asking_for_paint_order_numbers_the_badges()
    {
        var steps = new[] { Step(10), Step(20), Step(30) };
        var document = Scene(steps);
        Assert.Equal([1, 2, 3], Numbers(document));

        new RemoveAnnotation(steps[0]).Do(document);

        var painted = document.InPaintOrder().OfType<StepAnnotation>().ToList();

        Assert.Equal([1, 2], painted.Select(s => s.Number));
    }

    [Fact]
    public void The_size_control_is_the_badge_diameter()
    {
        var step = new StepAnnotation();

        step.SizeControl = 60;

        Assert.Equal(60, step.Diameter);
        Assert.Equal((StepAnnotation.MinDiameter, 96), step.SizeControlRange);
    }

    /// <summary>
    /// A badge is placed, not dragged out: the tool stays active so several go down
    /// in a row, and a plain click has to leave one behind rather than being
    /// discarded as a zero-size shape.
    /// </summary>
    [Fact]
    public void A_click_places_a_full_sized_badge()
    {
        var step = Step();

        step.Fit(new Point(40, 50), new Point(40, 50));

        Assert.Equal(StepAnnotation.DefaultDiameter, step.LocalBounds.Width);
        Assert.Equal(new Point(40, 50), new Point(step.Transform.OffsetX, step.Transform.OffsetY));
        Assert.True(step.PlacesRepeatedly);
    }

    [Fact]
    public void Resizing_keeps_it_circular()
    {
        var step = new StepAnnotation();

        step.RestoreGeometry(step.GeometryForBounds(new Size(80, 40)));

        Assert.Equal(40, step.Diameter);
        Assert.Equal(step.LocalBounds.Width, step.LocalBounds.Height);
    }

    /// <summary>
    /// A badge is its fill, so recolouring sets stroke and fill together — and a
    /// digit painted in the stroke colour would disappear into the circle, which is
    /// precisely what happened to text's plate. The number is not a colour anyone
    /// should have to choose, so it is derived and this asserts the badge survives
    /// being made any colour at all.
    /// </summary>
    [Fact]
    public void Recolouring_a_badge_colours_the_circle()
    {
        var step = new StepAnnotation();

        step.Style = step.Recoloured(System.Windows.Media.Colors.DodgerBlue);

        Assert.Equal(System.Windows.Media.Colors.DodgerBlue, step.Style.Fill);
    }
}
