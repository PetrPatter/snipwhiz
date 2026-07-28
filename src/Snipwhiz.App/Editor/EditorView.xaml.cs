using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.App.Editor.Tools;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Project;
using Snipwhiz.Core.Scene;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Editor;

/// <summary>
/// The editor: a tool rail, a canvas, and the keyboard.
///
/// <para>A view rather than a window. The shell switches between Library and Edit
/// at the top, which is one window in the taskbar, one Mica surface, one Escape
/// chain and one lifetime. It also replaces spec §4.15's pull-up sheet — the sheet
/// existed to avoid a second window, and a view switch avoids it more cheaply.</para>
///
/// <para><b>There is no dirty state anywhere in this class</b>, deliberately.
/// Leaving the view saves; so does switching documents. That removes the
/// Save/Don't-save/Cancel path, the flag threaded through every exit, and the
/// question of what Copy does on unsaved work — spec §4.14.</para>
/// </summary>
public partial class EditorView : UserControl
{
    private readonly CaptureStore _store;
    private SceneDocument _document = new() { CaptureId = Guid.Empty };
    private UndoStack _undo;
    private CaptureRecord? _record;
    private ITool _tool = null!;

    public EditorView() : this(null!) { }   // designer only

    public EditorView(CaptureStore store)
    {
        InitializeComponent();
        _store = store;
        _undo = new UndoStack(_document);

        SelectToolButton.Click += (_, _) => UseSelect();
        RectToolButton.Click += (_, _) => UseShape(RectToolButton, () => new RectangleAnnotation());
        EllipseToolButton.Click += (_, _) => UseShape(EllipseToolButton, () => new EllipseAnnotation());
        LineToolButton.Click += (_, _) => UseShape(LineToolButton, () => new LineAnnotation());

        Canvas.SelectionChanged += RefreshStatus;
        Canvas.MouseLeftButtonDown += OnCanvasPress;
        Canvas.MouseMove += OnCanvasDrag;
        Canvas.MouseLeftButtonUp += OnCanvasRelease;
        Canvas.MouseWheel += (_, _) => RefreshStatus();

        SizeChanged += (_, _) => RefreshStatus();
    }

    /// <summary>
    /// The document should be written. Task 11 gives this a save pipeline; until
    /// then nothing listens and nothing is persisted.
    /// </summary>
    public event Action<CaptureRecord, SceneDocument, BitmapSource>? SaveRequested;

    private BitmapSource? _source;

    /// <summary>
    /// The document must be on disk before this call returns. Raised when the
    /// window is closing, where the normal background save would be killed with
    /// the process.
    /// </summary>
    public event Action<CaptureRecord, SceneDocument>? UrgentSaveRequested;

    /// <summary>Escape with nothing left to unwind. The shell decides where to go.</summary>
    public event Action? ExitRequested;

    public CaptureRecord? Record => _record;

