using PdfModifier.Core.Imaging;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using PdfModifier.Core.Text;
using SkiaSharp;

namespace PdfModifier.Tests;

public class EnhanceAndLanguageTests
{
    [Theory]
    [InlineData(2.5)]
    [InlineData(-1.5)]
    [InlineData(4.0)]
    public void SkewOfATiltedScanIsMeasured(double degrees)
    {
        using var scan = TestPdf.FakeScan(skewDegrees: degrees);
        var measured = ImageFilters.EstimateSkew(PlanarImage.FromBitmap(scan, true));
        Assert.Equal(degrees, measured, 0.3);
    }

    [Fact]
    public void AStraightScanReportsNoSkew()
    {
        using var scan = TestPdf.FakeScan();
        Assert.Equal(0, ImageFilters.EstimateSkew(PlanarImage.FromBitmap(scan, true)));
    }

    [Fact]
    public void EnhanceStraightensWhitensAndKeepsTheTextDark()
    {
        using var scan = TestPdf.FakeScan(skewDegrees: 3, paper: 200);
        var pdf = TestPdf.FromImage(scan);

        var result = PdfEnhance.Enhance(pdf);

        Assert.Equal(1, result.ImagesEnhanced);
        using var rendered = TestPdf.Render(result.Pdf, 0, 2);
        Assert.InRange(rendered.GetPixel(rendered.Width / 2, 40).Red, 250, 255); // paper is white now
        Assert.True(TestPdf.InkRatio(rendered) > 0.02, "text must survive");
        Assert.Equal(0, ImageFilters.EstimateSkew(PlanarImage.FromBitmap(rendered, true)), 0.35);
    }

    [Fact]
    public void BinarizeLeavesOnlyBlackAndWhite()
    {
        using var scan = TestPdf.FakeScan(800, 1100, paper: 190);
        using var result = PdfEnhance.EnhanceBitmap(scan, new EnhanceOptions { Binarize = true, Deskew = false });
        Assert.Equal(SKColorType.Gray8, result.ColorType);
        var values = result.Bytes.Distinct().Order().ToArray();
        Assert.Equal([(byte)0, (byte)255], values);
    }

    [Fact]
    public void StrongWhiteningWipesOutAFaintMarkButKeepsTheText()
    {
        using var scan = TestPdf.FakeScan(800, 1100, paper: 240);
        using (var canvas = new SKCanvas(scan))
        using (var paint = new SKPaint { Color = new SKColor(205, 205, 205), TextSize = 150, IsAntialias = true })
            canvas.DrawText("COPY", 180, 600, paint);

        using var result = PdfEnhance.EnhanceBitmap(scan, new EnhanceOptions { WhitenStrength = 0.8, Deskew = false, Sharpen = false });

        // Compare pixel by pixel: where the source shows the mark's exact gray, the result must be paper white.
        var before = scan.Bytes;
        var after = result.Bytes;
        int step = result.BytesPerPixel, markPixels = 0, wiped = 0, dark = 0;
        for (var i = 0; i < scan.Width * scan.Height; i++)
        {
            if (before[i * 4] == 205)
            {
                markPixels++;
                if (after[i * step] >= 250) wiped++;
            }

            if (after[i * step] < 60) dark++;
        }

        Assert.True(markPixels > 5000, "the fixture should contain the mark");
        Assert.True(wiped >= markPixels * 0.99, $"{wiped} of {markPixels} mark pixels wiped");
        Assert.True(dark > 2000, "the dark text must remain");
    }

    [Fact]
    public void PhotographsAreNotWhitened()
    {
        using var photo = ContentOperationTests.Photo(300, 300);
        using var dark = new SKBitmap(photo.Info);
        using (var canvas = new SKCanvas(dark))
        using (var paint = new SKPaint { ColorFilter = SKColorFilter.CreateLighting(new SKColor(110, 110, 110), SKColors.Black) })
            canvas.DrawBitmap(photo, 0, 0, paint);
        Assert.False(ImageFilters.WhitenBackground(PlanarImage.FromBitmap(dark, false)));
    }

