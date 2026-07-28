namespace Snipwhiz.Core.Scene;

/// <summary>
/// The editor's history.
///
/// <para><b>Bounded by count, not bytes</b> — see <see cref="ISceneCommand"/> for
/// why that is safe. Fifty entries of a few structs each is nothing, and a limit
/// expressed in entries is one a user can reason about.</para>
///
/// <para>History does not survive closing the editor (spec §4.14). The capture is
/// immutable and every annotation stays editable, so nothing is unrecoverable —
/// a mistake is corrected by editing again.</para>
/// </summary>
public sealed class UndoStack(SceneDocument document, int depth = 50)
{
    private readonly List<ISceneCommand> _done = [];
    private readonly List<ISceneCommand> _undone = [];

    /// <summary>
    /// The entry the current gesture is still filling in, or null between gestures.
    ///
    /// <para>Absorbing has to be bounded by the gesture, not by the object. Without
    /// this, an <c>AddAnnotation</c> swallows every later change to the shape it
    /// created — so rotating a rectangle and pressing Ctrl+Z deletes the rectangle
    /// instead of unrotating it — and a second drag of the same object folds into
    /// the first, making one Ctrl+Z undo both.</para>
    /// </summary>
    private ISceneCommand? _open;

    public bool CanUndo => _done.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    public int Depth => _done.Count;

    /// <summary>
    /// Runs a command and records it.
    ///
    /// <para>If the entry on top can absorb it — the same object being dragged, the
    /// same slider being moved — the change still applies but no new entry appears.</para>
    /// </summary>
    /// <summary>
    /// Starts a new user action. Everything applied until the next call may fold
    /// into one undo step; nothing older can.
    ///
    /// <para>Called when a gesture starts — a press, a delete, a duplicate. Not
    /// called between arrow-key nudges, so holding an arrow stays one step.</para>
    /// </summary>
    public void BeginGesture() => _open = null;

    public void Apply(ISceneCommand command)
    {
        command.Do(document);

        // A new action makes the redo branch unreachable. Keeping it would let
        // Ctrl+Y replay an edit against a scene that has since diverged.
        _undone.Clear();

        if (_open is not null && _open.TryAbsorb(command)) return;

        _done.Add(command);
        if (_done.Count > depth) _done.RemoveAt(0);
        _open = command;
    }

    public void Undo()
    {
        if (!CanUndo) return;
        // Whatever gesture was open is over; the next change must not fold into a
        // command that has just been taken back.
        _open = null;
        var command = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        command.Undo(document);
        _undone.Add(command);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _open = null;
        var command = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        command.Do(document);
        _done.Add(command);
    }

    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        _open = null;
    }
}
