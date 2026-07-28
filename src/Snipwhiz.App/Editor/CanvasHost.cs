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

    private readonly ContainerVisual _layers = new();

    public CanvasHost()
    {
        AddVisualChild(_layers);
        _layers.Children.Add(_root);
        _layers.Children.Add(_overlay);
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
    public Point ToImage(Point elementPoint) =>
        new((elementPoint.X - _pan.X) / _zoom, (elementPoint.Y - _pan.Y) / _zoom);

    /// <summary>Image pixels to element coordinates.</summary>
    public Point ToElement(Point imagePoint) =>
        new(imagePoint.X * _zoom + _pan.X, imagePoint.Y * _zoom + _pan.Y);

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

        var scale = Math.Min(ActualWidth / ImageSize.Width, ActualHeight / ImageSize.Height);
        SetZoomCentred(Math.Clamp(Math.Min(scale, 1.0), MinZoom, MaxZoom));
    }

    public void ActualSize() => SetZoomCentred(1);

    private void SetZoomCentred(double zoom)
    {
        if (_source is null) return;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _pan = new Vector(
            (ActualWidth - ImageSize.Width * _zoom) / 2,
            (ActualHeight - ImageSize.Height * _zoom) / 2);
        ApplyView();
    }

    private void ApplyView()
    {
        var view = Matrix.Identity;
        view.Scale(_zoom, _zoom);
        view.Translate(_pan.X, _pan.Y);
        _root.Transform = new MatrixTransform(view);
        // Handles are positioned from image space, so they move when the view does.
        RefreshOverlay();
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
