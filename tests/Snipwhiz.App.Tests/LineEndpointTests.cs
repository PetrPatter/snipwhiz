using System.Windows;
using System.Windows.Input;
using Snipwhiz.App.Editor;
using Snipwhiz.App.Editor.Tools;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Scene;
using Xunit;

namespace Snipwhiz.App.Tests;

/// <summary>
/// A line and an arrow are edited by their two ends.
///
/// <para>They used to wear the same eight-handle box as a rectangle, which was
/// confusing in the obvious way and wrong in a less obvious one: a corner handle
/// resizes within the quadrant the line already occupies, so there was no gesture
/// anywhere in the app that could drag one end past the other and turn an arrow
/// around.</para>
///
/// <para><b>The end that is not being dragged must not move.</b> A line is stored
/// centred on its own midpoint, so moving one end moves the object's origin — the
/// far end therefore slides across the picture unless the frame is re-anchored onto
/// it. That is invisible in a screenshot of a static line and obvious the moment
/// anyone drags one, which is why it is asserted in image space here rather than
/// left to the eye.</para>
/// </summary>
public class LineEndpointTests
{
    /// <summary>Comfortably under a handle's 7px grab tolerance, in image pixels.</summary>
    private const double Epsilon = 0.5;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // an arrow is a line with a head; the geometry is shared
    public void Dragging_one_end_leaves_the_other_exactly_where_it_was(bool arrow)
    {
        Drag(arrow, HandleKind.End, to: new Point(30, 20), (line, _) =>
        {
            Assert.Equal(10, Image(line, HandleKind.Start).X, Epsilon);
            Assert.Equal(10, Image(line, HandleKind.Start).Y, Epsilon);
        });
    }

    [Fact]
    public void The_dragged_end_lands_under_the_pointer()
    {
        Drag(arrow: false, HandleKind.End, to: new Point(30, 20), (line, _) =>
        {
            Assert.Equal(30, Image(line, HandleKind.End).X, Epsilon);
            Assert.Equal(20, Image(line, HandleKind.End).Y, Epsilon);
        });
    }

    /// <summary>The same thing from the other end, which is not symmetric in the code.</summary>
    [Fact]
    public void Dragging_the_start_pins_the_end_instead()
    {
        Drag(arrow: false, HandleKind.Start, to: new Point(70, 15), (line, _) =>
        {
            Assert.Equal(90, Image(line, HandleKind.End).X, Epsilon);
            Assert.Equal(60, Image(line, HandleKind.End).Y, Epsilon);
            Assert.Equal(70, Image(line, HandleKind.Start).X, Epsilon);
            Assert.Equal(15, Image(line, HandleKind.Start).Y, Epsilon);
        });
    }

    /// <summary>
    /// The gesture the box handles could not express: an end taken through the other
    /// one reverses the vector, and with it an arrowhead.
    /// </summary>
    [Fact]
    public void An_end_dragged_past_the_other_reverses_the_line()
    {
        Drag(arrow: true, HandleKind.End, to: new Point(4, 4), (line, _) =>
        {
            // Started at +80,+50 from start to end; now the end is up and left of it.
            Assert.True(line.Delta.X < 0, $"Delta.X came out {line.Delta.X:F1}, so it never flipped");
            Assert.True(line.Delta.Y < 0, $"Delta.Y came out {line.Delta.Y:F1}, so it never flipped");
            Assert.Equal(10, Image(line, HandleKind.Start).X, Epsilon);
            Assert.Equal(10, Image(line, HandleKind.Start).Y, Epsilon);
        });
    }

    /// <summary>
    /// A drag applies a command on every mouse-move and the stack absorbs them, so
    /// one gesture is one Ctrl+Z. Several frames here specifically to prove the
    /// absorption still holds now the endpoint drag emits a resize rather than a
    /// reshape.
    /// </summary>
    [Fact]
    public void A_whole_endpoint_drag_is_one_undo_step()
    {
        Drag(arrow: false, HandleKind.End, to: new Point(30, 20), (line, undo) =>
        {
            Assert.Equal(1, undo.Depth);

            undo.Undo();
            Assert.Equal(90, Image(line, HandleKind.End).X, Epsilon);
            Assert.Equal(60, Image(line, HandleKind.End).Y, Epsilon);
        });
    }

