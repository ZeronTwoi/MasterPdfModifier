using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Tests;

/// <summary>
/// Pages come as text, as pictures of text, and as both at once. The tools have to tell which they are looking at.
/// </summary>
public class PageAnalysisTests
{
    private static PageContent Analyse(byte[] pdf)
    {
        using var view = new PdfView(pdf);
        return view.Analyse(0);
    }

    [Fact]
    public void ATextPageHasNoPicturesAndNothingHidden()
    {
        var content = Analyse(TestPdf.WithText("Plain typed text"));
        Assert.Equal(0, content.ImageCoverage);
        Assert.False(content.HasPictures);
        Assert.Equal(14, content.VisibleLetters);
        Assert.Empty(content.HiddenText);
        Assert.Empty(content.NestedText);
        var area = Assert.Single(content.TextAreas);
        Assert.Equal(72, area.Left, 3.0);
        Assert.False(PdfOcr.NeedsReading(content));
    }

    [Fact]
    public void AScanIsAllPicture()
    {
        using var scan = TestPdf.FakeScan(620, 877);
        var content = Analyse(TestPdf.FromImage(scan));
        Assert.Equal(1, content.ImageCoverage, 0.01);
        Assert.Equal(0, content.VisibleLetters);
        Assert.Empty(content.TextAreas);
        Assert.True(PdfOcr.NeedsReading(content));
    }

    [Fact]
    public void SmallPicturesDoNotMakeAPageAScan()
    {
        using var logo = new SKBitmap(new SKImageInfo(200, 100, SKColorType.Bgra8888, SKAlphaType.Opaque));
        logo.Erase(SKColors.Navy);
        var pdf = TestPdf.Compose((page, fonts) =>
        {
            PageDraw.DrawImage(page, logo, new RectD(400, 40, 520, 100));
            PageDraw.DrawText(page, fonts, "A letter with a logo in the corner", new TextStyle(14), 72, 200);
        });
        var content = Analyse(pdf);
        Assert.InRange(content.ImageCoverage, 0.01, 0.03);
        Assert.False(PdfOcr.NeedsReading(content));
    }

    [Fact]
    public void TextInsideANestedGraphicIsFoundLineByLine()
    {
        var inner = TestPdf.Compose((page, fonts) =>
        {
            PageDraw.DrawText(page, fonts, "Wrapped heading", new TextStyle(24), 72, 100);
            PageDraw.DrawText(page, fonts, "second wrapped line", new TextStyle(12), 72, 140);
        });
        var pdf = TestPdf.Nested(inner, (page, fonts) => PageDraw.DrawText(page, fonts, "Typed on top", new TextStyle(12), 72, 300));

        // Only the text that is the page's own can be edited in place...
        Assert.Equal("Typed on top", Assert.Single(PdfTextEdit.GetTextRuns(pdf, 0)).Text);

        // ...the rest is still known, with its words and where they are.
        var content = Analyse(pdf);
        Assert.Equal(["Wrapped heading", "second wrapped line"], content.NestedText.Select(l => l.Text));
        var heading = content.NestedText[0];
        Assert.Equal(72, heading.Bounds.Left, 3.0);
        Assert.Equal(100, heading.Bounds.Bottom, 6.0);
        Assert.InRange(heading.Bounds.Height, 14, 26);
        Assert.Equal(["Wrapped", "heading"], heading.Words.Select(w => w.Text));
        Assert.True(heading.Words[0].Bounds.Right < heading.Words[1].Bounds.Left);
        Assert.Empty(content.HiddenText);
        Assert.Equal(3, content.TextAreas.Count);
        Assert.Equal(10, content.VisibleLetters);
    }

    [Fact]
    public void TheSearchLayerOfAScanIsReadWithoutRecognisingAnything()
    {
        using var scan = TestPdf.FakeScan(620, 877);
        var pdf = TestPdf.ScanWith(scan, over: (page, fonts) =>
        {
            PageDraw.DrawText(page, fonts, "Recognised earlier, first line", new TextStyle(10, Invisible: true), 40, 60);
            PageDraw.DrawText(page, fonts, "and the second line", new TextStyle(10, Invisible: true), 40, 80);
        });

        var content = Analyse(pdf);

        Assert.True(content.HasPictures);
        Assert.Equal(["Recognised earlier, first line", "and the second line"], content.HiddenText.Select(l => l.Text));
        Assert.Equal(60, content.HiddenText[0].Bounds.Bottom, 4.0);
        Assert.Equal(0, content.VisibleLetters);
        Assert.Empty(content.TextAreas);
        Assert.Empty(PdfTextEdit.GetTextRuns(pdf, 0));

        // The page is searchable already, so making it searchable has nothing to do and needs no engine for that.
        Assert.False(PdfOcr.NeedsReading(content));
        var (result, recognised) = PdfOcr.MakeSearchable(pdf, null, OcrSettings.DefaultFor("en"));
        Assert.Equal(0, recognised);
        Assert.Same(pdf, result);
    }

