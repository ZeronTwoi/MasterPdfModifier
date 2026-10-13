using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PdfModifier.Controls;

/// <summary>
/// The line drawings of the interface. All are drawn on a square of 24 units with a round pen, so they share one
/// weight whatever size they are shown at (see <see cref="Icon"/>).
/// </summary>
public static class Icons
{
    // The window's own commands
    public static Geometry Open { get; } = Parse("M3 8 a2 2 0 0 1 2 -2 h4 l2 2 h8 a2 2 0 0 1 2 2 v8 a2 2 0 0 1 -2 2 h-14 a2 2 0 0 1 -2 -2 z");
    public static Geometry Save { get; } = Parse("M5 3 h11 l4 4 v12 a2 2 0 0 1 -2 2 h-13 a2 2 0 0 1 -2 -2 v-14 a2 2 0 0 1 2 -2 z M8 3 v5 h7 v-5 M7 21 v-7 h10 v7");
    public static Geometry SaveAs { get; } = Parse("M12 3 v12 M7 10 l5 5 5 -5 M4 20 h16");
    public static Geometry Undo { get; } = Parse("M9 14 l-5 -5 5 -5 M4 9 h10 a6 6 0 0 1 0 12 h-3");
    public static Geometry Redo { get; } = Parse("M15 14 l5 -5 -5 -5 M20 9 h-10 a6 6 0 0 0 0 12 h3");
    public static Geometry Previous { get; } = Parse("M15 5 l-7 7 7 7");
    public static Geometry Next { get; } = Parse("M9 5 l7 7 -7 7");
    public static Geometry Minus { get; } = Parse("M5 12 h14");
    public static Geometry Plus { get; } = Parse("M12 5 v14 M5 12 h14");
    public static Geometry FitPage { get; } = Parse("M4 9 v-5 h5 M15 4 h5 v5 M20 15 v5 h-5 M9 20 h-5 v-5");
    public static Geometry FitWidth { get; } = Parse("M3 12 h18 M7 8 l-4 4 4 4 M17 8 l4 4 -4 4");
    public static Geometry Search { get; } = Parse("M18 11 a7 7 0 1 1 -14 0 a7 7 0 1 1 14 0 M16 16 l4.5 4.5");
    public static Geometry Close { get; } = Parse("M6 6 l12 12 M18 6 l-12 12");
    public static Geometry Picture { get; } = Parse("M5 4 h14 a1 1 0 0 1 1 1 v14 a1 1 0 0 1 -1 1 h-14 a1 1 0 0 1 -1 -1 v-14 a1 1 0 0 1 1 -1 z M4 16 l5 -5 4 4 3 -3 4 4 M16.5 8.5 a1 1 0 1 1 -2 0 a1 1 0 1 1 2 0");
    public static Geometry Trash { get; } = Parse("M4 7 h16 M9 7 v-3 h6 v3 M6 7 l1 13 h10 l1 -13 M10 11 v6 M14 11 v6");
    public static Geometry RotateRight { get; } = Parse("M20 12 a8 8 0 1 1 -2.4 -5.7 M20 3 v5 h-5");
    public static Geometry RotateLeft { get; } = Parse("M4 12 a8 8 0 1 0 2.4 -5.7 M4 3 v5 h5");
    public static Geometry Document { get; } = Parse("M6 3 h8 l5 5 v11 a2 2 0 0 1 -2 2 h-11 a2 2 0 0 1 -2 -2 v-14 a2 2 0 0 1 2 -2 z M14 3 v5 h5");
    public static Geometry Drop { get; } = Parse("M12 4 v11 M7 10 l5 5 5 -5 M4 15 v3 a2 2 0 0 0 2 2 h12 a2 2 0 0 0 2 -2 v-3");

    public static Geometry Pin { get; } = Parse("M9 3 h6 M10 3 v6 l-3 4 h10 l-3 -4 v-6 M12 13 v8");

    // What is done to pages: turning, mirroring, moving
    public static Geometry RotateHalf { get; } = Parse("M4.5 10 a8 8 0 0 1 14.6 -2 M20 3 v5 h-5 M19.5 14 a8 8 0 0 1 -14.6 2 M4 21 v-5 h5");
    public static Geometry MirrorAcross { get; } = Parse("M12 3 v2 M12 8 v2 M12 13 v2 M12 18 v3 M9 7 l-6 5 6 5 z M15 7 l6 5 -6 5 z");
    public static Geometry MirrorDown { get; } = Parse("M3 12 h2 M8 12 h2 M13 12 h2 M18 12 h3 M7 9 l5 -6 5 6 z M7 15 l5 6 5 -6 z");
    public static Geometry Up { get; } = Parse("M12 19 v-14 M6 11 l6 -6 6 6");
    public static Geometry Down { get; } = Parse("M12 5 v14 M6 13 l6 6 6 -6");
    public static Geometry ToStart { get; } = Parse("M5 4 h14 M12 20 v-12 M7 13 l5 -5 5 5");
    public static Geometry ToEnd { get; } = Parse("M5 20 h14 M12 4 v12 M7 11 l5 5 5 -5");
    public static Geometry Reverse { get; } = Parse("M8 4 v16 M4 8 l4 -4 4 4 M16 20 v-16 M12 16 l4 4 4 -4");

