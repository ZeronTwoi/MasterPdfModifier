using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;

namespace PdfModifier.Tests;

/// <summary>
/// Recognition tests need the Tesseract engine and language files. They pass vacuously when those are not installed,
/// so the suite still runs on a fresh machine; point PDFMODIFIER_TESSDATA at a folder with the files to exercise them.
/// </summary>
public class OcrTests
{
    private static bool Ready(params string[] languages) =>
        PdfOcr.EngineStatus().Available && TessData.Missing(languages).Count == 0;

    [Fact]
    public void TsvOutputIsGroupedIntoLines()
    {
        const string tsv =
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
            "1\t1\t0\t0\t0\t0\t0\t0\t800\t600\t-1\t\n" +
            "5\t1\t1\t1\t1\t1\t10\t20\t50\t18\t96.5\tHello\n" +
            "5\t1\t1\t1\t1\t2\t70\t20\t60\t18\t91\tworld\n" +
            "5\t1\t1\t1\t2\t1\t10\t50\t40\t18\t88\tNext\n" +
            "5\t1\t1\t1\t2\t2\t60\t50\t5\t18\t30\t \n";

        var lines = TesseractCli.ParseTsv(tsv);

        Assert.Equal(2, lines.Count);
        Assert.Equal(["Hello", "world"], lines[0].Words.Select(w => w.Text));
        Assert.Equal((70, 20, 130, 38), (lines[0].Words[1].Left, lines[0].Words[1].Top, lines[0].Words[1].Right, lines[0].Words[1].Bottom));
        Assert.Equal("Next", Assert.Single(lines[1].Words).Text);
    }

    [Fact]
    public void MissingLanguageDataIsReportedByLanguage()
    {
        if (!PdfOcr.EngineStatus().Available) return;
        var original = TessData.Directory;
        TessData.Directory = Path.Combine(Path.GetTempPath(), "pdfmodifier-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var scan = TestPdf.FakeScan(600, 400);
            // Only the built-in engine needs local files; an installed command-line Tesseract brings its own.
            if (PdfOcr.EngineStatus().Detail.Contains("built in"))
            {
                var error = Assert.Throws<OcrLanguageMissingException>(() => PdfOcr.Recognize(scan, OcrSettings.DefaultFor("en", "tr")));
                Assert.Equal(["en", "tr"], error.Languages);
            }
        }
        finally
        {
            TessData.Directory = original;
        }
    }

    [Fact]
    public void TextOnAScannedPageIsFoundAndRead()
    {
        if (!Ready("en")) return;
        using var scan = TestPdf.FakeScan(1240, 1754);
        var pdf = TestPdf.FromImage(scan, 150);

        var result = PdfOcr.RecognizePage(pdf, 0, OcrSettings.DefaultFor("en"));

        Assert.True(result.Lines.Count >= 15, $"{result.Lines.Count} lines found");
        Assert.Contains("quick brown fox jumps over the lazy dog", result.Text);
        // First line: baseline at 220px on a 150 dpi page, i.e. about 105pt from the top.
        var first = result.Lines[0];
        Assert.InRange(first.Bounds.Bottom, 98, 112);
        Assert.InRange(first.Bounds.Left, 60, 72);
        Assert.Equal(["en"], result.DetectedLanguages);
    }

    [Fact]
    public void TheLanguageOfAPageIsDetectedAmongSeveralSelected()
    {
        if (!Ready("en", "ru", "de")) return;
        using var scan = TestPdf.FakeScan(1240, 900, text: "Съешь ещё этих мягких французских булок да выпей чаю");
        var pdf = TestPdf.FromImage(scan, 150);

        var result = PdfOcr.RecognizePage(pdf, 0, new OcrSettings(["en", "ru", "de"]));

        Assert.Equal(["ru"], result.DetectedLanguages);
        Assert.Contains("французских булок", result.Text);
    }

