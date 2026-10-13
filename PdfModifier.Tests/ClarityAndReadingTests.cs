using PdfModifier.Core;
using PdfModifier.Core.Imaging;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Services;
using SkiaSharp;

namespace PdfModifier.Tests;

/// <summary>
/// Pages that are hard on the eye and on text recognition (out of focus, pale, coarse, unevenly lit): what Improve
/// clarity makes of them, and what a careful reading gets out of them.
/// </summary>
public class ClarityAndReadingTests
{
    private static bool Ready(params string[] languages) =>
        PdfOcr.EngineStatus().Available && TessData.Missing(languages).Count == 0;

    private static OcrSettings English => OcrSettings.DefaultFor("en");

    /// <summary>How steeply brightness changes from one pixel to the next, on average: high for crisp print, low for blurred.</summary>
    private static double Crispness(PlanarImage image)
    {
        var luma = image.Luminance();
        long sum = 0;
        for (var y = 0; y < image.Height; y++)
        for (var x = 1; x < image.Width; x++)
            sum += Math.Abs(luma[y * image.Width + x] - luma[y * image.Width + x - 1]);
        return (double)sum / luma.Length;
    }

    private static double Share(PlanarImage image, Func<byte, bool> of) => image.Luminance().Count(of) / (double)image.Width / image.Height;

    private static SKBitmap Colour(SKBitmap bitmap) => bitmap.ColorType == SKColorType.Bgra8888 ? bitmap : bitmap.Copy(SKColorType.Bgra8888);

    // ---- the filters ------------------------------------------------------------------------------------------------

    [Fact]
    public void BlurredPrintIsBroughtBackIntoFocus()
    {
        using var poor = TestPdf.PoorScan(noise: 0);
        var image = PlanarImage.FromBitmap(poor, true);
        var before = Crispness(image);

        ImageFilters.Deblur(image, 0.45);

        Assert.True(Crispness(image) > before * 1.6, $"{before:0.00} before, {Crispness(image):0.00} after");
        // The strokes got darker, the paper stayed what it was.
        var paper = image.Luminance().GroupBy(v => v).MaxBy(g => g.Count())!.Key;
        Assert.InRange(paper, 200, 210);
        Assert.True(image.Luminance().Min() < 60);
    }

    [Fact]
    public void FocusingKeepsTheColoursOfAColouredPage()
    {
        using var poor = TestPdf.PoorScan(noise: 0);
        using (var canvas = new SKCanvas(poor))
        using (var red = new SKPaint { Color = new SKColor(200, 40, 40) })
            canvas.DrawRect(SKRect.Create(900, 700, 200, 200), red);
        var image = PlanarImage.FromBitmap(poor, false);
        Assert.False(image.IsGray);

        ImageFilters.Deblur(image, 0.5);

        using var focused = image.ToBitmap();
        var patch = focused.GetPixel(1000, 800);
        Assert.InRange(patch.Red, 190, 210);
        Assert.InRange(patch.Green, 30, 50);
    }

    [Fact]
    public void ThinPrintGetsHeavier()
    {
        using var thin = TestPdf.PoorScan(blur: 0.4, noise: 0, paper: 255, ink: 120, size: 20);
        var image = PlanarImage.FromBitmap(thin, true);
        var before = Share(image, v => v < 170);

        ImageFilters.Thicken(image, 0.6);

        Assert.True(Share(image, v => v < 170) > before * 1.4, $"{before:P2} before, {Share(image, v => v < 170):P2} after");
        Assert.Equal(255, image.Luminance()[0]);
    }

    [Fact]
    public void UnevenLightIsEvenedOutInColour()
    {
        using var page = new SKBitmap(new SKImageInfo(900, 600, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(page))
        {
            canvas.Clear(SKColors.White);
            using var blue = new SKPaint { Color = new SKColor(30, 60, 200) };
            canvas.DrawRect(SKRect.Create(700, 250, 30, 30), blue);
            // A shadow that deepens towards the right, as from a hand holding the camera.
            using var shade = new SKPaint();
            shade.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(900, 0),
                [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 130)], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, 900, 600, shade);
        }

        var image = PlanarImage.FromBitmap(page, false);
        ImageFilters.EvenLight(image);

