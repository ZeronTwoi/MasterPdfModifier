using PdfModifier.Core;
using PdfModifier.Core.Fonts;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Tests;

public class TextEditTests
{
    [Fact]
    public void TextRunsAreFoundWithTheirPositionAndLook()
    {
        var run = Assert.Single(PdfTextEdit.GetTextRuns(TestPdf.WithText("Hello world"), 0));
        Assert.Equal("Hello world", run.Text);
        Assert.Equal(24, run.FontSize, 0.1);
        Assert.Equal(0xFF000000, run.Color);
        Assert.Equal(72, run.Bounds.Left, 3.0);
        Assert.Equal(100, run.Bounds.Bottom, 6.0);
        Assert.Contains("Arial", run.FontName);
    }

    [Fact]
    public void WordsStoredOneByOneAndOutOfOrderAreStillFoundAsLines()
    {
        // Many programs write every word as a piece of its own, and not always in the order the words are read.
        string[] first = ["The", "words", "of", "the", "first", "line"], second = ["and", "those", "of", "the", "second"];
        var pdf = TestPdf.Compose((page, fonts) =>
        {
            // One font for all of it, as in a real document.
            fonts.Prepare(string.Join(' ', first.Concat(second)), null);
            var pieces = new List<(string Word, double X, double Y)>();
            foreach (var (words, y) in new[] { (first, 100.0), (second, 130.0) })
            {
                double x = 72;
                foreach (var word in words)
                {
                    pieces.Add((word, x, y));
                    x += FontResolver.Measure(word + " ", null, false, false, 14);
                }
            }

            // Written back to front, the two lines interleaved.
            foreach (var (word, x, y) in pieces.AsEnumerable().Reverse().OrderBy(p => p.Word.Length))
                PageDraw.DrawText(page, fonts, word, new TextStyle(14), x, y);
        });

        var runs = PdfTextEdit.GetTextRuns(pdf, 0);
        Assert.Equal(["The words of the first line", "and those of the second"], runs.Select(r => r.Text));
        Assert.True(runs[0].Upright);
        Assert.Equal(100, runs[0].Baseline, 0.5);
        Assert.Equal(72, runs[0].StartX, 0.5);

        // And such a line is still changed as one.
        var edited = PdfTextEdit.ApplyEdits(pdf, 0, [new ReplaceTextEdit(1, "the second, in one piece")]);
        Assert.Equal(["The words of the first line", "the second, in one piece"], PdfTextEdit.GetTextRuns(edited, 0).Select(r => r.Text));
    }

    [Fact]
    public void ReplacingWithCharactersTheFontAlreadyHasKeepsTheOriginalFont()
    {
        var pdf = TestPdf.WithText("Hello world");
        var before = PdfTextEdit.GetTextRuns(pdf, 0)[0];

        // Only letters that occur in "Hello world": the document's own font is known to have exactly those.
        var edited = PdfTextEdit.ApplyEdits(pdf, 0, [new ReplaceTextEdit(0, "Hold lower")]);

        Assert.Equal("Hold lower", TestPdf.Text(edited, 0));
        var after = Assert.Single(PdfTextEdit.GetTextRuns(edited, 0));
        Assert.Equal(before.FontName, after.FontName);
        Assert.Equal(before.Bounds.Left, after.Bounds.Left, 1.5);
        Assert.Equal(before.Bounds.Bottom, after.Bounds.Bottom, 1.5);
        // No second font was embedded for the edit.
        Assert.True(edited.Length < pdf.Length + 2000, $"{pdf.Length} -> {edited.Length} bytes");
    }

    [Fact]
    public void ReplacingWithNewCharactersBringsInAMatchingFontAtTheSamePlaceAndSize()
    {
        var pdf = TestPdf.WithText("Hello world");
        var before = PdfTextEdit.GetTextRuns(pdf, 0)[0];

        var edited = PdfTextEdit.ApplyEdits(pdf, 0, [new ReplaceTextEdit(0, "Grüße, Привет, こんにちは, İğş!")]);

        Assert.Equal("Grüße, Привет, こんにちは, İğş!", TestPdf.Text(edited, 0));
        var runs = PdfTextEdit.GetTextRuns(edited, 0);
        Assert.All(runs, r => Assert.Equal(before.FontSize, r.FontSize, 0.1));
        Assert.Equal(before.Bounds.Left, runs.Min(r => r.Bounds.Left), 2.0);
        Assert.Equal(before.Bounds.Bottom, runs.Where(r => r.Text.StartsWith("Gr")).Max(r => r.Bounds.Bottom), 8.0);
        using var rendered = TestPdf.Render(edited, 0, 2);
        Assert.True(TestPdf.InkRatio(rendered) > 0.003, "the replacement text must actually be drawn");
    }

    [Fact]
    public void DeletingARunRemovesItsText()
    {
        var edited = PdfTextEdit.ApplyEdits(TestPdf.WithText("Gone soon"), 0, [new DeleteTextEdit(0)]);
        Assert.Equal("", TestPdf.Text(edited, 0));
        Assert.Empty(PdfTextEdit.GetTextRuns(edited, 0));
        using var rendered = TestPdf.Render(edited, 0);
        Assert.Equal(0, TestPdf.InkRatio(rendered));
    }

