using PdfModifier.Core;
using PdfModifier.Core.Fonts;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Tests;

/// <summary>Text boxes, images and patches the app adds must stay changeable, also after saving and reopening.</summary>
public class PageObjectTests
{
    private static PageText Box(int id, string text, double left = 100, double top = 200, double width = 220, double size = 14) =>
        PdfPageObjects.Fit(new PageText { Id = id, Text = text, Bounds = new RectD(left, top, left + width, top + 10), FontSize = size });

    private static ImagePixels Picture(int width = 60, int height = 40, SKColor? color = null, bool jpeg = false)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bitmap.Erase(color ?? SKColors.Red);
        return ImagePixels.FromFile(PdfImages.Encode(bitmap, jpeg ? ImageFormat.Jpeg : ImageFormat.Png));
    }

    [Fact]
    public void ATextBoxIsWrittenAndReadBackAsItWasDefined()
    {
        var box = Box(1, "First line of the box\nSecond line, bold and blue") with
        {
            FontFamily = "Times New Roman", Bold = true, Italic = true, Color = 0xFF1040C0, Align = TextAlign.Center, Rotation = 0
        };
        box = PdfPageObjects.Fit(box);

        var pdf = PdfPageObjects.Write(TestPdf.WithText("Existing"), 0, [box]);

        var read = Assert.IsType<PageText>(Assert.Single(PdfPageObjects.Read(pdf, 0)));
        Assert.Equal(box.Text, read.Text);
        Assert.Equal((box.FontFamily, box.Bold, box.Italic, box.Color, box.Align), (read.FontFamily, read.Bold, read.Italic, read.Color, read.Align));
        Assert.Equal(box.FontSize, read.FontSize, 0.01);
        AssertBox(box.Bounds, read.Bounds, 0.05);
        Assert.Equal(0, read.Rotation, 0.01);

        var text = TestPdf.Text(pdf, 0);
        Assert.Contains("First line of the box", text);
        Assert.Contains("Second line, bold and blue", text);
        // Added boxes are changed as objects; they are not offered again as lines of the document.
        Assert.Equal("Existing", Assert.Single(PdfTextEdit.GetTextRuns(pdf, 0)).Text);
    }

    [Fact]
    public void LongTextIsWrappedToTheWidthOfTheBox()
    {
        var text = string.Join(' ', Enumerable.Repeat("wrapping", 30));
        var narrow = TextLayout.Create(text, 150, 12, null, false, false);
        var wide = TextLayout.Create(text, 400, 12, null, false, false);

        Assert.True(narrow.Lines.Count > wide.Lines.Count && wide.Lines.Count > 1);
        Assert.All(narrow.Lines, line => Assert.True(line.Width <= 150.01, $"{line.Width} wide"));
        Assert.Equal(text, string.Join(' ', narrow.Lines.Select(l => l.Text)));
        Assert.Equal(narrow.Lines.Count * 12 * 1.2, narrow.Height, 0.001);

        // A word that is too wide on its own is cut rather than left sticking out.
        var cut = TextLayout.Create(new string('W', 60), 100, 12, null, false, false);
        Assert.True(cut.Lines.Count > 1);
        Assert.All(cut.Lines, line => Assert.True(line.Width <= 100.01));

        // Typed line breaks are kept, including empty lines; scripts without spaces break between characters.
        Assert.Equal(["a", "", "b"], TextLayout.Create("a\n\nb", 300, 12, null, false, false).Lines.Select(l => l.Text));
        Assert.True(TextLayout.Create(new string('日', 40), 100, 12, null, false, false).Lines.Count > 3);
    }

    [Fact]
    public void LinesAreAlignedInsideTheBox()
    {
        var left = TextLayout.Create("short", 200, 12, null, false, false);
        var centre = TextLayout.Create("short", 200, 12, null, false, false, TextAlign.Center);
        var right = TextLayout.Create("short", 200, 12, null, false, false, TextAlign.Right);
        var width = left.Lines[0].Width;
        Assert.Equal(0, left.Lines[0].X);
        Assert.Equal((200 - width) / 2, centre.Lines[0].X, 0.001);
        Assert.Equal(200 - width, right.Lines[0].X, 0.001);
    }

    [Fact]
    public void MovingTurningAndScalingABoxKeepsItsTextAndDoesNotEmbedAnotherFont()
    {
        var box = Box(1, "This box is going to move");
        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0, [box]);

        // Twice the size, somewhere else, tilted.
        var moved = box with { Bounds = new RectD(300, 500, 300 + box.Bounds.Width * 2, 500 + box.Bounds.Height * 2), FontSize = box.FontSize * 2, Rotation = 30 };
        var changed = PdfPageObjects.Write(pdf, 0, [moved]);

        var read = Assert.IsType<PageText>(Assert.Single(PdfPageObjects.Read(changed, 0)));
        AssertBox(moved.Bounds, read.Bounds, 0.1);
        Assert.Equal(30, read.Rotation, 0.01);
        Assert.Equal(moved.FontSize, read.FontSize, 0.01);
        Assert.Equal(box.Text, read.Text);
        Assert.True(changed.Length < pdf.Length + 1500, $"{pdf.Length} -> {changed.Length} bytes");

        // Writing the same objects again changes nothing at all.
        Assert.Same(changed, PdfPageObjects.Write(changed, 0, [read]));
    }

    [Fact]
    public void ChangingTheWordsOrTheLookRewritesTheBox()
    {
        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0, [Box(1, "Before"), Box(2, "Untouched", top: 400)]);

        var objects = PdfPageObjects.Read(pdf, 0).Cast<PageText>().ToList();
        var edited = PdfPageObjects.Write(pdf, 0, [objects[0] with { Text = "After the change", Color = 0xFFCC0000, FontSize = 22 }, objects[1]]);

        var text = TestPdf.Text(edited, 0);
        Assert.Contains("After the change", text);
        Assert.DoesNotContain("Before", text);
        Assert.Contains("Untouched", text);
        var read = PdfPageObjects.Read(edited, 0).Cast<PageText>().ToList();
        Assert.Equal([1, 2], read.Select(o => o.Id));
        Assert.Equal((0xFFCC0000u, 22.0), (read[0].Color, Math.Round(read[0].FontSize, 2)));
    }

    [Fact]
    public void AnImageKeepsItsPlaceOpacityAndPixels()
    {
        var image = new PageImage { Id = 3, Pixels = Picture(), Bounds = new RectD(100, 200, 280, 320), Opacity = 0.5 };
        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0, [image]);

        using (var rendered = TestPdf.Render(pdf, 0))
        {
            var inside = rendered.GetPixel(190, 260);
            Assert.InRange(inside.Green, 110, 145); // red at half strength over white
            Assert.InRange(inside.Red, 250, 255);
            Assert.Equal(SKColors.White, rendered.GetPixel(90, 260));
            Assert.Equal(SKColors.White, rendered.GetPixel(190, 330));
        }

        var read = Assert.IsType<PageImage>(Assert.Single(PdfPageObjects.Read(pdf, 0)));
        AssertBox(image.Bounds, read.Bounds, 0.05);
        Assert.Equal(0.5, read.Opacity, 0.01);
        Assert.Equal((60, 40), (read.Pixels.Width, read.Pixels.Height));
        Assert.False(read.Pixels.HasAlpha);
        // The stored pixels are the picture itself, at full strength.
        var pixel = read.Pixels.Bitmap.GetPixel(30, 20);
        Assert.True(pixel.Red > 250 && pixel.Green < 5 && pixel.Alpha == 255, pixel.ToString());

        // Moving, resizing, turning and fading it only changes how it is placed: the picture is not stored again.
        var changed = PdfPageObjects.Write(pdf, 0, [read with { Bounds = new RectD(300, 400, 400, 460), Rotation = 90, Opacity = 1 }]);
        Assert.True(changed.Length < pdf.Length + 600, $"{pdf.Length} -> {changed.Length} bytes");
        var again = Assert.IsType<PageImage>(Assert.Single(PdfPageObjects.Read(changed, 0)));
        AssertBox(new RectD(300, 400, 400, 460), again.Bounds, 0.05);
        Assert.Equal(90, again.Rotation, 0.01);
        Assert.Equal(1, again.Opacity, 0.01);
        using var turned = TestPdf.Render(changed, 0);
        // Turned a quarter, the 100 x 60 box stands 60 wide and 100 tall around its centre (350, 430).
        Assert.True(turned.GetPixel(350, 385).Green < 20);
        Assert.Equal(SKColors.White, turned.GetPixel(310, 430));
    }

    [Fact]
    public void PhotographsAreStoredAsTheFileTheyCameFromAndDrawingsWithoutLoss()
    {
        using var photo = ContentOperationTests.Photo(900, 700);
        var jpeg = PdfImages.Encode(photo, ImageFormat.Jpeg, 85);
        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0,
            [new PageImage { Id = 1, Pixels = ImagePixels.FromFile(jpeg), Bounds = new RectD(50, 50, 500, 400) }]);
        Assert.InRange(pdf.Length, jpeg.Length, jpeg.Length + 4000);

        // A drawing with hard edges comes back pixel for pixel.
        using var drawing = new SKBitmap(new SKImageInfo(80, 80, SKColorType.Bgra8888, SKAlphaType.Opaque));
        drawing.Erase(SKColors.White);
        for (var i = 0; i < 80; i += 2) drawing.SetPixel(i, 40, new SKColor(200, 10, 10));
        var stored = PdfPageObjects.Write(TestPdf.WithText(""), 0,
            [new PageImage { Id = 1, Pixels = ImagePixels.FromFile(PdfImages.Encode(drawing, ImageFormat.Png)), Bounds = new RectD(50, 50, 130, 130) }]);
        var read = Assert.IsType<PageImage>(Assert.Single(PdfPageObjects.Read(stored, 0)));
        Assert.Equal(new SKColor(200, 10, 10), read.Pixels.Bitmap.GetPixel(10, 40));
        Assert.Equal(SKColors.White, read.Pixels.Bitmap.GetPixel(11, 40));
    }

    [Fact]
    public void TransparentPicturesStayTransparentAndUpright()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(40, 40, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Transparent);
        for (var y = 0; y < 20; y++)
        for (var x = 0; x < 40; x++)
            bitmap.SetPixel(x, y, SKColors.Blue); // the top half is blue, the bottom half see-through
        var pixels = ImagePixels.FromFile(PdfImages.Encode(bitmap, ImageFormat.Png));
        Assert.True(pixels.HasAlpha);

        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0, [new PageImage { Id = 1, Pixels = pixels, Bounds = new RectD(100, 100, 300, 300) }]);

        using var rendered = TestPdf.Render(pdf, 0);
        Assert.True(rendered.GetPixel(200, 150).Blue > 240 && rendered.GetPixel(200, 150).Red < 20);
        Assert.Equal(SKColors.White, rendered.GetPixel(200, 250));

        var read = Assert.IsType<PageImage>(Assert.Single(PdfPageObjects.Read(pdf, 0)));
        Assert.True(read.Pixels.HasAlpha);
        Assert.Equal(255, read.Pixels.Bitmap.GetPixel(20, 5).Alpha);
        Assert.Equal(0, read.Pixels.Bitmap.GetPixel(20, 35).Alpha);
    }

    [Fact]
    public void PartOfAnImageCanBeCutOutSoThePageShowsThrough()
    {
        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0, [new PageImage { Id = 1, Pixels = Picture(), Bounds = new RectD(100, 200, 280, 320) }]);
        var image = Assert.IsType<PageImage>(Assert.Single(PdfPageObjects.Read(pdf, 0)));

        // The right half goes.
        var cut = PdfPageObjects.CutOut(image, new RectD(190, 150, 400, 400));
        Assert.NotNull(cut);
        Assert.NotSame(image.Pixels, cut.Pixels);
        var result = PdfPageObjects.Write(pdf, 0, [cut]);

        using var rendered = TestPdf.Render(result, 0);
        Assert.True(rendered.GetPixel(140, 260).Green < 20, "the left half is still there");
        Assert.Equal(SKColors.White, rendered.GetPixel(240, 260));

        // An area that misses the image leaves it as it is; one that covers it takes it away altogether.
        Assert.Same(image, PdfPageObjects.CutOut(image, new RectD(400, 400, 500, 500)));
        Assert.Null(PdfPageObjects.CutOut(image, new RectD(0, 0, 595, 842)));
    }

    [Fact]
    public void APatchHidesWhatIsUnderItAndCanRemoveTheTextThere()
    {
        var pdf = TestPdf.WithText("Erase me");
        var run = PdfTextEdit.GetTextRuns(pdf, 0)[0];
        var area = new RectD(run.Bounds.Left - 3, run.Bounds.Top - 3, run.Bounds.Right + 3, run.Bounds.Bottom + 3);

        var covered = PdfPageObjects.Write(pdf, 0, [new PagePatch { Id = 1, Bounds = area, Color = 0xFFFFFFFF }]);
        using (var rendered = TestPdf.Render(covered, 0))
            Assert.Equal(0, TestPdf.InkRatio(rendered));
        Assert.Equal("Erase me", TestPdf.Text(covered, 0)); // only hidden so far

        var erased = PdfPageObjects.Write(pdf, 0, [new PagePatch { Id = 1, Bounds = area, Color = 0xFFFFE0A0 }], [area]);
        Assert.Equal("", TestPdf.Text(erased, 0));
        var patch = Assert.IsType<PagePatch>(Assert.Single(PdfPageObjects.Read(erased, 0)));
        Assert.Equal(0xFFFFE0A0, patch.Color);
        AssertBox(area, patch.Bounds, 0.05);
        using var coloured = TestPdf.Render(erased, 0);
        Assert.Equal(new SKColor(0xFF, 0xE0, 0xA0), coloured.GetPixel((int)area.CenterX, (int)area.CenterY));
    }

    [Fact]
    public void ObjectsAreStackedInTheOrderGivenAndCanBeRestacked()
    {
        var red = new PagePatch { Id = 1, Bounds = new RectD(100, 100, 300, 300), Color = 0xFFFF0000 };
        var green = new PagePatch { Id = 2, Bounds = new RectD(200, 200, 400, 400), Color = 0xFF00FF00 };
        var pdf = PdfPageObjects.Write(TestPdf.WithText("Below everything"), 0, [red, green]);
        using (var rendered = TestPdf.Render(pdf, 0))
            Assert.Equal(SKColors.Lime, rendered.GetPixel(250, 250));

        var restacked = PdfPageObjects.Write(pdf, 0, [green, red]);
        using (var rendered = TestPdf.Render(restacked, 0))
            Assert.Equal(SKColors.Red, rendered.GetPixel(250, 250));
        Assert.Equal([2, 1], PdfPageObjects.Read(restacked, 0).Select(o => o.Id));
        Assert.Equal("Below everything", TestPdf.Text(restacked, 0));
    }

    [Fact]
    public void ANewObjectCanBeSlippedUnderTheOnesAlreadyThere()
    {
        var top = new PagePatch { Id = 1, Bounds = new RectD(200, 200, 400, 400), Color = 0xFF00FF00 };
        var pdf = PdfPageObjects.Write(TestPdf.WithText("Below everything"), 0, [top]);

        // Written in a later session, yet it has to end up underneath: above the page, below the green one.
        var under = new PagePatch { Id = 2, Bounds = new RectD(100, 100, 300, 300), Color = 0xFFFF0000 };
        var result = PdfPageObjects.Write(pdf, 0, [under, PdfPageObjects.Read(pdf, 0)[0]]);

        Assert.Equal([2, 1], PdfPageObjects.Read(result, 0).Select(o => o.Id));
        using var rendered = TestPdf.Render(result, 0);
        Assert.Equal(SKColors.Lime, rendered.GetPixel(250, 250));
        Assert.Equal(SKColors.Red, rendered.GetPixel(150, 150));
    }

    [Fact]
    public void RewordingABoxThatLiesUnderAnImageLeavesItUnderTheImage()
    {
        var note = Box(1, "Under the picture", 100, 200);
        var image = new PageImage { Id = 2, Pixels = Picture(), Bounds = new RectD(90, 190, 330, 230) };
        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0, [note, image]);

        var objects = PdfPageObjects.Read(pdf, 0);
        var reworded = PdfPageObjects.Write(pdf, 0, [(PageText)objects[0] with { Text = "Still under it" }, objects[1]]);

        Assert.Equal([1, 2], PdfPageObjects.Read(reworded, 0).Select(o => o.Id));
        Assert.Contains("Still under it", TestPdf.Text(reworded, 0));
        using var rendered = TestPdf.Render(reworded, 0);
        Assert.True(rendered.GetPixel(200, 210).Green < 20 && rendered.GetPixel(200, 210).Red > 240, "the image still covers the text");
    }

    [Fact]
    public void RestackingAPhotographDoesNotCostQualityOrSpace()
    {
        using var photo = ContentOperationTests.Photo(700, 500);
        var jpeg = PdfImages.Encode(photo, ImageFormat.Jpeg, 80);
        var image = new PageImage { Id = 1, Pixels = ImagePixels.FromFile(jpeg), Bounds = new RectD(50, 50, 400, 300) };
        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0, [image, Box(2, "On top at first", 60, 60)]);

        var objects = PdfPageObjects.Read(pdf, 0);
        var before = ((PageImage)objects[0]).Pixels.Bitmap.Bytes;
        var restacked = PdfPageObjects.Write(pdf, 0, [objects[1], objects[0]]);

        var read = PdfPageObjects.Read(restacked, 0);
        Assert.Equal([2, 1], read.Select(o => o.Id));
        Assert.Equal(before, ((PageImage)read[1]).Pixels.Bitmap.Bytes);
        Assert.InRange(restacked.Length, pdf.Length - 2000, pdf.Length + 2000);
    }

    [Fact]
    public void TakingObjectsAwayRestoresThePageAndCleansOutTheirPictures()
    {
        var original = TestPdf.WithText("Original content");
        using var photo = ContentOperationTests.Photo(600, 600);
        var with = PdfPageObjects.Write(original, 0,
        [
            new PageImage { Id = 1, Pixels = ImagePixels.FromFile(PdfImages.Encode(photo, ImageFormat.Jpeg)), Bounds = new RectD(50, 300, 350, 600) },
            Box(2, "A note that will be removed")
        ]);
        Assert.True(with.Length > original.Length + 20_000);

        var without = PdfPageObjects.Write(with, 0, []);

        Assert.Empty(PdfPageObjects.Read(without, 0));
        Assert.Equal(0, TestPdf.Difference(original, without), 0.0001);
        Assert.Equal("Original content", TestPdf.Text(without, 0));
        Assert.True(without.Length < original.Length + 15_000, $"{original.Length} -> {with.Length} -> {without.Length} bytes");
    }

    [Fact]
    public void ThePageCanBeDrawnWithoutTheObjectsOnIt()
    {
        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0, [new PagePatch { Id = 1, Bounds = new RectD(100, 100, 300, 300), Color = 0xFF000000 }]);
        using var view = new PdfView(pdf);
        using var all = view.Render(0, 1);
        using var bare = view.Render(0, 1, withoutEditable: true);
        Assert.Equal(SKColors.Black, all.GetPixel(200, 200));
        Assert.Equal(SKColors.White, bare.GetPixel(200, 200));
        using var again = view.Render(0, 1);
        Assert.Equal(SKColors.Black, again.GetPixel(200, 200)); // hiding was only for that one picture
    }

    [Fact]
    public void ObjectsFollowThePageWhenItIsTurnedOrScaled()
    {
        var box = Box(1, "Follows the page", 100, 200, 200, 16);
        var patch = new PagePatch { Id = 2, Bounds = new RectD(300, 600, 400, 650), Color = 0xFF0000FF };
        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0, [box, patch]);

        // Half size: everything is half as large and half as far from the corner.
        var scaled = PdfPageObjects.Read(PdfPages.Scale(pdf, null, ScaleMode.Factor, 0.5), 0);
        var text = Assert.IsType<PageText>(scaled[0]);
        Assert.Equal(8, text.FontSize, 0.02);
        AssertBox(new RectD(50, 100, 150, 100 + box.Bounds.Height / 2), text.Bounds, 0.1);
        AssertBox(new RectD(150, 300, 200, 325), scaled[1].Bounds, 0.1);

        // A quarter turn clockwise: what was at the top-left is now at the top-right, lying on its side.
        var turned = PdfPageObjects.Read(PdfPages.Rotate(pdf, null, 90), 0);
        var sideways = Assert.IsType<PageText>(turned[0]);
        Assert.Equal(-90, sideways.Rotation, 0.01);
        Assert.Equal(16, sideways.FontSize, 0.02);
        Assert.Equal(842 - 200 - box.Bounds.Height / 2, sideways.Bounds.CenterX, 0.1);
        Assert.Equal(100 + box.Bounds.Width / 2, sideways.Bounds.CenterY, 0.1);

        // Changed there and written back, it still lands where it is shown.
        var rotated = PdfPages.Rotate(pdf, null, 90);
        var upright = PdfPageObjects.Fit(sideways with { Rotation = 0, Bounds = new RectD(100, 100, 300, 120) });
        var written = PdfPageObjects.Read(PdfPageObjects.Write(rotated, 0, [upright, turned[1]]), 0);
        AssertBox(upright.Bounds, written[0].Bounds, 0.1);
        Assert.Equal(0, written[0].Rotation, 0.01);
    }

    [Fact]
    public void ThePreviewOfABoxShowsTheSameLettersInTheSamePlacesAsTheDocument()
    {
        var box = PdfPageObjects.Fit(new PageText
        {
            Id = 1, Text = "Preview and document must agree, line after line, word for word.", FontSize = 20,
            Bounds = new RectD(60, 100, 360, 110), Align = TextAlign.Right
        });
        var pdf = PdfPageObjects.Write(TestPdf.WithText(""), 0, [box]);
        const double scale = 2;

        using var page = TestPdf.Render(pdf, 0, scale);
        var picture = PdfPageObjects.RenderText(box, scale);

        double DifferenceWhenShiftedBy(int dx, int dy)
        {
            using var preview = new SKBitmap(page.Info);
            using var canvas = new SKCanvas(preview);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(picture.Bitmap, (float)((box.Bounds.Left - picture.Padding) * scale) + dx, (float)((box.Bounds.Top - picture.Padding) * scale) + dy);
            return PdfModifier.Core.Imaging.ImageAnalysis.DifferenceRatio(page, preview, 96);
        }

        var ink = TestPdf.InkRatio(page);
        Assert.True(ink > 0.004);
        // Two rasterisers never agree on every edge pixel, so the pictures are compared with the preview nudged around:
        // it has to fit best exactly where it is, and far better than one pixel (half a point) off.
        var inPlace = DifferenceWhenShiftedBy(0, 0);
        Assert.True(inPlace < ink * 0.25, $"{inPlace:P3} of the page differs, {ink:P3} is ink");
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            Assert.True(DifferenceWhenShiftedBy(dx, dy) > inPlace * 1.5, $"fits no worse when moved by {dx},{dy}");
    }

    [Fact]
    public void TextNeedsAsMuchWidthAsItsLongestLine()
    {
        var box = new PageText { Id = 1, Text = "short\na considerably longer line", FontSize = 12, Bounds = new RectD(0, 0, 10, 10) };
        var width = PdfPageObjects.NaturalWidth(box);
        Assert.Equal(FontResolver.Measure("a considerably longer line", null, false, false, 12), width, 0.001);
        Assert.Equal(2, TextLayout.Create(box with { Bounds = new RectD(0, 0, width + 0.5, 10) }).Lines.Count);
    }

    [Fact]
    public void ObjectsNeedTheirOwnIds()
    {
        var pdf = TestPdf.WithText("");
        Assert.Throws<ArgumentException>(() => PdfPageObjects.Write(pdf, 0, [Box(0, "no id")]));
        Assert.Throws<ArgumentException>(() => PdfPageObjects.Write(pdf, 0, [Box(4, "one"), Box(4, "two")]));
    }

    [Fact]
    public void InstalledFontsAreListedForChoosing()
    {
        var families = PdfPageObjects.FontFamilies();
        Assert.NotEmpty(families);
        Assert.Equal(families.Count, families.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (OperatingSystem.IsWindows()) Assert.Contains("Arial", families);
    }

    private static void AssertBox(RectD expected, RectD actual, double tolerance)
    {
        Assert.Equal(expected.Left, actual.Left, tolerance);
        Assert.Equal(expected.Top, actual.Top, tolerance);
        Assert.Equal(expected.Right, actual.Right, tolerance);
        Assert.Equal(expected.Bottom, actual.Bottom, tolerance);
    }
}
