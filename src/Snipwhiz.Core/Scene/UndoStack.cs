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

    public bool CanUndo => _done.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    public int Depth => _done.Count;

    /// <summary>
    /// Runs a command and records it.
    ///
    /// <para>If the entry on top can absorb it — the same object being dragged, the
    /// same slider being moved — the change still applies but no new entry appears.</para>
    /// </summary>
    public void Apply(ISceneCommand command)
    {
        command.Do(document);

        // A new action makes the redo branch unreachable. Keeping it would let
        // Ctrl+Y replay an edit against a scene that has since diverged.
        _undone.Clear();

        if (_done.Count > 0 && _done[^1].TryAbsorb(command)) return;

        _done.Add(command);
        if (_done.Count > depth) _done.RemoveAt(0);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var command = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        command.Undo(document);
        _undone.Add(command);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var command = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        command.Do(document);
        _done.Add(command);
    }

    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
    }
}