    // How a sheet is divided into pages, and the order they are taken in
    private const string Sheet = "M5 4 h14 a1 1 0 0 1 1 1 v14 a1 1 0 0 1 -1 1 h-14 a1 1 0 0 1 -1 -1 v-14 a1 1 0 0 1 1 -1 z ";
    public static Geometry LayoutAuto { get; } = Parse(Sheet + "M12 7.5 l1.3 3.2 3.2 1.3 -3.2 1.3 -1.3 3.2 -1.3 -3.2 -3.2 -1.3 3.2 -1.3 z");
    public static Geometry LayoutTwo { get; } = Parse(Sheet + "M12 4 v16");
    public static Geometry LayoutStack { get; } = Parse(Sheet + "M4 12 h16");
    public static Geometry LayoutFour { get; } = Parse(Sheet + "M12 4 v16 M4 12 h16");
    public static Geometry LayoutGrid { get; } = Parse(Sheet + "M9.3 4 v16 M14.7 4 v16 M4 9.3 h16 M4 14.7 h16");
    public static Geometry OrderRows { get; } = Parse("M5 6.5 h14 l-14 11 h13 M15.5 14.8 l3 2.7 -3 2.7");
    public static Geometry OrderRowsBack { get; } = Parse("M19 6.5 h-14 l14 11 h-13 M8.5 14.8 l-3 2.7 3 2.7");
    public static Geometry OrderColumns { get; } = Parse("M6.5 5 v14 l11 -14 v13 M14.8 15.5 l2.7 3 2.7 -3");
    public static Geometry OrderClick { get; } = Parse("M6 3 l12 8.5 -5.3 1.2 3.1 6 -2.4 1.3 -3.2 -6 -4.2 3.7 z");
    public static Geometry Settings => ForTool("Settings");

    private static readonly Dictionary<string, Geometry> Tools = new()
    {
        ["Edit"] = Parse("M4 20 l1 -5 11 -11 a1.4 1.4 0 0 1 2 0 l2 2 a1.4 1.4 0 0 1 0 2 l-11 11 z M14 6 l4 4"),
        ["Watermark"] = Parse("M12 3 c4 5 6 8 6 11 a6 6 0 0 1 -12 0 c0 -3 2 -6 6 -11 z"),
        ["RemoveWatermark"] = Parse("M4 15 l9 -9 a1.4 1.4 0 0 1 2 0 l4 4 a1.4 1.4 0 0 1 0 2 l-7 7 h-4 l-4 -4 z M9 10 l6 6 M12 19 h8"),
        ["PageNumbers"] = Parse("M9 4 l-2 16 M17 4 l-2 16 M5 9 h15 M4 15 h15"),
        ["Flatten"] = Parse("M12 4 l9 5 -9 5 -9 -5 z M3 14 l9 5 9 -5"),
        ["Metadata"] = Parse("M21 12 a9 9 0 1 1 -18 0 a9 9 0 1 1 18 0 M12 11 v6 M12 7.4 v0.2"),
        ["Merge"] = Parse("M6 4 v3 a5 5 0 0 0 5 5 h1 M18 4 v3 a5 5 0 0 1 -5 5 h-1 v8 M9 17 l3 3 3 -3"),
        ["Split"] = Parse("M12 4 v7 M12 11 h-1 a5 5 0 0 0 -5 5 v4 M12 11 h1 a5 5 0 0 1 5 5 v4 M3.5 17.5 l2.5 2.5 2.5 -2.5 M15.5 17.5 l2.5 2.5 2.5 -2.5"),
        ["Rotate"] = Parse("M20 12 a8 8 0 1 1 -2.4 -5.7 M20 3 v5 h-5"),
        ["Delete"] = Parse("M4 7 h16 M9 7 v-3 h6 v3 M6 7 l1 13 h10 l1 -13 M10 11 v6 M14 11 v6"),
        ["Reorder"] = Parse("M8 4 v16 M4 8 l4 -4 4 4 M16 20 v-16 M12 16 l4 4 4 -4"),
        ["Insert"] = Parse("M6 3 h8 l5 5 v11 a2 2 0 0 1 -2 2 h-11 a2 2 0 0 1 -2 -2 v-14 a2 2 0 0 1 2 -2 z M12 11 v6 M9 14 h6"),
        ["Extract"] = Parse("M13 21 h-7 a2 2 0 0 1 -2 -2 v-14 a2 2 0 0 1 2 -2 h8 l5 5 v3 M12 16 h9 M18 13 l3 3 -3 3"),
        ["Resize"] = Parse("M5 19 l14 -14 M13 5 h6 v6 M11 19 h-6 v-6"),
        ["Crop"] = Parse("M7 3 v14 h14 M3 7 h14 v14"),
        ["Sheets"] = Parse("M5 4 h14 a1 1 0 0 1 1 1 v14 a1 1 0 0 1 -1 1 h-14 a1 1 0 0 1 -1 -1 v-14 a1 1 0 0 1 1 -1 z M12 4 v3 M12 10 v4 M12 17 v3 M4 12 h3 M10 12 h4 M17 12 h3"),
        ["Compress"] = Parse("M4 4 l6 6 M10 5 v5 h-5 M20 20 l-6 -6 M14 19 v-5 h5"),
        ["Enhance"] = Parse("M11 4 l2 5 5 2 -5 2 -2 5 -2 -5 -5 -2 5 -2 z M19 15 v4 M17 17 h4"),
        ["Ocr"] = Parse("M4 8 v-3 a1 1 0 0 1 1 -1 h3 M16 4 h3 a1 1 0 0 1 1 1 v3 M20 16 v3 a1 1 0 0 1 -1 1 h-3 M8 20 h-3 a1 1 0 0 1 -1 -1 v-3 M8 9 h8 M12 9 v7"),
        ["Automate"] = Parse("M13 3 l-8 11 h6 l-1 7 8 -11 h-6 z"),
        ["Compare"] = Parse("M5 5 h5 v14 h-5 z M14 5 h5 v14 h-5 z"),
        ["Protect"] = Parse("M7 11 h10 a1 1 0 0 1 1 1 v7 a1 1 0 0 1 -1 1 h-10 a1 1 0 0 1 -1 -1 v-7 a1 1 0 0 1 1 -1 z M8 11 v-3 a4 4 0 0 1 8 0 v3"),
        ["Settings"] = Parse("M4 7 h9 M17 7 h3 M4 17 h3 M11 17 h9 M15 5 v4 M9 15 v4")
    };

