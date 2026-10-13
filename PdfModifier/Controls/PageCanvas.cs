using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PdfModifier.Core;
using PdfModifier.Core.Operations;
using PdfModifier.ViewModels;

namespace PdfModifier.Controls;

/// <summary>
/// Shows one rendered page with what can be worked on drawn over it: highlighted text, the movable objects of the
/// active tool, and an area marked by dragging. All positions it reports are in visual page coordinates (points from
/// the page's top-left corner), independent of the zoom.
/// </summary>
public class PageCanvas : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<PageCanvas, IImage?>(nameof(Source));
    public static readonly StyledProperty<double> PageWidthProperty = AvaloniaProperty.Register<PageCanvas, double>(nameof(PageWidth));
    public static readonly StyledProperty<double> PageHeightProperty = AvaloniaProperty.Register<PageCanvas, double>(nameof(PageHeight));
    public static readonly StyledProperty<double> PointScaleProperty = AvaloniaProperty.Register<PageCanvas, double>(nameof(PointScale), 1);
    public static readonly StyledProperty<IEnumerable<Overlay>?> OverlaysProperty = AvaloniaProperty.Register<PageCanvas, IEnumerable<Overlay>?>(nameof(Overlays));
    public static readonly StyledProperty<Overlay?> SelectedOverlayProperty = AvaloniaProperty.Register<PageCanvas, Overlay?>(nameof(SelectedOverlay));
    public static readonly StyledProperty<CanvasMode> ModeProperty = AvaloniaProperty.Register<PageCanvas, CanvasMode>(nameof(Mode));
    public static readonly StyledProperty<IEnumerable<CanvasObject>?> ObjectsProperty = AvaloniaProperty.Register<PageCanvas, IEnumerable<CanvasObject>?>(nameof(Objects));

    public static readonly StyledProperty<CanvasObject?> SelectedObjectProperty =
        AvaloniaProperty.Register<PageCanvas, CanvasObject?>(nameof(SelectedObject), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<RectD?> RegionProperty =
        AvaloniaProperty.Register<PageCanvas, RectD?>(nameof(Region), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> DimOutsideRegionProperty = AvaloniaProperty.Register<PageCanvas, bool>(nameof(DimOutsideRegion));

    /// <summary>While set (the space bar is held), dragging with the left button moves the page whatever the tool.</summary>
    public static readonly StyledProperty<bool> GrabToPanProperty = AvaloniaProperty.Register<PageCanvas, bool>(nameof(GrabToPan));

    private const double HandleSize = 9, HandleReach = 8, RotateArm = 24, MinimumSize = 4;

    private static readonly Color TextColor = Color.Parse("#2F6FED");
    private static readonly Color ScanColor = Color.Parse("#D97A00");
    private static readonly IBrush Accent = new SolidColorBrush(TextColor);
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IPen HandlePen = new Pen(Accent, 1.5);
    private static readonly IPen SelectionPen = new Pen(Accent, 1.5);
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(TextColor, 0.6), 1, DashStyle.Dash);
    private static readonly IPen RegionPen = new Pen(Accent, 1.5, DashStyle.Dash);
    private static readonly IBrush RegionFill = new SolidColorBrush(TextColor, 0.12);
    private static readonly IBrush Dim = new SolidColorBrush(Colors.Black, 0.45);
    private static readonly IBrush FrameFill = new SolidColorBrush(TextColor, 0.10);
    private static readonly IBrush FrameLit = new SolidColorBrush(TextColor, 0.20);
    private static readonly IPen FramePen = new Pen(Accent, 2);
    private static readonly IPen FrameRim = new Pen(Brushes.White, 1.5);
    private static readonly Color DoubtColor = Color.Parse("#D9364A");
    private static readonly IBrush Doubt = new SolidColorBrush(DoubtColor);
    private static readonly IBrush DoubtFill = new SolidColorBrush(DoubtColor, 0.10);
    private static readonly IBrush DoubtLit = new SolidColorBrush(DoubtColor, 0.20);
    private static readonly IPen DoubtPen = new Pen(Doubt, 2, DashStyle.Dash);

    private enum Drag
    {
        None,
        Pan,
        Band,
        Click,
        Move,
        Resize,
        Rotate,
        MoveRegion,
        ResizeRegion
    }

    // One cursor of each kind for the life of the app: the pointer asks for one with every move.
    private static readonly Dictionary<StandardCursorType, Cursor> Cursors = new();

    private readonly HashSet<CanvasObject> _watched = [];
    private Overlay? _hoveredOverlay;
    private CanvasObject? _hoveredObject;

    private Drag _drag;
    private bool _moved;
    private Point _pressed, _current, _lastRoot;
    private (int X, int Y) _handle;
    private RectD _startBounds;
    private double _startRotation, _startAngle;

    static PageCanvas()
    {
        AffectsRender<PageCanvas>(SourceProperty, OverlaysProperty, SelectedOverlayProperty, PointScaleProperty, ObjectsProperty,
            SelectedObjectProperty, RegionProperty, DimOutsideRegionProperty, ModeProperty);
        AffectsMeasure<PageCanvas>(PageWidthProperty, PageHeightProperty, PointScaleProperty);
        FocusableProperty.OverrideDefaultValue<PageCanvas>(true);
    }

    public PageCanvas() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double PageWidth
    {
        get => GetValue(PageWidthProperty);
        set => SetValue(PageWidthProperty, value);
    }

    public double PageHeight
    {
        get => GetValue(PageHeightProperty);
        set => SetValue(PageHeightProperty, value);
    }

    /// <summary>Screen units per PDF point.</summary>
    public double PointScale
    {
        get => GetValue(PointScaleProperty);
        set => SetValue(PointScaleProperty, value);
    }

    public IEnumerable<Overlay>? Overlays
    {
        get => GetValue(OverlaysProperty);
        set => SetValue(OverlaysProperty, value);
    }

    public Overlay? SelectedOverlay
    {
        get => GetValue(SelectedOverlayProperty);
        set => SetValue(SelectedOverlayProperty, value);
    }

    public CanvasMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>Movable objects, bottom-most first.</summary>
    public IEnumerable<CanvasObject>? Objects
    {
        get => GetValue(ObjectsProperty);
        set => SetValue(ObjectsProperty, value);
    }

    public CanvasObject? SelectedObject
    {
        get => GetValue(SelectedObjectProperty);
        set => SetValue(SelectedObjectProperty, value);
    }

    /// <summary>The marked area that stays on the page in <see cref="CanvasMode.Area"/>; tools may also just show one.</summary>
    public RectD? Region
    {
        get => GetValue(RegionProperty);
        set => SetValue(RegionProperty, value);
    }

    /// <summary>Darkens everything outside <see cref="Region"/>, which shows what cropping to it would leave.</summary>
    public bool DimOutsideRegion
    {
        get => GetValue(DimOutsideRegionProperty);
        set => SetValue(DimOutsideRegionProperty, value);
    }

    public bool GrabToPan
    {
        get => GetValue(GrabToPanProperty);
        set => SetValue(GrabToPanProperty, value);
    }

    public event Action<Overlay?>? OverlayClicked;
    public event Action<double, double>? PageClicked;
    public event Action<RectD>? RegionSelected;

    /// <summary>
    /// An object is being moved, resized or turned: its new box and rotation, the factor by which it grew when resized
    /// as a whole, and whether the mouse button has been released.
    /// </summary>
    public event Action<CanvasObject, RectD, double, double, bool>? ObjectTransformed;

    public event Action<CanvasObject>? ObjectActivated;

    /// <summary>An object was clicked without being dragged.</summary>
    public event Action<CanvasObject>? ObjectClicked;

    /// <summary>The page is being dragged by this much, in screen units.</summary>
    public event Action<Vector>? Panned;

    // ---- keeping up with the collections ------------------------------------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OverlaysProperty)
        {
            if (change.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= OverlaysChanged;
            if (change.NewValue is INotifyCollectionChanged current) current.CollectionChanged += OverlaysChanged;
            _hoveredOverlay = null;
        }
        else if (change.Property == ObjectsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= ObjectsChanged;
            if (change.NewValue is INotifyCollectionChanged current) current.CollectionChanged += ObjectsChanged;
            WatchObjects();
        }
        else if (change.Property == ModeProperty || change.Property == GrabToPanProperty)
        {
            _hoveredOverlay = null;
            _hoveredObject = null;
            UpdateCursor(_current);
        }
    }

    private void OverlaysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _hoveredOverlay = null;
        InvalidateVisual();
    }

    private void ObjectsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchObjects();
        InvalidateVisual();
    }

    /// <summary>Redraws whenever one of the objects changes its place or its picture.</summary>
    private void WatchObjects()
    {
        var present = Objects?.ToHashSet() ?? [];
        foreach (var gone in _watched.Where(o => !present.Contains(o)).ToList())
        {
            gone.PropertyChanged -= ObjectChanged;
            _watched.Remove(gone);
        }

        foreach (var added in present.Where(o => _watched.Add(o))) added.PropertyChanged += ObjectChanged;
        if (_hoveredObject != null && !present.Contains(_hoveredObject)) _hoveredObject = null;
    }

    private void ObjectChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(PageWidth * PointScale, PageHeight * PointScale);

    // ---- drawing ------------------------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 1 || bounds.Height < 1) return;
        context.FillRectangle(Brushes.White, bounds);
        if (Source is { } image) context.DrawImage(image, new Rect(image.Size), bounds);

        if (Objects != null)
            // Like the page itself, the objects end at its edge.
            using (context.PushClip(bounds))
                foreach (var obj in Objects)
                    DrawObject(context, obj);

        // Also where a frame is drawn on the page: what the frame takes in is marked the same way.
        if (Overlays != null && Mode is CanvasMode.Select or CanvasMode.Area)
            foreach (var overlay in Overlays)
            {
                var color = overlay.Doubtful ? DoubtColor : overlay.IsScan ? ScanColor : TextColor;
                var selected = ReferenceEquals(overlay, SelectedOverlay);
                var hovered = ReferenceEquals(overlay, _hoveredOverlay);
                var fill = new SolidColorBrush(color, selected ? 0.32 : hovered ? 0.22 : 0.08);
                var pen = new Pen(new SolidColorBrush(color, selected || hovered ? 1 : 0.55), selected ? 2 : 1);
                context.DrawRectangle(fill, pen, ToScreen(overlay.Bounds).Inflate(1));
            }

        DrawRegion(context, bounds);

        if (Mode == CanvasMode.Select)
        {
            if (_hoveredObject is { } hovered && !ReferenceEquals(hovered, SelectedObject) && _drag == Drag.None)
                DrawOutline(context, hovered.Model, HoverPen);
            if (SelectedObject is { } selection) DrawSelection(context, selection);
        }

        if (_drag == Drag.Band && _moved)
            context.DrawRectangle(RegionFill, RegionPen, new Rect(_pressed, _current).Normalize().Intersect(bounds));
    }

    private void DrawObject(DrawingContext context, CanvasObject obj)
    {
        var model = obj.Model;
        var box = ToScreen(model.Bounds);
        using var turned = context.PushTransform(Turn(box.Center, model.Rotation));
        switch (model)
        {
            case PagePatch patch:
                context.FillRectangle(new SolidColorBrush(Color.FromUInt32(patch.Color | 0xFF000000)), box);
                break;
            case SheetArea area:
                DrawFrame(context, area, box, ReferenceEquals(obj, SelectedObject) || ReferenceEquals(obj, _hoveredObject));
                break;
            case PageImage picture when obj.Picture is { } pixels:
                using (context.PushOpacity(Math.Clamp(picture.Opacity, 0, 1)))
                    context.DrawImage(pixels, new Rect(pixels.Size), box);
                break;
            case PageText when obj.Picture is { } letters:
                context.DrawImage(letters, new Rect(letters.Size), box.Inflate(obj.PicturePadding * PointScale));
                break;
        }
    }

    /// <summary>A frame around a page on a sheet, with its number in a disc at its top-left corner.</summary>
    private static void DrawFrame(DrawingContext context, SheetArea area, Rect box, bool lit)
    {
        // Red and dashed where the frame is only a guess.
        if (area.Doubtful) context.DrawRectangle(lit ? DoubtLit : DoubtFill, DoubtPen, box);
        else context.DrawRectangle(lit ? FrameLit : FrameFill, FramePen, box);
        if (area.Label.Length == 0) return;
        var radius = Math.Clamp(Math.Min(box.Width, box.Height) / 7, 11, 20);
        var middle = new Point(box.Left + radius + 6, box.Top + radius + 6);
        context.DrawEllipse(area.Doubtful ? Doubt : Accent, FrameRim, middle, radius, radius);
        var number = new FormattedText(area.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), radius * 1.15, Brushes.White);
        context.DrawText(number, new Point(middle.X - number.Width / 2, middle.Y - number.Height / 2));
    }

    /// <summary>Turns what is drawn around a point, counter-clockwise as seen (screen y points down).</summary>
    private static Matrix Turn(Point center, double degrees) =>
        degrees == 0
            ? Matrix.Identity
            : Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(-degrees * Math.PI / 180) *
              Matrix.CreateTranslation(center.X, center.Y);

    private void DrawOutline(DrawingContext context, PageObject model, IPen pen)
    {
        var box = ToScreen(model.Bounds);
        using var turned = context.PushTransform(Turn(box.Center, model.Rotation));
        context.DrawRectangle(null, pen, box);
    }

    private void DrawSelection(DrawingContext context, CanvasObject obj)
    {
        var model = obj.Model;
        DrawOutline(context, model, SelectionPen);
        foreach (var (x, y) in Handles(model))
        {
            var at = HandlePoint(model, x, y);
            context.DrawRectangle(HandleFill, HandlePen, new Rect(at.X - HandleSize / 2, at.Y - HandleSize / 2, HandleSize, HandleSize));
        }

        if (!Turns(model)) return;
        // The knob for turning stands off the top edge on a short stalk.
        var top = HandlePoint(model, 0, -1);
        var knob = RotatePoint(model);
        context.DrawLine(SelectionPen, top, knob);
        context.DrawEllipse(HandleFill, HandlePen, knob, HandleSize / 2 + 1, HandleSize / 2 + 1);
    }

    private void DrawRegion(DrawingContext context, Rect bounds)
    {
        if (Region is not { } region) return;
        var area = ToScreen(region).Intersect(bounds);
        if (DimOutsideRegion)
        {
            context.FillRectangle(Dim, new Rect(0, 0, bounds.Width, area.Top));
            context.FillRectangle(Dim, new Rect(0, area.Bottom, bounds.Width, Math.Max(0, bounds.Height - area.Bottom)));
            context.FillRectangle(Dim, new Rect(0, area.Top, area.Left, area.Height));
            context.FillRectangle(Dim, new Rect(area.Right, area.Top, Math.Max(0, bounds.Width - area.Right), area.Height));
        }

        context.DrawRectangle(DimOutsideRegion ? null : RegionFill, RegionPen, area);
        if (Mode != CanvasMode.Area) return;
        foreach (var (x, y) in AllHandles)
        {
            var at = new Point(area.Center.X + x * area.Width / 2, area.Center.Y + y * area.Height / 2);
            context.DrawRectangle(HandleFill, HandlePen, new Rect(at.X - HandleSize / 2, at.Y - HandleSize / 2, HandleSize, HandleSize));
        }
    }

    // ---- geometry -----------------------------------------------------------------------------------------------

    private static readonly (int X, int Y)[] AllHandles = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];
    private static readonly (int X, int Y)[] TextHandles = [(-1, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (1, 1)];

    /// <summary>
    /// Text boxes have no handles on their top and bottom edges: their height follows from the text, so there is
    /// nothing to drag there.
    /// </summary>
    private static (int X, int Y)[] Handles(PageObject model) => model is PageText ? TextHandles : AllHandles;

    /// <summary>Frames around pages on a sheet stay upright: what is cut out of a sheet is cut straight.</summary>
    private static bool Turns(PageObject model) => model is not SheetArea;

    private Rect ToScreen(RectD r)
    {
        var s = PointScale;
        return new Rect(r.Left * s, r.Top * s, Math.Max(0, r.Width * s), Math.Max(0, r.Height * s));
    }

    /// <summary>A point given relative to the middle of a turned box, in page coordinates.</summary>
    private static (double X, double Y) FromBox(RectD box, double rotation, double u, double v)
    {
        var a = rotation * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        return (box.CenterX + cos * u + sin * v, box.CenterY - sin * u + cos * v);
    }

    /// <summary>A page point seen from the middle of a turned box, along the box's own axes.</summary>
    private static (double U, double V) IntoBox(RectD box, double rotation, double x, double y)
    {
        var a = rotation * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a), dx = x - box.CenterX, dy = y - box.CenterY;
        return (cos * dx - sin * dy, sin * dx + cos * dy);
    }

    private Point HandlePoint(PageObject model, int x, int y)
    {
        var (px, py) = FromBox(model.Bounds, model.Rotation, x * model.Bounds.Width / 2, y * model.Bounds.Height / 2);
        return new Point(px * PointScale, py * PointScale);
    }

    private Point RotatePoint(PageObject model)
    {
        var (px, py) = FromBox(model.Bounds, model.Rotation, 0, -model.Bounds.Height / 2 - RotateArm / PointScale);
        return new Point(px * PointScale, py * PointScale);
    }

    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= HandleReach && Math.Abs(a.Y - b.Y) <= HandleReach;

    private (int X, int Y)? HitHandle(PageObject model, Point position)
    {
        foreach (var (x, y) in Handles(model))
            if (Near(HandlePoint(model, x, y), position))
                return (x, y);
        return null;
    }

    private (int X, int Y)? HitRegionHandle(Point position)
    {
        if (Region is not { } region) return null;
        var area = ToScreen(region);
        foreach (var (x, y) in AllHandles)
            if (Near(new Point(area.Center.X + x * area.Width / 2, area.Center.Y + y * area.Height / 2), position))
                return (x, y);
        return null;
    }

    /// <summary>The top-most object under the pointer.</summary>
    private CanvasObject? HitObject(Point position)
    {
        if (Objects == null) return null;
        double x = position.X / PointScale, y = position.Y / PointScale, slack = 3 / PointScale;
        foreach (var obj in Objects.Reverse())
        {
            var (u, v) = IntoBox(obj.Model.Bounds, obj.Model.Rotation, x, y);
            if (Math.Abs(u) <= obj.Model.Bounds.Width / 2 + slack && Math.Abs(v) <= obj.Model.Bounds.Height / 2 + slack) return obj;
        }

        return null;
    }

    /// <summary>The smallest highlight under the pointer, so nested or overlapping areas stay reachable.</summary>
    private Overlay? HitOverlay(Point position)
    {
        if (Overlays == null) return null;
        double x = position.X / PointScale, y = position.Y / PointScale;
        return Overlays
            .Where(o => o.Bounds.Contains(x, y))
            .MinBy(o => o.Bounds.Width * o.Bounds.Height);
    }

    // ---- mouse --------------------------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_drag != Drag.None) return;
        var point = e.GetCurrentPoint(this);
        var position = point.Position;
        var left = point.Properties.IsLeftButtonPressed;
        if (!left && !point.Properties.IsMiddleButtonPressed) return;
        Focus();

        _pressed = _current = position;
        _lastRoot = e.GetPosition(null);
        _moved = false;
        _drag = Begin(position, left, e.ClickCount);
        if (_drag == Drag.None) return;
        e.Pointer.Capture(this);
        e.Handled = true;
        UpdateCursor(position);
    }

    private Drag Begin(Point position, bool left, int clicks)
    {
        if (!left || GrabToPan) return Drag.Pan;
        switch (Mode)
        {
            case CanvasMode.Click or CanvasMode.Pick:
                return Drag.Click;
            case CanvasMode.Region:
                return Drag.Band;
            case CanvasMode.Area:
                if (Region is not { } region) return Drag.Band;
                _startBounds = region;
                if (HitRegionHandle(position) is { } grip)
                {
                    _handle = grip;
                    return Drag.ResizeRegion;
                }

                return ToScreen(region).Contains(position) ? Drag.MoveRegion : Drag.Band;
        }

        // Select: the handles of what is selected come first, then objects, then highlighted text, then the bare page.
        if (SelectedObject is { } selected)
        {
            if (Turns(selected.Model) && Near(RotatePoint(selected.Model), position))
            {
                var center = ToScreen(selected.Model.Bounds).Center;
                (_startBounds, _startRotation) = (selected.Model.Bounds, selected.Model.Rotation);
                _startAngle = Math.Atan2(-(position.Y - center.Y), position.X - center.X);
                return Drag.Rotate;
            }

            if (HitHandle(selected.Model, position) is { } grip)
            {
                _handle = grip;
                (_startBounds, _startRotation) = (selected.Model.Bounds, selected.Model.Rotation);
                return Drag.Resize;
            }
        }

        if (HitObject(position) is { } hit)
        {
            SelectedObject = hit;
            if (clicks >= 2)
            {
                ObjectActivated?.Invoke(hit);
                return Drag.None;
            }

            (_startBounds, _startRotation) = (hit.Model.Bounds, hit.Model.Rotation);
            return Drag.Move;
        }

        // Pressing on a highlighted line is a click on it; pressing anywhere else takes hold of the page.
        return HitOverlay(position) != null ? Drag.Click : Drag.Pan;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _current = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            Hover(_current);
            return;
        }

        if (!_moved && Math.Abs(_current.X - _pressed.X) <= 3 && Math.Abs(_current.Y - _pressed.Y) <= 3) return;
        _moved = true;
        // A press that was going to be a click and turns into a drag moves the page instead: where a click means
        // something (a place for a watermark, a line of text), the page can still be pulled about.
        if (_drag == Drag.Click)
        {
            _drag = Drag.Pan;
            UpdateCursor(_current);
        }
        var s = PointScale;
        double dx = (_current.X - _pressed.X) / s, dy = (_current.Y - _pressed.Y) / s;

        switch (_drag)
        {
            case Drag.Pan:
                // Measured against the window: the page itself moves under the pointer while it is dragged.
                var root = e.GetPosition(null);
                Panned?.Invoke(root - _lastRoot);
                _lastRoot = root;
                break;
            case Drag.Band:
                InvalidateVisual();
                break;
            case Drag.Move when SelectedObject is { } obj:
                ObjectTransformed?.Invoke(obj, Shift(_startBounds, dx, dy), _startRotation, 1, false);
                break;
            case Drag.Resize when SelectedObject is { } obj:
                var (bounds, scale) = Resize(obj.Model, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                ObjectTransformed?.Invoke(obj, bounds, _startRotation, scale, false);
                break;
            case Drag.Rotate when SelectedObject is { } obj:
                ObjectTransformed?.Invoke(obj, _startBounds, Rotation(e.KeyModifiers.HasFlag(KeyModifiers.Shift)), 1, false);
                break;
            case Drag.MoveRegion:
                Region = Inside(Shift(_startBounds, dx, dy), keepSize: true);
                break;
            case Drag.ResizeRegion:
                Region = Inside(Stretch(_startBounds, _handle, dx, dy), keepSize: false);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        var end = e.GetPosition(this);
        var s = PointScale;
        InvalidateVisual();

        switch (drag)
        {
            case Drag.Band when _moved:
                var area = new Rect(_pressed, end).Normalize().Intersect(new Rect(Bounds.Size));
                if (area.Width > 2 && area.Height > 2)
                {
                    var marked = new RectD(area.Left / s, area.Top / s, area.Right / s, area.Bottom / s);
                    if (Mode == CanvasMode.Area) Region = marked;
                    RegionSelected?.Invoke(marked);
                }

                break;
            case Drag.Band or Drag.Click when Mode != CanvasMode.Select:
                // A press without a drag is a click, also where a drag was expected: tools give it a meaning of its own.
                PageClicked?.Invoke(end.X / s, end.Y / s);
                break;
            case Drag.Click:
                OverlayClicked?.Invoke(HitOverlay(end));
                break;
            case Drag.Pan when !_moved && Mode == CanvasMode.Select && !GrabToPan:
                // A click on the bare page lets go of whatever was selected.
                SelectedObject = null;
                OverlayClicked?.Invoke(null);
                break;
            case Drag.Move or Drag.Resize or Drag.Rotate when _moved && SelectedObject is { } obj:
                ObjectTransformed?.Invoke(obj, obj.Model.Bounds, obj.Model.Rotation, 1, true);
                break;
            case Drag.Move when SelectedObject is { } clicked:
                ObjectClicked?.Invoke(clicked);
                break;
            case Drag.MoveRegion or Drag.ResizeRegion when _moved && Region is { } region:
                RegionSelected?.Invoke(region);
                break;
        }

        Hover(end);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredOverlay == null && _hoveredObject == null) return;
        _hoveredOverlay = null;
        _hoveredObject = null;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = Drag.None;
        InvalidateVisual();
    }

    private void Hover(Point position)
    {
        var overlay = Mode == CanvasMode.Select ? HitOverlay(position) : null;
        var obj = Mode == CanvasMode.Select ? HitObject(position) : null;
        if (obj != null) overlay = null;
        if (!ReferenceEquals(overlay, _hoveredOverlay) || !ReferenceEquals(obj, _hoveredObject))
        {
            (_hoveredOverlay, _hoveredObject) = (overlay, obj);
            InvalidateVisual();
        }

        UpdateCursor(position);
    }

    private void UpdateCursor(Point position)
    {
        var type = StandardCursorType.Arrow;
        if (_drag == Drag.Pan || GrabToPan) type = StandardCursorType.SizeAll;
        else
            switch (Mode)
            {
                case CanvasMode.Region or CanvasMode.Click or CanvasMode.Pick:
                    type = StandardCursorType.Cross;
                    break;
                case CanvasMode.Area:
                    type = HitRegionHandle(position) is { } edge ? ResizeCursor(edge, 0)
                        : Region is { } region && ToScreen(region).Contains(position) ? StandardCursorType.SizeAll
                        : StandardCursorType.Cross;
                    break;
                case CanvasMode.Select:
                    if (SelectedObject is { } selected && Turns(selected.Model) && Near(RotatePoint(selected.Model), position)) type = StandardCursorType.Hand;
                    else if (SelectedObject is { } sized && HitHandle(sized.Model, position) is { } grip) type = ResizeCursor(grip, sized.Model.Rotation);
                    else if (_hoveredObject != null) type = StandardCursorType.SizeAll;
                    else if (_hoveredOverlay != null) type = StandardCursorType.Hand;
                    break;
            }

        if (!Cursors.TryGetValue(type, out var cursor)) Cursors[type] = cursor = new Cursor(type);
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
    }

    /// <summary>The double arrow that points the way a handle moves, taking the object's rotation into account.</summary>
    private static StandardCursorType ResizeCursor((int X, int Y) handle, double rotation)
    {
        // Direction of the handle as seen on screen, folded into half a turn and rounded to the nearest eighth.
        var angle = Math.Atan2(-handle.Y, handle.X) * 180 / Math.PI + rotation;
        var eighth = ((int)Math.Round(angle / 45) % 4 + 4) % 4;
        return eighth switch
        {
            0 => StandardCursorType.SizeWestEast,
            1 => StandardCursorType.TopRightCorner,
            2 => StandardCursorType.SizeNorthSouth,
            _ => StandardCursorType.TopLeftCorner
        };
    }

    // ---- moving, resizing and turning ---------------------------------------------------------------------------

    private static RectD Shift(RectD box, double dx, double dy) => new(box.Left + dx, box.Top + dy, box.Right + dx, box.Bottom + dy);

    private static RectD Stretch(RectD box, (int X, int Y) handle, double dx, double dy)
    {
        double left = box.Left, top = box.Top, right = box.Right, bottom = box.Bottom;
        if (handle.X < 0) left = Math.Min(left + dx, right - MinimumSize);
        if (handle.X > 0) right = Math.Max(right + dx, left + MinimumSize);
        if (handle.Y < 0) top = Math.Min(top + dy, bottom - MinimumSize);
        if (handle.Y > 0) bottom = Math.Max(bottom + dy, top + MinimumSize);
        return new RectD(left, top, right, bottom);
    }

    /// <summary>Keeps a marked area on the page: moved as a whole it stops at the edges, stretched it is cut off there.</summary>
    private RectD Inside(RectD box, bool keepSize)
    {
        if (keepSize)
        {
            var dx = Math.Max(0, -box.Left) - Math.Max(0, box.Right - PageWidth);
            var dy = Math.Max(0, -box.Top) - Math.Max(0, box.Bottom - PageHeight);
            box = Shift(box, dx, dy);
        }

        return new RectD(Math.Max(0, box.Left), Math.Max(0, box.Top), Math.Min(PageWidth, box.Right), Math.Min(PageHeight, box.Bottom));
    }

    /// <summary>
    /// The box an object gets when its handle is dragged to where the pointer is. The opposite edge or corner stays
    /// where it is, also for a turned object, whose edges do not run along the page's.
    /// </summary>
    private (RectD Bounds, double Scale) Resize(PageObject model, bool shift)
    {
        var (u, v) = IntoBox(_startBounds, _startRotation, _current.X / PointScale, _current.Y / PointScale);
        double w0 = _startBounds.Width, h0 = _startBounds.Height;
        // The anchor: the edge or corner across from the handle.
        double anchorU = -_handle.X * w0 / 2, anchorV = -_handle.Y * h0 / 2;
        var w = _handle.X != 0 ? Math.Max(MinimumSize, _handle.X * (u - anchorU)) : w0;
        var h = _handle.Y != 0 ? Math.Max(MinimumSize, _handle.Y * (v - anchorV)) : h0;

        var corner = _handle.X != 0 && _handle.Y != 0;
        // By a corner, text grows as a whole and pictures keep their proportions (Shift lets them be distorted);
        // patches are just areas and stretch freely.
        var whole = corner && model switch
        {
            PageText => true,
            PageImage => !shift,
            _ => shift
        };
        double scale = 1;
        if (whole)
        {
            scale = Math.Max(w / w0, h / h0);
            (w, h) = (w0 * scale, h0 * scale);
        }

        var (cx, cy) = FromBox(_startBounds, _startRotation, anchorU + _handle.X * w / 2, anchorV + _handle.Y * h / 2);
        return (new RectD(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), model is PageText && whole ? scale : 1);
    }

    private double Rotation(bool shift)
    {
        var center = ToScreen(_startBounds).Center;
        var angle = Math.Atan2(-(_current.Y - center.Y), _current.X - center.X);
        var rotation = _startRotation + (angle - _startAngle) * 180 / Math.PI;
        rotation = ((rotation + 180) % 360 + 360) % 360 - 180;
        if (shift) return Math.Round(rotation / 15) * 15;
        // Upright and the quarter turns are what is wanted most of the time: close to them, snap.
        var quarter = Math.Round(rotation / 90) * 90;
        return Math.Abs(rotation - quarter) < 3 ? quarter : Math.Round(rotation, 1);
    }
}
