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

// The field named Canvas is the drawing surface, so the panel type needs saying
// out loud wherever its attached properties are set.
using WpfCanvas = System.Windows.Controls.Canvas;

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
    private readonly ToolDefaults _defaults;
    private TextEditingOverlay _text = null!;

    public EditorView() : this(null!, new Core.Settings(), Path.GetTempPath()) { }   // designer only

    public EditorView(CaptureStore store, Core.Settings settings, string root)
    {
        InitializeComponent();
        _store = store;
        _defaults = new ToolDefaults(settings, root);
        _undo = new UndoStack(_document);
        BuildPill();
        BuildTextEditing();

        SelectToolButton.Click += (_, _) => UseSelect();
        RectToolButton.Click += (_, _) => UseShape(RectToolButton, () => new RectangleAnnotation());
        EllipseToolButton.Click += (_, _) => UseShape(EllipseToolButton, () => new EllipseAnnotation());
        LineToolButton.Click += (_, _) => UseShape(LineToolButton, () => new LineAnnotation());
        ArrowToolButton.Click += (_, _) => UseShape(ArrowToolButton, () => new ArrowAnnotation());
        HighlightToolButton.Click += (_, _) => UseShape(HighlightToolButton, () => new HighlightAnnotation());
        TextToolButton.Click += (_, _) => UseShape(TextToolButton, () => new TextAnnotation());
        CalloutToolButton.Click += (_, _) => UseShape(CalloutToolButton, () => new CalloutAnnotation());
        StepToolButton.Click += (_, _) => UseShape(StepToolButton, () => new StepAnnotation());
        MagnifyToolButton.Click += (_, _) => UseShape(MagnifyToolButton, () => new MagnifyAnnotation());
        SpotlightToolButton.Click += (_, _) => UseShape(SpotlightToolButton, () => new SpotlightAnnotation());
        BlurToolButton.Click += (_, _) => UseShape(BlurToolButton, () => new BlurAnnotation());
        PixelateToolButton.Click += (_, _) => UseShape(PixelateToolButton, () => new PixelateAnnotation());
        CropToolButton.Click += (_, _) => UseCrop();

        Canvas.SelectionChanged += RefreshStatus;
        Canvas.ViewChanged += RefreshStatus;   // panning moves the pill too
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
        // Before anything reads the document. A text object mid-edit is drawing its
        // plate and not its words, so flattening now would export a caption-shaped
        // hole where the caption is.
        _text.Commit();

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
        _text.Commit();
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

    private void UseSelect() => SwitchTool(new SelectTool(Canvas, _document, _undo), SelectToolButton);

    private void UseCrop() => SwitchTool(new CropTool(Canvas, _document, _undo), CropToolButton);

    /// <summary>
    /// Puts one tool down and picks another up.
    ///
    /// <para>Crop is the first tool with state outside the document — it makes the
    /// canvas show the part being cropped away — so leaving it has to mean
    /// something. Routed through one place rather than remembered at each of the
    /// eight call sites that change tools.</para>
    /// </summary>
    private void SwitchTool(ITool next, ToggleButton button)
    {
        if (_tool is CropTool leaving) leaving.Deactivate();

        _tool = next;
        if (next is CropTool entering) entering.Activate();

        SetToolChecked(button);
    }

    private void UseShape(ToggleButton button, Func<Annotation> create)
    {
        // The remembered style is applied to each new object rather than baked into
        // the factory, so the tool picks up a change made since it was selected.
        var shape = new ShapeTool(
            Canvas, _document, _undo, () => _defaults.Apply(create()), Cursors.Cross);
        // One shape per press of the tool, then back to selecting — the object you
        // just drew is almost always the one you want to adjust.
        shape.Finished += created =>
        {
            // The object decides. A step badge is placed several in a row, so the
            // tool stays; everything else is drawn once and then adjusted.
            if (!created.PlacesRepeatedly) UseSelect();
            // A text object is created empty and is useless until it says something,
            // so placing one goes straight into typing rather than leaving a bare
            // plate selected and waiting to be discovered.
            if (created is TextAnnotation text) _text.Begin(text);
        };
        SwitchTool(shape, button);
    }

    private void SetToolChecked(ToggleButton? active)
    {
        foreach (var button in ToolButtons) button.IsChecked = ReferenceEquals(button, active);
        Canvas.Cursor = _tool.Cursor;
        RefreshStatus();
    }

    /// <summary>
    /// Read off the rail rather than listed here.
    ///
    /// <para>A hand-written list is a second place to remember a new tool, and it
    /// was already wrong: arrow and highlight were missing, so their buttons never
    /// lit from a keyboard shortcut and never cleared when another tool was picked.
    /// Clicking hid it, because a <see cref="ToggleButton"/> flips itself.</para>
    /// </summary>
    private IEnumerable<ToggleButton> ToolButtons => ToolRail.Children.OfType<ToggleButton>();

    // ---- text editing -----------------------------------------------------

    private void BuildTextEditing()
    {
        _text = new TextEditingOverlay(PillLayer, Canvas);

        // One undo entry for a whole typing session. Every keystroke already went
        // straight onto the annotation so the plate could grow as you type; this is
        // what makes Ctrl+Z afterwards restore the string you started with rather
        // than remove one letter.
        _text.Committed += (target, before) =>
        {
            _undo.BeginGesture();
            _undo.Apply(new ReshapeAnnotation(target, before, target.CaptureGeometry()));
            RefreshStatus();
        };

        // Placed but never typed into. Left alone it is an invisible-ish plate that
        // hit-tests and shows handles, which reads as the app having put something
        // there by accident — which it did.
        _text.Discarded += target =>
        {
            _undo.BeginGesture();
            _undo.Apply(new RemoveAnnotation(target));
            Canvas.ClearSelection();
            Canvas.Rebuild();
            RefreshStatus();
        };
    }

    // ---- the style pill ---------------------------------------------------

    /// <summary>
    /// Seven colours, not a colour picker. The pill is meant to be used without
    /// looking at it; a full picker belongs behind one of these, later.
    /// </summary>
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xE5, 0x48, 0x4D),   // the default accent
        Color.FromRgb(0xE8, 0x83, 0x3A),
        Color.FromRgb(0xFF, 0xE0, 0x2B),   // the highlighter's yellow
        Color.FromRgb(0x2F, 0xB3, 0x44),
        Color.FromRgb(0x3B, 0x82, 0xF6),
        Color.FromRgb(0xF5, 0xF2, 0xEC),
        Color.FromRgb(0x1C, 0x1B, 0x1A),
    ];

    /// <summary>Clears the rotate handle, which sits 26px above the top edge.</summary>
    private const double PillGap = 38;

    private const double PillEdge = 8;

    /// <summary>
    /// True while the pill is being set <i>from</i> the selection. Without it the
    /// slider's own ValueChanged would echo straight back as an edit, so selecting
    /// an object would restyle it to the width it already had.
    /// </summary>
    private bool _syncingPill;

    private void BuildPill()
    {
        foreach (var colour in Palette)
        {
            var fill = new SolidColorBrush(colour);
            fill.Freeze();

            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = fill,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xF5, 0xF2, 0xEC)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 5, 0),
                Cursor = Cursors.Hand,
                ToolTip = ColourHex.Write(colour),
            };

            // The object decides what being made a colour means for it. Asking the
            // toolbar to know would put the rule that broke text in the one place
            // that happens to recolour things today.
            swatch.MouseLeftButtonDown += (_, _) => Restyle((_, target) => target.Recoloured(colour));

            SwatchRow.Children.Add(swatch);
        }

        WidthSlider.ValueChanged += (_, e) =>
        {
            WidthText.Text = $"{e.NewValue:F0}";
            ApplySize(e.NewValue);
        };

        // One undo entry per gesture. RestyleAnnotation absorbs consecutive restyles
        // of the same object, so without a boundary here two separate swatch clicks
        // would collapse into one step; with it, a slider drag is still one.
        StylePill.PreviewMouseLeftButtonDown += (_, _) => _undo.BeginGesture();

        // Persisted at the end of the gesture. Remember() runs on every tick;
        // twenty file writes for one slider drag is not worth the immediacy.
        StylePill.PreviewMouseLeftButtonUp += (_, _) => _defaults.Persist();
    }

    private void Restyle(Func<AnnotationStyle, Annotation, AnnotationStyle> change)
    {
        if (_syncingPill || Canvas.Selection.Count != 1) return;

        var target = Canvas.Selection[0];
        var before = target.Style;
        var after = change(before, target);
        if (after == before) return;   // a record, so this is value equality

        _undo.Apply(new RestyleAnnotation(target, before, after));
        Canvas.Invalidate(target);
        _defaults.Remember(target);
    }

    /// <summary>
    /// Moves the size control, and records it against whichever half of the object
    /// actually changed.
    ///
    /// <para>No type switch: a shape's size lives in its style and text's lives in
    /// its geometry, so this sets the one number and then looks at what moved. Both
    /// commands absorb, so a slider drag is still one undo entry either way.</para>
    /// </summary>
    private void ApplySize(double value)
    {
        if (_syncingPill || Canvas.Selection.Count != 1) return;

        var target = Canvas.Selection[0];
        var styleBefore = target.Style;
        var geometryBefore = target.CaptureGeometry();

        target.SizeControl = value;

        if (target.Style != styleBefore)
        {
            _undo.Apply(new RestyleAnnotation(target, styleBefore, target.Style));
        }
        else if (!Equals(geometryBefore, target.CaptureGeometry()))
        {
            _undo.Apply(new ReshapeAnnotation(target, geometryBefore, target.CaptureGeometry()));
        }
        else
        {
            return;
        }

        Canvas.Invalidate(target);
        // Text changes size as well as appearance, so the handles and the pill's own
        // position have to follow it.
        Canvas.RefreshOverlay();
        _defaults.Remember(target);
        PositionPill(target);
    }

    private void UpdatePill()
    {
        // Single selection only, matching the handles. With several selected, one
        // RestyleAnnotation per object means Ctrl+Z unwinds the recolour an object
        // at a time, because the undo stack absorbs per annotation. Multi-select
        // styling belongs with multi-select transforms in phase E.
        if (Canvas.Selection.Count != 1)
        {
            StylePill.Visibility = Visibility.Collapsed;
            return;
        }

        var target = Canvas.Selection[0];

        var (min, max) = target.SizeControlRange;

        _syncingPill = true;
        try
        {
            // The range moves with the type: 0-24 reads as a stroke width and would
            // let someone drag a caption down to 0pt, which is a caption that has
            // vanished for the second time.
            WidthSlider.Minimum = min;
            WidthSlider.Maximum = max;
            WidthSlider.Value = Math.Clamp(target.SizeControl, min, max);
            WidthSlider.ToolTip = target.SizeControlLabel;
            WidthText.Text = $"{target.SizeControl:F0}";
        }
        finally
        {
            _syncingPill = false;
        }

        StylePill.Visibility = Visibility.Visible;
        PositionPill(target);
    }

    private static readonly HandleKind[] PillCorners =
        [HandleKind.TopLeft, HandleKind.TopRight, HandleKind.BottomRight, HandleKind.BottomLeft];

    private void PositionPill(Annotation target)
    {
        // The four corners pushed through the transform, not Annotation.Bounds. A
        // rotated object's axis-aligned bounds are larger than the object, and the
        // pill would drift away from the thing it is editing as you turn it.
        var box = Rect.Empty;
        foreach (var kind in PillCorners)
        {
            box.Union(Canvas.ToElement(Handles.ImagePosition(target, kind, 0)));
        }

        StylePill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = StylePill.DesiredSize;

        var x = box.X + (box.Width - size.Width) / 2;
        var y = box.Y - size.Height - PillGap;

        // Flipped below when above would leave the viewport, then clamped both ways:
        // a pill half off the side is no more usable than one off the top.
        if (y < PillEdge) y = box.Bottom + PillGap;

        x = Math.Clamp(x, PillEdge, Math.Max(PillEdge, PillLayer.ActualWidth - size.Width - PillEdge));
        y = Math.Clamp(y, PillEdge, Math.Max(PillEdge, PillLayer.ActualHeight - size.Height - PillEdge));

        WpfCanvas.SetLeft(StylePill, x);
        WpfCanvas.SetTop(StylePill, y);
    }

    // ---- pointer ----------------------------------------------------------

    private void OnCanvasPress(object sender, MouseButtonEventArgs e)
    {
        if (Canvas.IsPanning) return;

        var image = Canvas.ToImage(e.GetPosition(Canvas));

        // Double-click a caption to edit it, the way every canvas editor works.
        // Checked before the tool sees the press, or the select tool starts a drag
        // on the object the user is about to type into.
        if (e.ClickCount == 2 && Canvas.HitTest(image) is TextAnnotation existing)
        {
            Canvas.SetSelection([existing]);
            _text.Begin(existing);
            return;
        }

        // A press anywhere else ends the edit. Committing on lost focus alone would
        // miss it, because the canvas is not always what takes focus next.
        _text.Commit();

        Canvas.Focus();
        Canvas.CaptureMouse();
        _tool.OnPress(image, Keyboard.Modifiers);
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
        // Nothing while a text box has the keyboard. Every bare-letter shortcut
        // below is also a letter someone is trying to type, and the window's KeyDown
        // reaches here whether or not a TextBox is focused — a TextBox does not mark
        // ordinary characters handled. Escape is already dealt with by the overlay's
        // own PreviewKeyDown, which tunnels before this bubbles.
        if (_text.IsEditing) return false;

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
            case Key.A when !control: UseShape(ArrowToolButton, () => new ArrowAnnotation()); break;
            case Key.H when !control: UseShape(HighlightToolButton, () => new HighlightAnnotation()); break;
            case Key.T when !control: UseShape(TextToolButton, () => new TextAnnotation()); break;
            case Key.O when !control: UseShape(CalloutToolButton, () => new CalloutAnnotation()); break;
            case Key.N when !control: UseShape(StepToolButton, () => new StepAnnotation()); break;
            case Key.M when !control: UseShape(MagnifyToolButton, () => new MagnifyAnnotation()); break;
            case Key.S when !control: UseShape(SpotlightToolButton, () => new SpotlightAnnotation()); break;
            case Key.B when !control: UseShape(BlurToolButton, () => new BlurAnnotation()); break;
            case Key.P when !control: UseShape(PixelateToolButton, () => new PixelateAnnotation()); break;
            case Key.C when !control: UseCrop(); break;

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

        // The crop is the one thing undo can change that the view itself is built
        // from, so rebuilding the objects is not enough.
        Canvas.SyncView();
        if (_tool is CropTool crop) crop.SyncPreview();
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

        // Here rather than only on SelectionChanged: the pill has to follow the
        // object through a move, a resize, a rotate, a zoom and a window resize, and
        // every one of those already lands here.
        UpdatePill();
        _text.Reposition();
    }
}
