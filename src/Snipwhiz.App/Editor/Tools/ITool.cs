using System.Windows;
using System.Windows.Input;

namespace Snipwhiz.App.Editor.Tools;

/// <summary>
/// One way of using the pointer on the canvas.
///
/// <para>A tool produces commands; it never mutates the scene directly. That is
/// what keeps undo complete without every tool having to remember to record
/// anything — if it went through a command, it can be taken back.</para>
///
/// <para>Points arrive in <b>image</b> space, already converted, so no tool has to
/// know about zoom or pan.</para>
/// </summary>
internal interface ITool
{
    Cursor Cursor { get; }

    void OnPress(Point image, ModifierKeys modifiers);

    void OnDrag(Point image, ModifierKeys modifiers);

    void OnRelease(Point image, ModifierKeys modifiers);

    /// <summary>Abandons an in-progress gesture. Escape, or the window losing focus.</summary>
    void Cancel() { }
}
