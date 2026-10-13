using PdfModifier.Core;
using PdfModifier.Core.Imaging;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Tests;

public class ContentOperationTests
{
    [Fact]
    public void ExportedPageImagesHaveTheRequestedResolution()
    {
        var images = new Dictionary<int, byte[]>();
        PdfImages.ExportPages(TestPdf.Numbered(3), [0, 2], 144, ImageFormat.Png, 90, (page, data) => images[page] = data);

        Assert.Equal([0, 2], images.Keys.Order());
        using var bitmap = SKBitmap.Decode(images[2]);
        Assert.Equal((int)Math.Round(TestPdf.A4Width * 2), bitmap.Width);
        Assert.Equal((int)Math.Round(TestPdf.A4Height * 2), bitmap.Height);
        Assert.True(TestPdf.InkRatio(bitmap) > 0.0005);
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Webp)]
    public void ExportSupportsOtherFormats(ImageFormat format)
    {
        byte[]? data = null;
        PdfImages.ExportPages(TestPdf.Numbered(1), null, 72, format, 80, (_, d) => data = d);
        using var bitmap = SKBitmap.Decode(data);
        Assert.Equal((int)TestPdf.A4Width, bitmap.Width);
    }

    [Fact]
    public void ImagesBecomePagesSizedFromTheirResolution()
    {
        using var scan = TestPdf.FakeScan(600, 900);
        var png = PdfImages.Encode(scan, ImageFormat.Png);
        var pdf = PdfImages.InsertImages(TestPdf.Numbered(2), [png], 1, new ImagePageOptions(Dpi: 150));

        Assert.Equal(3, TestPdf.PageCount(pdf));
        var info = PdfPages.GetPageInfos(pdf)[1];
        Assert.Equal(600 * 72.0 / 150, info.Width, 0.5);
        Assert.Equal(900 * 72.0 / 150, info.Height, 0.5);
        using var rendered = TestPdf.Render(pdf, 1);
        Assert.InRange(rendered.GetPixel(4, 4).Red, 205, 225); // the scan's gray paper reaches the page corner
        Assert.Equal("Page 2", TestPdf.Text(pdf, 2));
    }

    [Fact]
    public void ImagesAreFittedAndCentredOnAFixedPageSize()
    {
        using var wide = new SKBitmap(new SKImageInfo(400, 100, SKColorType.Bgra8888, SKAlphaType.Opaque));
        wide.Erase(SKColors.Black);
        var pdf = PdfImages.InsertImages(null, [PdfImages.Encode(wide, ImageFormat.Png)], 0, new ImagePageOptions(200, 200, Margin: 10));
        using var rendered = TestPdf.Render(pdf, 0);
        Assert.Equal(200, rendered.Width);
        // 180pt wide, 45pt tall, vertically centred.
        Assert.Equal(SKColors.Black, rendered.GetPixel(100, 100));
        Assert.Equal(SKColors.White, rendered.GetPixel(100, 60));
        Assert.Equal(SKColors.White, rendered.GetPixel(4, 100));
    }

    [Fact]
    public void TextWatermarkIsVisibleAndCanBeRemovedAgain()
    {
        var pdf = TestPdf.Numbered(3);
        var marked = PdfStamps.AddWatermark(pdf, null, new WatermarkOptions { Text = "CONFIDENTIAL" });

        Assert.True(TestPdf.Difference(pdf, marked) > 0.005, "the watermark should change the page");
        Assert.Contains("CONFIDENTIAL", TestPdf.Text(marked, 1));

        var candidate = Assert.Single(PdfWatermarkRemoval.Detect(marked));
        Assert.Equal(WatermarkKind.Tagged, candidate.Kind);
        Assert.Equal(3, candidate.PageCount);

        var (cleaned, removed) = PdfWatermarkRemoval.Remove(marked);
        Assert.Equal(3, removed);
        Assert.Equal(0, TestPdf.Difference(pdf, cleaned), 0.0001);
        Assert.Equal("Page 2", TestPdf.Text(cleaned, 1));
    }

    [Fact]
    public void WatermarkCanBeLimitedToSomePages()
    {
        var pdf = TestPdf.Numbered(3);
        var marked = PdfStamps.AddWatermark(pdf, [1], new WatermarkOptions { Text = "DRAFT", Layout = WatermarkLayout.Tile });
        Assert.Equal(0, TestPdf.Difference(pdf, marked, 0), 0.0001);
        Assert.True(TestPdf.Difference(pdf, marked, 1) > 0.005);
    }

    [Fact]
    public void WatermarkTextCanMixScripts()
    {
        var marked = PdfStamps.AddWatermark(TestPdf.Numbered(1), null, new WatermarkOptions { Text = "Черновик 下書き Taslak ığşİ" });
        var text = TestPdf.Text(marked, 0);
        Assert.Contains("Черновик", text);
        Assert.Contains("下書き", text);
        Assert.Contains("ığşİ", text);
    }

    [Fact]
    public void ImageWatermarkIsDrawnSemiTransparent()
    {
        using var logo = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Bgra8888, SKAlphaType.Opaque));
        logo.Erase(SKColors.Black);
        var marked = PdfStamps.AddWatermark(TestPdf.WithText(""), null,
            new WatermarkOptions { ImageData = PdfImages.Encode(logo, ImageFormat.Png), Opacity = 0.5, RotationDegrees = 0, ImageScale = 0.4 });
        using var rendered = TestPdf.Render(marked, 0);
        var centre = rendered.GetPixel(rendered.Width / 2, rendered.Height / 2);
        Assert.InRange(centre.Red, 110, 145); // black at 50% over white
        Assert.Equal(SKColors.White, rendered.GetPixel(20, 20));
    }

    [Fact]
    public void RepeatedTransparentRotatedTextIsRecognisedAsAWatermark()
    {
        // A watermark from another program: same tilted, see-through text on every page, without any tag.
        var pdf = TestPdf.Numbered(4);
        byte[] foreign;
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            using var fonts = new DocFonts(doc);
            fonts.Prepare("SAMPLE", null, true);
            for (var i = 0; i < doc.PageCount; i++)
            {
                using var page = doc.LoadPage(i);
                PageDraw.DrawText(page, fonts, "SAMPLE", new TextStyle(80, 0x40FF0000, Bold: true), 120, 560, 45);
                page.GenerateContent();
            }

            foreign = doc.Save();
        }

        var candidates = PdfWatermarkRemoval.Detect(foreign);
        var candidate = Assert.Single(candidates);
        Assert.Equal(WatermarkKind.Repeated, candidate.Kind);
        Assert.True(candidate.Confidence >= PdfWatermarkRemoval.AutoThreshold);
        Assert.Contains("SAMPLE", candidate.Description);

        var (cleaned, removed) = PdfWatermarkRemoval.Remove(foreign);
        Assert.Equal(4, removed);
        Assert.Equal(0, TestPdf.Difference(pdf, cleaned, 2), 0.0001);
    }

    [Fact]
    public void OrdinaryPageContentIsNotMistakenForAWatermark()
    {
        // The same heading on every page, but upright and opaque: not removed automatically.
        var pdf = TestPdf.WithText("Annual report", "Annual report", "Annual report");
        Assert.DoesNotContain(PdfWatermarkRemoval.Detect(pdf), c => c.Confidence >= PdfWatermarkRemoval.AutoThreshold);
        var (result, removed) = PdfWatermarkRemoval.Remove(pdf);
        Assert.Equal(0, removed);
        Assert.Same(pdf, result);
    }

    [Fact]
    public void PageNumbersFollowTheFormatAndStartNumber()
    {
        var pdf = PdfStamps.AddPageNumbers(TestPdf.Numbered(3), null, new PageNumberOptions { Format = "{n} / {total}", StartNumber = 5 });
        Assert.Contains("5 / 7", TestPdf.Text(pdf, 0));
        Assert.Contains("7 / 7", TestPdf.Text(pdf, 2));
    }

    [Theory]
    [InlineData(StampPosition.BottomCenter, 0.5, 0.96)]
    [InlineData(StampPosition.TopLeft, 0.06, 0.04)]
    [InlineData(StampPosition.BottomRight, 0.94, 0.96)]
    public void PageNumbersLandWhereAsked(StampPosition position, double expectedX, double expectedY)
    {
        var pdf = PdfStamps.AddPageNumbers(TestPdf.WithText(""), null, new PageNumberOptions { Position = position });
        var run = Assert.Single(PdfTextEdit.GetTextRuns(pdf, 0));
        Assert.Equal("1", run.Text);
        Assert.Equal(expectedX, run.Bounds.CenterX / TestPdf.A4Width, 0.03);
        Assert.Equal(expectedY, run.Bounds.CenterY / TestPdf.A4Height, 0.03);
    }

    [Fact]
    public void PageNumbersStayUprightAndInTheCornerOnRotatedPages()
    {
        var rotated = PdfPages.Rotate(TestPdf.WithText(""), null, 90);
        var pdf = PdfStamps.AddPageNumbers(rotated, null, new PageNumberOptions { Format = "Page {n}", Position = StampPosition.BottomRight });
        var run = Assert.Single(PdfTextEdit.GetTextRuns(pdf, 0));
        Assert.True(run.Bounds.Width > run.Bounds.Height * 2, "text should read left to right as displayed");
        Assert.True(run.Bounds.Right > TestPdf.A4Height - 40 && run.Bounds.Bottom > TestPdf.A4Width - 40);
    }

    [Fact]
    public void MetadataCanBeWrittenReadAndRemoved()
    {
        var pdf = PdfMetadataOps.Write(TestPdf.Numbered(1), new PdfMetadata
        {
            Title = "Quarterly résumé", Author = "Ada", Subject = "Тест", Keywords = "a, b", Creator = "Unit test"
        });

        var read = PdfMetadataOps.Read(pdf);
        Assert.Equal(("Quarterly résumé", "Ada", "Тест", "a, b", "Unit test"),
            (read.Title, read.Author, read.Subject, read.Keywords, read.Creator));

        var stripped = PdfMetadataOps.Read(PdfMetadataOps.RemoveAll(pdf));
        Assert.Equal(new PdfMetadata(), stripped);
        Assert.Equal("Page 1", TestPdf.Text(PdfMetadataOps.RemoveAll(pdf), 0));
    }

    [Fact]
    public void ProtectedDocumentsNeedTheirPasswordAndCanBeUnlocked()
    {
        var pdf = TestPdf.WithText("Secret");
        var locked = PdfSecurity.Protect(pdf, "pässword");

        Assert.False(PdfPages.NeedsPassword(pdf));
        Assert.True(PdfPages.NeedsPassword(locked));
        Assert.Throws<PdfPasswordException>(() => PdfPages.Unlock(locked, "wrong"));

        var unlocked = PdfPages.Unlock(locked, "pässword");
        Assert.False(PdfPages.NeedsPassword(unlocked));
        Assert.Equal("Secret", TestPdf.Text(unlocked, 0));
    }

    [Fact]
    public void CompressShrinksImageHeavyDocumentsAndKeepsThemReadable()
    {
        // 300 dpi photo-like page: detailed enough that lossless storage is large.
        using var photo = Photo(1654, 2339);
        var pdf = PdfPages.Merge([TestPdf.FromImage(photo, 300), TestPdf.WithText("Text page")]);

        var result = PdfCompress.Compress(pdf, new CompressOptions { JpegQuality = 60, MaxImageDpi = 100 });

        Assert.Equal(1, result.ImagesRecompressed);
        Assert.True(result.NewSize < result.OriginalSize / 4, $"{result.NewSize} of {result.OriginalSize} bytes");
        Assert.Equal("Text page", TestPdf.Text(result.Pdf, 1));
        Assert.True(TestPdf.Difference(pdf, result.Pdf) < 0.05, "the page should still look the same");
    }

    [Fact]
    public void CompressNeverGrowsADocumentAndReturnsTheInputWhenThereIsNothingToGain()
    {
        var pdf = TestPdf.Numbered(2);
        var once = PdfCompress.Compress(pdf);
        Assert.True(once.NewSize <= pdf.Length);
        Assert.Equal("Page 2", TestPdf.Text(once.Pdf, 1));

        var twice = PdfCompress.Compress(once.Pdf);
        Assert.Same(once.Pdf, twice.Pdf);
        Assert.Equal(0, twice.SavedRatio);
    }

    [Fact]
    public void CompressCanConvertImagesToGrayscale()
    {
        using var photo = Photo(800, 800);
        var pdf = TestPdf.FromImage(photo, 150);
        var result = PdfCompress.Compress(pdf, new CompressOptions { Grayscale = true, MaxImageDpi = 0 });
        using var rendered = TestPdf.Render(result.Pdf, 0);
        var pixel = rendered.GetPixel(rendered.Width / 3, rendered.Height / 3);
        Assert.InRange(Math.Abs(pixel.Red - pixel.Blue), 0, 3);
    }

    [Fact]
    public void IdenticalDocumentsCompareAsIdentical()
    {
        var pdf = TestPdf.Numbered(3);
        var result = PdfCompare.Compare(pdf, (byte[])pdf.Clone());
        Assert.True(result.Identical);
        Assert.Equal(0, result.DifferentPages);
    }

    [Fact]
    public void CompareReportsChangedWordsAndExtraPages()
    {
        var first = TestPdf.WithText("The quick brown fox", "Same page");
        var second = TestPdf.WithText("The slow brown fox", "Same page", "Appendix");

        var result = PdfCompare.Compare(first, second);

        Assert.False(result.Identical);
        Assert.Equal(2, result.DifferentPages);
        Assert.Equal([new TextChange(TextChangeKind.Removed, "quick"), new TextChange(TextChangeKind.Added, "slow")], result.Pages[0].TextChanges);
        Assert.True(result.Pages[0].PixelDifference > 0);
        Assert.True(result.Pages[1].Identical);
        Assert.True(result.Pages[2] is { InFirst: false, InSecond: true });
        Assert.Equal("Appendix", Assert.Single(result.Pages[2].TextChanges).Text);

        using var overlay = PdfCompare.RenderDifference(first, second, 0);
        var colours = Enumerable.Range(0, overlay.Width).SelectMany(x => Enumerable.Range(120, 110).Select(y => overlay.GetPixel(x, y))).ToHashSet();
        Assert.Contains(new SKColor(0xD9, 0x2D, 0x20), colours);
        Assert.Contains(new SKColor(0x12, 0xA1, 0x50), colours);
    }

    [Fact]
    public void WordDiffHandlesInsertionsAtTheEdges()
    {
        Assert.Equal([new TextChange(TextChangeKind.Added, "new start")], PdfCompare.DiffWords("a b c", "new start a b c"));
        Assert.Equal([new TextChange(TextChangeKind.Removed, "c d")], PdfCompare.DiffWords("a b c d", "a b"));
        Assert.Empty(PdfCompare.DiffWords("a  b\nc", "a b c"));
    }

    internal static SKBitmap Photo(int width, int height)
    {
        // Smooth gradients plus fine texture: compresses like a photograph, not like flat artwork.
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var random = new Random(3);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var n = random.Next(-18, 19);
            var i = (y * width + x) * 4;
            pixels[i] = (byte)Math.Clamp(128 + 100 * Math.Sin((x + y) / 120.0) + n, 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(128 + 100 * Math.Cos(y / 70.0) + n, 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(128 + 100 * Math.Sin(x / 90.0) + n, 0, 255);
            pixels[i + 3] = 255;
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }
}
