using SkiaSharp;

namespace PdfModifier.Tests;

/// <summary>
/// Pictures of sheets as scanners, copiers and cameras really make them: with the dark of the lid around a book, the
/// shadow in its fold, black borders, uneven light. They are what finding the edges of a page is tried on.
/// </summary>
internal static class SheetPictures
{
    public static readonly SKColor Lid = new(26, 26, 29);
    public static readonly SKColor BookPaper = new(236, 233, 226);

    private const string Words = "The quick brown fox jumps over the lazy dog and keeps on running through the field until the evening comes";

    /// <summary>Fills a page area with lines of print and, when it has one, its number at the foot.</summary>
    public static void Print(SKCanvas canvas, SKRect page, int? number = null, float margin = 0.1f, byte ink = 35)
    {
        using var paint = new SKPaint { Color = new SKColor(ink, ink, ink), IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Arial") };
        paint.TextSize = page.Height / 46;
        canvas.Save();
        canvas.ClipRect(SKRect.Create(page.Left + page.Width * margin, page.Top, page.Width * (1 - 2 * margin), page.Height));
        for (var line = 0; line < 24; line++)
            canvas.DrawText(Words, page.Left + page.Width * margin, page.Top + page.Height * (0.1f + line * 0.78f / 23), paint);
        canvas.Restore();
        if (number == null) return;
        paint.TextSize = page.Height / 36;
        var text = number.Value.ToString();
        canvas.DrawText(text, page.MidX - paint.MeasureText(text) / 2, page.Bottom - page.Height * 0.045f, paint);
    }

    /// <summary>The shadow where two pages curve into the binding: darkest in the fold, fading out to both sides.</summary>
    public static void Fold(SKCanvas canvas, float x, float top, float bottom, int halfWidth, byte darkest = 96)
    {
        for (var d = -halfWidth; d <= halfWidth; d++)
        {
            // Not darker than the paper it lies on.
            var share = 1 - Math.Abs(d) / (double)(halfWidth + 1);
            using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(share * share * (255 - darkest))), BlendMode = SKBlendMode.SrcOver };
            canvas.DrawRect(SKRect.Create(x + d, top, 1, bottom - top), shadow);
        }
    }

    /// <summary>
    /// An open book on the glass of a scanner. The book does not cover the glass: where it ends, the dark of the open
    /// lid shows, as wide as the arguments say. With <paramref name="block"/> the edges of the pages underneath show
    /// as a striped rim along the outer sides of the book, as they do in a thick one.
    /// </summary>
    public static SKBitmap BookOnScanner(int width = 2400, int height = 1700, int left = 0, int top = 0, int right = 170, int bottom = 120,
        int fold = 34, int block = 0, double tilt = 0, int noise = 0, int? firstNumber = 14)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(Lid);
            canvas.RotateDegrees((float)tilt, width / 2f, height / 2f);
            var book = new SKRect(left, top, width - right, height - bottom);
            using (var paper = new SKPaint { Color = BookPaper }) canvas.DrawRect(book, paper);
            if (block > 0)
            {
                // The pages underneath: thin lines, lighter and darker, running along the outer edges.
                for (var i = 0; i < block; i++)
                {
                    var shade = (byte)(150 + (i * 53) % 70);
                    using var stripe = new SKPaint { Color = new SKColor(shade, shade, (byte)(shade - 6)) };
                    if (left > 0) canvas.DrawRect(SKRect.Create(book.Left + i, book.Top, 1, book.Height), stripe);
                    if (right > 0) canvas.DrawRect(SKRect.Create(book.Right - 1 - i, book.Top, 1, book.Height), stripe);
                    if (bottom > 0) canvas.DrawRect(SKRect.Create(book.Left, book.Bottom - 1 - i, book.Width, 1), stripe);
                }
            }

            var middle = book.MidX;
            Print(canvas, new SKRect(book.Left + block, book.Top, middle, book.Bottom - block), firstNumber);
            Print(canvas, new SKRect(middle, book.Top, book.Right - block, book.Bottom - block), firstNumber + 1);
            Fold(canvas, middle, book.Top, book.Bottom, fold);
        }

        Grain(bitmap, noise);
        return bitmap;
    }

    /// <summary>
    /// A photocopy of one page: the paper lay a little askew and smaller than the glass, so the copy has black along
    /// two of its edges, ragged where the toner ran out.
    /// </summary>
    public static SKBitmap Photocopy(int width = 1240, int height = 1754, int blackLeft = 70, int blackBottom = 90, int noise = 6)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(244, 244, 244));
            Print(canvas, new SKRect(blackLeft, 0, width, height - blackBottom), 3);
            using var black = new SKPaint { Color = new SKColor(18, 18, 18) };
            var random = new Random(3);
            // The edge of the black is not a straight line.
            for (var y = 0; y < height; y += 4) canvas.DrawRect(SKRect.Create(0, y, blackLeft - random.Next(0, 5), 4), black);
            for (var x = 0; x < width; x += 4) canvas.DrawRect(SKRect.Create(x, height - blackBottom + random.Next(0, 5), 4, blackBottom), black);
        }

        Grain(bitmap, noise);
        return bitmap;
    }

    /// <summary>
    /// The right-hand page of an open book as a cut that missed the fold leaves it: at its left a sliver of the page
    /// before it with the ends of that page's lines, then the shadow of the fold, then the page itself.
    /// </summary>
    public static SKBitmap PageWithSliver(int width = 1300, int height = 1700, int sliver = 60, int fold = 22, int noise = 5)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(BookPaper);
            // The page before reaches in from the left; its lines end close to the fold.
            Print(canvas, new SKRect(sliver - 1200, 0, sliver, height), null, margin: 0.03f);
            Print(canvas, new SKRect(sliver, 0, width, height), 15);
            Fold(canvas, sliver, 0, height, fold);
        }

        Grain(bitmap, noise);
        return bitmap;
    }

    /// <summary>
    /// A page whose upper third is a dark photograph that runs off its top and both sides, as in a magazine. That
    /// is page, not something around it.
    /// </summary>
    public static SKBitmap PageWithPictureToTheEdge(int width = 1240, int height = 1754)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var picture = new SKPaint();
        picture.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height * 0.36f),
            [new SKColor(30, 50, 70), new SKColor(90, 60, 40), new SKColor(20, 30, 30)], SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, width, height * 0.36f, picture);
        // A photograph has light in it as well as dark.
        using (var light = new SKPaint { Color = new SKColor(235, 215, 150), IsAntialias = true })
        {
            canvas.DrawCircle(width * 0.72f, height * 0.11f, height * 0.05f, light);
            light.Color = new SKColor(150, 160, 170);
            canvas.DrawRect(0, height * 0.27f, width, height * 0.035f, light);
        }

        Print(canvas, new SKRect(0, height * 0.34f, width, height), 8);
        return bitmap;
    }

    /// <summary>A part of a picture as a picture of its own.</summary>
    public static SKBitmap Part(SKBitmap source, int left, int top, int right, int bottom)
    {
        var part = new SKBitmap(new SKImageInfo(right - left, bottom - top, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(part);
        canvas.DrawBitmap(source, -left, -top);
        return part;
    }

    /// <summary>Adds the grain every scan and photograph has.</summary>
    public static unsafe void Grain(SKBitmap bitmap, int amount, int seed = 5)
    {
        if (amount <= 0) return;
        var random = new Random(seed);
        var bytes = (byte*)bitmap.GetPixels();
        for (var i = 0; i < bitmap.RowBytes * bitmap.Height; i += 4)
        {
            var grain = random.Next(-amount, amount + 1);
            for (var c = 0; c < 3; c++) bytes[i + c] = (byte)Math.Clamp(bytes[i + c] + grain, 0, 255);
        }
    }

    /// <summary>Darkens a picture from one corner to the other, as light from a window at one side does.</summary>
    public static void Shade(SKBitmap bitmap, byte darkest = 110)
    {
        using var canvas = new SKCanvas(bitmap);
        using var shade = new SKPaint();
        shade.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(bitmap.Width, bitmap.Height),
            [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, darkest)], SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, bitmap.Width, bitmap.Height, shade);
    }

    /// <summary>The picture with frames drawn on it, for looking at what was found.</summary>
    public static SKBitmap Framed(SKBitmap picture, IEnumerable<(PdfModifier.Core.RectD Area, bool Doubt)> frames)
    {
        var copy = picture.Copy();
        using var canvas = new SKCanvas(copy);
        foreach (var (area, doubt) in frames)
        {
            using var pen = new SKPaint
            {
                Color = doubt ? new SKColor(220, 40, 60) : new SKColor(40, 110, 240), Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(3, picture.Width / 400f), IsAntialias = true
            };
            canvas.DrawRect(new SKRect((float)area.Left, (float)area.Top, (float)area.Right, (float)area.Bottom), pen);
        }

        return copy;
    }
}
