using PdfModifier.Core;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;

namespace PdfModifier.Tests;

public class PageOperationTests
{
    [Fact]
    public void CreatedDocumentRoundTripsItsText()
    {
        var pdf = TestPdf.Numbered(3);
        Assert.Equal(3, TestPdf.PageCount(pdf));
        Assert.Equal("Page 2", TestPdf.Text(pdf, 1));
    }

    [Fact]
    public void MergeKeepsPagesInOrder()
    {
        var merged = PdfPages.Merge([TestPdf.WithText("A1", "A2"), TestPdf.WithText("B1"), TestPdf.WithText("C1", "C2")]);
        Assert.Equal(5, TestPdf.PageCount(merged));
        Assert.Equal(["A1", "A2", "B1", "C1", "C2"], Enumerable.Range(0, 5).Select(i => TestPdf.Text(merged, i)));
    }

    [Fact]
    public void MergeCanTakePartOfASource()
    {
        var merged = PdfPages.Merge([(TestPdf.Numbered(5), [4, 0]), (TestPdf.WithText("X"), null)]);
        Assert.Equal(["Page 5", "Page 1", "X"], Enumerable.Range(0, 3).Select(i => TestPdf.Text(merged, i)));
    }

    [Fact]
    public void ExtractReturnsRequestedPagesInRequestedOrder()
    {
        var extracted = PdfPages.Extract(TestPdf.Numbered(5), [3, 1]);
        Assert.Equal(["Page 4", "Page 2"], Enumerable.Range(0, 2).Select(i => TestPdf.Text(extracted, i)));
    }

    [Fact]
    public void SplitEveryTwoPagesGivesThreeFilesForFivePages()
    {
        var pdf = TestPdf.Numbered(5);
        var parts = PdfPages.Split(pdf, PdfPages.GroupsOf(5, 2));
        Assert.Equal([2, 2, 1], parts.Select(TestPdf.PageCount));
        Assert.Equal("Page 5", TestPdf.Text(parts[2], 0));
    }

    [Fact]
    public void DeleteRemovesPagesAndTheirContent()
    {
        // The deleted page carries a large image, so the file must shrink once its objects are really gone.
        using var noise = Noise(900, 900);
        var heavy = TestPdf.FromImage(noise);
        var pdf = PdfPages.Merge([TestPdf.WithText("Keep 1"), heavy, TestPdf.WithText("Keep 2")]);

        var result = PdfPages.Delete(pdf, [1]);

        Assert.Equal(["Keep 1", "Keep 2"], Enumerable.Range(0, 2).Select(i => TestPdf.Text(result, i)));
        Assert.True(result.Length < pdf.Length / 4, $"{result.Length} of {pdf.Length} bytes left");
    }

    [Fact]
    public void DeletingEveryPageIsRefused() =>
        Assert.Throws<PdfException>(() => PdfPages.Delete(TestPdf.Numbered(2), [0, 1]));

    [Fact]
    public void RotateTurnsThePageAndSwapsItsDisplayedSize()
    {
        var rotated = PdfPages.Rotate(TestPdf.Numbered(2), [0], 90);
        var infos = PdfPages.GetPageInfos(rotated);
        Assert.Equal(90, infos[0].RotationDegrees);
        Assert.Equal(TestPdf.A4Height, infos[0].Width, 0.5);
        Assert.Equal(TestPdf.A4Width, infos[0].Height, 0.5);
        Assert.Equal(0, infos[1].RotationDegrees);

        var back = PdfPages.Rotate(rotated, [0], -90);
        Assert.Equal(0, PdfPages.GetPageInfos(back)[0].RotationDegrees);
    }

    [Fact]
    public void ReorderAppliesThePermutation()
    {
        var reordered = PdfPages.Reorder(TestPdf.Numbered(4), [2, 0, 3, 1]);
        Assert.Equal(["Page 3", "Page 1", "Page 4", "Page 2"], Enumerable.Range(0, 4).Select(i => TestPdf.Text(reordered, i)));
    }