    /// <summary>The drawing for a tool, by the tool's id; a plain sheet for one that has none of its own.</summary>
    public static Geometry ForTool(string id) => Tools.GetValueOrDefault(id, Document);

    private static Geometry Parse(string path) => StreamGeometry.Parse(path);
}

/// <summary>
/// The sign for "this wants your attention": a filled triangle with an exclamation mark. Unlike the line drawings it
/// has a colour of its own, so it stands out among them.
/// </summary>
public sealed class WarningSign : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<WarningSign, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> MarkProperty = AvaloniaProperty.Register<WarningSign, IBrush?>(nameof(Mark));

    private static readonly Geometry Triangle = StreamGeometry.Parse("M12 4 L21 19.5 H3 Z");
    private static readonly Geometry Exclamation = StreamGeometry.Parse("M12 10 v4.6 M12 17.3 v0.1");

    static WarningSign()
    {
        AffectsRender<WarningSign>(FillProperty, MarkProperty);
        WidthProperty.OverrideDefaultValue<WarningSign>(16);
        HeightProperty.OverrideDefaultValue<WarningSign>(16);
    }

    /// <summary>The colour of the triangle.</summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The colour of the exclamation mark on it.</summary>
    public IBrush? Mark
    {
        get => GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Fill is not { } fill) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        if (scale <= 0) return;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) *
                                     Matrix.CreateTranslation((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2)))
        {
            // The outline in the colour of the fill rounds the corners off.
            context.DrawGeometry(fill, new Pen(fill, 3, lineJoin: PenLineJoin.Round), Triangle);
            if (Mark is { } mark) context.DrawGeometry(null, new Pen(mark, 2.2, lineCap: PenLineCap.Round), Exclamation);
        }
    }
}

/// <summary>
/// Shows one of the <see cref="Icons"/> in the colour of the text around it. The size is that of the control; the
/// pen grows with it only a little, so small icons do not turn faint and large ones not heavy.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> WeightProperty = AvaloniaProperty.Register<Icon, double>(nameof(Weight), 1.5);

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, WeightProperty);
        WidthProperty.OverrideDefaultValue<Icon>(18);
        HeightProperty.OverrideDefaultValue<Icon>(18);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Thickness of the lines, in screen units.</summary>
    public double Weight
    {
        get => GetValue(WeightProperty);
        set => SetValue(WeightProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        if (scale <= 0) return;
        var pen = new Pen(brush, Weight / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) *
                                     Matrix.CreateTranslation((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2)))
            context.DrawGeometry(null, pen, data);
    }
}
