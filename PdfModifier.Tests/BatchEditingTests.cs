using System.Diagnostics;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfPages = PdfModifier.Core.Operations.PdfPages;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace PdfModifier.Tests;

/// <summary>
/// Multi-page edits on large documents run in small scratch documents whose results are put back onto the original
/// pages. These tests force that path on small documents and compare it with the direct one.
/// </summary>
public class BatchEditingTests
{
    /// <summary>Runs an operation the direct way and again in batches of <paramref name="chunk"/> pages.</summary>
    private static (T Direct, T Batched) Both<T>(Func<T> operation, int chunk = 3)
    {
        var direct = operation();
        ContentEditor.ForcedChunkSize = chunk;
        try
        {
            return (direct, operation());
        }
        finally
        {
            ContentEditor.ForcedChunkSize = null;
        }
    }

    private static void AssertSamePictures(byte[] a, byte[] b)
    {
        var count = TestPdf.PageCount(a);
        Assert.Equal(count, TestPdf.PageCount(b));
        for (var i = 0; i < count; i++) Assert.Equal(0, TestPdf.Difference(a, b, i), 0.0001);
    }

    private static byte[] WithBookmarks(byte[] pdf, params int[] pages)
    {
        using var input = new MemoryStream(pdf);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (var page in pages) doc.Outlines.Add($"Chapter on page {page + 1}", doc.Pages[page], true);
        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    [Fact]
    public void PageNumbersLookTheSameEitherWay()
    {
        var pdf = TestPdf.Numbered(10);
        var (direct, batched) = Both(() => PdfStamps.AddPageNumbers(pdf, null, new PageNumberOptions { Format = "{n} / {total}" }));

        AssertSamePictures(direct, batched);
        for (var i = 0; i < 10; i++)
        {
            Assert.Contains($"Page {i + 1}", TestPdf.Text(batched, i));
            Assert.Contains($"{i + 1} / 10", TestPdf.Text(batched, i));
        }
    }

    [Fact]
    public void OnlyTheRequestedPagesChange()
    {
        var pdf = TestPdf.Numbered(8);
        var (_, batched) = Both(() => PdfStamps.AddPageNumbers(pdf, [1, 6], new PageNumberOptions { Format = "No. {n}" }), 1);
        Assert.Equal(["Page 1", "Page 3", "Page 8"], new[] { 0, 2, 7 }.Select(i => TestPdf.Text(batched, i)));
        Assert.Contains("No. 1", TestPdf.Text(batched, 1));
        Assert.Contains("No. 2", TestPdf.Text(batched, 6));
    }

    [Fact]
    public void BookmarksStillPointAtTheirPagesAfterBatchedEdits()
    {
        var pdf = WithBookmarks(TestPdf.Numbered(9), 0, 4, 8);
        Assert.Equal([0, 4, 8], PdfPages.GetBookmarkTargets(pdf));

        var (_, stamped) = Both(() => PdfStamps.AddWatermark(pdf, null, new WatermarkOptions { Text = "DRAFT" }));
        Assert.Equal([0, 4, 8], PdfPages.GetBookmarkTargets(stamped));

        var (_, numbered) = Both(() => PdfStamps.AddPageNumbers(stamped, null, new PageNumberOptions()), 2);
        Assert.Equal([0, 4, 8], PdfPages.GetBookmarkTargets(numbered));
        Assert.Contains("Page 5", TestPdf.Text(numbered, 4));
    }

    [Fact]
    public void WatermarksAddedInBatchesAreFoundAndRemovedAgain()
    {
        var pdf = TestPdf.Numbered(7);
        var (direct, batched) = Both(() => PdfStamps.AddWatermark(pdf, null, new WatermarkOptions { Text = "CONFIDENTIAL" }));
        AssertSamePictures(direct, batched);

        var (_, cleaned) = Both(() => PdfWatermarkRemoval.Remove(batched));
        Assert.Equal(7, cleaned.Removed);
        AssertSamePictures(pdf, cleaned.Pdf);
        Assert.Empty(PdfWatermarkRemoval.Detect(cleaned.Pdf));
    }

    [Fact]
    public void AnImageWatermarkIsStoredOnceHoweverManyPagesCarryIt()
    {
        using var logo = ContentOperationTests.Photo(500, 500);
        var png = PdfImages.Encode(logo, ImageFormat.Png);
        var pdf = TestPdf.Numbered(12);
        var options = new WatermarkOptions { ImageData = png, Opacity = 0.4, RotationDegrees = 0 };

        var (direct, batched) = Both(() => PdfStamps.AddWatermark(pdf, null, options));
        var onePage = PdfStamps.AddWatermark(pdf, [0], options);
        var imageCost = onePage.Length - pdf.Length;

        Assert.True(imageCost > 100_000, "the fixture image should be heavy enough to notice copies");
        Assert.True(direct.Length < pdf.Length + imageCost * 1.3, $"direct: {direct.Length} bytes for a {imageCost}-byte image");
        Assert.True(batched.Length < pdf.Length + imageCost * 1.3, $"batched: {batched.Length} bytes for a {imageCost}-byte image");
        AssertSamePictures(direct, batched);
    }

    [Fact]
    public void ResourcesSharedByAllPagesAreNotCopiedPerBatch()
    {
        // Every page shows the same heavy image through one shared object.
        using var logo = ContentOperationTests.Photo(600, 600);
        var shared = PdfStamps.AddWatermark(TestPdf.Numbered(12), null,
            new WatermarkOptions { ImageData = PdfImages.Encode(logo, ImageFormat.Png), Opacity = 1, RotationDegrees = 0 });

        var (direct, batched) = Both(() => PdfStamps.AddPageNumbers(shared, null, new PageNumberOptions()), 2);

        Assert.True(batched.Length < shared.Length * 1.25, $"{shared.Length} -> {batched.Length} bytes");
        AssertSamePictures(direct, batched);
    }

    [Fact]
    public void EnhancingAndCompressingScansWorkInBatches()
    {
        using var scan = TestPdf.FakeScan(900, 1270, skewDegrees: 2, paper: 200);
        var one = TestPdf.FromImage(scan);
        var pdf = PdfPages.Merge([one, TestPdf.WithText("Text between scans"), one, one]);

        var (direct, batched) = Both(() => PdfEnhance.Enhance(pdf), 2);
        Assert.Equal(3, direct.ImagesEnhanced);
        Assert.Equal(3, batched.ImagesEnhanced);
        AssertSamePictures(direct.Pdf, batched.Pdf);
        Assert.Equal("Text between scans", TestPdf.Text(batched.Pdf, 1));

        var (directSmall, batchedSmall) = Both(() => PdfCompress.Compress(pdf, new CompressOptions { MaxImageDpi = 72, JpegQuality = 50 }), 2);
        Assert.Equal(3, batchedSmall.ImagesRecompressed);
        Assert.True(batchedSmall.NewSize < pdf.Length / 2, $"{pdf.Length} -> {batchedSmall.NewSize} bytes");
        Assert.InRange(batchedSmall.NewSize, directSmall.NewSize * 0.8, directSmall.NewSize * 1.2);
        AssertSamePictures(directSmall.Pdf, batchedSmall.Pdf);
    }

    [Fact]
    public void IdenticalImagesFromMergedDocumentsAreFoldedByCompress()
    {
        using var photo = ContentOperationTests.Photo(700, 700);
        var one = TestPdf.FromImage(photo);
        var merged = PdfPages.Merge([one, one, one, one]);

        // Quality 100 at full resolution: nothing to gain from recompression, only from sharing the copies.
        var result = PdfCompress.Compress(merged, new CompressOptions { MaxImageDpi = 0, JpegQuality = 100 });

        Assert.True(result.NewSize < merged.Length * 0.4, $"{merged.Length} -> {result.NewSize} bytes");
        AssertSamePictures(merged, result.Pdf);
    }

    [Fact]
    public void RecognisedTextLayerIsAddedInBatches()
    {
        if (!PdfOcr.EngineStatus().Available || TessData.Missing(["en"]).Count > 0) return;
        using var scan = TestPdf.FakeScan(1240, 600);
        var one = TestPdf.FromImage(scan, 150);
        var pdf = PdfPages.Merge([one, one, one]);

        var (_, batched) = Both(() => PdfOcr.MakeSearchable(pdf, null, OcrSettings.DefaultFor("en")), 2);

        Assert.Equal(3, batched.PagesRecognised);
        for (var i = 0; i < 3; i++) Assert.Contains("quick brown fox", TestPdf.Text(batched.Pdf, i));
        AssertSamePictures(pdf, batched.Pdf);
    }

    [Fact]
    public void StampingALargeDocumentStaysFast()
    {
        var part = TestPdf.WithText(Enumerable.Range(1, 300).Select(i => $"Page {i} of a longer document").ToArray());
        var pdf = WithBookmarks(PdfPages.Merge([part, part, part]), 0, 450, 899);

        var watch = Stopwatch.StartNew();
        var numbered = PdfStamps.AddPageNumbers(pdf, null, new PageNumberOptions { Format = "{n} / {total}" });
        watch.Stop();

        // The direct way needs most of a minute for 900 pages; a generous bound still catches a relapse.
        Assert.True(watch.Elapsed.TotalSeconds < 20, $"took {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.Contains("900 / 900", TestPdf.Text(numbered, 899));
        Assert.Contains("451 / 900", TestPdf.Text(numbered, 450));
        Assert.Equal([0, 450, 899], PdfPages.GetBookmarkTargets(numbered));
        Assert.True(numbered.Length < pdf.Length * 2.2, $"{pdf.Length} -> {numbered.Length} bytes");
    }

    [Fact]
    public void ManyImagesBecomeADocumentQuickly()
    {
        using var small = new SKBitmap(new SKImageInfo(200, 280, SKColorType.Bgra8888, SKAlphaType.Opaque));
        small.Erase(SKColors.SteelBlue);
        var png = PdfImages.Encode(small, ImageFormat.Png);

        var watch = Stopwatch.StartNew();
        var pdf = PdfImages.InsertImages(TestPdf.Numbered(2), Enumerable.Repeat(png, 300).ToList(), 1);
        watch.Stop();

        Assert.Equal(302, TestPdf.PageCount(pdf));
        Assert.Equal("Page 1", TestPdf.Text(pdf, 0));
        Assert.Equal("Page 2", TestPdf.Text(pdf, 301));
        using var rendered = TestPdf.Render(pdf, 150);
        Assert.Equal(SKColors.SteelBlue, rendered.GetPixel(rendered.Width / 2, rendered.Height / 2));
        Assert.True(watch.Elapsed.TotalSeconds < 15, $"took {watch.Elapsed.TotalSeconds:0.0} s");
    }
}