    [Fact]
    public void TextPaintedOverByAScanCountsAsHidden()
    {
        using var scan = TestPdf.FakeScan(620, 877);
        var pdf = TestPdf.ScanWith(scan,
            under: (page, fonts) => PageDraw.DrawText(page, fonts, "Under the picture", new TextStyle(10), 40, 60),
            over: (page, fonts) => PageDraw.DrawText(page, fonts, "Stamped on top", new TextStyle(10), 40, 300));

        Assert.Equal("Stamped on top", Assert.Single(PdfTextEdit.GetTextRuns(pdf, 0)).Text);
        var content = Analyse(pdf);
        Assert.Equal("Under the picture", Assert.Single(content.HiddenText).Text);
        Assert.Equal(12, content.VisibleLetters);
    }

    [Fact]
    public void TextBoxesTheAppAddedAreNotCountedAsContentOfThePage()
    {
        using var scan = TestPdf.FakeScan(620, 877);
        var box = PdfPageObjects.Fit(new PageText { Id = 1, Text = "Added note", Bounds = new RectD(40, 40, 240, 50), FontSize = 12 });
        var pdf = PdfPageObjects.Write(TestPdf.FromImage(scan), 0, [box]);

        var content = Analyse(pdf);

        Assert.Equal(0, content.VisibleLetters);
        Assert.Empty(content.HiddenText);
        Assert.Empty(content.NestedText);
        Assert.Single(content.TextAreas); // but where it is, is known, so recognition does not add it a second time
        Assert.True(PdfOcr.NeedsReading(content));
    }

    [Fact]
    public void WordsThatAreNotInAnyRecognisedLineCountAsUnread()
    {
        using var scan = TestPdf.FakeScan(1240, 1754);
        Assert.InRange(PdfOcr.UnreadShare(scan, []), 0.99, 1.0);

        // Boxes over every line of the fixture: nothing is left unexplained.
        var lines = Enumerable.Range(0, 20).Select(i =>
        {
            var box = new RectD(130, 185 + i * 70, 1160, 232 + i * 70);
            return new OcrLine("line", box, [new OcrWord("line", box, 90)]);
        }).ToList();
        Assert.InRange(PdfOcr.UnreadShare(scan, lines), 0, 0.02);
        Assert.InRange(PdfOcr.UnreadShare(scan, lines.Take(10).ToList()), 0.45, 0.55);

        // Rules and frames are not writing; a blank page has nothing to read at all.
        using var ruled = new SKBitmap(new SKImageInfo(800, 800, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(ruled))
        using (var pen = new SKPaint { Color = SKColors.Black, StrokeWidth = 3, IsStroke = true })
        {
            canvas.Clear(SKColors.White);
            for (var y = 100; y < 800; y += 100) canvas.DrawLine(50, y, 750, y, pen);
            canvas.DrawRect(50, 50, 700, 700, pen);
        }

        Assert.Equal(0, PdfOcr.UnreadShare(ruled, []));
    }

    [Fact]
    public void UnevenLightingIsEvenedOut()
    {
        using var scan = TestPdf.FakeScan(800, 600, paper: 230);
        using (var canvas = new SKCanvas(scan))
        using (var shade = new SKPaint())
        {
            shade.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(800, 0),
                [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 150)], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, 800, 600, shade);
        }

        var before = scan.GetPixel(780, 40).Red; // paper in the shadow
        var flat = PdfModifier.Core.Imaging.ImageFilters.FlattenBackground(PdfModifier.Core.Imaging.PlanarImage.FromBitmap(scan, true));
        using var result = flat.ToBitmap();

        Assert.InRange(before, 80, 110);
        Assert.InRange(result.GetPixel(780, 40).Red, 240, 255);
        Assert.InRange(result.GetPixel(20, 40).Red, 240, 255);
        // The writing stays clearly darker than the paper on both sides.
        int Darkest(int x0) => Enumerable.Range(x0, 120).SelectMany(x => Enumerable.Range(200, 40).Select(y => (int)result.GetPixel(x, y).Red)).Min();
        Assert.True(Darkest(150) < 90 && Darkest(620) < 110, $"{Darkest(150)} / {Darkest(620)}");
    }
}