    [Fact]
    public void JapaneseIsRecognised()
    {
        if (!Ready("ja") || !OperatingSystem.IsWindows()) return;
        using var scan = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1240, 500, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque));
        using (var canvas = new SkiaSharp.SKCanvas(scan))
        using (var paint = new SkiaSharp.SKPaint { TextSize = 44, IsAntialias = true, Typeface = SkiaSharp.SKTypeface.FromFamilyName("Yu Gothic") })
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            canvas.DrawText("委員会は会議を開催することを決定しました", 120, 200, paint);
            canvas.DrawText("すべてのメンバーの出席が求められます", 120, 300, paint);
        }

        // 44px characters are body text at 300 dpi; declaring the true resolution matters for line detection.
        var result = PdfOcr.Recognize(scan, new OcrSettings(["en", "ja"]));

        Assert.True(result.DetectedLanguages.SequenceEqual(["ja"]) && result.Text.Contains("委員会は会議を開催") && result.Text.Contains("すべてのメンバー"),
            $"languages: {string.Join(",", result.DetectedLanguages)}; text: {string.Join(" / ", result.Lines.Select(l => l.Text))}");
    }

    [Fact]
    public void ABadlyLitSpeckledCopyIsStillRead()
    {
        if (!Ready("en")) return;
        // A slightly crooked, blurred copy with a shadow down one side: read plainly, hardly a word of it is found.
        using var clean = TestPdf.FakeScan(1240, 1754, paper: 232);
        using var scan = new SkiaSharp.SKBitmap(clean.Info);
        using (var canvas = new SkiaSharp.SKCanvas(scan))
        {
            canvas.Clear(new SkiaSharp.SKColor(232, 232, 232));
            canvas.RotateDegrees(2f, 620, 877);
            using var blur = new SkiaSharp.SKPaint { ImageFilter = SkiaSharp.SKImageFilter.CreateBlur(1.1f, 1.1f) };
            canvas.DrawBitmap(clean, 0, 0, blur);
            canvas.ResetMatrix();
            using var shade = new SkiaSharp.SKPaint();
            shade.Shader = SkiaSharp.SKShader.CreateLinearGradient(new SkiaSharp.SKPoint(0, 0), new SkiaSharp.SKPoint(1240, 0),
                [new SkiaSharp.SKColor(0, 0, 0, 0), new SkiaSharp.SKColor(0, 0, 0, 125)], SkiaSharp.SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, 1240, 1754, shade);
        }

        var plain = PdfOcr.Recognize(scan, new OcrSettings(["en"], Dpi: 150) { Thorough = false });
        var thorough = PdfOcr.Recognize(scan, new OcrSettings(["en"], Dpi: 150));

        int Found(OcrPageResult result) => result.Lines.Count(l => l.Text.Contains("quick brown fox") && l.Text.Contains("lazy dog"));
        Assert.True(Found(thorough) >= 18, $"{Found(thorough)} of 20 lines read after cleaning up ({Found(plain)} without)");
        Assert.True(Found(thorough) > Found(plain) + 5, $"{Found(plain)} lines read without cleaning up, {Found(thorough)} with");
    }

    [Fact]
    public void OnAPageThatMixesAScanWithTypedTextOnlyTheScanIsRecognised()
    {
        if (!Ready("en")) return;
        using var scan = TestPdf.FakeScan(1240, 1754);
        // A typed reference number on a scan, as scanner software and mail rooms add: the page has text, but hardly any.
        var pdf = TestPdf.ScanWith(scan, over: (page, fonts) =>
            PdfModifier.Core.Pdfium.PageDraw.DrawText(page, fonts, "Reference 2026-0417 received", new PdfModifier.Core.Pdfium.TextStyle(12), 60, 50));

        var (searchable, recognised) = PdfOcr.MakeSearchable(pdf, null, OcrSettings.DefaultFor("en"));

        Assert.Equal(1, recognised);
        var text = TestPdf.Text(searchable, 0);
        Assert.Contains("quick brown fox jumps over the lazy dog", text);
        // The typed line was text already and is there once, not once more as recognised text.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "2026-0417"));
        Assert.Equal("Reference 2026-0417 received", Assert.Single(PdfTextEdit.GetTextRuns(searchable, 0)).Text);
    }

    [Fact]
    public void MakeSearchableAddsHiddenTextWithoutChangingThePicture()
    {
        if (!Ready("en")) return;
        using var scan = TestPdf.FakeScan(1240, 1754);
        var pdf = PdfPages.Merge([TestPdf.FromImage(scan, 150), TestPdf.WithText("Already has real text on it")]);
        Assert.Equal("", TestPdf.Text(pdf, 0));

        var (searchable, recognised) = PdfOcr.MakeSearchable(pdf, null, OcrSettings.DefaultFor("en"));

        Assert.Equal(1, recognised); // the text page is skipped
        Assert.Contains("quick brown fox jumps over the lazy dog", TestPdf.Text(searchable, 0));
        Assert.Equal(0, TestPdf.Difference(pdf, searchable), 0.0005);
        Assert.Equal("Already has real text on it", TestPdf.Text(searchable, 1));
        // The hidden layer is not offered for editing.
        Assert.Empty(PdfTextEdit.GetTextRuns(searchable, 0));
    }
}
