using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Project;
using Snipwhiz.Core.Scene;
using Xunit;

namespace Snipwhiz.Core.Tests.Scene;

public class UndoCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));

    public UndoCommandTests() => Directory.CreateDirectory(_dir);

    private static SceneDocument Empty() => new()
    {
        CaptureId = Guid.Parse("0198f2c1-4a5b-7c6d-8e9f-a0b1c2d3e4f5"),
    };

    private static RectangleAnnotation Rect(double x = 0, double y = 0, double w = 100, double h = 50) => new()
    {
        Size = new Size(w, h),
        Transform = new Matrix(1, 0, 0, 1, x, y),
    };

    /// <summary>
    /// The scene as it would actually be written to disk.
    ///
    /// <para>Comparing through the real serializer rather than a hand-written
    /// equality means the check cannot drift from what a save produces — and it
    /// covers every field the format carries, including ones a future annotation
    /// type adds without touching this test.</para>
    /// </summary>
    private string Snapshot(SceneDocument document)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.ssproj");
        ProjectStore.Save(path, document);
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Applies a command, then asserts undo restores the document exactly and redo
    /// reproduces the change exactly. Every command type goes through this.
    /// </summary>
    private void AssertRoundTrips(SceneDocument document, ISceneCommand command)
    {
        var stack = new UndoStack(document);
        var before = Snapshot(document);

        stack.Apply(command);
        var after = Snapshot(document);
        Assert.NotEqual(before, after);   // a command that changes nothing proves nothing

        stack.Undo();
        Assert.Equal(before, Snapshot(document));

        stack.Redo();
        Assert.Equal(after, Snapshot(document));
    }

    // ---- one per command type --------------------------------------------

    [Fact]
    public void Add_round_trips()
    {
        var document = Empty();
        AssertRoundTrips(document, new AddAnnotation(Rect()));
    }

    [Fact]
    public void Remove_round_trips()
    {
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);
        AssertRoundTrips(document, new RemoveAnnotation(annotation));
    }

    [Fact]
    public void Move_round_trips()
    {
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);

        var turned = Matrix.Identity;
        turned.Rotate(30);
        turned.Translate(400, 250);

        AssertRoundTrips(document, new MoveAnnotation(annotation, annotation.Transform, turned));
    }

    [Fact]
    public void Reshape_round_trips()
    {
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);

        AssertRoundTrips(document, new ReshapeAnnotation(
            annotation, annotation.CaptureGeometry(), new RectangleGeometryState(new Size(300, 120))));
    }

    [Fact]
    public void Restyle_round_trips()
    {
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);

        AssertRoundTrips(document, new RestyleAnnotation(
            annotation, annotation.Style,
            annotation.Style with { Stroke = Colors.Blue, StrokeWidth = 9, Opacity = 0.25 }));
    }

    [Fact]
    public void Reorder_round_trips()
    {
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);

        AssertRoundTrips(document, new ReorderAnnotation(annotation, annotation.ZIndex, 7));
    }

    [Fact]
    public void Crop_round_trips()
    {
        var document = Empty();
        document.Annotations.Add(Rect());

        AssertRoundTrips(document, new CropDocument(null, new Rect(10, 20, 300, 200)));
    }

    [Fact]
    public void Cropping_keeps_every_annotation_including_the_ones_it_hides()
    {
        // The crop is a view on the document, not a knife. If applying one ever
        // dropped what it put out of sight, "widen it again next week" would be a
        // promise the format could not keep.
        var document = Empty();
        document.Annotations.Add(Rect(x: 20, y: 20));
        document.Annotations.Add(Rect(x: 900, y: 900));

        new UndoStack(document).Apply(new CropDocument(null, new Rect(0, 0, 100, 100)));

        Assert.Equal(2, document.Annotations.Count);
    }

    // ---- the harness's own negative control -------------------------------

    /// <summary>Changes the scene and then does not put it back.</summary>
    private sealed class CommandThatDoesNotUndo(Annotation annotation) : ISceneCommand
    {
        public void Do(SceneDocument document) => annotation.ZIndex = 99;

        public void Undo(SceneDocument document) { }
    }

    /// <summary>
    /// Proves the round-trip harness above discriminates, rather than passing
    /// because every command happens to be correct.
    ///
    /// <para>Kept as a test rather than run once by hand, so it keeps being true.
    /// A harness that has only ever seen working commands is not known to catch a
    /// broken one.</para>
    /// </summary>
    [Fact]
    public void The_round_trip_harness_catches_a_command_that_does_not_undo()
    {
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertRoundTrips(document, new CommandThatDoesNotUndo(annotation)));
    }

    // ---- list position ----------------------------------------------------

    [Fact]
    public void Undoing_a_remove_puts_the_object_back_where_it_was()
    {
        // Appending on undo instead of inserting silently reorders the document,
        // which changes paint order for anything sharing a z-index.
        var document = Empty();
        var first = Rect(x: 1);
        var middle = Rect(x: 2);
        var last = Rect(x: 3);
        document.Annotations.AddRange([first, middle, last]);

        var stack = new UndoStack(document);
        stack.Apply(new RemoveAnnotation(middle));
        stack.Undo();

        Assert.Equal([first, middle, last], document.Annotations);
    }

    // ---- stack behaviour --------------------------------------------------

    [Fact]
    public void A_new_action_discards_the_redo_branch()
    {
        var document = Empty();
        var stack = new UndoStack(document);

        stack.Apply(new AddAnnotation(Rect(x: 1)));
        stack.Undo();
        Assert.True(stack.CanRedo);

        stack.Apply(new AddAnnotation(Rect(x: 2)));

        Assert.False(stack.CanRedo);
        Assert.Single(document.Annotations);
    }

    [Fact]
    public void Undo_and_redo_are_no_ops_at_the_ends()
    {
        var document = Empty();
        var stack = new UndoStack(document);

        stack.Undo();
        stack.Redo();

        Assert.Empty(document.Annotations);
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void The_stack_is_bounded_and_drops_the_oldest_entry()
    {
        var document = Empty();
        var stack = new UndoStack(document, depth: 3);

        for (var i = 0; i < 10; i++) stack.Apply(new AddAnnotation(Rect(x: i)));

        Assert.Equal(3, stack.Depth);
        // Ten adds happened; only three can be taken back.
        for (var i = 0; i < 10; i++) stack.Undo();
        Assert.Equal(7, document.Annotations.Count);
    }

    // ---- absorbing --------------------------------------------------------

    [Fact]
    public void A_drag_of_one_object_is_a_single_undo_step()
    {
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);
        var start = annotation.Transform;

        var stack = new UndoStack(document);
        for (var i = 1; i <= 40; i++)
        {
            stack.Apply(new MoveAnnotation(annotation, start, new Matrix(1, 0, 0, 1, i * 5, 0)));
        }

        Assert.Equal(1, stack.Depth);
        Assert.Equal(200, annotation.Transform.OffsetX);

        stack.Undo();
        Assert.Equal(start, annotation.Transform);
    }

    /// <summary>
    /// Absorbing is bounded by the gesture, not by the object.
    ///
    /// <para>Reported from use: drawing a rectangle and then rotating it, then
    /// pressing Ctrl+Z, deleted the rectangle instead of unrotating it. The add
    /// command was swallowing every later change to the shape it created, forever.</para>
    /// </summary>
    [Fact]
    public void Changing_an_object_after_it_was_drawn_is_its_own_undo_step()
    {
        var document = Empty();
        var stack = new UndoStack(document);
        var annotation = Rect();

        // The drawing gesture: added, then resized as the pointer moves.
        stack.BeginGesture();
        stack.Apply(new AddAnnotation(annotation));
        stack.Apply(new ResizeAnnotation(annotation, annotation.CaptureGeometry(), annotation.Transform,
            new RectangleGeometryState(new Size(160, 90)), annotation.Transform));
        Assert.Equal(1, stack.Depth);

        // A separate gesture: rotate it.
        var turned = Matrix.Identity;
        turned.Rotate(30);
        stack.BeginGesture();
        stack.Apply(new MoveAnnotation(annotation, annotation.Transform, turned));
        Assert.Equal(2, stack.Depth);

        // One Ctrl+Z unrotates and leaves the rectangle alone.
        stack.Undo();
        Assert.Single(document.Annotations);
        Assert.Equal(new Size(160, 90), ((RectangleAnnotation)document.Annotations[0]).Size);

        // A second takes the rectangle away.
        stack.Undo();
        Assert.Empty(document.Annotations);
    }

    [Fact]
    public void Two_separate_drags_of_the_same_object_are_two_undo_steps()
    {
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);
        var start = annotation.Transform;
        var stack = new UndoStack(document);

        stack.BeginGesture();
        stack.Apply(new MoveAnnotation(annotation, start, new Matrix(1, 0, 0, 1, 50, 0)));

        stack.BeginGesture();
        stack.Apply(new MoveAnnotation(annotation, annotation.Transform, new Matrix(1, 0, 0, 1, 90, 0)));

        Assert.Equal(2, stack.Depth);

        stack.Undo();
        Assert.Equal(50, annotation.Transform.OffsetX);
    }

    [Fact]
    public void A_change_after_an_undo_does_not_fold_into_the_command_that_was_taken_back()
    {
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);
        var stack = new UndoStack(document);

        stack.BeginGesture();
        stack.Apply(new MoveAnnotation(annotation, annotation.Transform, new Matrix(1, 0, 0, 1, 50, 0)));
        stack.Undo();

        stack.Apply(new MoveAnnotation(annotation, annotation.Transform, new Matrix(1, 0, 0, 1, 70, 0)));

        Assert.Equal(1, stack.Depth);
        stack.Undo();
        Assert.Equal(0, annotation.Transform.OffsetX);
    }

    [Fact]
    public void Dragging_a_different_object_starts_a_new_undo_step()
    {
        var document = Empty();
        var a = Rect(x: 1);
        var b = Rect(x: 2);
        document.Annotations.AddRange([a, b]);

        var stack = new UndoStack(document);
        stack.Apply(new MoveAnnotation(a, a.Transform, new Matrix(1, 0, 0, 1, 50, 0)));
        stack.Apply(new MoveAnnotation(b, b.Transform, new Matrix(1, 0, 0, 1, 60, 0)));

        Assert.Equal(2, stack.Depth);
    }

    [Fact]
    public void An_absorbed_step_does_not_lose_the_original_starting_point()
    {
        // The bug this guards: absorbing by replacing the whole command rather than
        // just its end state, so undo returns to the middle of the drag.
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);
        var start = annotation.Transform;

        var stack = new UndoStack(document);
        stack.Apply(new MoveAnnotation(annotation, start, new Matrix(1, 0, 0, 1, 10, 0)));
        stack.Apply(new MoveAnnotation(annotation, new Matrix(1, 0, 0, 1, 10, 0), new Matrix(1, 0, 0, 1, 90, 0)));

        stack.Undo();

        Assert.Equal(start, annotation.Transform);
    }

    [Fact]
    public void A_restyle_of_the_same_object_absorbs_but_a_move_after_it_does_not()
    {
        var document = Empty();
        var annotation = Rect();
        document.Annotations.Add(annotation);

        var stack = new UndoStack(document);
        stack.Apply(new RestyleAnnotation(annotation, annotation.Style, annotation.Style with { Opacity = 0.9 }));
        stack.Apply(new RestyleAnnotation(annotation, annotation.Style, annotation.Style with { Opacity = 0.4 }));
        Assert.Equal(1, stack.Depth);

        stack.Apply(new MoveAnnotation(annotation, annotation.Transform, new Matrix(1, 0, 0, 1, 5, 5)));
        Assert.Equal(2, stack.Depth);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