    /// <summary>Points the editor at a capture, saving whatever was open before.</summary>
    public void Open(CaptureRecord record, BitmapSource source)
    {
        // By id, not by reference. The library hands over a fresh CaptureRecord
        // instance every time — records are values — so a reference comparison
        // treats re-opening the same capture as a document switch and saves a
        // document nobody changed. Harmless when the render works, and it
        // announced itself the moment the render did not.
        if (_record is not null && _record.Id != record.Id) Save();

        _record = record;
        _source = source;

        // The memory gate's negative control: keeps every source the editor is ever
        // given, modelling something in the app retaining them.
        if (Diagnostics.EditorMemoryVerification.BreakRelease)
            Diagnostics.EditorMemoryVerification.Retain(source);
        _document = LoadProject(record);
        _undo = new UndoStack(_document);

        Canvas.Load(source, _document);
        Canvas.ClearSelection();
        UseSelect();

        // Layout has to have happened before the viewport size means anything.
        Dispatcher.BeginInvoke(() => { Canvas.Fit(); RefreshStatus(); },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void Save()
    {
        if (_record is null || _source is null) return;
        SaveRequested?.Invoke(_record, _document, _source);
    }

    /// <summary>
    /// Points this view at the record the save produced, so a second save records
    /// paths against a row that already has them rather than re-deriving from a
    /// stale one.
    /// </summary>
    public void OnSaved(CaptureRecord saved)
    {
        if (_record?.Id == saved.Id) _record = saved;
    }

    /// <summary>Blocks until the work is on disk. Only for the closing path.</summary>
    public void SaveNow()
    {
        if (_record is null) return;
        UrgentSaveRequested?.Invoke(_record, _document);
    }

    /// <summary>Takes keyboard focus, so shortcuts reach the canvas rather than the grid.</summary>
    public void FocusCanvas() => Canvas.Focus();

    /// <summary>
    /// Lets go of the capture without saving, because it is being deleted.
    ///
    /// <para>Saving here would write a project and a render for a row that no longer
    /// exists. Nothing is lost by not saving: reaching Delete means going to the
    /// Library screen, and leaving the editor already saved.</para>
    /// </summary>
    public void Discard()
    {
        _record = null;
        _source = null;
        _document = new SceneDocument { CaptureId = Guid.Empty };
        _undo = new UndoStack(_document);
        Canvas.ClearSelection();
    }

    private SceneDocument LoadProject(CaptureRecord record)
    {
        var path = _store.Assets.Project(record.Id);
        if (!File.Exists(path)) return new SceneDocument { CaptureId = record.Id };

        try
        {
            return ProjectStore.Load(path);
        }
        catch (ProjectFormatException)
        {
            // The capture is intact and that is what matters. Refusing to open at
            // all would strand it; starting empty at least lets the user work.
            return new SceneDocument { CaptureId = record.Id };
        }
    }

    // ---- tools ------------------------------------------------------------

    private void UseSelect()
    {
        _tool = new SelectTool(Canvas, _document, _undo);
        SetToolChecked(SelectToolButton);
    }

    private void UseShape(ToggleButton button, Func<Annotation> create)
    {
        var shape = new ShapeTool(Canvas, _document, _undo, create, Cursors.Cross);
        // One shape per press of the tool, then back to selecting — the object you
        // just drew is almost always the one you want to adjust.
        shape.Finished += _ => UseSelect();
        _tool = shape;
        SetToolChecked(button);
    }

    private void SetToolChecked(ToggleButton? active)
    {
        foreach (var button in ToolButtons) button.IsChecked = ReferenceEquals(button, active);
        Canvas.Cursor = _tool.Cursor;
        RefreshStatus();
    }

    private ToggleButton[] ToolButtons =>
        [SelectToolButton, RectToolButton, EllipseToolButton, LineToolButton];

    // ---- pointer ----------------------------------------------------------

    private void OnCanvasPress(object sender, MouseButtonEventArgs e)
    {
        if (Canvas.IsPanning) return;
        Canvas.Focus();
        Canvas.CaptureMouse();
        _tool.OnPress(Canvas.ToImage(e.GetPosition(Canvas)), Keyboard.Modifiers);
    }

    private void OnCanvasDrag(object sender, MouseEventArgs e)
    {
        if (Canvas.IsPanning || e.LeftButton != MouseButtonState.Pressed) return;
        _tool.OnDrag(Canvas.ToImage(e.GetPosition(Canvas)), Keyboard.Modifiers);
        RefreshStatus();
    }

    private void OnCanvasRelease(object sender, MouseButtonEventArgs e)
    {
        if (!Canvas.IsMouseCaptured) return;
        Canvas.ReleaseMouseCapture();
        _tool.OnRelease(Canvas.ToImage(e.GetPosition(Canvas)), Keyboard.Modifiers);
        RefreshStatus();
    }

    // ---- keyboard ---------------------------------------------------------

    /// <summary>
    /// Handles a key, or says it did not.
    ///
    /// <para>Called by the shell rather than hooked directly, so one place decides
    /// whether the library or the editor sees a key — and Escape unwinds one level
    /// at a time: an active gesture, then the selection, then out of the view.</para>
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                _tool.Cancel();
                if (Canvas.Selection.Count > 0) Canvas.ClearSelection();
                else ExitRequested?.Invoke();
                break;

            case Key.Z when control: _undo.Undo(); AfterHistory(); break;
            case Key.Y when control: _undo.Redo(); AfterHistory(); break;

            case Key.S when control: Save(); break;
            case Key.D when control: Duplicate(); break;

            case Key.Delete or Key.Back: DeleteSelection(); break;

            case Key.D0 when control: Canvas.Fit(); break;
            case Key.D1 when control: Canvas.ActualSize(); break;

            case Key.OemOpenBrackets: Reorder(-1); break;
            case Key.OemCloseBrackets: Reorder(+1); break;

            case Key.V when !control: UseSelect(); break;
            case Key.R when !control: UseShape(RectToolButton, () => new RectangleAnnotation()); break;
            case Key.E when !control: UseShape(EllipseToolButton, () => new EllipseAnnotation()); break;
            case Key.L when !control: UseShape(LineToolButton, () => new LineAnnotation()); break;

            case Key.Left: Nudge(-Step(shift), 0); break;
            case Key.Right: Nudge(Step(shift), 0); break;
            case Key.Up: Nudge(0, -Step(shift)); break;
            case Key.Down: Nudge(0, Step(shift)); break;

            default: return false;
        }

        RefreshStatus();
        return true;
    }

