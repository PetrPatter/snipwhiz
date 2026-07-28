using System.Windows.Media;
using Snipwhiz.Core.Annotations;

namespace Snipwhiz.Core.Scene;

/// <summary>
/// One reversible change to a scene.
///
/// <para><b>Commands hold deltas, never bitmaps.</b> A 4K capture is 33 MB decoded;
/// a snapshot-based stack twenty entries deep is 660 MB, which is precisely the
/// failure the library spent a session fixing. Everything here is a couple of
/// structs and a Guid, which is why the stack can be bounded by count.</para>
/// </summary>
public interface ISceneCommand
{
    void Do(SceneDocument document);

    void Undo(SceneDocument document);

    /// <summary>
    /// Fold a newer command of the same kind into this one, so a continuous
    /// gesture is a single undo step.
    ///
    /// <para>A drag that raises forty moves should not cost forty presses of
    /// Ctrl+Z. Absorbing lets the tool apply each step live — the scene stays
    /// correct at every frame — while the stack still sees one entry. The
    /// alternative is every tool buffering its own start state and applying once
    /// on release, which is the same logic written once per tool.</para>
    /// </summary>
    /// <returns>True if <paramref name="newer"/> was folded in and must not be pushed.</returns>
    bool TryAbsorb(ISceneCommand newer) => false;
}

/// <summary>Puts a new object on the scene.</summary>
public sealed class AddAnnotation(Annotation annotation) : ISceneCommand
{
    public Annotation Annotation => annotation;

    public void Do(SceneDocument document) => document.Annotations.Add(annotation);

    public void Undo(SceneDocument document) => document.Annotations.Remove(annotation);
}

/// <summary>
/// Takes an object off the scene, remembering where it was in the list so redo and
/// undo do not quietly reorder the document.
/// </summary>
public sealed class RemoveAnnotation(Annotation annotation) : ISceneCommand
{
    private int _index = -1;

    public void Do(SceneDocument document)
    {
        _index = document.Annotations.IndexOf(annotation);
        if (_index >= 0) document.Annotations.RemoveAt(_index);
    }

    public void Undo(SceneDocument document)
    {
        if (_index < 0) return;
        document.Annotations.Insert(Math.Min(_index, document.Annotations.Count), annotation);
    }
}

/// <summary>Moves or turns an object. Absorbs, so a drag is one undo step.</summary>
public sealed class MoveAnnotation(Annotation annotation, Matrix before, Matrix after) : ISceneCommand
{
    public Annotation Annotation => annotation;
    public Matrix After { get; private set; } = after;

    public void Do(SceneDocument document) => annotation.Transform = After;

    public void Undo(SceneDocument document) => annotation.Transform = before;

    public bool TryAbsorb(ISceneCommand newer)
    {
        if (newer is not MoveAnnotation move || !ReferenceEquals(move.Annotation, annotation)) return false;
        After = move.After;
        return true;
    }
}

/// <summary>
/// Resizes an object. Separate from <see cref="MoveAnnotation"/> because the
/// transform carries no scale — resizing edits the object's own geometry.
/// </summary>
public sealed class ReshapeAnnotation(Annotation annotation, GeometryState before, GeometryState after)
    : ISceneCommand
{
    public Annotation Annotation => annotation;
    public GeometryState After { get; private set; } = after;

    public void Do(SceneDocument document) => annotation.RestoreGeometry(After);

    public void Undo(SceneDocument document) => annotation.RestoreGeometry(before);

    public bool TryAbsorb(ISceneCommand newer)
    {
        if (newer is not ReshapeAnnotation reshape || !ReferenceEquals(reshape.Annotation, annotation)) return false;
        After = reshape.After;
        return true;
    }
}

/// <summary>Changes an object's appearance. Absorbs, so dragging a slider is one undo step.</summary>
public sealed class RestyleAnnotation(Annotation annotation, AnnotationStyle before, AnnotationStyle after)
    : ISceneCommand
{
    public Annotation Annotation => annotation;
    public AnnotationStyle After { get; private set; } = after;

    public void Do(SceneDocument document) => annotation.Style = After;

    public void Undo(SceneDocument document) => annotation.Style = before;

    public bool TryAbsorb(ISceneCommand newer)
    {
        if (newer is not RestyleAnnotation restyle || !ReferenceEquals(restyle.Annotation, annotation)) return false;
        After = restyle.After;
        return true;
    }
}

/// <summary>Send backward / bring forward.</summary>
public sealed class ReorderAnnotation(Annotation annotation, int before, int after) : ISceneCommand
{
    public void Do(SceneDocument document) => annotation.ZIndex = after;

    public void Undo(SceneDocument document) => annotation.ZIndex = before;
}
