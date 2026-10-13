using PdfModifier.Core;
using PdfModifier.Core.Imaging;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Tests;

/// <summary>Mirroring pages, and finding the pages that lie on their side or stand on their head.</summary>
public class OrientationTests
{
    private static bool Ready(params string[] languages) =>
        PdfOcr.EngineStatus().Available && TessData.Missing(languages).Count == 0;

    /// <summary>A picture turned clockwise by quarter turns, as a scanner fed the wrong way round delivers it.</summary>
    internal static SKBitmap Turned(SKBitmap source, int quarters)
    {
        var sideways = quarters % 2 != 0;
        var result = new SKBitmap(new SKImageInfo(sideways ? source.Height : source.Width, sideways ? source.Width : source.Height,
            SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(result);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        canvas.RotateDegrees(quarters * 90);
        canvas.DrawBitmap(source, -source.Width / 2f, -source.Height / 2f);
        return result;
    }

    private static SKBitmap Flipped(SKBitmap source, bool topToBottom)
    {
        var result = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(result);
        if (topToBottom) canvas.Scale(1, -1, 0, source.Height / 2f);
        else canvas.Scale(-1, 1, source.Width / 2f, 0);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    /// <summary>A page that looks different in every corner, so a wrong flip or turn cannot pass for the right one.</summary>
    private static byte[] Lopsided() => TestPdf.Compose((page, fonts) =>
    {
        fonts.Prepare("Top left corner", null);
        PageDraw.DrawText(page, fonts, "Top left corner", new TextStyle(22), 50, 90);
        PageDraw.DrawRect(page, new RectD(380, 60, 540, 130), 0xFF2060C0);
        PageDraw.DrawRect(page, new RectD(60, 700, 130, 780), 0xFFC03020);
    });

    // ---- mirroring ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMirroredPageShowsTheSameTheOtherWayRound(bool topToBottom)
    {
        var pdf = PdfPages.Merge([Lopsided(), TestPdf.WithText("Second page")]);

        var mirrored = PdfPages.Mirror(pdf, [0], topToBottom);

        using var before = TestPdf.Render(pdf, 0, 2);
        using var expected = Flipped(before, topToBottom);
        using var after = TestPdf.Render(mirrored, 0, 2);
        Assert.True(ImageAnalysis.BlockDifference(expected, after) < 1.5, $"differs by {ImageAnalysis.BlockDifference(expected, after):0.0}");
        Assert.True(ImageAnalysis.BlockDifference(before, after) > 3);
        // The other page is as it was, and mirrored twice a page is itself again.
        Assert.Equal(0, TestPdf.Difference(pdf, mirrored, 1), 0.0001);
        using var again = TestPdf.Render(PdfPages.Mirror(mirrored, [0], topToBottom), 0, 2);
        Assert.True(ImageAnalysis.BlockDifference(before, again) < 1.5);
        Assert.Equal(PdfPages.GetPageInfos(pdf)[0], PdfPages.GetPageInfos(mirrored)[0]);
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(90, true)]
    [InlineData(180, false)]
    [InlineData(270, true)]
    public void APageThatIsShownTurnedIsMirroredTheWayItIsShown(int turned, bool topToBottom)
    {
        var pdf = PdfPages.Rotate(Lopsided(), null, turned);

        var mirrored = PdfPages.Mirror(pdf, null, topToBottom);

        using var before = TestPdf.Render(pdf, 0, 2);
        using var expected = Flipped(before, topToBottom);
        using var after = TestPdf.Render(mirrored, 0, 2);
        Assert.True(ImageAnalysis.BlockDifference(expected, after) < 1.5, $"differs by {ImageAnalysis.BlockDifference(expected, after):0.0}");
    }

    [Fact]
    public void AScanIsMirroredToo()
    {
        using var scan = TestPdf.FakeScan();
        var pdf = TestPdf.FromImage(scan, 150);

        var mirrored = PdfPages.Mirror(pdf, null);

        using var before = TestPdf.Render(pdf, 0);
        using var expected = Flipped(before, false);
        using var after = TestPdf.Render(mirrored, 0);
        Assert.True(ImageAnalysis.BlockDifference(expected, after) < 1.5);
    }

    // ---- pages of text ------------------------------------------------------------------------------------------

    private static byte[] Letter() => TestPdf.Compose((page, fonts) =>
    {
        const string line = "Invoices are payable within thirty days of receipt of the goods";
        fonts.Prepare(line, null);
        for (var i = 0; i < 12; i++) PageDraw.DrawText(page, fonts, line, new TextStyle(12), 60, 100 + i * 22);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void APageOfTextSaysItselfHowItHasToBeTurned(int turned)
    {
        var upright = Letter();
        var pdf = PdfPages.Rotate(upright, null, turned);
        using var view = new PdfView(pdf);

        var turn = PdfOrientation.Find(view, 0);

        Assert.Equal((360 - turned) % 360, turn.Degrees);
        Assert.Equal(TurnEvidence.Text, turn.Evidence);
        Assert.False(turn.Doubtful);
        Assert.True(turn.Certainty > 0.9);
        // Turned by what was found, the page is as it was written.
        var fixedUp = PdfPages.Rotate(pdf, null, turn.Degrees);
        Assert.Equal(0, PdfPages.GetPageInfos(fixedUp)[0].RotationDegrees);
        Assert.Equal(0, TestPdf.Difference(upright, fixedUp), 0.0001);
    }

    [Fact]
    public void TextWrittenSidewaysOnAnUprightPageIsFoundAsWell()
    {
        // A table set sideways on a page that is stored upright: the letters are turned, not the page.
        var pdf = TestPdf.Compose((page, fonts) =>
        {
            const string line = "Quarter one Quarter two Quarter three Quarter four";
            fonts.Prepare(line, null);
            for (var i = 0; i < 10; i++) PageDraw.DrawText(page, fonts, line, new TextStyle(12), 100 + i * 24, 700, 90);
        });
        using var view = new PdfView(pdf);

        var turn = PdfOrientation.Find(view, 0);

        // Text turned counter-clockwise reads from the bottom up: a quarter turn clockwise sets it upright.
        Assert.Equal(90, turn.Degrees);
        using var after = new PdfView(PdfPages.Rotate(pdf, null, turn.Degrees));
        Assert.Equal(0, PdfOrientation.Find(after, 0).Degrees);
    }

    [Fact]
    public void AWatermarkAcrossThePageDoesNotCountAndAFewWordsAreNotEnough()
    {
        var marked = PdfStamps.AddWatermark(Letter(), null, new WatermarkOptions { Text = "CONFIDENTIAL COPY NOT FOR DISTRIBUTION" });
        using (var view = new PdfView(marked)) Assert.Equal(0, PdfOrientation.Find(view, 0).Degrees);

        // A page with next to nothing on it gives nothing to go by, and is left alone without a mark.
        using var empty = new PdfView(PdfPages.Rotate(TestPdf.WithText("Hi"), null, 90));
        Assert.Equal(PageTurn.Unknown, PdfOrientation.Find(empty, 0));
        using var blank = new PdfView(TestPdf.WithText(""));
        Assert.Equal(PageTurn.Unknown, PdfOrientation.Find(blank, 0));
    }

    [Fact]
    public void MostOfTheLettersDecideAndAnEvenSplitIsLeftAloneWithAMark()
    {
        static byte[] Mixed(int upright, int sideways) => TestPdf.Compose((page, fonts) =>
        {
            const string line = "Delivery is expected on the fourteenth of March";
            fonts.Prepare(line, null);
            for (var i = 0; i < upright; i++) PageDraw.DrawText(page, fonts, line, new TextStyle(11), 60, 80 + i * 18);
            for (var i = 0; i < sideways; i++) PageDraw.DrawText(page, fonts, line, new TextStyle(11), 380 + i * 18, 760, 90);
        });

        // A sideways caption beside a page of upright text changes nothing.
        using (var view = new PdfView(Mixed(10, 1))) Assert.Equal(new PageTurn(0, 1, false, TurnEvidence.Text), PdfOrientation.Find(view, 0) with { Certainty = 1 });
        // Mostly sideways, but not by much: found as turned only by someone ready to believe it, and marked either way.
        using var close = new PdfView(Mixed(4, 6));
        var cautious = PdfOrientation.Find(close, 0, readiness: 0.2);
        Assert.Equal((0, true), (cautious.Degrees, cautious.Doubtful));
        var ready = PdfOrientation.Find(close, 0, readiness: 1);
        Assert.Equal((90, true), (ready.Degrees, ready.Doubtful));
    }

    // ---- scans --------------------------------------------------------------------------------------------------

    /// <summary>A scanned page of ordinary print: sentences, not one line repeated.</summary>
    private static SKBitmap Page(float size = 30)
    {
        var bitmap = new SKBitmap(new SKImageInfo(1240, 1754, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(232, 230, 224));
        using var paint = new SKPaint { Color = new SKColor(40, 40, 40), IsAntialias = true, TextSize = size, Typeface = SKTypeface.FromFamilyName("Times New Roman") };
        var y = 170f;
        for (var i = 0; y < 1620; i++, y += size * 1.7f)
            canvas.DrawText(TestPdf.Sentences[i % TestPdf.Sentences.Length] + ", " + TestPdf.Sentences[(i * 7 + 3) % TestPdf.Sentences.Length].ToLowerInvariant(), 110, y, paint);
        return bitmap;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AScanIsJudgedByTheLookOfItsPrintWhenItCannotBeRead(int quarters)
    {
        using var upright = Page();
        using var scan = Turned(upright, quarters);

        // Nothing is read: the lines and the letters have to tell.
        var turn = PdfOrientation.Find(scan, read: false);

        Assert.Equal((4 - quarters) % 4 * 90, turn.Degrees);
        Assert.Equal(TurnEvidence.Look, turn.Evidence);
        Assert.True(turn.Certainty is > 0.5 and <= 0.8, $"certainty {turn.Certainty:0.00}");
        // What rests on the look alone is always marked for a look when it leads to a turn.
        Assert.Equal(quarters != 0, turn.Doubtful);
    }

    private static readonly string[] Russian =
    [
        "Счета подлежат оплате в течение тридцати дней", "Комитет утвердил бюджет на следующий год",
        "Пожалуйста, верните подписанную форму в наш офис", "Доставка ожидается четырнадцатого марта"
    ];

    private static SKBitmap RussianPage()
    {
        var bitmap = new SKBitmap(new SKImageInfo(1240, 1754, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(232, 230, 224));
        using var paint = new SKPaint { Color = new SKColor(40, 40, 40), IsAntialias = true, TextSize = 30, Typeface = SKTypeface.FromFamilyName("Times New Roman") };
        var y = 170f;
        for (var i = 0; y < 1620; i++, y += 51) canvas.DrawText(Russian[i % Russian.Length], 110, y, paint);
        return bitmap;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WhereTheLettersAreNoClueAPageOnItsSideIsMarkedAndNotGuessedAt(int quarters)
    {
        // In Cyrillic as much hangs below the line as rises above it: which way up a page stands cannot be told by
        // its look. That it lies on its side can.
        using var upright = RussianPage();
        using var scan = Turned(upright, quarters);

        var turn = PdfOrientation.Find(scan, ["ru"], read: false);

        Assert.Equal(0, turn.Degrees);
        Assert.Equal(quarters % 2 == 1, turn.Doubtful);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PrintInAnotherLanguageThanThoseChosenIsNeverTurnedTheWrongWay(int quarters)
    {
        // Russian print with only English chosen. Its letters alone would point the wrong way: in Cyrillic more
        // hangs below the line than rises above it. Read as English it gives few words, but more the right way
        // round than any other: enough to turn it, or at least to mark it, and never to turn it wrongly.
        if (!Ready("en")) return;
        using var upright = RussianPage();
        using var scan = Turned(upright, quarters);

        var turn = PdfOrientation.Find(scan, ["en"]);

        var right = (4 - quarters) % 4 * 90;
        Assert.True(turn.Degrees == right || (turn.Degrees == 0 && turn.Doubtful), $"turned by {turn.Degrees}, right would be {right}");
        // By its look alone, taken for Latin print, it would be turned on its head.
        if (quarters == 0) Assert.Equal(180, PdfOrientation.Find(scan, ["en"], read: false).Degrees);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void APoorScanIsReadTheRightWayRoundAllTheSame(int quarters)
    {
        if (!Ready("en")) return;
        using var poor = TestPdf.PoorScan();
        using var scan = Turned(poor, quarters);

        var turn = PdfOrientation.Find(scan, ["en"]);

        Assert.Equal((4 - quarters) % 4 * 90, turn.Degrees);
        Assert.Equal(TurnEvidence.Reading, turn.Evidence);
        Assert.False(turn.Doubtful);
    }

    [Fact]
    public void APageThatIsAskewIsStillFoundToLieOnItsSide()
    {
        using var askew = TestPdf.FakeScan(skewDegrees: 2);
        using var scan = Turned(askew, 1);

        // By its look alone: this text has as much hanging as rising, so it is marked rather than turned.
        var look = PdfOrientation.Find(scan, read: false);
        Assert.Equal((0, true, TurnEvidence.Look), (look.Degrees, look.Doubtful, look.Evidence));
        if (!Ready("en")) return;
        var read = PdfOrientation.Find(scan, ["en"]);
        Assert.Equal((270, false, TurnEvidence.Reading), (read.Degrees, read.Doubtful, read.Evidence));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AScanIsReadEachWayRoundWhereThatIsPossible(int quarters)
    {
        if (!Ready("en")) return;
        using var upright = Page();
        using var scan = Turned(upright, quarters);
        using var view = new PdfView(TestPdf.FromImage(scan, 150));

        var turn = PdfOrientation.Find(view, 0, ["en"]);

        Assert.Equal((4 - quarters) % 4 * 90, turn.Degrees);
        Assert.Equal(TurnEvidence.Reading, turn.Evidence);
        Assert.False(turn.Doubtful);
        Assert.True(turn.Certainty > 0.8, $"certainty {turn.Certainty:0.00}");
    }

    [Fact]
    public void PrintInCapitalsStandsOnItsHeadUnnoticedByTheLookButNotByAReading()
    {
        // Capitals and figures all have the same height: nothing rises or hangs to tell top from bottom by.
        using var upright = new SKBitmap(new SKImageInfo(1240, 1754, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(upright))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, TextSize = 30, Typeface = SKTypeface.FromFamilyName("Arial") };
            for (var i = 0; i < 26; i++) canvas.DrawText(TestPdf.Sentences[i % TestPdf.Sentences.Length].ToUpperInvariant() + " 2026", 110, 180 + i * 56, paint);
        }

        using var onItsHead = Turned(upright, 2);

        var look = PdfOrientation.Find(onItsHead, read: false);
        Assert.Equal(0, look.Degrees);
        if (!Ready("en")) return;
        var read = PdfOrientation.Find(onItsHead, ["en"]);
        Assert.Equal((180, TurnEvidence.Reading), (read.Degrees, read.Evidence));
    }

    [Fact]
    public void APictureWithoutPrintIsLeftAlone()
    {
        using var picture = new SKBitmap(new SKImageInfo(1200, 900, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(picture))
        {
            using var paint = new SKPaint();
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1200, 900),
                [new SKColor(30, 60, 120), new SKColor(220, 180, 90)], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, 1200, 900, paint);
        }

        Assert.Equal(PageTurn.Unknown, PdfOrientation.Find(picture, read: false));
        using var blank = new SKBitmap(new SKImageInfo(800, 1100, SKColorType.Bgra8888, SKAlphaType.Opaque));
        blank.Erase(SKColors.White);
        Assert.Equal(PageTurn.Unknown, PdfOrientation.Find(blank, ["en"]));
    }

    [Fact]
    public void AScanWithItsSearchLayerGoesByThatLayer()
    {
        if (!Ready("en")) return;
        using var upright = Page();
        var searchable = PdfOcr.MakeSearchable(TestPdf.FromImage(upright, 150), null, OcrSettings.DefaultFor("en")).Pdf;
        // Turned after it was made searchable, as a page that was put in the wrong way round is.
        using var view = new PdfView(PdfPages.Rotate(searchable, null, 90));

        var turn = PdfOrientation.Find(view, 0);

        Assert.Equal((270, TurnEvidence.Text), (turn.Degrees, turn.Evidence));
    }
}
