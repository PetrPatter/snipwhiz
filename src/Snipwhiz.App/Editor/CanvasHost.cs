using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Scene;

namespace Snipwhiz.App.Editor;

/// <summary>
/// The drawing surface: the capture, the objects on it, and the zoom and pan that
/// decide which part you are looking at.
///
/// <para><b>One <see cref="DrawingVisual"/> per annotation, inside a
/// <see cref="ContainerVisual"/>.</b> An element per object would carry layout,
/// styles, templates and routed events for every rectangle on the canvas — the
/// obvious choice, and wrong at a hundred objects. Drawing everything in one
/// <c>OnRender</c> retains nothing, so any change repaints the whole scene. Retained
/// visuals give per-object invalidation and native hit-testing for the cost of
/// managing a child collection.</para>
///
/// <para>The view transform lives on the container, not on each child, so zooming
/// touches one object regardless of how many annotations exist — and annotation
/// geometry stays in image pixels exactly as stored.</para>
/// </summary>
public sealed class CanvasHost : FrameworkElement
{
    public const double MinZoom = 0.1;
    public const double MaxZoom = 8.0;

    private readonly ContainerVisual _root = new();
    private readonly DrawingVisual _backdrop = new();
    private readonly Dictionary<Annotation, DrawingVisual> _visuals = [];

    /// <summary>
    /// Handles and marquee, deliberately <b>outside</b> the view transform.
    ///
    /// <para>Drawn in element coordinates so a grab handle is the same size under
    /// the mouse at 10% and at 800%. Inside the transform they would scale with the
    /// image, and their stroke widths with them.</para>
    /// </summary>
    private readonly DrawingVisual _overlay = new();

    private BitmapSource? _source;
    private SceneDocument? _document;
    private double _zoom = 1;
    private Vector _pan;

    /// <summary>What <see cref="ApplyView"/> last sized the view to.</summary>
    private Size _viewedContent = Size.Empty;

    private readonly ContainerVisual _layers = new();

    /// <summary>
    /// Carries the crop's clip. Its own space is element space — it has no transform
    /// of its own — which is what makes the clip rectangle expressible at all;
    /// putting it on <see cref="_root"/> would clip in image space, before the view.
    /// </summary>
    private readonly ContainerVisual _scene = new();

    public CanvasHost()
    {
        AddVisualChild(_layers);
        _layers.Children.Add(_scene);
        _layers.Children.Add(_overlay);
        _scene.Children.Add(_root);
        _root.Children.Add(_backdrop);
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>The objects currently selected, topmost-last.</summary>
    public IReadOnlyList<Annotation> Selection => _selection;

    private readonly List<Annotation> _selection = [];

    /// <summary>Rubber band in element coordinates while one is being dragged.</summary>
    public Rect? Marquee { get; set; }

    public event Action? SelectionChanged;

    /// <summary>Zoom or pan changed. Anything positioned in element space has to move with it.</summary>
    public event Action? ViewChanged;

    public void SetSelection(IEnumerable<Annotation> annotations)
    {
        _selection.Clear();
        _selection.AddRange(annotations);
        RefreshOverlay();
        SelectionChanged?.Invoke();
    }

    public void ClearSelection() => SetSelection([]);

    /// <summary>Redraws handles and marquee. Cheap: one visual, whatever the scene holds.</summary>
    public void RefreshOverlay()
    {
        using var dc = _overlay.RenderOpen();
        SelectionOverlay.Render(dc, this, _selection, Marquee);
    }

    /// <summary>
    /// How many times a single annotation's visual has been re-rendered.
    ///
    /// <para>Diagnostic. "It felt smooth" is not evidence that invalidation is
    /// per-object — on a fast machine a full rebuild feels identical until the
    /// scene is large. This is what the canvas gate reads.</para>
    /// </summary>
    public int VisualRenderCount { get; private set; }

    public double Zoom => _zoom;

    public Vector Pan => _pan;

    public Size ImageSize => _source is null
        ? Size.Empty
        : new Size(_source.PixelWidth, _source.PixelHeight);

    /// <summary>
    /// Show the whole capture even where the document is cropped.
    ///
    /// <para>Set while the crop tool is active, so the part being cropped away is
    /// still there to drag back. Off, the outside is not dimmed — it is gone.</para>
    /// </summary>
    public bool ShowUncropped { get; set; }

    /// <summary>The crop the view is currently honouring, or null for the whole capture.</summary>
    private Rect? ViewCrop => ShowUncropped ? null : _document?.Crop;

    /// <summary>Top-left of what is being looked at, in image pixels.</summary>
    public Point ContentOrigin => ViewCrop?.Location ?? new Point(0, 0);

    /// <summary>How big the visible picture is — the crop when there is one.</summary>
    public Size ContentSize => ViewCrop?.Size ?? ImageSize;

    /// <summary>
    /// A pending crop rectangle to draw, in image space. Only the crop tool sets it,
    /// and only while it is active.
    /// </summary>
    public Rect? CropPreview { get; set; }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _layers;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Fills whatever it is given; panning replaces scrollbars, so the content
        // size is not the element's desired size.
        var width = double.IsInfinity(availableSize.Width) ? ImageSize.Width : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? ImageSize.Height : availableSize.Height;
        return new Size(width, height);
    }

