using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;

namespace Snipwhiz.App.Editor;

/// <summary>
/// A real <c>TextBox</c>, held over the annotation being typed into.
///
/// <para><b>Not a hand-rolled caret.</b> Selection, click-to-position, double-click
/// word select, shift-arrow, undo within the field, IME composition for anyone
/// typing a language that needs one, screen-reader support and the context menu are
/// all things a text editor is expected to have and none of them are worth
/// reimplementing on a <c>DrawingVisual</c>.</para>
///
/// <para>The annotation underneath keeps drawing its plate and stops drawing its
/// words, so there is exactly one set of glyphs on screen and the plate still grows
/// as you type.</para>
/// </summary>
internal sealed class TextEditingOverlay
{
    private readonly Canvas _layer;
    private readonly CanvasHost _canvas;
    private readonly TextBox _box;

    private TextAnnotation? _target;
    private string _original = "";
    private bool _ending;

    public TextEditingOverlay(Canvas layer, CanvasHost canvas)
    {
        _layer = layer;
        _canvas = canvas;

        _box = new TextBox
        {
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            // The plate is the annotation's, drawn underneath. A background here
            // would be a second plate, half a pixel off the first.
            Background = Brushes.Transparent,
            CaretBrush = Brushes.White,
            SelectionOpacity = 0.4,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0, 0),
            Visibility = Visibility.Collapsed,
        };

        _box.TextChanged += (_, _) => OnTyped();
        _box.PreviewKeyDown += OnKey;
        _box.LostKeyboardFocus += (_, _) => Commit();

        _layer.Children.Add(_box);
    }

    public bool IsEditing => _target is not null;

    public TextAnnotation? Target => _target;

    /// <summary>The typed string was accepted. Carries what it was before, for one undo entry.</summary>
    public event Action<TextAnnotation, GeometryState>? Committed;

    public void Begin(TextAnnotation target)
    {
        if (_target is not null) Commit();

        _target = target;
        _original = target.Text;
        _before = target.CaptureGeometry();

        target.IsBeingEdited = true;

        _box.Text = target.Text;
        _box.Visibility = Visibility.Visible;
        Reposition();

        _canvas.Invalidate(target);

        _box.Focus();
        _box.SelectAll();
    }

    private GeometryState? _before;

    /// <summary>Keeps the box over the object through zoom, pan, and the plate growing.</summary>
    public void Reposition()
    {
        if (_target is null) return;

        TextOverlayPlacement.Apply(_box, _target, _canvas.Zoom, _canvas.Pan);
    }

    private void OnTyped()
    {
        if (_target is null) return;

        // Straight onto the annotation rather than through a command: the whole
        // typing session becomes one undo entry when it commits, so a per-keystroke
        // command would only be something to collapse again.
        _target.Text = _box.Text;

        Reposition();
        _canvas.Invalidate(_target);
        _canvas.RefreshOverlay();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
        // Enter adds a line, because the tool is for captions and those wrap. Commit
        // is Ctrl+Enter or simply clicking away, which is what every canvas editor
        // that allows multi-line text does.
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Commit();
            e.Handled = true;
        }
    }

    /// <summary>Puts the string back as it was. Conventional Escape, and not itself an undo step.</summary>
    public void Cancel()
    {
        if (_target is null) return;

        var target = _target;
        target.Text = _original;
        End();

        _canvas.Invalidate(target);
        _canvas.RefreshOverlay();

        // An empty annotation that was never typed into is litter: it hit-tests, it
        // shows handles, and it renders as a bare plate the user did not ask for.
        if (target.Text.Length == 0) Discarded?.Invoke(target);
    }

    /// <summary>Cancelled with nothing ever typed, so the object should not survive.</summary>
    public event Action<TextAnnotation>? Discarded;

    public void Commit()
    {
        if (_target is null || _ending) return;

        var target = _target;
        var before = _before!;
        target.Text = _box.Text;
        End();

        _canvas.Invalidate(target);
        _canvas.RefreshOverlay();

        if (target.Text.Length == 0) { Discarded?.Invoke(target); return; }
        if (!Equals(before, target.CaptureGeometry())) Committed?.Invoke(target, before);
    }

    private void End()
    {
        _ending = true;
        try
        {
            if (_target is not null) _target.IsBeingEdited = false;
            _target = null;
            _before = null;
            _box.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _ending = false;
        }
    }
}
