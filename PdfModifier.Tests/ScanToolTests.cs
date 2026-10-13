using PdfModifier.Core;
using PdfModifier.Core.Imaging;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Tests;

/// <summary>The tools must also work when a page is a picture: there is nothing on it to take away object by object.</summary>
public class ScanToolTests
{
    /// <summary>
    /// A scan with dark text and, baked into the same picture, a large mark across it. Printed marks and ink stamps
    /// tint what they cross (<paramref name="printed"/>); a mark added on a computer afterwards lies over it.
    /// </summary>
    private static SKBitmap MarkedScan(SKColor mark, bool printed = true, byte paper = 240, int width = 1240, int height = 1754)
    {
        var scan = TestPdf.FakeScan(width, height, paper: paper);
        using var canvas = new SKCanvas(scan);
        using var paint = new SKPaint
        {
            Color = mark, TextSize = width / 6f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold),
            BlendMode = printed ? SKBlendMode.Multiply : SKBlendMode.SrcOver
        };
        canvas.RotateDegrees(-30, width / 2f, height / 2f);
        canvas.DrawText("SAMPLE", width * 0.15f, height / 2f, paint);
        canvas.DrawText("SAMPLE", width * 0.15f, height * 0.75f, paint);
        return scan;
    }

    /// <summary>The page drawn one pixel per pixel of a 150 dpi scan, so it can be compared with the scan point by point.</summary>
    private static SKBitmap AsScanned(byte[] pdf) => TestPdf.Render(pdf, 0, 150 / 72.0);

    /// <summary>
    /// Looks at the pixels where the page showed the mark and at those where it showed writing, and tells how many of
    /// each are, after the clean-up, what they should be: paper where the mark was, still dark where the writing is.
    /// </summary>
    private static (int Mark, int MarkGone, int Writing, int WritingKept) Compare(SKBitmap before, SKBitmap after, Func<SKColor, bool> isMark,
        int paper, SKRectI? within = null)
    {
        int mark = 0, gone = 0, writing = 0, kept = 0;
        var area = within ?? new SKRectI(0, 0, before.Width, before.Height);
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
        {
            var was = before.GetPixel(x, y);
            var now = after.GetPixel(x, y);
            if (isMark(was))
            {
                mark++;
                if (Math.Abs(now.Red - paper) < 14 && Math.Abs(now.Green - paper) < 14 && Math.Abs(now.Blue - paper) < 14) gone++;
            }
            else if (was.Red < 70 && was.Green < 70 && was.Blue < 70)
            {
                writing++;
                if (now.Red < 110 && now.Green < 110 && now.Blue < 110) kept++;
            }
        }

        return (mark, gone, writing, kept);
    }

    /// <summary>The flat gray a printed mark of tone 196 shows on paper of tone 240, give or take what JPEG does to it.</summary>
    private static bool IsGrayMark(SKColor p) => Math.Abs(p.Red - 184) < 3 && Math.Abs(p.Green - 184) < 3 && Math.Abs(p.Blue - 184) < 3;

    [Fact]
    public void ALightMarkThatIsPartOfAScanIsFadedOutWhileTheTextStays()
    {
        using var scan = MarkedScan(new SKColor(196, 196, 196));
        var pdf = TestPdf.FromImage(scan);

        // There is no object to remove: the mark is pixels of the page picture.
        Assert.DoesNotContain(PdfWatermarkRemoval.Detect(pdf), c => c.Confidence >= PdfWatermarkRemoval.AutoThreshold);

        var (cleaned, pages) = PdfWatermarkRemoval.RemoveFromScans(pdf, null, new ScanMarkOptions { Kind = ScanMarkKind.Light, Strength = 0.5 });

        Assert.Equal(1, pages);
        using var before = AsScanned(pdf);
        using var after = AsScanned(cleaned);
        var (mark, gone, writing, kept) = Compare(before, after, IsGrayMark, 240);
        Assert.True(mark > 50_000, $"the fixture should show the mark ({mark} pixels)");
        Assert.True(gone > mark * 0.975, $"{gone} of {mark} mark pixels are paper again");
        Assert.True(kept > writing * 0.98, $"{kept} of {writing} dark pixels are still dark");
        // The paper keeps its own tone; the page is not bleached.
        Assert.InRange(after.GetPixel(30, 30).Red, 234, 246);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AColouredStampOnAScanIsRemovedAndTheTextUnderItComesBack(bool realInk)
    {
        // A stamp of real ink tints what it runs across; one added on a computer afterwards partly covers it.
        using var scan = realInk ? MarkedScan(new SKColor(235, 120, 120)) : MarkedScan(new SKColor(215, 60, 60, 170), printed: false);
        var pdf = TestPdf.FromImage(scan);
        static bool IsRed(SKColor p) => p.Red > p.Green + 14 && p.Red > p.Blue + 14;
        // The colour as someone would pick it from the page: the middle of a letter of the stamp, on plain paper.
        using var before = AsScanned(pdf);
        var picked = Enumerable.Range(0, before.Height / 3).SelectMany(y => Enumerable.Range(0, before.Width / 3).Select(x => before.GetPixel(x * 3, y * 3)))
            .Where(p => IsRed(p) && p.Green > 90).GroupBy(p => (uint)p).MaxBy(g => g.Count())!.First();

        var (cleaned, pages) = PdfWatermarkRemoval.RemoveFromScans(pdf, null,
            new ScanMarkOptions { Kind = ScanMarkKind.Colored, Color = (uint)picked, Strength = 0.5 });

        Assert.Equal(1, pages);
        using var after = AsScanned(cleaned);
        int red = 0, stillRed = 0, flat = 0, paperAgain = 0, crossed = 0, darkAgain = 0;
        for (var y = 0; y < before.Height; y++)
        for (var x = 0; x < before.Width; x++)
        {
            var was = before.GetPixel(x, y);
            if (!IsRed(was)) continue;
            red++;
            var now = after.GetPixel(x, y);
            if (IsRed(now)) stillRed++;
            if (Math.Abs(was.Red - picked.Red) < 6 && Math.Abs(was.Green - picked.Green) < 6)
            {
                // The stamp on bare paper: the paper has to be back, in its own tone.
                flat++;
                if (Math.Abs(now.Red - 240) < 12 && Math.Abs(now.Blue - 240) < 12) paperAgain++;
            }
            else if (was.Green < (realInk ? 24 : 60))
            {
                // The stamp right across the middle of a letter: the letter has to be back, dark and gray.
                crossed++;
                if (now.Red < 100 && Math.Abs(now.Red - now.Blue) < 30) darkAgain++;
            }
        }

        Assert.True(flat > 50_000 && crossed > 2_000, $"the fixture should show the stamp, also on text ({flat} / {crossed} pixels)");
        Assert.True(stillRed < red * 0.01, $"{stillRed} of {red} stamp pixels are still red");
        Assert.True(paperAgain > flat * 0.995, $"{paperAgain} of {flat} pixels of the stamp on paper are paper again");
        Assert.True(darkAgain > crossed * 0.9, $"{darkAgain} of {crossed} pixels where the stamp crossed text are text again");
        var (_, _, writing, kept) = Compare(before, after, _ => false, 240);
        Assert.True(kept > writing * 0.98, $"{kept} of {writing} dark pixels are still dark");
    }

    [Fact]
    public void AGrayMarkPickedByItsColourIsRemovedByItsLightness()
    {
        using var scan = MarkedScan(new SKColor(196, 196, 196));
        var image = PlanarImage.FromBitmap(scan, false);
        Assert.True(ImageFilters.RemoveColoredMarks(image, [184, 184, 184], 0.3));
        using var after = image.ToBitmap();
        using var same = scan.Copy(after.ColorType);
        var (mark, gone, writing, kept) = Compare(same, after, IsGrayMark, 240);
        Assert.True(gone > mark * 0.975 && kept > writing * 0.98, $"{gone}/{mark} mark, {kept}/{writing} writing");
        // A "mark" in the colour of the paper is none.
        Assert.False(ImageFilters.RemoveColoredMarks(PlanarImage.FromBitmap(scan, false), [240, 240, 240], 0.3));
    }

    [Fact]
    public void RemovalCanBeLimitedToAnAreaOfThePage()
    {
        using var scan = MarkedScan(new SKColor(196, 196, 196));
        var pdf = TestPdf.FromImage(scan);
        var info = PdfPages.GetPageInfos(pdf)[0];
        var upper = new RectD(0, 0, info.Width, info.Height * 0.6);

        var (cleaned, _) = PdfWatermarkRemoval.RemoveFromScans(pdf, null, new ScanMarkOptions { Kind = ScanMarkKind.Light, Area = upper });

        using var before = AsScanned(pdf);
        using var after = AsScanned(cleaned);
        var inside = Compare(before, after, IsGrayMark, 240, new SKRectI(0, 0, scan.Width, (int)(scan.Height * 0.59)));
        var outside = Compare(before, after, IsGrayMark, 240, new SKRectI(0, (int)(scan.Height * 0.61), scan.Width, scan.Height));
        Assert.True(inside.Mark > 10_000 && outside.Mark > 10_000, $"{inside.Mark} / {outside.Mark} mark pixels in the fixture");
        Assert.True(inside.MarkGone > inside.Mark * 0.975, $"{inside.MarkGone} of {inside.Mark} mark pixels inside the area are gone");
        Assert.True(outside.MarkGone < outside.Mark * 0.01, $"{outside.MarkGone} of {outside.Mark} mark pixels outside the area were touched");
    }

    [Fact]
    public void AMarkInAFixedPlaceIsCoveredWithThePaperColourOnEveryPageAndStaysAdjustable()
    {
        // A scanner app's footer, the same on each page.
        using var scan = TestPdf.FakeScan(620, 877, paper: 224);
        using (var canvas = new SKCanvas(scan))
        using (var paint = new SKPaint { Color = new SKColor(20, 90, 200), TextSize = 22, IsAntialias = true })
            canvas.DrawText("Scanned with SomeScanner", 330, 850, paint);
        var page = TestPdf.FromImage(scan);
        var pdf = PdfPages.Merge([page, page, page]);
        var area = new RectD(150, 395, 295, 415);

        var (covered, pages) = PdfWatermarkRemoval.RemoveFromScans(pdf, [0, 2], new ScanMarkOptions { Kind = ScanMarkKind.Cover, Area = area });

        Assert.Equal(2, pages);
        static int Blue(SKBitmap page) =>
            Enumerable.Range(0, page.Height).Sum(y => Enumerable.Range(0, page.Width).Count(x => page.GetPixel(x, y) is var p && p.Blue > p.Red + 50));
        using var first = TestPdf.Render(covered, 0, 2);
        using var second = TestPdf.Render(covered, 1, 2);
        Assert.Equal(0, Blue(first));
        Assert.True(Blue(second) > 200, "the page that was not selected keeps its footer");
        Assert.InRange(first.GetPixel((int)(area.CenterX * 2), (int)(area.CenterY * 2)).Red, 218, 230);

        // The cover is an object like any other the app adds: it can be moved, recoloured or taken away later.
        var patch = Assert.IsType<PagePatch>(Assert.Single(PdfPageObjects.Read(covered, 0)));
        Assert.Equal(area.Left, patch.Bounds.Left, 0.1);
        Assert.Empty(PdfPageObjects.Read(covered, 1));
        Assert.Throws<ArgumentException>(() => PdfWatermarkRemoval.RemoveFromScans(pdf, null, new ScanMarkOptions { Kind = ScanMarkKind.Cover }));
    }

    [Fact]
    public void PagesWithoutPicturesAreLeftAlone()
    {
        var pdf = TestPdf.Numbered(2);
        var (result, pages) = PdfWatermarkRemoval.RemoveFromScans(pdf, null, new ScanMarkOptions());
        Assert.Equal(0, pages);
        Assert.Same(pdf, result);
    }

    [Fact]
    public void TheScansOfADocumentAreNotMistakenForAWatermarkButARepeatedPictureIs()
    {
        // Three different page scans, all filling their page: same place, different pictures.
        var pdf = PdfPages.Merge(Enumerable.Range(0, 3).Select(i =>
        {
            using var scan = TestPdf.FakeScan(620, 877, text: "Page number " + (i + 1) + " of the scanned document");
            return TestPdf.FromImage(scan);
        }).ToList());
        Assert.Empty(PdfWatermarkRemoval.Detect(pdf));

        // The same logo stamped on each of them is found, and removing it leaves the scans as they were.
        using var logo = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Bgra8888, SKAlphaType.Opaque));
        logo.Erase(SKColors.DarkGreen);
        var marked = PdfStamps.AddWatermark(pdf, null,
            new WatermarkOptions { ImageData = PdfImages.Encode(logo, ImageFormat.Png), Opacity = 0.4, RotationDegrees = 20, ImageScale = 0.5 });
        Assert.True(TestPdf.Difference(pdf, marked, 1) > 0.05, "an image watermark must show on a scanned page");

        var candidate = Assert.Single(PdfWatermarkRemoval.Detect(marked));
        Assert.Equal(3, candidate.PageCount);
        var (cleaned, removed) = PdfWatermarkRemoval.Remove(marked);
        Assert.Equal(3, removed);
        Assert.Equal(0, TestPdf.Difference(pdf, cleaned, 2), 0.001);
    }

    [Fact]
    public void AnUntaggedPictureRepeatedOnEveryScanIsOfferedForRemoval()
    {
        // As another program would have added it: no tag, just the same see-through picture in the same place.
        using var logo = new SKBitmap(new SKImageInfo(300, 300, SKColorType.Bgra8888, SKAlphaType.Opaque));
        logo.Erase(SKColors.OrangeRed);
        var pages = Enumerable.Range(0, 4).Select(i =>
        {
            using var scan = TestPdf.FakeScan(620, 877, text: "Scanned page " + (i + 1));
            return TestPdf.ScanWith(scan, over: (page, _) => PageDraw.DrawImage(page, logo, new RectD(60, 120, 240, 300), 0, 0.35));
        }).ToList();
        var pdf = PdfPages.Merge(pages);

        var candidate = Assert.Single(PdfWatermarkRemoval.Detect(pdf));
        Assert.Equal(WatermarkKind.Repeated, candidate.Kind);
        Assert.Equal("Image", candidate.Description);
        Assert.Equal(4, candidate.PageCount);

        var (cleaned, removed) = PdfWatermarkRemoval.Remove(pdf, null, [candidate.Key]);
        Assert.Equal(4, removed);
        using var rendered = TestPdf.Render(cleaned, 3);
        Assert.InRange(rendered.GetPixel(75, 60).Red, 205, 225); // paper again where the logo was
        Assert.Equal(rendered.GetPixel(75, 60).Red, rendered.GetPixel(75, 60).Blue);
    }

    [Fact]
    public void APageScannedInStripsIsCleanedUpAsOnePicture()
    {
        using var scan = TestPdf.FakeScan(1240, 1754, paper: 205);
        // Six strips, each a sixth of the page: none is big enough to count as "the scan" on its own.
        const int strips = 6;
        var pdf = TestPdf.Compose((page, _) =>
        {
            for (var i = 0; i < strips; i++)
            {
                using var strip = new SKBitmap();
                int top = scan.Height * i / strips, bottom = scan.Height * (i + 1) / strips;
                scan.ExtractSubset(strip, new SKRectI(0, top, scan.Width, bottom));
                using var copy = strip.Copy();
                PageDraw.DrawImage(page, copy, new RectD(0, top * 72.0 / 150, 595.2, bottom * 72.0 / 150));
            }
        }, 595.2, 841.92);

        var result = PdfEnhance.Enhance(pdf, null, new EnhanceOptions { Sharpen = false });

        Assert.Equal(strips, result.ImagesEnhanced);
        using var rendered = TestPdf.Render(result.Pdf, 0, 2);
        // White paper in every strip, in the same white: no banding from strip to strip.
        var paper = Enumerable.Range(0, strips).Select(i => (int)rendered.GetPixel(40, rendered.Height * (2 * i + 1) / (2 * strips)).Red).ToList();
        Assert.All(paper, value => Assert.InRange(value, 248, 255));
        Assert.True(TestPdf.InkRatio(rendered) > 0.02, "the text must survive");
    }

    [Fact]
    public void WhatTheAutomaticCropWouldKeepCanBeAskedBeforehand()
    {
        var pdf = TestPdf.WithText("Tight crop");
        var run = Assert.Single(PdfTextEdit.GetTextRuns(pdf, 0));
        var area = Assert.NotNull(PdfPages.AutoCropArea(pdf, 0, padding: 10));
        Assert.Equal(run.Bounds.Left - 10, area.Left, 2.0);
        Assert.Equal(run.Bounds.Bottom + 10, area.Bottom, 2.0);
        Assert.Null(PdfPages.AutoCropArea(TestPdf.WithText(""), 0));

        var info = PdfPages.GetPageInfos(PdfPages.AutoCrop(pdf, null, 10))[0];
        Assert.Equal(area.Width, info.Width, 0.5);
        Assert.Equal(area.Height, info.Height, 0.5);
    }
}