    private static double Step(bool shift) => shift ? 10 : 1;

    private void AfterHistory()
    {
        // Undo can add or remove objects, so the visuals are rebuilt rather than
        // invalidated; the selection may now point at something that is gone.
        Canvas.Rebuild();
        Canvas.SetSelection(Canvas.Selection.Where(_document.Annotations.Contains));
    }

    /// <summary>
    /// Deliberately does <b>not</b> begin a gesture, so holding an arrow key stays
    /// one undo step. A drag or a delete begins its own.
    /// </summary>
    private void Nudge(double dx, double dy)
    {
        foreach (var annotation in Canvas.Selection)
        {
            var moved = annotation.Transform;
            moved.OffsetX += dx;
            moved.OffsetY += dy;
            _undo.Apply(new MoveAnnotation(annotation, annotation.Transform, moved));
            Canvas.Invalidate(annotation);
        }
        Canvas.RefreshOverlay();
    }

    private void DeleteSelection()
    {
        _undo.BeginGesture();
        foreach (var annotation in Canvas.Selection.ToList())
            _undo.Apply(new RemoveAnnotation(annotation));

        Canvas.ClearSelection();
        Canvas.Rebuild();
    }

    private void Duplicate()
    {
        _undo.BeginGesture();
        var copies = new List<Annotation>();
        foreach (var annotation in Canvas.Selection.OfType<RectangleAnnotation>())
        {
            var moved = annotation.Transform;
            moved.OffsetX += 16;
            moved.OffsetY += 16;
            var copy = new RectangleAnnotation
            {
                Size = annotation.Size,
                Transform = moved,
                Style = annotation.Style,
                ZIndex = annotation.ZIndex + 1,
            };
            _undo.Apply(new AddAnnotation(copy));
            copies.Add(copy);
        }

        Canvas.Rebuild();
        if (copies.Count > 0) Canvas.SetSelection(copies);
    }

    private void Reorder(int direction)
    {
        _undo.BeginGesture();
        foreach (var annotation in Canvas.Selection)
            _undo.Apply(new ReorderAnnotation(annotation, annotation.ZIndex, annotation.ZIndex + direction));

        Canvas.Rebuild();
    }

    private void RefreshStatus()
    {
        ZoomText.Text = $"{Canvas.Zoom * 100:F0}%";
        var count = _document.Annotations.Count;
        var selected = Canvas.Selection.Count;
        StatusText.Text = selected > 0
            ? $"{selected} of {count} selected"
            : count == 1 ? "1 object" : $"{count} objects";
    }
}
