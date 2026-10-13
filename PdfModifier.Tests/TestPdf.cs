using PdfModifier.Core;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Tests;

/// <summary>Builds small documents to test against, so the suite needs no fixture files.</summary>
internal static class TestPdf
{
    public const double A4Width = 595, A4Height = 842;

    /// <summary>One line of 24pt text per page, baseline at (72, 100).</summary>
    public static byte[] WithText(params string[] pageTexts) => WithText(A4Width, A4Height, pageTexts);

    public static byte[] WithText(double width, double height, params string[] pageTexts)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.CreateNew();
            using var fonts = new DocFonts(doc);
            foreach (var text in pageTexts) fonts.Prepare(text, null);
            for (var i = 0; i < pageTexts.Length; i++)
            {
                using var page = doc.NewPage(i, width, height);
                if (pageTexts[i].Length > 0) PageDraw.DrawText(page, fonts, pageTexts[i], new TextStyle(24), 72, 100);
                page.GenerateContent();
            }

            return doc.Save();
        }
    }

    /// <summary>One page drawn by the caller, for documents the other helpers cannot describe.</summary>
    public static byte[] Compose(Action<PdfPage, DocFonts> draw, double width = A4Width, double height = A4Height)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.CreateNew();
            using var fonts = new DocFonts(doc);
            using var page = doc.NewPage(0, width, height);
            draw(page, fonts);
            page.GenerateContent();
            return doc.Save();
        }
    }

    /// <summary>
    /// A page whose whole content is the first page of <paramref name="inner"/> wrapped in a nested graphic (a form
    /// XObject), the way many programs write their pages. <paramref name="draw"/> adds ordinary content on top.
    /// </summary>
    public static byte[] Nested(byte[] inner, Action<PdfPage, DocFonts>? draw = null)
    {
        lock (PdfDoc.Sync)
        {
            using var source = PdfDoc.Load(inner);
            var (width, height) = source.GetPageSize(0);
            using var doc = PdfDoc.CreateNew();
            using var fonts = new DocFonts(doc);
            using var page = doc.NewPage(0, width, height);
            var xobject = Native.FPDF_NewXObjectFromPage(doc.Handle, source.Handle, 0);
            Native.FPDFPage_InsertObject(page.Handle, Native.FPDF_NewFormObjectFromXObject(xobject));
            Native.FPDF_CloseXObject(xobject);
            draw?.Invoke(page, fonts);
            page.GenerateContent();
            return doc.Save();
        }
    }

    /// <summary>A page-filling scan with content drawn before it (<paramref name="under"/>) and after it (<paramref name="over"/>).</summary>
    public static byte[] ScanWith(SKBitmap scan, Action<PdfPage, DocFonts>? under = null, Action<PdfPage, DocFonts>? over = null, double dpi = 150)
    {
        double width = scan.Width * 72.0 / dpi, height = scan.Height * 72.0 / dpi;
        return Compose((page, fonts) =>
        {
            under?.Invoke(page, fonts);
            PageDraw.DrawImage(page, scan, new RectD(0, 0, width, height));
            over?.Invoke(page, fonts);
        }, width, height);
    }

    public static byte[] Numbered(int pages) => WithText(Enumerable.Range(1, pages).Select(n => $"Page {n}").ToArray());

    /// <summary>A page whose whole area is one image, like a scanner produces.</summary>
    public static byte[] FromImage(SKBitmap bitmap, double dpi = 150)
    {
        var png = PdfImages.Encode(bitmap, ImageFormat.Png);
        return PdfImages.InsertImages(null, [png], 0, new ImagePageOptions(Dpi: dpi));
    }

    /// <summary>Black text lines on a paper-coloured background, optionally tilted, to stand in for a scan.</summary>
    public static SKBitmap FakeScan(int width = 1240, int height = 1754, double skewDegrees = 0, byte paper = 215, string? text = null)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(paper, paper, paper));
        canvas.RotateDegrees((float)skewDegrees, width / 2f, height / 2f);
        using var paint = new SKPaint { Color = new SKColor(40, 40, 40), IsAntialias = true, TextSize = 36, Typeface = SKTypeface.FromFamilyName("Arial") };
        var line = text ?? "The quick brown fox jumps over the lazy dog and keeps running";
        for (var y = 220; y < height - 200; y += 70) canvas.DrawText(line, 140, y, paint);
        return bitmap;
    }

    /// <summary>The lines of <see cref="PoorScan"/>: ordinary sentences without a word that could be guessed from another.</summary>
    public static readonly string[] Sentences =
    [
        "Invoices are payable within thirty days of receipt", "The committee approved the budget for next year",
        "Please return the signed form to our office", "Delivery is expected on the fourteenth of March",
        "Water damage is not covered by this policy", "The warranty period begins on the day of purchase",
        "All employees must complete the safety training", "Parking is available behind the main building",
        "The meeting was postponed until further notice", "Customers may cancel their order at any time",
        "Our records show an outstanding balance", "Kindly confirm your attendance before Friday",
        "The contract remains valid for twelve months", "Shipping costs are calculated at checkout",
        "This certificate is issued without alteration", "Applications received after the deadline are rejected"
    ];

    /// <summary>
    /// A scan as a bad copier or an unsteady hand makes it: sixteen lines of print, out of focus by
    /// <paramref name="blur"/> pixels, grainy, grey on grey. The first line has its baseline 90 pixels down, the
    /// others follow every 2.2 times the size of the print.
    /// </summary>
    public static unsafe SKBitmap PoorScan(double blur = 2.2, int noise = 8, byte paper = 205, byte ink = 70, float size = 24,
        int width = 1240, int height = 1100)
    {
        using var sharp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(sharp))
        {
            canvas.Clear(new SKColor(paper, paper, paper));
            using var paint = new SKPaint { Color = new SKColor(ink, ink, ink), IsAntialias = true, TextSize = size, Typeface = SKTypeface.FromFamilyName("Times New Roman") };
            var y = 90f;
            foreach (var sentence in Sentences)
            {
                canvas.DrawText(sentence, 90, y, paint);
                y += size * 2.2f;
            }
        }

        var poor = new SKBitmap(sharp.Info);
        using (var canvas = new SKCanvas(poor))
        using (var paint = new SKPaint())
        {
            if (blur > 0) paint.ImageFilter = SKImageFilter.CreateBlur((float)blur, (float)blur);
            canvas.Clear(new SKColor(paper, paper, paper));
            canvas.DrawBitmap(sharp, 0, 0, paint);
        }

        var random = new Random(11);
        var bytes = (byte*)poor.GetPixels();
        for (var i = 0; noise > 0 && i < poor.RowBytes * poor.Height; i += 4)
        {
            var grain = random.Next(-noise, noise + 1);
            for (var c = 0; c < 3; c++) bytes[i + c] = (byte)Math.Clamp(bytes[i + c] + grain, 0, 255);
        }

        return poor;
    }

    /// <summary>How many of the words of <see cref="Sentences"/> a reading of the page got right.</summary>
    public static int WordsRight(IEnumerable<OcrLine> lines)
    {
        var known = Sentences.SelectMany(s => s.Split(' ')).Select(w => w.ToLowerInvariant()).ToHashSet();
        return lines.SelectMany(line => line.Text.Split(' ')).Count(word => known.Contains(word.Trim('.', ',', ';', ':').ToLowerInvariant()));
    }

    public static string Text(byte[] pdf, int page)
    {
        using var view = new PdfView(pdf);
        return view.GetText(page).Trim();
    }

    public static int PageCount(byte[] pdf)
    {
        using var view = new PdfView(pdf);
        return view.PageCount;
    }

    public static SKBitmap Render(byte[] pdf, int page, double scale = 1)
    {
        using var view = new PdfView(pdf);
        return view.Render(page, scale);
    }

    /// <summary>Fraction of pixels that are not near-white.</summary>
    public static unsafe double InkRatio(SKBitmap bitmap)
    {
        var pixels = (uint*)bitmap.GetPixels();
        var stride = bitmap.RowBytes / 4;
        long ink = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var p = pixels[y * stride + x];
            if ((p & 0xFF) < 200 || ((p >> 8) & 0xFF) < 200 || ((p >> 16) & 0xFF) < 200) ink++;
        }

        return (double)ink / (bitmap.Width * bitmap.Height);
    }

    public static double Difference(byte[] a, byte[] b, int page = 0)
    {
        using var bitmapA = Render(a, page);
        using var bitmapB = Render(b, page);
        return PdfModifier.Core.Imaging.ImageAnalysis.DifferenceRatio(bitmapA, bitmapB);
    }
}