    [Fact]
    public void ReplacingWithNothingDeletes()
    {
        var edited = PdfTextEdit.ApplyEdits(TestPdf.WithText("Gone soon"), 0, [new ReplaceTextEdit(0, "")]);
        Assert.Empty(PdfTextEdit.GetTextRuns(edited, 0));
    }

    [Fact]
    public void AddedTextAppearsWhereItWasPlaced()
    {
        var edited = PdfTextEdit.ApplyEdits(TestPdf.WithText("Existing"), 0,
            [new AddTextEdit(200, 400, "Added line", 18, 0xFFCC0000, Bold: true)]);

        var added = Assert.Single(PdfTextEdit.GetTextRuns(edited, 0), r => r.Text == "Added line");
        Assert.Equal(200, added.Bounds.Left, 3.0);
        Assert.Equal(400, added.Bounds.Bottom, 5.0);
        Assert.Equal(18, added.FontSize, 0.1);
        Assert.Equal(0xFFCC0000, added.Color);
        Assert.Contains("Bold", added.FontName);
        Assert.Contains("Existing", TestPdf.Text(edited, 0));
    }

    [Fact]
    public void SeveralEditsInOneBatchAllApply()
    {
        var pdf = TwoLines("First line", "Second line");
        var runs = PdfTextEdit.GetTextRuns(pdf, 0);
        Assert.Equal(["First line", "Second line"], runs.Select(r => r.Text));

        var edited = PdfTextEdit.ApplyEdits(pdf, 0,
        [
            new ReplaceTextEdit(0, "First Zeile"),
            new DeleteTextEdit(1),
            new AddTextEdit(72, 300, "Third", 12)
        ]);

        var text = TestPdf.Text(edited, 0);
        Assert.Contains("First Zeile", text);
        Assert.DoesNotContain("Second", text);
        Assert.Contains("Third", text);
    }

    [Fact]
    public void TextFollowingAnEditOnTheSameLineMovesWithIt()
    {
        // Two differently coloured pieces on one baseline: the second must slide when the first grows.
        byte[] pdf;
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.CreateNew();
            using var fonts = new DocFonts(doc);
            fonts.Prepare("Total: 12345 wide", null);
            using var page = doc.NewPage(0, 595, 842);
            PageDraw.DrawText(page, fonts, "Total:", new TextStyle(20), 72, 100);
            var width = FontResolver.Measure("Total:", null, false, false, 20);
            PageDraw.DrawText(page, fonts, "12", new TextStyle(20, 0xFF0000FF), 72 + width + 6, 100);
            page.GenerateContent();
            pdf = doc.Save();
        }

        var before = PdfTextEdit.GetTextRuns(pdf, 0);
        Assert.Equal(["Total:", "12"], before.Select(r => r.Text));