    [Fact]
    public void InsertBlankAddsAnEmptyPageAtThePosition()
    {
        var result = PdfPages.InsertBlank(TestPdf.Numbered(2), 1, 300, 400);
        Assert.Equal(3, TestPdf.PageCount(result));
        Assert.Equal("", TestPdf.Text(result, 1));
        var info = PdfPages.GetPageInfos(result)[1];
        Assert.Equal((300, 400), (info.Width, info.Height));
        Assert.Equal("Page 2", TestPdf.Text(result, 2));
    }

    [Fact]
    public void InsertPdfPlacesTheOtherDocumentsPagesBeforeTheIndex()
    {
        var result = PdfPages.InsertPdf(TestPdf.Numbered(2), TestPdf.WithText("X", "Y", "Z"), [2, 0], 1);
        Assert.Equal(["Page 1", "Z", "X", "Page 2"], Enumerable.Range(0, 4).Select(i => TestPdf.Text(result, i)));
    }

    [Fact]
    public void ScaleByFactorResizesPageAndContentTogether()
    {
        var pdf = TestPdf.WithText("Scale me");
        var scaled = PdfPages.Scale(pdf, null, ScaleMode.Factor, 0.5);

        var info = PdfPages.GetPageInfos(scaled)[0];
        Assert.Equal(TestPdf.A4Width / 2, info.Width, 0.5);
        Assert.Equal(TestPdf.A4Height / 2, info.Height, 0.5);
        Assert.Equal("Scale me", TestPdf.Text(scaled, 0));

        // Rendered at twice the zoom, the half-size page must look like the original.
        using var original = TestPdf.Render(pdf, 0, 1);
        using var half = TestPdf.Render(scaled, 0, 2);
        Assert.True(PdfModifier.Core.Imaging.ImageAnalysis.DifferenceRatio(original, half) < 0.01);
    }

    [Fact]
    public void ScaleToSizeFitsAndCentresTheContent()
    {
        // A square page into A4: content scales by the width ratio and is centred vertically.
        var pdf = TestPdf.WithText(400, 400, "Fit");
        var scaled = PdfPages.Scale(pdf, null, ScaleMode.FitToSize, targetWidth: TestPdf.A4Width, targetHeight: TestPdf.A4Height);
        var info = PdfPages.GetPageInfos(scaled)[0];
        Assert.Equal((TestPdf.A4Width, TestPdf.A4Height), (Math.Round(info.Width), Math.Round(info.Height)));

        var run = Assert.Single(PdfTextEdit.GetTextRuns(scaled, 0));
        var factor = TestPdf.A4Width / 400;
        Assert.Equal(72 * factor, run.Bounds.Left, 4.0);
        Assert.Equal((TestPdf.A4Height - 400 * factor) / 2 + 100 * factor, run.Bounds.Bottom, 4.0);
    }

    [Fact]
    public void ScaleWorksOnRotatedPages()
    {
        var rotated = PdfPages.Rotate(TestPdf.WithText("Turned"), null, 90);
        var scaled = PdfPages.Scale(rotated, null, ScaleMode.FitToSize, targetWidth: 421, targetHeight: 297.5);
        var info = PdfPages.GetPageInfos(scaled)[0];
        Assert.Equal((421, 298), (Math.Round(info.Width), Math.Round(info.Height)));
        using var before = TestPdf.Render(rotated, 0, 1);
        using var after = TestPdf.Render(scaled, 0, 2);
        Assert.True(PdfModifier.Core.Imaging.ImageAnalysis.DifferenceRatio(before, after) < 0.01);
    }

    [Fact]
    public void CropMarginsShrinksThePageAndKeepsWhatIsInside()
    {
        var pdf = TestPdf.WithText("Inside");
        var cropped = PdfPages.CropMargins(pdf, null, 50, 60, 70, 80);
        var info = PdfPages.GetPageInfos(cropped)[0];
        Assert.Equal(TestPdf.A4Width - 120, info.Width, 0.5);
        Assert.Equal(TestPdf.A4Height - 140, info.Height, 0.5);

        var run = Assert.Single(PdfTextEdit.GetTextRuns(cropped, 0));
        Assert.Equal(72 - 50, run.Bounds.Left, 4.0);
        Assert.Equal(100 - 60, run.Bounds.Bottom, 4.0);
    }