    // ---- scene ------------------------------------------------------------

    /// <summary>Points the canvas at a capture and its annotations.</summary>
    public void Load(BitmapSource source, SceneDocument document)
    {
        _source = source;
        _document = document;

        using (var dc = _backdrop.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }

        Rebuild();
    }

    /// <summary>
    /// Re-creates the child visuals to match the document, in paint order.
    ///
    /// <para>For structural change only — an object added, removed, or moved in z.
    /// Changing what an object <i>looks like</i> goes through <see cref="Invalidate"/>,
    /// and the difference is the whole point of retained visuals.</para>
    /// </summary>
    public void Rebuild()
    {
        _root.Children.Clear();
        _root.Children.Add(_backdrop);
        _visuals.Clear();

        if (_document is null) return;

        foreach (var annotation in _document.InPaintOrder())
        {
            var visual = new DrawingVisual();
            Draw(annotation, visual);
            _visuals[annotation] = visual;
            _root.Children.Add(visual);
        }
    }

    /// <summary>
    /// The scene as the canvas is currently drawing it, at image scale.
    ///
    /// <para>For the WYSIWYG gate, and it renders the <i>same</i> visuals that are
    /// on screen rather than building new ones — a comparison against a fresh
    /// render would only prove the flattener agrees with itself.</para>
    ///
    /// <para>The view transform is set aside for the duration so the result is in
    /// image pixels, which is the space the flattener works in. Zoom and pan are
    /// how the scene is <i>looked at</i>, not what it is.</para>
    /// </summary>
    internal BitmapSource RenderSceneAtImageScale()
    {
        // The document's crop, not the view's: the flattener knows nothing about the
        // crop tool showing the outside, and the gate compares against the flattener.
        var crop = _document?.Crop;
        var origin = crop?.Location ?? new Point(0, 0);
        var size = crop?.Size ?? ImageSize;

        var view = _root.Transform;
        var offset = Matrix.Identity;
        offset.Translate(-origin.X, -origin.Y);
        _root.Transform = new MatrixTransform(offset);
        try
        {
            var target = new RenderTargetBitmap(
                (int)Math.Round(size.Width), (int)Math.Round(size.Height), 96, 96, PixelFormats.Pbgra32);
            target.Render(_root);
            target.Freeze();
            return target;
        }
        finally
        {
            _root.Transform = view;
        }
    }

    /// <summary>Re-renders one object. Nothing else in the scene is touched.</summary>
    public void Invalidate(Annotation annotation)
    {
        if (!_visuals.TryGetValue(annotation, out var visual)) return;
        Draw(annotation, visual);
    }

    private void Draw(Annotation annotation, DrawingVisual visual)
    {
        using var dc = visual.RenderOpen();
        annotation.Render(dc);
        VisualRenderCount++;
    }

    /// <summary>
    /// The topmost object at an image-space point, or null.
    ///
    /// <para>Walks the document rather than <c>VisualTreeHelper.HitTest</c>, because
    /// an unfilled shape has no pixels in its middle and the user still expects
    /// clicking there to select it. <see cref="Annotation.HitTest"/> owns that
    /// decision; letting WPF's geometry hit-testing own it would put the rule in a
    /// second place.</para>
    /// </summary>
    public Annotation? HitTest(Point imagePoint, double screenTolerance = 4)
    {
        if (_document is null) return null;
        var tolerance = ToImageLength(screenTolerance);

        foreach (var annotation in _document.InPaintOrder().Reverse())
        {
            if (annotation.HitTest(imagePoint, tolerance)) return annotation;
        }
        return null;
    }

    // ---- view -------------------------------------------------------------

    /// <summary>Element coordinates to image pixels.</summary>
    public Point ToImage(Point elementPoint)
    {
        var origin = ContentOrigin;
        return new((elementPoint.X - _pan.X) / _zoom + origin.X,
                   (elementPoint.Y - _pan.Y) / _zoom + origin.Y);
    }