        var edited = PdfTextEdit.ApplyEdits(pdf, 0, [new ReplaceTextEdit(0, "Total wide:")]);
        var after = PdfTextEdit.GetTextRuns(edited, 0);
        var label = after.Single(r => r.Text == "Total wide:");
        var value = after.Single(r => r.Text == "12");
        var grown = label.Bounds.Width - before[0].Bounds.Width;
        Assert.True(grown > 20);
        Assert.Equal(before[1].Bounds.Left + grown, value.Bounds.Left, 1.5);
    }

    [Fact]
    public void CoverEditPaintsOverAScanAndTypesInMatchingColours()
    {
        using var scan = TestPdf.FakeScan(1240, 1754, paper: 230);
        var pdf = TestPdf.FromImage(scan, 150);
        // First text line of the fake scan: baseline at y=220px, 36px type, at 150 dpi.
        const double k = 72.0 / 150;
        var area = new RectD(130 * k, 185 * k, 1150 * k, 232 * k);

        var edited = PdfTextEdit.ApplyEdits(pdf, 0, [new CoverTextEdit(area, "Replaced by the editor")]);

        Assert.Contains("Replaced by the editor", TestPdf.Text(edited, 0));
        using var rendered = TestPdf.Render(edited, 0);
        // To the right of the new, shorter text the old line is painted over with the paper colour.
        var patch = rendered.GetPixel((int)(area.Right - 30), (int)area.CenterY);
        Assert.InRange(patch.Red, 222, 238);
        var run = Assert.Single(PdfTextEdit.GetTextRuns(edited, 0));
        Assert.True((run.Color & 0xFF) < 90, $"ink colour {run.Color:X8} should be dark like the scanned text");
        Assert.InRange(run.FontSize, 14, 24); // 36px at 150dpi is about 17pt
    }

    [Fact]
    public void CoverEditWithoutTextJustErases()
    {
        var pdf = TestPdf.WithText("Erase me");
        var run = PdfTextEdit.GetTextRuns(pdf, 0)[0];
        var area = new RectD(run.Bounds.Left - 2, run.Bounds.Top - 2, run.Bounds.Right + 2, run.Bounds.Bottom + 2);

        var edited = PdfTextEdit.ApplyEdits(pdf, 0, [new CoverTextEdit(area, "")]);

        using var rendered = TestPdf.Render(edited, 0);
        Assert.Equal(0, TestPdf.InkRatio(rendered));
        Assert.Equal("", TestPdf.Text(edited, 0)); // the hidden text is not left behind for search or copy
    }

    [Fact]
    public void ImagesCanBePlacedOnAPage()
    {
        using var square = new SKBitmap(new SKImageInfo(50, 50, SKColorType.Bgra8888, SKAlphaType.Opaque));
        square.Erase(SKColors.Red);
        var edited = PdfTextEdit.ApplyEdits(TestPdf.WithText(""), 0,
            [new AddImageEdit(new RectD(100, 200, 200, 300), PdfImages.Encode(square, ImageFormat.Png))]);
        using var rendered = TestPdf.Render(edited, 0);
        Assert.True(rendered.GetPixel(150, 250).Red > 240 && rendered.GetPixel(150, 250).Green < 20);
        Assert.Equal(SKColors.White, rendered.GetPixel(90, 250));
        Assert.Equal(SKColors.White, rendered.GetPixel(150, 310));
    }

    [Fact]
    public void EditsWorkOnRotatedPages()
    {
        var rotated = PdfPages.Rotate(TestPdf.WithText("Sideways"), null, 90);
        var edited = PdfTextEdit.ApplyEdits(rotated, 0, [new AddTextEdit(300, 300, "Upright", 20)]);
        var added = PdfTextEdit.GetTextRuns(edited, 0).Single(r => r.Text == "Upright");
        Assert.True(added.Bounds.Width > added.Bounds.Height * 2);
        Assert.Equal(300, added.Bounds.Left, 3.0);
        Assert.Equal(300, added.Bounds.Bottom, 5.0);
    }

    [Fact]
    public void UnknownRunIdsAreRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextEdit.ApplyEdits(TestPdf.WithText("One"), 0, [new DeleteTextEdit(5)]));

    [Theory]
    [InlineData("ABCDEF+TimesNewRomanPS-BoldItalicMT", "Times New Roman", true, true)]
    [InlineData("ArialMT", "Arial", false, false)]
    [InlineData("Helvetica-Bold", "Arial", true, false)]
    [InlineData("CourierNewPSMT", "Courier New", false, false)]
    public void PdfFontNamesMapToInstalledFamilies(string baseFont, string family, bool bold, bool italic)
    {
        // The expected families ship with Windows; elsewhere the generic class is what matters.
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal((family, bold, italic), FontResolver.ParsePdfFontName(baseFont));
    }

    [Fact]
    public void UnknownFontsFallBackToTheirStyleClass()
    {
        Assert.Equal("serif", FontResolver.ParsePdfFontName("XQZWVU+NoSuchFontAtAll-Regular", fontFlags: 2).Family);
        Assert.Equal("monospace", FontResolver.ParsePdfFontName("NoSuchFontAtAll", fontFlags: 1).Family);
        Assert.Equal("sans-serif", FontResolver.ParsePdfFontName("NoSuchFontAtAll").Family);
    }

    [Fact]
    public void SubsetFontsAreSmallAndDrawTheSameGlyphs()
    {
        var font = FontResolver.Resolve(null)!;
        const string text = "Subset ÄÖ ğ Привет";
        var map = text.EnumerateRunes().Select(r => r.Value).Distinct().ToDictionary(cp => cp, cp => font.Typeface.GetGlyph(cp));
        var subset = FontSubsetter.Subset(font.Data, map);

        Assert.True(subset.Length < font.Data.Length / 5, $"{font.Data.Length} -> {subset.Length} bytes");

        using var typeface = SKTypeface.FromData(SKData.CreateCopy(subset));
        Assert.NotNull(typeface);
        using var full = Draw(font.Typeface, text);
        using var cut = Draw(typeface, text);
        Assert.Equal(0, PdfModifier.Core.Imaging.ImageAnalysis.DifferenceRatio(full, cut, 8), 0.002);
        Assert.Equal(0, typeface.GetGlyph('Z')); // not requested, so no longer mapped
    }

    private static SKBitmap Draw(SKTypeface typeface, string text)
    {
        var bitmap = new SKBitmap(new SKImageInfo(700, 80, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Typeface = typeface, TextSize = 40, IsAntialias = true, Color = SKColors.Black };
        canvas.DrawText(text, 10, 55, paint);
        return bitmap;
    }

    private static byte[] TwoLines(string first, string second)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.CreateNew();
            using var fonts = new DocFonts(doc);
            fonts.Prepare(first + second, null);
            using var page = doc.NewPage(0, 595, 842);
            PageDraw.DrawText(page, fonts, first, new TextStyle(16), 72, 100);
            PageDraw.DrawText(page, fonts, second, new TextStyle(16), 72, 140);
            page.GenerateContent();
            return doc.Save();
        }
    }
}
