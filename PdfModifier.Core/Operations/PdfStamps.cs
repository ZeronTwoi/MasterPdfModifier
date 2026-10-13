using PdfModifier.Core.Fonts;
using PdfModifier.Core.Pdfium;

namespace PdfModifier.Core.Operations;

public enum WatermarkLayout
{
    Center,
    Tile
}

/// <summary>
/// A text or image watermark. Set <see cref="Text"/> or <see cref="ImageData"/>. Colours are ARGB; the alpha channel is
/// ignored in favour of <see cref="Opacity"/>.
/// </summary>
public sealed record WatermarkOptions
{
    public string? Text { get; init; }
    public byte[]? ImageData { get; init; }
    public string? FontFamily { get; init; }
    public bool Bold { get; init; } = true;
    public double FontSize { get; init; } = 54;
    public uint Color { get; init; } = 0xFF808080;
    public double Opacity { get; init; } = 0.3;
    public double RotationDegrees { get; init; } = 45;
    public WatermarkLayout Layout { get; init; } = WatermarkLayout.Center;

    /// <summary>Image width as a fraction of the page width.</summary>
    public double ImageScale { get; init; } = 0.5;

    /// <summary>
    /// Where the middle of a single watermark goes, as fractions of the page width and height from the top-left corner.
    /// Given as fractions so the same setting suits pages of different sizes.
    /// </summary>
    public double CenterX { get; init; } = 0.5;

    public double CenterY { get; init; } = 0.5;
}

public enum StampPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

/// <summary>Page numbering. <see cref="Format"/> understands {n} (page number) and {total}.</summary>
public sealed record PageNumberOptions
{
    public string Format { get; init; } = "{n}";
    public int StartNumber { get; init; } = 1;
    public StampPosition Position { get; init; } = StampPosition.BottomCenter;
    public double Margin { get; init; } = 28;
    public double FontSize { get; init; } = 11;
    public uint Color { get; init; } = 0xFF000000;
    public string? FontFamily { get; init; }
}

public static class PdfStamps
{
    /// <summary>Marked-content tag put on watermarks this app adds, so they can be found and removed reliably later.</summary>
    internal const string WatermarkMark = "Watermark";

    public static byte[] AddWatermark(byte[] pdf, IReadOnlyCollection<int>? pages, WatermarkOptions options)
    {
        var hasText = !string.IsNullOrWhiteSpace(options.Text);
        if (!hasText && options.ImageData == null) throw new ArgumentException("A watermark needs text or an image.");
        var opacity = Math.Clamp(options.Opacity, 0.02, 1);

        using var image = options.ImageData != null ? PdfImages.Decode(options.ImageData) : null;
        var result = ContentEditor.Run(pdf, pages, (page, _, fonts) =>
        {
            foreach (var (cx, cy) in Positions(page.Width, page.Height, options, image == null ? null : (image.Width, image.Height)))
            {
                if (image != null)
                {
                    var w = page.Width * Math.Clamp(options.ImageScale, 0.02, 2);
                    var h = w * image.Height / image.Width;
                    PageDraw.DrawImage(page, image, new RectD(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2),
                        options.RotationDegrees, opacity, mark: WatermarkMark);
                }

                if (hasText) DrawCenteredText(page, fonts, options, opacity, cx, cy);
            }

            return true;
        }, hasText ? (fonts, _) => fonts.Prepare(options.Text!, options.FontFamily, options.Bold) : null);

        // Every page got its own copy of the picture; keep just one.
        return image != null ? Sharp.ShareDuplicates(result) : result;
    }

    private static void DrawCenteredText(PdfPage page, DocFonts fonts, WatermarkOptions options, double opacity, double cx, double cy)
    {
        var width = FontResolver.Measure(options.Text!, options.FontFamily, options.Bold, false, options.FontSize);
        var a = options.RotationDegrees * Math.PI / 180;
        // Unit vectors of the rotated text in visual space: along the baseline and towards the top of the letters.
        double ux = Math.Cos(a), uy = -Math.Sin(a), vx = -Math.Sin(a), vy = -Math.Cos(a);
        var halfCap = options.FontSize * 0.35;
        var x = cx - ux * width / 2 - vx * halfCap;
        var y = cy - uy * width / 2 - vy * halfCap;
        var color = (options.Color & 0x00FFFFFF) | ((uint)Math.Round(opacity * 255) << 24);
        var style = new TextStyle(options.FontSize, color, options.FontFamily, options.Bold, Mark: WatermarkMark);
        PageDraw.DrawText(page, fonts, options.Text!, style, x, y, options.RotationDegrees);
    }

    private static IEnumerable<(double X, double Y)> Positions(double pageW, double pageH, WatermarkOptions options, (int W, int H)? image)
    {
        if (options.Layout == WatermarkLayout.Center)
        {
            yield return (pageW * options.CenterX, pageH * options.CenterY);
            yield break;
        }

        double stampW, stampH;
        if (image is { } img)
        {
            stampW = pageW * Math.Clamp(options.ImageScale, 0.02, 2);
            stampH = stampW * img.H / img.W;
        }
        else
        {
            stampW = FontResolver.Measure(options.Text!, options.FontFamily, options.Bold, false, options.FontSize);
            stampH = options.FontSize;
        }

        // Rows are staggered so the repeats do not line up into columns.
        var stepX = stampW + Math.Max(40, stampH * 2);
        var stepY = Math.Max(stampH * 3, 110);
        var row = 0;
        for (var y = stepY / 2; y < pageH + stepY / 2; y += stepY, row++)
        for (var x = row % 2 == 0 ? stepX / 4 : stepX * 3 / 4; x < pageW + stepX / 2; x += stepX)
            yield return (x, y);
    }

    public static byte[] AddPageNumbers(byte[] pdf, IReadOnlyCollection<int>? pages, PageNumberOptions options)
    {
        var selected = PdfPages.ResolvePages(pages, PdfPages.CountPages(pdf));
        var total = options.StartNumber + selected.Count - 1;
        var labels = selected.Select((page, i) => (page, Label: options.Format
                .Replace("{n}", (options.StartNumber + i).ToString())
                .Replace("{total}", total.ToString())))
            .ToDictionary(x => x.page, x => x.Label);
        var style = new TextStyle(options.FontSize, options.Color | 0xFF000000, options.FontFamily);

        return ContentEditor.Run(pdf, selected, (page, index, fonts) =>
        {
            var label = labels[index];
            if (label.Length == 0) return false;
            var width = FontResolver.Measure(label, options.FontFamily, false, false, options.FontSize);
            var x = options.Position switch
            {
                StampPosition.TopLeft or StampPosition.BottomLeft => options.Margin,
                StampPosition.TopRight or StampPosition.BottomRight => page.Width - options.Margin - width,
                _ => (page.Width - width) / 2
            };
            var top = options.Position is StampPosition.TopLeft or StampPosition.TopCenter or StampPosition.TopRight;
            var baseline = top ? options.Margin + options.FontSize * 0.8 : page.Height - options.Margin;
            PageDraw.DrawText(page, fonts, label, style, x, baseline);
            return true;
        }, (fonts, _) => fonts.PrepareAll(labels.Values, options.FontFamily));
    }
}