    /// <summary>Image pixels to element coordinates.</summary>
    public Point ToElement(Point imagePoint)
    {
        var origin = ContentOrigin;
        return new((imagePoint.X - origin.X) * _zoom + _pan.X,
                   (imagePoint.Y - origin.Y) * _zoom + _pan.Y);
    }

    /// <summary>
    /// A length on screen, in image pixels.
    ///
    /// <para>Handle sizes and hit tolerances are perceptual — a grab handle must
    /// stay the same size under the mouse at every zoom — so they are specified in
    /// screen units and converted here. Everything else is stored in image space.</para>
    /// </summary>
    public double ToImageLength(double screenLength) => screenLength / _zoom;

    /// <summary>Zooms while holding one point still under the pointer.</summary>
    public void ZoomAt(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 1e-9) return;

        var image = ToImage(anchor);
        _zoom = zoom;
        // Solve for the pan that puts the same image point back under the anchor.
        _pan = new Vector(anchor.X - image.X * _zoom, anchor.Y - image.Y * _zoom);
        ApplyView();
    }

    public void PanBy(Vector delta)
    {
        _pan += delta;
        ApplyView();
    }

    /// <summary>
    /// Fits the capture in the viewport, never enlarging.
    ///
    /// <para>Blowing a small capture up to fill the window on open would be a
    /// screenshot tool showing you a blurrier version of your screenshot.</para>
    /// </summary>
    public void Fit()
    {
        if (_source is null || ActualWidth <= 0 || ActualHeight <= 0) return;

        var content = ContentSize;
        if (content.Width <= 0 || content.Height <= 0) return;

        var scale = Math.Min(ActualWidth / content.Width, ActualHeight / content.Height);
        SetZoomCentred(Math.Clamp(Math.Min(scale, 1.0), MinZoom, MaxZoom));
    }

    public void ActualSize() => SetZoomCentred(1);

    /// <summary>
    /// Re-fits if the document's crop changed underneath the view.
    ///
    /// <para>Undo and redo edit the document directly, and the crop is the one part
    /// of it the <i>view</i> is built from — <see cref="Rebuild"/> re-draws the
    /// objects but leaves the transform and the clip sized to the old crop, so
    /// undoing a crop looked like nothing had happened.</para>
    ///
    /// <para>Guarded on the size so undoing anything else — a move, a style change —
    /// does not yank the zoom the user set.</para>
    /// </summary>
    public void SyncView()
    {
        if (ContentSize != _viewedContent) Fit();
    }

    private void SetZoomCentred(double zoom)
    {
        if (_source is null) return;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var content = ContentSize;
        _pan = new Vector(
            (ActualWidth - content.Width * _zoom) / 2,
            (ActualHeight - content.Height * _zoom) / 2);
        ApplyView();
    }

    private void ApplyView()
    {
        var origin = ContentOrigin;

        var view = Matrix.Identity;
        // The crop shifts the picture; annotation geometry is in image space and
        // rides along, which is why cropping moves the objects with the picture
        // rather than sliding them across it. The flattener does the same translate.
        view.Translate(-origin.X, -origin.Y);
        view.Scale(_zoom, _zoom);
        view.Translate(_pan.X, _pan.Y);
        _root.Transform = new MatrixTransform(view);

        // Cosmetic only: it hides what falls outside the crop on a canvas element
        // larger than the picture. The exported bitmap is crop-sized, so the
        // flattener needs no equivalent — the clip there is the bitmap's own edge.
        var content = ContentSize;
        _viewedContent = content;
        var clip = new RectangleGeometry(new Rect(
            _pan.X, _pan.Y, content.Width * _zoom, content.Height * _zoom));
        clip.Freeze();
        _scene.Clip = clip;
        // Handles are positioned from image space, so they move when the view does.
        RefreshOverlay();
        ViewChanged?.Invoke();
    }

    // ---- navigation input -------------------------------------------------

    private Point _panFrom;
    private bool _panning;

    /// <summary>Whether the pointer is currently panning, so tools stay out of the way.</summary>
    public bool IsPanning => _panning;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        // Plain wheel is left alone: the editor has no scrollbars, and a bare
        // wheel that zooms surprises anyone who reached for it to scroll.
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        // Multiplicative, so each notch feels the same at 10% as at 800%.
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        ZoomAt(_zoom * factor, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        var wantsPan = e.ChangedButton == MouseButton.Middle
            || (e.ChangedButton == MouseButton.Left
                && Keyboard.IsKeyDown(Key.Space));
        if (!wantsPan) return;

        _panning = true;
        _panFrom = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_panning) return;

        var now = e.GetPosition(this);
        PanBy(now - _panFrom);
        _panFrom = now;
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_panning) return;

        _panning = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }
}
