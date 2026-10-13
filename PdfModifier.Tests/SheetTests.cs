using System.Text;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Tests;

/// <summary>Sheets that show several pages at once: finding the pages, ordering them and cutting them apart.</summary>
public class SheetTests
{
    private static readonly SKColor Table = new(46, 44, 48);

    /// <summary>The colour of the bar at the head of the first, second, third and fourth page of a sheet.</summary>
    private static readonly SKColor[] Marks = [new(200, 30, 50), new(30, 150, 80), new(40, 90, 210), new(230, 140, 20)];

    /// <summary>
    /// A picture of several pages. Each is a sheet with lines of text and, when it has one, its number at the foot.
    /// On a white background the sheets have no visible edge, as on a print with several pages per sheet.
    /// </summary>
    internal static SKBitmap Sheet(int width, int height, SKColor background, params (SKRect Area, int? Number)[] pages)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);
        using var paper = new SKPaint { Color = SKColors.White };
        using var ink = new SKPaint { Color = new SKColor(30, 30, 30), IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Arial") };
        for (var index = 0; index < pages.Length; index++)
        {
            var (area, number) = pages[index];
            if (area.Width < 1) continue;
            canvas.DrawRect(area, paper);
            // Tells the pages apart once they are cut: their text is the same.
            using (var mark = new SKPaint { Color = Marks[index % Marks.Length] })
                canvas.DrawRect(SKRect.Create(area.MidX - area.Width * 0.12f, area.Top + area.Height * 0.075f, area.Width * 0.24f, area.Height * 0.015f), mark);
            canvas.Save();
            canvas.ClipRect(SKRect.Create(area.Left + area.Width * 0.1f, area.Top, area.Width * 0.8f, area.Height));
            ink.TextSize = area.Height / 46;
            // The text fills the page down to a line's distance above the page number, as running text does.
            for (var line = 0; line < 22; line++)
                canvas.DrawText("The quick brown fox jumps over the lazy dog and keeps on running through the field",
                    area.Left + area.Width * 0.1f, area.Top + area.Height * (0.12f + line * 0.78f / 21), ink);
            canvas.Restore();
            if (number == null) continue;
            ink.TextSize = area.Height / 36;
            var text = number.Value.ToString();
            canvas.DrawText(text, area.MidX - ink.MeasureText(text) / 2, area.Bottom - area.Height * 0.045f, ink);
        }

        return bitmap;
    }

    internal static SKBitmap TwoOnATable(int? first = 1, int? second = 2) => Sheet(2000, 1400, Table,
        (new SKRect(120, 130, 940, 1280), first), (new SKRect(1060, 110, 1880, 1260), second));

    internal static SKBitmap FourOnOneSheet(params int?[] numbers) => Sheet(2400, 3200, SKColors.White,
        (new SKRect(0, 0, 1200, 1600), numbers[0]), (new SKRect(1200, 0, 2400, 1600), numbers[1]),
        (new SKRect(0, 1600, 1200, 3200), numbers[2]), (new SKRect(1200, 1600, 2400, 3200), numbers[3]));

    private static void Close(RectD expected, RectD actual, double tolerance)
    {
        Assert.InRange(actual.Left, expected.Left - tolerance, expected.Left + tolerance);
        Assert.InRange(actual.Top, expected.Top - tolerance, expected.Top + tolerance);
        Assert.InRange(actual.Right, expected.Right - tolerance, expected.Right + tolerance);
        Assert.InRange(actual.Bottom, expected.Bottom - tolerance, expected.Bottom + tolerance);
    }

    private static bool Ready(params string[] languages) =>
        PdfOcr.EngineStatus().Available && TessData.Missing(languages).Count == 0;

    // ---- finding ------------------------------------------------------------------------------------------------

    [Fact]
    public void PagesLyingOnADarkTableAreFoundByTheirPaper()
    {
        using var sheet = TwoOnATable();

        var pages = PdfSheets.Detect(sheet);

        Assert.Equal(2, pages.Count);
        Close(new RectD(120, 130, 940, 1280), pages[0], 14);
        Close(new RectD(1060, 110, 1880, 1260), pages[1], 14);
    }

    [Fact]
    public void PagesOnAGreyScreenshotBackgroundAreFoundToo()
    {
        // The pale grey around the pages of a viewer showing two of them side by side.
        using var sheet = Sheet(2000, 1400, new SKColor(232, 232, 232),
            (new SKRect(80, 60, 960, 1340), 1), (new SKRect(1040, 60, 1920, 1340), 2));

        var pages = PdfSheets.Detect(sheet);

        Assert.Equal(2, pages.Count);
        Close(new RectD(80, 60, 960, 1340), pages[0], 14);
        Close(new RectD(1040, 60, 1920, 1340), pages[1], 14);
    }

    /// <summary>
    /// Two pages photographed on a wooden table: turned a little, with grain in the picture, optionally lit from one
    /// side, optionally lying so close that only a dark line shows between them.
    /// </summary>
    internal static unsafe SKBitmap Photograph(double tilt, bool unevenLight, bool touching)
    {
        var wood = new SKColor(70, 52, 40);
        using var flat = Sheet(2000, 1400, wood, (new SKRect(120, 130, 940, 1280), 1),
            (touching ? new SKRect(952, 124, 1772, 1274) : new SKRect(1060, 110, 1880, 1260), 2));
        var photo = new SKBitmap(new SKImageInfo(2000, 1400, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(photo))
        {
            canvas.Clear(wood);
            canvas.RotateDegrees((float)tilt, 1000, 700);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(flat, 0, 0, paint);
            canvas.ResetMatrix();
            if (unevenLight)
            {
                using var shade = new SKPaint();
                shade.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(2000, 1400),
                    [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 110)], SKShaderTileMode.Clamp);
                canvas.DrawRect(0, 0, 2000, 1400, shade);
            }
        }

        var random = new Random(7);
        var bytes = (byte*)photo.GetPixels();
        for (var i = 0; i < photo.RowBytes * photo.Height; i++)
            if (i % 4 != 3)
                bytes[i] = (byte)Math.Clamp(bytes[i] + random.Next(-12, 13), 0, 255);
        return photo;
    }

    [Theory]
    [InlineData(2.0, false)]
    [InlineData(-1.0, true)]
    [InlineData(4.0, true)]
    public void PagesInAPhotographAreFoundThoughTheyLieAskewAndTheLightIsUneven(double tilt, bool unevenLight)
    {
        using var photo = Photograph(tilt, unevenLight, touching: false);

        var pages = PdfSheets.Detect(photo);

        // Each page comes with the upright box around it, which is the larger the more the page is turned.
        Assert.Equal(2, pages.Count);
        var slack = 30 + Math.Abs(tilt) * 22;
        Close(new RectD(120, 130, 940, 1280), pages[0], slack);
        Close(new RectD(1060, 110, 1880, 1260), pages[1], slack);
        Assert.True(pages[0].Right < pages[1].Left);
    }

    [Fact]
    public void PagesLyingEdgeToEdgeAreDividedAlongTheLineBetweenThem()
    {
        using var photo = Photograph(0, unevenLight: false, touching: true);

        var pages = PdfSheets.Detect(photo);

        Assert.Equal(2, pages.Count);
        Assert.InRange(pages[0].Right, 940, 952);
        Assert.Equal(pages[0].Right, pages[1].Left);
        Close(new RectD(120, 124, 946, 1280), pages[0], 12);
    }

    [Fact]
    public void FourPagesPrintedOnOneSheetAreFoundByTheGapsBetweenThem()
    {
        using var sheet = FourOnOneSheet(1, 2, 3, 4);

        var pages = PdfSheets.Detect(sheet);

        Assert.Equal(4, pages.Count);
        Close(new RectD(0, 0, 1200, 1600), pages[0], 40);
        Close(new RectD(1200, 0, 2400, 1600), pages[1], 40);
        Close(new RectD(0, 1600, 1200, 3200), pages[2], 40);
        Close(new RectD(1200, 1600, 2400, 3200), pages[3], 40);
    }

    [Fact]
    public void AnOpenBookIsCutAtItsFold()
    {
        using var spread = Sheet(2400, 1700, new SKColor(236, 233, 226),
            (new SKRect(0, 0, 0, 0), null));
        using (var canvas = new SKCanvas(spread))
        {
            using var ink = new SKPaint { Color = new SKColor(35, 35, 35), IsAntialias = true, TextSize = 34, Typeface = SKTypeface.FromFamilyName("Arial") };
            foreach (var left in new[] { 150f, 1290f })
            {
                canvas.Save();
                canvas.ClipRect(SKRect.Create(left, 0, 960, 1700));
                for (var y = 190f; y < 1520; y += 56) canvas.DrawText("The quick brown fox jumps over the lazy dog and keeps on running", left, y, ink);
                canvas.Restore();
            }

            // The shadow where the pages curve into the binding: darkest in the middle, fading to both sides.
            for (var d = -16; d <= 16; d++)
            {
                var shade = (byte)(96 + Math.Abs(d) * 8);
                using var shadow = new SKPaint { Color = new SKColor(shade, shade, (byte)(shade - 4)) };
                canvas.DrawRect(SKRect.Create(1200 + d, 0, 1, 1700), shadow);
            }
        }

        var pages = PdfSheets.Detect(spread);

        Assert.Equal(2, pages.Count);
        Assert.InRange(pages[0].Right, 1190, 1210);
        Assert.Equal(pages[0].Right, pages[1].Left);
        Assert.InRange(pages[1].Right, 2390, 2400);
    }

    /// <summary>
    /// A page of running text in paragraphs, cropped close to the text as a screenshot or a trimmed scan is: one
    /// page, however its paragraphs fall. The second gap lies in the middle of the page.
    /// </summary>
    internal static SKBitmap Paragraphs(int width = 1240, int height = 1754, float gap = 70, bool landscapeColumns = false)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var ink = new SKPaint { Color = new SKColor(25, 25, 25), IsAntialias = true, TextSize = 26, Typeface = SKTypeface.FromFamilyName("Arial") };
        const string words = "The quick brown fox jumps over the lazy dog and keeps on running through the field until the evening comes and beyond";
        if (landscapeColumns)
        {
            // Two columns of text on a page lying on its side, with the narrow gap columns have between them.
            foreach (var left in new[] { 70f, width / 2f + 22 })
            {
                canvas.Save();
                canvas.ClipRect(SKRect.Create(left, 0, width / 2f - 92, height));
                for (var y = 110f; y < height - 80; y += 40) canvas.DrawText(words, left, y, ink);
                canvas.Restore();
            }

            return bitmap;
        }

        canvas.Save();
        canvas.ClipRect(SKRect.Create(40, 0, width - 80, height));
        var line = 58f;
        foreach (var lines in new[] { 7, 12, 6, 9, 5 })
        {
            for (var i = 0; i < lines && line < height - 30; i++, line += 40) canvas.DrawText(words, 40, line, ink);
            line += gap;
        }

        canvas.Restore();
        return bitmap;
    }

    [Theory]
    [InlineData(40)]
    [InlineData(70)]
    [InlineData(90)]
    public void GapsBetweenParagraphsDoNotMakeTwoPagesOfOne(float gap)
    {
        using var page = Paragraphs(gap: gap);

        var finding = PdfSheets.Find(page);

        Assert.Single(finding.Pages);
        Close(new RectD(0, 0, 1240, 1754), finding.Pages[0], 3);
        Assert.False(finding.Doubtful, $"certainty {finding.Certainty:0.00}");
    }

    [Fact]
    public void TwoColumnsOnAPageLyingOnItsSideAreNotTwoPages()
    {
        using var page = Paragraphs(1754, 1240, landscapeColumns: true);
        Assert.Single(PdfSheets.Find(page).Pages);
    }

    [Fact]
    public void TheSensitivityDecidesHowMuchHasToSpeakForADivision()
    {
        // One page whose paragraphs leave a wide band across its middle: at the usual setting it stays whole, and
        // someone who knows better can ask for it to be divided all the same.
        using var page = Paragraphs(gap: 110);
        Assert.Single(PdfSheets.Find(page, 0).Pages);
        Assert.Single(PdfSheets.Find(page, 0.5).Pages);
        Assert.Equal(2, PdfSheets.Find(page, 1).Pages.Count);

        // Two pages that are plainly two are found however cautious the setting.
        using var sheet = TwoOnATable();
        Assert.Equal(2, PdfSheets.Find(sheet, 0).Pages.Count);
        using var four = FourOnOneSheet(1, 2, 3, 4);
        Assert.Equal(4, PdfSheets.Find(four, 0).Pages.Count);
        Assert.Equal(4, PdfSheets.Find(four, 1).Pages.Count);
    }

    [Fact]
    public void ACloseCallIsReportedAsDoubtful()
    {
        using var four = FourOnOneSheet(1, 2, 3, 4);
        var sure = PdfSheets.Find(four);
        Assert.False(sure.Doubtful);
        Assert.True(sure.Certainty > 0.75, $"certainty {sure.Certainty:0.00}");

        // The same page with the wide band, just short of and just past what the setting asks for.
        using var page = Paragraphs(gap: 110);
        var limit = Enumerable.Range(25, 26).Select(i => i / 50.0).First(sensitivity => PdfSheets.Find(page, sensitivity).Pages.Count == 2);
        var justDivided = PdfSheets.Find(page, limit);
        var justNot = PdfSheets.Find(page, limit - 0.02);
        Assert.True(justDivided.Doubtful);
        Assert.True(justNot.Doubtful);
        Assert.Single(justNot.Pages);
        // Far from the limit on either side there is nothing to doubt.
        Assert.False(PdfSheets.Find(page, Math.Max(0, limit - 0.4)).Doubtful);
    }

    [Fact]
    public void AnOrdinaryPageStaysWhole()
    {
        using var scan = TestPdf.FakeScan();
        var page = Assert.Single(PdfSheets.Detect(scan));
        Close(new RectD(0, 0, 1240, 1754), page, 3);
    }

    [Fact]
    public void TwoColumnsOfTextAreNotTwoPages()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1240, 1754, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true, TextSize = 26, Typeface = SKTypeface.FromFamilyName("Arial") };
            foreach (var left in new[] { 130f, 640f })
            {
                canvas.Save();
                canvas.ClipRect(SKRect.Create(left, 0, 470, 1754));
                for (var y = 200f; y < 1600; y += 42) canvas.DrawText("The quick brown fox jumps over the lazy dog and runs", left, y, ink);
                canvas.Restore();
            }
        }

        Assert.Single(PdfSheets.Detect(bitmap));
    }

    [Fact]
    public void ASheetLyingOnATableIsFoundAsOnePage()
    {
        using var sheet = Sheet(1600, 2000, Table, (new SKRect(140, 160, 1460, 1860), 7));
        var page = Assert.Single(PdfSheets.Detect(sheet));
        Close(new RectD(140, 160, 1460, 1860), page, 14);
    }

    [Fact]
    public void ADivisionByHandFollowsTheGapNearIt()
    {
        // Two pages whose gap lies a little to the left of the middle of the sheet.
        using var sheet = Sheet(2400, 1600, SKColors.White,
            (new SKRect(0, 0, 1140, 1600), null), (new SKRect(1140, 0, 2400, 1600), null));

        var halves = PdfSheets.Grid(sheet, 2, 1);
        var plain = PdfSheets.Grid(TestPdf.FakeScan(1200, 800), 3, 2);

        Assert.Equal(2, halves.Count);
        Assert.InRange(halves[0].Right, 1100, 1180);
        Assert.Equal(6, plain.Count);
        Close(new RectD(400, 400, 800, 800), plain[4], 2);
    }

    [Fact]
    public void PagesOfADocumentAreFoundInItsOwnCoordinates()
    {
        using var sheet = TwoOnATable();
        using var view = new PdfView(TestPdf.FromImage(sheet, 150));

        var pages = PdfSheets.Detect(view, 0);

        // 150 dpi: two pixels of the picture are about one point of the page.
        Assert.Equal(2, pages.Count);
        Close(new RectD(120 * 0.48, 130 * 0.48, 940 * 0.48, 1280 * 0.48), pages[0], 7);
        Close(new RectD(1060 * 0.48, 110 * 0.48, 1880 * 0.48, 1260 * 0.48), pages[1], 7);
    }

    // ---- where a page ends --------------------------------------------------------------------------------------

    private static readonly SheetEdges Usual = SheetEdges.Automatic(0.5);

    /// <summary>
    /// The share of a frame's outline that lies on something dark: the lid of a scanner, a table, the depth of a fold.
    /// A frame that sits on its page has next to none.
    /// </summary>
    private static double OffThePaper(SKBitmap picture, RectD frame, int inset = 4, int darkBelow = 130)
    {
        int left = (int)frame.Left + inset, top = (int)frame.Top + inset, right = (int)frame.Right - 1 - inset, bottom = (int)frame.Bottom - 1 - inset;
        int dark = 0, all = 0;

        void Look(int x, int y)
        {
            var c = picture.GetPixel(Math.Clamp(x, 0, picture.Width - 1), Math.Clamp(y, 0, picture.Height - 1));
            all++;
            if ((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000 < darkBelow) dark++;
        }

        for (var x = left; x <= right; x += 2)
        {
            Look(x, top);
            Look(x, bottom);
        }

        for (var y = top; y <= bottom; y += 2)
        {
            Look(left, y);
            Look(right, y);
        }

        return (double)dark / all;
    }

    private static void Within(RectD outer, RectD inner, double slack = 2)
    {
        Assert.True(inner.Left >= outer.Left - slack && inner.Top >= outer.Top - slack && inner.Right <= outer.Right + slack &&
                    inner.Bottom <= outer.Bottom + slack, $"{inner} is not inside {outer}");
    }

    [Fact]
    public void ABookWithTheEdgesOfItsPagesShowingIsStillCutAtItsFold()
    {
        // The shadow in the fold runs on over the rim of pages at the foot of the book, and a book that lies a
        // little askew smears the line of its fold over several columns: both are one fold all the same.
        using var thick = SheetPictures.BookOnScanner(left: 90, top: 60, right: 130, bottom: 150, block: 14);
        using var askew = SheetPictures.BookOnScanner(left: 60, top: 40, right: 200, bottom: 160, tilt: 0.8, noise: 6);

        var pages = PdfSheets.Detect(thick);
        Assert.Equal(2, pages.Count);
        Assert.InRange(pages[0].Right, 1170, 1190);
        Assert.Equal(2, PdfSheets.Detect(askew).Count);
    }

    [Fact]
    public void TheDarkAroundABookAndTheShadowInItsFoldAreLeftOut()
    {
        // The book lies at (90, 60) to (2270, 1550) and shows 14 pixels of the pages underneath along its outer edges.
        using var book = SheetPictures.BookOnScanner(left: 90, top: 60, right: 130, bottom: 150, block: 14, noise: 6);

        var found = PdfSheets.Find(book, edges: Usual);

        Assert.Equal(2, found.Pages.Count);
        Assert.False(found.AnyEdgeInDoubt);
        var (left, right) = (found.Pages[0], found.Pages[1]);
        // Past the rim of pages on the outside, short of the shadow on the inside.
        Assert.InRange(left.Left, 102, 112);
        Assert.InRange(left.Right, 1130, 1166);
        Assert.InRange(right.Left, 1194, 1230);
        Assert.InRange(right.Right, 2248, 2258);
        Assert.InRange(left.Top, 60, 70);
        Assert.InRange(left.Bottom, 1526, 1538);
        Assert.Equal(0, OffThePaper(book, left), 0.004);
        Assert.Equal(0, OffThePaper(book, right), 0.004);
        // Nothing of the pages themselves is given up: their print and their numbers are inside.
        Within(left, new RectD(104 + 108, 60 + 110, 1180 - 108, 1536 - 50));
        Within(right, new RectD(1180 + 108, 60 + 110, 2256 - 108, 1536 - 50));
    }

    [Theory]
    [InlineData(0.8)]
    [InlineData(-1.2)]
    public void ABookLyingAskewOnTheGlassComesWithoutTheGlass(double tilt)
    {
        using var book = SheetPictures.BookOnScanner(left: 60, top: 40, right: 200, bottom: 160, tilt: tilt, noise: 6);

        var asFound = PdfSheets.Find(book);
        var found = PdfSheets.Find(book, edges: Usual);

        Assert.Equal(2, found.Pages.Count);
        Assert.False(found.AnyEdgeInDoubt);
        // As found, the frames come with wedges of the lid; cut closer, they lie on paper all the way round.
        Assert.True(OffThePaper(book, asFound.Pages[0]) > 0.02);
        Assert.All(found.Pages, page => Assert.Equal(0, OffThePaper(book, page), 0.004));
        for (var i = 0; i < 2; i++) Within(asFound.Pages[i], found.Pages[i]);
        Assert.True(found.Pages[0].Right < found.Pages[1].Left - 20);
    }

    [Fact]
    public void ADeepShadowInTheFoldGoesWithNeitherPage()
    {
        using var book = SheetPictures.BookOnScanner(fold: 90, noise: 6);

        var found = PdfSheets.Find(book, edges: Usual);

        Assert.Equal(2, found.Pages.Count);
        Assert.False(found.AnyEdgeInDoubt);
        Assert.True(found.Pages[1].Left - found.Pages[0].Right >= 80, $"{found.Pages[0].Right} to {found.Pages[1].Left}");
        Assert.All(found.Pages, page => Assert.Equal(0, OffThePaper(book, page), 0.004));
        // The print of both pages begins 111 pixels from the fold at x = 1115.
        Assert.True(found.Pages[0].Right > 1115 - 100 && found.Pages[1].Left < 1115 + 100);
    }

    [Fact]
    public void TheBlackAlongTheEdgesOfAPhotocopyIsCutOff()
    {
        // 70 pixels of black on the left and 90 at the foot, ragged where they end.
        using var copy = SheetPictures.Photocopy();

        var found = PdfSheets.Find(copy, edges: Usual);

        var page = Assert.Single(found.Pages);
        Assert.False(found.AnyEdgeInDoubt);
        Assert.InRange(page.Left, 70, 78);
        Assert.InRange(page.Bottom, 1754 - 98, 1754 - 90);
        Assert.Equal((0.0, 1240.0), (page.Top, page.Right));
    }

    [Fact]
    public void ASliverOfTheNextPageGoesWhenTheCutMayReachThatFar()
    {
        // 60 pixels of the page before, then the shadow of the fold, 22 pixels to either side of x = 60.
        using var page = SheetPictures.PageWithSliver();

        // Within reach of the usual setting there is no clean paper yet: the page stays whole, marked for a look.
        var usual = PdfSheets.Find(page, edges: Usual);
        Close(new RectD(0, 0, 1300, 1700), Assert.Single(usual.Pages), 2);
        Assert.True(usual.EdgeInDoubt(0));

        var closer = PdfSheets.Find(page, edges: SheetEdges.Automatic(0.7));
        Assert.False(closer.AnyEdgeInDoubt);
        Assert.InRange(closer.Pages[0].Left, 70, 92);
        // The page's own print begins 124 pixels to the right of the fold.
        Assert.Equal((0.0, 1300.0, 1700.0), (closer.Pages[0].Top, closer.Pages[0].Right, closer.Pages[0].Bottom));
    }

    public static IEnumerable<object[]> PagesWithNothingAroundThem() =>
        new[] { "four", "paragraphs", "scan", "to the edge", "table" }.SelectMany(kind => new[] { 0.25, 0.5, 0.7 }.Select(c => new object[] { kind, c }));

    [Theory]
    [MemberData(nameof(PagesWithNothingAroundThem))]
    public void WhatIsPageIsNotCutInto(string kind, double closeness)
    {
        using var scan = TestPdf.FakeScan();
        using var picture = kind switch
        {
            "four" => FourOnOneSheet(1, 2, 3, 4),
            "paragraphs" => Paragraphs(gap: 70),
            "scan" => scan.Copy(),
            // Cropped so that the first letter of every line touches the left edge, and the print nearly the others.
            "to the edge" => SheetPictures.Part(scan, 140, 190, 1180, 1560),
            _ => TwoOnATable()
        };

        var asFound = PdfSheets.Find(picture);
        var found = PdfSheets.Find(picture, edges: SheetEdges.Automatic(closeness));

        Assert.Equal(asFound.Pages.Count, found.Pages.Count);
        Assert.False(found.AnyEdgeInDoubt);
        // The sheets on the table lose the few pixels of table their frames came with, and nothing else.
        for (var i = 0; i < found.Pages.Count; i++) Close(asFound.Pages[i], found.Pages[i], kind == "table" ? 6 : 1);
    }

    [Fact]
    public void APictureThatRunsOffThePageIsNotTakenForWhatLiesAroundIt()
    {
        using var page = SheetPictures.PageWithPictureToTheEdge();

        foreach (var closeness in new[] { 0.25, 0.5, 0.7, 1 })
        {
            var found = PdfSheets.Find(page, edges: SheetEdges.Automatic(closeness));
            var area = Assert.Single(found.Pages);
            // The photograph is dark from side to side as a band would be, but not of one shade: it stays, and the
            // page is marked as one to look at.
            Assert.Equal((0.0, 0.0, 1240.0), (area.Left, area.Top, area.Right));
            Assert.True(found.EdgeInDoubt(0));
        }
    }

    [Fact]
    public void PagesInAPhotographLoseTheTableAroundThem()
    {
        using var slightly = Photograph(2, unevenLight: false, touching: false);
        var found = PdfSheets.Find(slightly, edges: Usual);
        Assert.Equal(2, found.Pages.Count);
        Assert.False(found.AnyEdgeInDoubt);
        Assert.All(found.Pages, page => Assert.Equal(0, OffThePaper(slightly, page), 0.004));

        // Turned further, the corners of table reach deeper than the usual setting cuts: said so, not guessed at.
        using var further = Photograph(4, unevenLight: true, touching: false);
        var usual = PdfSheets.Find(further, edges: Usual);
        Assert.True(usual.EdgeInDoubt(0) && usual.EdgeInDoubt(1));
        var closer = PdfSheets.Find(further, edges: SheetEdges.Automatic(0.7));
        Assert.False(closer.AnyEdgeInDoubt);
        // (In the shade of this photograph the paper itself is nearly as dark as 130; the table is far darker. A page
        // turned this far has its print almost at the upright frame, which may graze a letter.)
        Assert.True(OffThePaper(further, usual.Pages[0], darkBelow: 100) > 0.02);
        Assert.All(closer.Pages, page => Assert.Equal(0, OffThePaper(further, page, darkBelow: 100), 0.01));
    }

    [Fact]
    public void TheCloserTheCutTheLessIsLeft()
    {
        using var book = SheetPictures.BookOnScanner(left: 90, top: 60, right: 130, bottom: 150, block: 14, noise: 6);
        var steps = new[] { 0, 0.25, 0.5, 0.7, 0.85, 1 }.Select(c => PdfSheets.Find(book, edges: SheetEdges.Automatic(c)).Pages).ToList();

        for (var i = 1; i < steps.Count; i++)
        for (var page = 0; page < 2; page++)
            Within(steps[i - 1][page], steps[i][page], 4);
        // Up to 0.7 only what is not page goes; beyond that the margins go too, and at 1 the cut lies around the print:
        // 24 lines from 110 to 1265 pixels down the page and its number below them.
        static double Area(RectD r) => r.Width * r.Height;
        Assert.True(Area(steps[3][0]) > Area(steps[0][0]) * 0.9);
        Assert.True(Area(steps[5][0]) < Area(steps[3][0]) * 0.8);
        Assert.InRange(steps[5][0].Left, 104 + 60, 104 + 108);
        Assert.InRange(steps[5][0].Bottom, 1480, 1510);
    }

    [Fact]
    public void StripsSetByHandComeOffAtTheEdgesOfTheSheetAndWherePagesMeet()
    {
        using var sheet = FourOnOneSheet(1, 2, 3, 4);
        var asFound = PdfSheets.Find(sheet).Pages;

        // 2 % of the sheet's 2400 pixels off its left, 4 % off its right; 3 and 5 % of its 3200 off top and bottom;
        // and 1 % off every page where it meets another.
        var pages = PdfSheets.Find(sheet, edges: SheetEdges.ByHand(0.02, 0.03, 0.04, 0.05, 0.01)).Pages;

        Close(new RectD(asFound[0].Left + 48, asFound[0].Top + 96, asFound[0].Right - 24, asFound[0].Bottom - 32), pages[0], 3);
        Close(new RectD(asFound[1].Left + 24, asFound[1].Top + 96, asFound[1].Right - 96, asFound[1].Bottom - 32), pages[1], 3);
        Close(new RectD(asFound[2].Left + 48, asFound[2].Top + 32, asFound[2].Right - 24, asFound[2].Bottom - 160), pages[2], 3);
        Close(new RectD(asFound[3].Left + 24, asFound[3].Top + 32, asFound[3].Right - 96, asFound[3].Bottom - 160), pages[3], 3);

        // A page that has the sheet to itself meets no other: only the edges of the sheet count.
        using var scan = TestPdf.FakeScan();
        Close(new RectD(124, 0, 1240, 1754), Assert.Single(PdfSheets.Find(scan, edges: SheetEdges.ByHand(0.1, 0, 0, 0, 0.3)).Pages), 2);
        // And a division asked for is seen to the same way.
        var halves = PdfSheets.Divide(scan, 2, 1, SheetEdges.ByHand(0, 0, 0, 0, 0.05)).Pages;
        Assert.Equal(2, halves.Count);
        Assert.Equal(124, halves[1].Left - halves[0].Right, 3.0);
    }

    [Fact]
    public void TheEdgesOfAPageOfADocumentAreFoundInItsOwnCoordinates()
    {
        using var book = SheetPictures.BookOnScanner(left: 90, top: 60, right: 130, bottom: 150, block: 14, noise: 6);
        using var view = new PdfView(TestPdf.FromImage(book, 150));

        var found = PdfSheets.Find(view, 0, PdfSheets.UsualSensitivity, Usual);

        // 150 dpi: a pixel of the picture is 0.48 points of the page.
        Assert.Equal(2, found.Pages.Count);
        Assert.InRange(found.Pages[0].Left, 102 * 0.48, 113 * 0.48);
        Assert.InRange(found.Pages[1].Right, 2247 * 0.48, 2258 * 0.48);
        Assert.InRange(found.Pages[0].Bottom, 1526 * 0.48, 1538 * 0.48);
    }

    // ---- ordering -----------------------------------------------------------------------------------------------

    private static readonly RectD[] Quarters =
    [
        new(0, 0, 100, 140), new(100, 2, 200, 141), new(1, 140, 100, 280), new(100, 141, 200, 280)
    ];

    [Fact]
    public void PagesAreOrderedTheWayTheyAreRead()
    {
        Assert.Equal([0, 1, 2, 3], PdfSheets.Order(Quarters, SheetOrder.Rows));
        Assert.Equal([1, 0, 3, 2], PdfSheets.Order(Quarters, SheetOrder.RowsRightToLeft));
        Assert.Equal([0, 2, 1, 3], PdfSheets.Order(Quarters, SheetOrder.Columns));
        Assert.Equal([0, 1, 2, 3], PdfSheets.Order(Quarters, SheetOrder.Automatic));
    }

    [Fact]
    public void PrintedPageNumbersDecideTheOrder()
    {
        Assert.Equal([1, 2, 3, 0], PdfSheets.Order(Quarters, SheetOrder.Automatic, [4, 1, 2, 3]));
        // The two halves of a folded sheet: the last page is printed left of the first.
        Assert.Equal([1, 0], PdfSheets.Order(Quarters[..2], SheetOrder.Automatic, [8, 1]));
        // Without a number a page keeps its place after the numbered one before it: a title page comes first.
        Assert.Equal([0, 1, 2, 3], PdfSheets.Order(Quarters, SheetOrder.Automatic, [null, 2, null, 4]));
        Assert.Equal([2, 3, 0, 1], PdfSheets.Order(Quarters, SheetOrder.Automatic, [5, null, 1, 2]));
        // Numbers are for the automatic order only.
        Assert.Equal([0, 1, 2, 3], PdfSheets.Order(Quarters, SheetOrder.Rows, [4, 1, 2, 3]));
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("- 12 -", 12)]
    [InlineData("Page 31", 31)]
    [InlineData("Sayfa 4", 4)]
    [InlineData("стр. 15", 15)]
    [InlineData("3 / 10", 3)]
    [InlineData("Page 2 of 9", 2)]
    [InlineData("12ページ", 12)]
    public void APageNumberIsRecognisedInItsUsualForms(string line, int expected) =>
        Assert.Equal(expected, PdfSheets.Candidate(line)?.Number);

    [Theory]
    [InlineData("The quick brown fox")]
    [InlineData("Chapter 3 Introduction")]
    [InlineData("12/2024")]
    [InlineData("3.5.2024")]
    [InlineData("")]
    public void OrdinaryTextIsNotAPageNumber(string line) => Assert.Null(PdfSheets.Candidate(line));

    [Fact]
    public void PageNumbersAreReadFromTheSheet()
    {
        if (!Ready("en")) return;
        using var sheet = FourOnOneSheet(3, 4, 1, 2);
        using var view = new PdfView(TestPdf.FromImage(sheet, 150));
        var pages = PdfSheets.Detect(view, 0);

        var numbers = PdfSheets.ReadPageNumbers(view, 0, pages, ["en"]);

        Assert.Equal([3, 4, 1, 2], numbers);
        Assert.Equal([2, 3, 0, 1], PdfSheets.Order(pages, SheetOrder.Automatic, numbers));
    }

    [Fact]
    public void APageWithoutANumberHasNone()
    {
        if (!Ready("en")) return;
        using var sheet = TwoOnATable(first: null, second: 12);

        var numbers = PdfSheets.ReadPageNumbers(sheet, PdfSheets.Detect(sheet), ["en"]);

        Assert.Equal([null, 12], numbers);
    }

    // ---- cutting ------------------------------------------------------------------------------------------------

    internal static unsafe List<(int Width, int Height, string Filter)> Pictures(byte[] pdf, int pageIndex)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            using var page = doc.LoadPage(pageIndex);
            var found = new List<(int, int, string)>();
            var name = stackalloc byte[64];
            foreach (var obj in page.GetObjects())
            {
                if (Native.FPDFPageObj_GetType(obj) != Native.FPDF_PAGEOBJ_IMAGE) continue;
                uint w, h;
                Native.FPDFImageObj_GetImagePixelSize(obj, &w, &h);
                var length = (int)Native.FPDFImageObj_GetImageFilter(obj, 0, name, new System.Runtime.InteropServices.CULong(64)).Value;
                found.Add(((int)w, (int)h, length > 1 ? Encoding.ASCII.GetString(name, length - 1) : ""));
            }

            return found;
        }
    }

    /// <summary>
    /// How much two pictures of a page differ, seen from a distance, in levels of brightness: print that was sampled
    /// a fraction of a pixel differently does not count, a page cut in the wrong place does.
    /// </summary>
    private static double Difference(SKBitmap expected, SKBitmap actual)
    {
        using var resized = PdfModifier.Core.Imaging.ImageFilters.Resize(actual, expected.Width, expected.Height);
        return PdfModifier.Core.Imaging.ImageAnalysis.BlockDifference(expected, resized);
    }

    /// <summary>Which page of the sheet a cut-out page is, by the colour of the bar at its head.</summary>
    internal static int Mark(byte[] pdf, int pageIndex)
    {
        using var page = TestPdf.Render(pdf, pageIndex);
        // The bar is the first thing of any colour down the middle of the page; just where it lies depends on
        // where in the space between two pages the cut was made.
        var seen = Enumerable.Range((int)(page.Height * 0.02), (int)(page.Height * 0.12))
            .Select(y => page.GetPixel(page.Width / 2, y))
            .FirstOrDefault(c => Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue)) > 70);
        return Array.IndexOf(Marks, Marks.MinBy(m => Math.Abs(m.Red - seen.Red) + Math.Abs(m.Green - seen.Green) + Math.Abs(m.Blue - seen.Blue)));
    }

    private static SKBitmap Crop(SKBitmap source, RectD area)
    {
        var part = new SKBitmap(new SKImageInfo((int)Math.Round(area.Width), (int)Math.Round(area.Height), SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(part);
        canvas.DrawBitmap(source, (float)-area.Left, (float)-area.Top);
        return part;
    }

    [Fact]
    public void CuttingGivesEveryPageItsOwnPartOfTheSheet()
    {
        using var sheet = TwoOnATable();
        var scan = TestPdf.FromImage(sheet, 150);
        var pdf = PdfPages.Merge([TestPdf.WithText("Before"), scan, TestPdf.WithText("After")]);
        IReadOnlyList<RectD> pages;
        using (var view = new PdfView(pdf)) pages = PdfSheets.Detect(view, 1);

        // The second page of the sheet first, as someone might ask for by clicking them in that order.
        var result = PdfSheets.Split(pdf, new Dictionary<int, IReadOnlyList<RectD>> { [1] = [pages[1], pages[0]] });

        Assert.Equal(4, TestPdf.PageCount(result));
        Assert.Equal("Before", TestPdf.Text(result, 0));
        Assert.Equal("After", TestPdf.Text(result, 3));
        var infos = PdfPages.GetPageInfos(result);
        Assert.Equal(pages[1].Width, infos[1].Width, 0.6);
        Assert.Equal(pages[1].Height, infos[1].Height, 0.6);
        Assert.Equal(pages[0].Width, infos[2].Width, 0.6);

        using var whole = TestPdf.Render(scan, 0, 2);
        for (var i = 0; i < 2; i++)
        {
            var area = pages[1 - i];
            using var expected = Crop(whole, new RectD(area.Left * 2, area.Top * 2, area.Right * 2, area.Bottom * 2));
            using var actual = TestPdf.Render(result, 1 + i, 2);
            Assert.True(Difference(expected, actual) < 3, $"page {i + 1} differs by {Difference(expected, actual):0.0}");
        }

        Assert.Equal((1, 0), (Mark(result, 1), Mark(result, 2)));

        // Each page holds the pixels of its own part and no more.
        var (width, height, _) = Assert.Single(Pictures(result, 1));
        Assert.InRange(width, 820, 860);
        Assert.InRange(height, 1150, 1190);
        Assert.True(result.Length < pdf.Length * 1.1, $"{result.Length} bytes after, {pdf.Length} before");
    }

    [Fact]
    public void APhotographStaysAPhotographAndTheSheetCanBeKept()
    {
        using var sheet = TwoOnATable();
        var jpeg = PdfImages.Encode(sheet, ImageFormat.Jpeg, 85);
        var pdf = PdfImages.InsertImages(null, [jpeg], 0, ImagePageOptions.Natural);
        Assert.Equal("DCTDecode", Assert.Single(Pictures(pdf, 0)).Filter);
        IReadOnlyList<RectD> pages;
        using (var view = new PdfView(pdf)) pages = PdfSheets.Detect(view, 0);
        Assert.Equal(2, pages.Count);

        var result = PdfSheets.Split(pdf, new Dictionary<int, IReadOnlyList<RectD>> { [0] = pages }, keepSheets: true);

        Assert.Equal(3, TestPdf.PageCount(result));
        Assert.Equal((2000, 1400, "DCTDecode"), Assert.Single(Pictures(result, 0)));
        Assert.Equal("DCTDecode", Assert.Single(Pictures(result, 1)).Filter);
        Assert.InRange(Assert.Single(Pictures(result, 2)).Width, 820, 860);
        Assert.Equal((0, 1), (Mark(result, 1), Mark(result, 2)));
    }

    [Fact]
    public void TextAndDrawingsGoWithTheirOwnPage()
    {
        // Two pages of real text printed side by side on one landscape sheet.
        var pdf = TestPdf.Compose((page, fonts) =>
        {
            fonts.Prepare("Left page Right", null);
            PageDraw.DrawText(page, fonts, "Left page", new TextStyle(20), 60, 120);
            PageDraw.DrawText(page, fonts, "Right page", new TextStyle(20), 480, 120);
            PageDraw.DrawRect(page, new RectD(480, 300, 760, 420), 0xFF2060C0);
        }, 842, 595);

        var result = PdfSheets.Split(pdf, new Dictionary<int, IReadOnlyList<RectD>>
        {
            [0] = [new RectD(0, 0, 421, 595), new RectD(421, 0, 842, 595)]
        });

        Assert.Equal(2, TestPdf.PageCount(result));
        Assert.Equal("Left page", TestPdf.Text(result, 0));
        Assert.Equal("Right page", TestPdf.Text(result, 1));
        var infos = PdfPages.GetPageInfos(result);
        Assert.Equal(421, infos[0].Width, 0.01);
        Assert.Equal(595, infos[0].Height, 0.01);
        using var right = TestPdf.Render(result, 1);
        // The blue box is on the right page, where it was on the sheet: 59 points in from the cut.
        Assert.Equal(new SKColor(0x20, 0x60, 0xC0), right.GetPixel(200, 360));
        using var left = TestPdf.Render(result, 0);
        Assert.Equal(SKColors.White, left.GetPixel(200, 360));
    }

    [Fact]
    public void ATurnedSheetIsCutTheWayItIsShown()
    {
        using var sheet = TwoOnATable();
        // Stored on its side and turned upright by the page, as a scanner fed sideways does.
        var turned = PdfPages.Rotate(TestPdf.FromImage(sheet, 150), null, 90);
        IReadOnlyList<RectD> pages;
        using (var view = new PdfView(turned)) pages = PdfSheets.Detect(view, 0);
        Assert.Equal(2, pages.Count);

        var result = PdfSheets.Split(turned, new Dictionary<int, IReadOnlyList<RectD>> { [0] = pages });

        using var whole = TestPdf.Render(turned, 0, 2);
        for (var i = 0; i < 2; i++)
        {
            using var expected = Crop(whole, new RectD(pages[i].Left * 2, pages[i].Top * 2, pages[i].Right * 2, pages[i].Bottom * 2));
            using var actual = TestPdf.Render(result, i, 2);
            Assert.True(Difference(expected, actual) < 3, $"page {i + 1} differs by {Difference(expected, actual):0.0}");
        }

        // The picture lies on its side in the file: what is cut from it is as wide as the page is in the picture.
        Assert.InRange(Assert.Single(Pictures(result, 0)).Width, 820, 860);
    }

    [Fact]
    public void NothingToCutLeavesTheDocumentAsItIs()
    {
        var pdf = TestPdf.Numbered(2);
        Assert.Same(pdf, PdfSheets.Split(pdf, new Dictionary<int, IReadOnlyList<RectD>>()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfSheets.Split(pdf, new Dictionary<int, IReadOnlyList<RectD>> { [5] = [new RectD(0, 0, 10, 10)] }));
    }

    // ---- pictures as pages --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2480, 3508, 300)]
    [InlineData(1240, 1754, 150)]
    [InlineData(1654, 2339, 200)]
    [InlineData(4000, 3000, 400)]
    [InlineData(800, 600, 72)]
    public void TheResolutionOfAPictureIsGuessedFromItsSize(int width, int height, double expected) =>
        Assert.Equal(expected, PdfImages.GuessDpi(width, height));

    [Fact]
    public void PicturesBecomePagesStoredTheWayTheyCame()
    {
        using var scan = TestPdf.FakeScan(1240, 1754);
        var jpeg = PdfImages.Encode(scan, ImageFormat.Jpeg, 80);
        var png = PdfImages.Encode(scan, ImageFormat.Png);

        var pdf = PdfImages.InsertImages(null, [jpeg, png], 0, ImagePageOptions.Natural);

        var infos = PdfPages.GetPageInfos(pdf);
        Assert.Equal((595.2, 841.92), (Math.Round(infos[0].Width, 2), Math.Round(infos[0].Height, 2)));
        // The JPEG file is embedded as it is, the PNG without loss.
        Assert.Equal("DCTDecode", Assert.Single(Pictures(pdf, 0)).Filter);
        Assert.Equal("FlateDecode", Assert.Single(Pictures(pdf, 1)).Filter);
        Assert.InRange(pdf.Length, jpeg.Length, jpeg.Length + png.Length * 3);
        Assert.True(PdfImages.IsImageFile(@"C:\scans\page 1.JPG"));
        Assert.False(PdfImages.IsImageFile("notes.txt"));
    }
}