    [Fact]
    public void CropUsesWhatTheUserSeesOnARotatedPage()
    {
        var rotated = PdfPages.Rotate(TestPdf.WithText("Turned"), null, 90);
        using var before = TestPdf.Render(rotated, 0);
        var cropped = PdfPages.CropMargins(rotated, null, 100, 0, 0, 0);
        using var after = TestPdf.Render(cropped, 0);
        Assert.Equal(before.Width - 100, after.Width);
        Assert.Equal(before.Height, after.Height);
        // The remaining picture is the right-hand part of the original.
        Assert.Equal(before.GetPixel(before.Width - 5, 300), after.GetPixel(after.Width - 5, 300));
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2, 6, 9, 10, 11 }, "1-3, 7, 10-12")]
    [InlineData(new[] { 4 }, "5")]
    [InlineData(new[] { 3, 1, 2, 2 }, "2-4")]
    [InlineData(new int[0], "")]
    public void PageListsAreWrittenTheWayTheyAreTyped(int[] pages, string expected)
    {
        Assert.Equal(expected, PageRange.Format(pages));
        if (expected.Length > 0) Assert.Equal(pages.Distinct().Order(), PageRange.Parse(expected, 20));
    }

    [Fact]
    public void AutoCropTrimsToTheContentPlusPadding()
    {
        var pdf = TestPdf.WithText("Tight crop");
        var run = Assert.Single(PdfTextEdit.GetTextRuns(pdf, 0));
        var cropped = PdfPages.AutoCrop(pdf, null, padding: 10);
        var info = PdfPages.GetPageInfos(cropped)[0];
        Assert.Equal(run.Bounds.Width + 20, info.Width, 4.0);
        Assert.Equal(run.Bounds.Height + 20, info.Height, 4.0);
        Assert.Equal("Tight crop", TestPdf.Text(cropped, 0));
    }

    [Fact]
    public void AutoCropLeavesBlankPagesAlone()
    {
        var pdf = TestPdf.WithText("");
        var info = PdfPages.GetPageInfos(PdfPages.AutoCrop(pdf, null))[0];
        Assert.Equal((TestPdf.A4Width, TestPdf.A4Height), (info.Width, info.Height));
    }

    [Fact]
    public void FlattenProducesAValidDocument()
    {
        var flattened = PdfPages.Flatten(TestPdf.Numbered(2), null);
        Assert.Equal("Page 2", TestPdf.Text(flattened, 1));
    }

    [Theory]
    [InlineData("", new[] { 0, 1, 2, 3, 4 })]
    [InlineData("2", new[] { 1 })]
    [InlineData("1-3", new[] { 0, 1, 2 })]
    [InlineData("4-, 1", new[] { 3, 4, 0 })]
    [InlineData("-2", new[] { 0, 1 })]
    [InlineData("3-1", new[] { 2, 1, 0 })]
    [InlineData("4-99", new[] { 3, 4 })]
    public void PageRangesParse(string text, int[] expected) => Assert.Equal(expected, PageRange.Parse(text, 5));

    [Fact]
    public void PageRangeRejectsNonsense()
    {
        Assert.Throws<FormatException>(() => PageRange.Parse("a-b", 5));
        Assert.Throws<FormatException>(() => PageRange.Parse("0", 5));
    }

    [Fact]
    public void PageRangeGroupsSplitOnSemicolons() =>
        Assert.Equal([[0, 1], [4], [2, 3]], PageRange.ParseGroups("1-2; 5; 3-4", 5));

    internal static SkiaSharp.SKBitmap Noise(int width, int height)
    {
        var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque));
        var random = new Random(7);
        var bytes = new byte[width * height * 4];
        random.NextBytes(bytes);
        for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;
    }
}
