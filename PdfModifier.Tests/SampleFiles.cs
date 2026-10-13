using PdfModifier.Core.Operations;

namespace PdfModifier.Tests;

public class SampleFiles
{
    /// <summary>
    /// Writes a text document and a fake scan to the folder named by PDFMODIFIER_SAMPLES, for trying the app by hand.
    /// Does nothing when the variable is not set.
    /// </summary>
    [Fact]
    public void WriteSamplesWhenAsked()
    {
        var folder = Environment.GetEnvironmentVariable("PDFMODIFIER_SAMPLES");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "text.pdf"), TestPdf.WithText("Invoice 2026-104", "Terms and conditions", "Appendix"));
        using var scan = TestPdf.FakeScan(1240, 1754, skewDegrees: 2, paper: 205);
        File.WriteAllBytes(Path.Combine(folder, "scan.pdf"), PdfPages.Merge([TestPdf.FromImage(scan, 150), TestPdf.FromImage(scan, 150)]));
        // Sheets that show several pages at once, as pictures: to drop into the window and cut apart.
        using var four = SheetTests.Sheet(2400, 3200, SkiaSharp.SKColors.White,
            (new SkiaSharp.SKRect(0, 0, 1200, 1600), 3), (new SkiaSharp.SKRect(1200, 0, 2400, 1600), 4),
            (new SkiaSharp.SKRect(0, 1600, 1200, 3200), 1), (new SkiaSharp.SKRect(1200, 1600, 2400, 3200), 2));
        File.WriteAllBytes(Path.Combine(folder, "four-pages-on-one-sheet.png"), PdfImages.Encode(four, ImageFormat.Png));
        using var two = SheetTests.Sheet(2000, 1400, new SkiaSharp.SKColor(46, 44, 48),
            (new SkiaSharp.SKRect(120, 130, 940, 1280), 1), (new SkiaSharp.SKRect(1060, 110, 1880, 1260), 2));
        File.WriteAllBytes(Path.Combine(folder, "two-pages-on-a-table.jpg"), PdfImages.Encode(two, ImageFormat.Jpeg, 88));
        // One page whose paragraphs leave gaps: not to be taken for two pages. And a scan that is hard to read.
        using var paragraphs = SheetTests.Paragraphs(gap: 110);
        File.WriteAllBytes(Path.Combine(folder, "one-page-with-paragraph-gaps.png"), PdfImages.Encode(paragraphs, ImageFormat.Png));
        using var blurry = TestPdf.PoorScan();
        File.WriteAllBytes(Path.Combine(folder, "blurry-scan.png"), PdfImages.Encode(blurry, ImageFormat.Png));
        Assert.True(File.Exists(Path.Combine(folder, "scan.pdf")));
    }
}