    [Fact]
    public void FaintTextOnTextPagesIsDarkened()
    {
        byte[] pdf;
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.CreateNew();
            using var fonts = new DocFonts(doc);
            fonts.Prepare("Faint Red White", null);
            using var page = doc.NewPage(0, 595, 842);
            PageDraw.DrawText(page, fonts, "Faint", new TextStyle(20, 0xFFAAAAAA), 72, 100);
            PageDraw.DrawText(page, fonts, "Red", new TextStyle(20, 0xFFCC2222), 72, 140);
            page.GenerateContent();
            pdf = doc.Save();
        }

        var result = PdfEnhance.Enhance(pdf);

        Assert.Equal(1, result.TextObjectsDarkened);
        var runs = PdfTextEdit.GetTextRuns(result.Pdf, 0);
        Assert.Equal(0xFF000000, runs.Single(r => r.Text == "Faint").Color);
        Assert.Equal(0xFFCC2222, runs.Single(r => r.Text == "Red").Color);
    }

    [Fact]
    public void EnhanceLeavesCleanTextDocumentsUntouched()
    {
        var pdf = TestPdf.Numbered(2);
        Assert.Same(pdf, PdfEnhance.Enhance(pdf).Pdf);
    }

    [Theory]
    [InlineData("en", "The committee has decided that the meeting will be held on the first day of the month, and all members are expected to attend.")]
    [InlineData("es", "El comité ha decidido que la reunión se celebrará el primer día del mes y se espera que todos los miembros asistan sin excepción.")]
    [InlineData("de", "Der Ausschuss hat beschlossen, dass die Sitzung am ersten Tag des Monats stattfindet und alle Mitglieder werden erwartet.")]
    [InlineData("fr", "Le comité a décidé que la réunion se tiendra le premier jour du mois et tous les membres sont priés d'être présents.")]
    [InlineData("pt", "O comité decidiu que a reunião será realizada no primeiro dia do mês e todos os membros devem estar presentes, sem exceção.")]
    [InlineData("tr", "Komite, toplantının ayın ilk günü yapılmasına karar verdi ve bütün üyelerin katılması bekleniyor; bu çok önemli bir konu.")]
    [InlineData("ru", "Комитет решил, что заседание состоится в первый день месяца, и все члены должны присутствовать.")]
    [InlineData("ja", "委員会は、会議を月の最初の日に開催することを決定しました。すべてのメンバーの出席が求められます。")]
    public void EachSupportedLanguageIsRecognised(string expected, string text) =>
        Assert.Equal(expected, LanguageDetector.DetectBest(text));

    [Fact]
    public void DetectionCanBeRestrictedToTheSelectedLanguages()
    {
        const string spanish = "El comité ha decidido que la reunión se celebrará el primer día del mes.";
        Assert.Equal("pt", LanguageDetector.DetectBest(spanish, ["pt", "de", "ru"]));
        Assert.Null(LanguageDetector.DetectBest(spanish, ["ru", "ja"]));
    }

    [Fact]
    public void MixedDocumentsReportBothLanguages()
    {
        var ranked = LanguageDetector.Detect("This is the first part of the document. Вторая часть документа написана на русском языке.");
        Assert.Equal(["en", "ru"], ranked.Take(2).Select(r => r.Code).Order());
        Assert.All(ranked.Take(2), r => Assert.True(r.Score >= 0.4));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345 67 — 890 !!!")]
    public void TextWithoutLettersGivesNoLanguage(string text) => Assert.Null(LanguageDetector.DetectBest(text));

    [Fact]
    public void AllEightLanguagesAreOfferedWithEnglishAsDefault()
    {
        Assert.Equal(["en", "es", "de", "ru", "fr", "ja", "pt", "tr"], Languages.All.Select(l => l.Code));
        Assert.Equal("en", Languages.Default);
        Assert.Equal("eng", Languages.Get("nonsense").TesseractCode);
    }
}