    /// <summary>
    /// Neither the eight resize handles nor the rotate arm exists on a line, and they
    /// must not be grabbable while invisible — an eight-handle hit region drawn
    /// nowhere is worse than one drawn wrongly.
    /// </summary>
    ///
    /// <remarks>
    /// Two of the box corners sit exactly on the two ends, because for a line the
    /// bounding box <i>is</i> the line's own extent. Those positions must still
    /// resolve — to the end that is there, which is what the second loop checks. The
    /// first loop is therefore about the handles that are somewhere the line is not:
    /// the four edge midpoints, the two empty corners, and the rotate arm.
    /// </remarks>
    [Fact]
    public void A_line_offers_no_box_handle_except_where_one_of_its_ends_already_is()
    {
        Harness.Canvas((canvas, document) =>
        {
            var line = Place(document, canvas, arrow: false);

            Assert.False(line.HasBoundingBox);

            var ends = new[] { Image(line, HandleKind.Start), Image(line, HandleKind.End) };
            var offTheLine = 0;

            foreach (var kind in Handles.Resizers.Append(HandleKind.Rotate))
            {
                var image = Handles.ImagePosition(line, kind, canvas.ToImageLength(SelectionOverlay.RotateGap));
                if (ends.Any(end => (end - image).Length < 1)) continue;

                offTheLine++;
                Assert.Equal(HandleKind.None,
                    SelectionOverlay.HandleAt(canvas, line, canvas.ToElement(image)));
            }

            // Guards the `continue` above from quietly skipping everything.
            Assert.Equal(7, offTheLine);

            // And the ends themselves are found, so None above is a real answer
            // rather than HandleAt refusing everything.
            foreach (var kind in new[] { HandleKind.Start, HandleKind.End })
            {
                var at = canvas.ToElement(Handles.ImagePosition(line, kind, 0));
                Assert.Equal(kind, SelectionOverlay.HandleAt(canvas, line, at));
            }
        });
    }

    // ---- harness ----------------------------------------------------------

    /// <summary>
    /// Runs a press-drag-release on one of the line's ends, in several steps.
    ///
    /// <para>Several rather than one, deliberately. The endpoint drag re-derives the
    /// geometry from where the gesture <i>began</i> on every frame; a single step
    /// cannot tell that apart from one that builds on the previous frame, and building
    /// on the previous frame is how the callout tail once accelerated off the canvas.</para>
    /// </summary>
    private static void Drag(bool arrow, HandleKind grab, Point to, Action<LineAnnotation, UndoStack> assert) =>
        Harness.Canvas((canvas, document) =>
        {
            var line = Place(document, canvas, arrow);
            var undo = new UndoStack(document);
            var tool = new SelectTool(canvas, document, undo);

            var from = Handles.ImagePosition(line, grab, 0);
            tool.OnPress(from, ModifierKeys.None);

            for (var step = 1; step <= 4; step++)
            {
                var t = step / 4.0;
                tool.OnDrag(new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t),
                    ModifierKeys.None);
            }

            tool.OnRelease(to, ModifierKeys.None);
            assert(line, undo);
        });

    /// <summary>A line from (10,10) to (90,60) on the canvas, selected.</summary>
    private static LineAnnotation Place(SceneDocument document, CanvasHost canvas, bool arrow)
    {
        LineAnnotation line = arrow ? new ArrowAnnotation() : new LineAnnotation();
        line.Fit(new Point(10, 10), new Point(90, 60));

        document.Annotations.Add(line);
        canvas.Rebuild();
        canvas.SetSelection([line]);
        return line;
    }

    private static Point Image(Annotation annotation, HandleKind kind) =>
        Handles.ImagePosition(annotation, kind, 0);
}