        using var evened = image.ToBitmap();
        Assert.True(evened.GetPixel(50, 300).Red > 245);
        Assert.True(evened.GetPixel(850, 300).Red > 245, "the paper in the shadow is white again");
        var mark = evened.GetPixel(715, 265);
        Assert.True(mark.Blue > mark.Red + 100, "and the blue mark is still blue");
    }

    [Fact]
    public void CoarseScansCanBeDoubledAndFineOnesAreLeftAtTheirSize()
    {
        using var scan = TestPdf.PoorScan(blur: 1.2, width: 620, height: 550, size: 12);
        var options = new EnhanceOptions { Deskew = false, Upscale = true, Deblur = 0.4 };
        using (var doubled = PdfEnhance.EnhanceBitmap(scan, options))
            Assert.Equal((1240, 1100), (doubled.Width, doubled.Height));

        // In a document: a scan of 75 dots per inch is doubled, the same picture at 300 is fine enough already.
        var coarse = PdfEnhance.Enhance(TestPdf.FromImage(scan, 75), null, options).Pdf;
        var fine = PdfEnhance.Enhance(TestPdf.FromImage(scan, 300), null, options).Pdf;
        Assert.Equal(1240, SheetTests.Pictures(coarse, 0).Single().Width);
        Assert.Equal(620, SheetTests.Pictures(fine, 0).Single().Width);
        Assert.Equal(PdfPages.GetPageInfos(TestPdf.FromImage(scan, 75))[0].Width, PdfPages.GetPageInfos(coarse)[0].Width, 0.01);
    }

    [Fact]
    public void APartOfAPageCanBeDrawnOnItsOwn()
    {
        using var scan = TestPdf.FakeScan(620, 877);
        using var view = new PdfView(TestPdf.FromImage(scan, 75));
        var area = new RectD(100, 150, 400, 330);

        using var part = view.RenderArea(0, area, 2);
        using var whole = view.Render(0, 2);

        Assert.Equal((600, 360), (part.Width, part.Height));
        using var expected = new SKBitmap(part.Info);
        using (var canvas = new SKCanvas(expected)) canvas.DrawBitmap(whole, -200, -300);
        Assert.True(ImageAnalysis.DifferenceRatio(expected, part) < 0.002);
    }

    // ---- reading ------------------------------------------------------------------------------------------------------

    [Fact]
    public void BlurredPrintReadsBetterOnceItIsInFocus()
    {
        if (!Ready("en")) return;
        using var poor = TestPdf.PoorScan();
        var before = TestPdf.WordsRight(PdfOcr.Recognize(poor, English).Lines);

        // What "Blurry print" in Improve clarity does.
        using var cleared = PdfEnhance.EnhanceBitmap(poor, new EnhanceOptions { Deskew = false, Deblur = 0.45, WhitenStrength = 0.1, Thicken = 0.2, Sharpen = false });
        using var colour = Colour(cleared);
        var after = TestPdf.WordsRight(PdfOcr.Recognize(colour, English).Lines);

        Assert.True(after >= before + 20, $"{before} words right before, {after} after");
        Assert.True(after >= 65, $"{after} of 116 words right");
        if (!ReferenceEquals(colour, cleared)) colour.Dispose();
    }

    [Fact]
    public void ACarefulReadingGetsMoreOutOfAPoorScan()
    {
        if (!Ready("en")) return;
        using var poor = TestPdf.PoorScan();

        var standard = PdfOcr.Recognize(poor, English);
        var careful = PdfOcr.Recognize(poor, English with { Effort = OcrEffort.Careful });

        var (quick, slow) = (TestPdf.WordsRight(standard.Lines), TestPdf.WordsRight(careful.Lines));
        Assert.True(slow >= quick + 20, $"{quick} words right at once, {slow} with care");
        // Every line is still there once, where it is on the page.
        Assert.InRange(careful.Lines.Count, 15, 17);
        Assert.InRange(careful.Lines[0].Bounds.Bottom, 80, 106);

        // On a page that reads well there is nothing to gain and nothing is lost.
        using var good = TestPdf.PoorScan(blur: 0.6, noise: 2, paper: 240, ink: 30);
        Assert.Equal(116, TestPdf.WordsRight(PdfOcr.Recognize(good, English with { Effort = OcrEffort.Careful }).Lines));
    }

    [Fact]
    public void LinesThatWereMoreGuessedThanReadAreKnown()
    {
        if (!Ready("en")) return;
        using var poor = TestPdf.PoorScan();
        using var good = TestPdf.PoorScan(blur: 0.6, noise: 2, paper: 240, ink: 30);

        var doubtful = PdfOcr.Recognize(poor, English).Lines.Count(line => PdfOcr.Certainty(line) < PdfOcr.DoubtfulBelow);
        var none = PdfOcr.Recognize(good, English).Lines.Count(line => PdfOcr.Certainty(line) < PdfOcr.DoubtfulBelow);

        Assert.True(doubtful >= 8, $"{doubtful} lines in doubt on the poor scan");
        Assert.Equal(0, none);
    }

    [Fact]
    public void AMarkedPassageIsReadOnItsOwnAndComesBackInPlace()
    {
        if (!Ready("en")) return;
        using var poor = TestPdf.PoorScan();
        using var view = new PdfView(TestPdf.FromImage(poor, 150));
        // The third to the sixth line: baselines at 195.6, 248.4, 301.2 and 354 pixels, half as many points.
        var area = new RectD(30, 82, 420, 176);
        bool Inside(OcrLine line) => area.Contains(line.Bounds.CenterX, line.Bounds.CenterY);

        var whole = PdfOcr.RecognizePage(view, 0, English).Lines.Where(Inside).ToList();
        var passage = PdfOcr.RecognizeArea(view, 0, area, English).Lines;

        Assert.All(passage, line => Assert.True(Inside(line), $"{line.Text} at {line.Bounds}"));
        Assert.Equal(4, passage.Count);
        Assert.True(TestPdf.WordsRight(passage) >= TestPdf.WordsRight(whole) + 5,
            $"{TestPdf.WordsRight(whole)} words right in the reading of the page, {TestPdf.WordsRight(passage)} in the passage");
    }

    [Fact]
    public void MarkedPassagesGoIntoTheSearchLayer()
    {
        if (!Ready("en")) return;
        using var poor = TestPdf.PoorScan();
        var pdf = TestPdf.FromImage(poor, 150);
        var area = new RectD(30, 82, 420, 176);
        int Right(byte[] document) => TestPdf.Text(document, 0).Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Count(word => TestPdf.Sentences.Any(s => s.Split(' ').Contains(word.Trim('.', ','), StringComparer.OrdinalIgnoreCase)));

        var plain = PdfOcr.MakeSearchable(pdf, null, English).Pdf;
        var marked = PdfOcr.MakeSearchable(pdf, null, English, carefulAreas: new Dictionary<int, IReadOnlyList<RectD>> { [0] = [area] }).Pdf;

        Assert.True(Right(marked) >= Right(plain) + 3, $"{Right(plain)} words right without the mark, {Right(marked)} with it");
    }

    // ---- the list of images used before ---------------------------------------------------------------------------------

    [Fact]
    public void PinnedImagesStayAndOthersCanBeTakenOffTheList()
    {
        Settings.FilePath = Path.Combine(Path.GetTempPath(), "PdfModifier.Tests", Guid.NewGuid().ToString("N"), "settings.json");
        var settings = new Settings();
        settings.RememberImage("logo.png");
        settings.RememberImage("signature.png");
        Assert.Equal([("signature.png", false), ("logo.png", false)], settings.ImageHistory());

        settings.PinImage("logo.png", true);
        Assert.Equal([("logo.png", true), ("signature.png", false)], settings.ImageHistory());

        // However many others are used after it, the pinned one stays; the oldest of the others make way.
        for (var i = 0; i < 20; i++) settings.RememberImage($"scan{i}.png");
        settings.RememberImage("logo.png");
        var history = settings.ImageHistory().ToList();
        Assert.Equal(("logo.png", true), history[0]);
        Assert.Equal(13, history.Count);
        Assert.DoesNotContain(("signature.png", false), history);

        settings.ForgetImage("scan19.png");
        settings.PinImage("logo.png", false);
        Assert.Equal(("logo.png", false), settings.ImageHistory().First());
        Assert.DoesNotContain(settings.ImageHistory(), entry => entry.Path == "scan19.png");

        // And all of it is still so the next time the program starts.
        settings.PinImage("scan3.png", true);
        var again = Settings.Load();
        Assert.Equal(["scan3.png"], again.PinnedImages);
        Assert.Equal(settings.RecentImages, again.RecentImages);
    }
}
