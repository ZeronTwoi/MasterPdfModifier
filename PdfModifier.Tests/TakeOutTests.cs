using Avalonia.Headless.XUnit;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using PdfModifier.Localization;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using SkiaSharp;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>
/// Taking things out of a document: the pictures it is made with, and the text or a picture of a page or a part of
/// one. Tests that read scans need the recognition data and pass vacuously without it (see <see cref="OcrTests"/>).
/// </summary>
public class TakeOutTests
{
    private static bool CanRead => PdfOcr.EngineStatus().Available && TessData.Missing(["en"]).Count == 0;

    /// <summary>A picture of one colour with a darker square in it, so that it can be told from any other.</summary>
    private static SKBitmap Photo(int width, int height, SKColor colour, bool seeThrough = false)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, seeThrough ? SKAlphaType.Unpremul : SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(seeThrough ? colour.WithAlpha(120) : colour);
        using var paint = new SKPaint { Color = new SKColor(30, 30, 30) };
        canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, paint);
        return bitmap;
    }

    /// <summary>A page with a photograph, a small ornament and a picture one can see through, each in a place of its own.</summary>
    private static byte[] PageWithPictures()
    {
        using var photo = Photo(400, 300, new SKColor(200, 120, 60));
        using var ornament = Photo(40, 40, new SKColor(60, 120, 200));
        using var glass = Photo(200, 200, new SKColor(60, 200, 120), seeThrough: true);
        return TestPdf.Compose((page, fonts) =>
        {
            PageDraw.DrawText(page, fonts, "A page with pictures", new TextStyle(18), 72, 60);
            PageDraw.DrawImage(page, photo, new RectD(100, 100, 300, 250));
            PageDraw.DrawImage(page, ornament, new RectD(400, 100, 420, 120));
            PageDraw.DrawImage(page, glass, new RectD(100, 400, 250, 550));
        });
    }

    [Fact]
    public void ThePicturesOfAPageAreListedWithTheirSizeTheirPlaceAndHowTheyAreStored()
    {
        var pictures = PdfPictures.In(PageWithPictures(), null);

        Assert.Equal(3, pictures.Count);
        Assert.Equal([(400, 300), (40, 40), (200, 200)], pictures.Select(picture => (picture.Width, picture.Height)));
        Assert.Equal([0, 1, 2], pictures.Select(picture => picture.Index));
        Assert.All(pictures, picture => Assert.Equal(0, picture.Page));
        // Where it shows on the page, not how many pixels it has.
        Assert.Equal(100, pictures[0].Bounds.Left, 0.5);
        Assert.Equal(100, pictures[0].Bounds.Top, 0.5);
        Assert.Equal(300, pictures[0].Bounds.Right, 0.5);
        Assert.Equal(250, pictures[0].Bounds.Bottom, 0.5);
        // A photograph is stored as a JPEG; what one can see through cannot be.
        Assert.True(pictures[0].Jpeg);
        Assert.False(pictures[2].Jpeg);
        Assert.Equal(3, pictures.Select(picture => picture.Mark).Distinct().Count());

        // Bullets, rules and ornaments are left out by their size.
        Assert.Equal([(400, 300), (200, 200)], PdfPictures.In(PageWithPictures(), null, smallest: 100).Select(picture => (picture.Width, picture.Height)));
    }

    [Fact]
    public void APictureIsTakenOutAsItIsStoredOrAsTheKindAskedFor()
    {
        var pdf = PageWithPictures();
        var files = new List<(PagePicture Picture, byte[] Data, string Ending)>();

        var written = PdfPictures.Extract(pdf, null, new PictureOptions(Smallest: 100), (picture, data, ending) => files.Add((picture, data, ending)));

        Assert.Equal(2, written);
        // The JPEG that lies in the file, byte for byte; the other one without loss, as a PNG.
        Assert.Equal([".jpg", ".png"], files.Select(file => file.Ending));
        Assert.Equal([0xFF, 0xD8], files[0].Data.Take(2).Select(b => (int)b));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], files[1].Data.Take(4).Select(b => (int)b));
        using (var photo = PdfImages.Decode(files[0].Data))
        {
            Assert.Equal((400, 300), (photo.Width, photo.Height));
            var corner = photo.GetPixel(10, 10);
            Assert.True(corner.Red > 170 && corner.Blue < 100, $"the photograph has its colour ({corner})");
            Assert.True(photo.GetPixel(200, 150).Red < 70, "and its dark square");
        }

        using (var glass = PdfImages.Decode(files[1].Data)) Assert.Equal((200, 200), (glass.Width, glass.Height));

        // Asked for as one kind, all are of that kind.
        var kinds = new List<string>();
        PdfPictures.Extract(pdf, null, new PictureOptions(Format: PictureFormat.Png), (_, data, ending) => kinds.Add(ending + data[1]));
        Assert.Equal([".png80", ".png80", ".png80"], kinds);
        kinds.Clear();
        PdfPictures.Extract(pdf, null, new PictureOptions(Format: PictureFormat.Jpeg, JpegQuality: 60), (_, data, ending) => kinds.Add(ending + data[1]));
        Assert.Equal([".jpg216", ".jpg216", ".jpg216"], kinds);
    }

    [Fact]
    public void APictureSeveralPagesUseIsTakenOutOnceUnlessEveryUseIsWanted()
    {
        // A letterhead on three pages, and on the second a photograph of its own.
        using var letterhead = Photo(300, 80, new SKColor(40, 90, 160));
        using var photo = Photo(400, 300, new SKColor(200, 120, 60));
        byte[] Sheet(bool withPhoto) => TestPdf.Compose((page, _) =>
        {
            PageDraw.DrawImage(page, letterhead, new RectD(50, 30, 350, 110));
            if (withPhoto) PageDraw.DrawImage(page, photo, new RectD(100, 200, 500, 500));
        });
        var pdf = PdfPages.Merge([Sheet(false), Sheet(true), Sheet(false)]);

        var once = new List<PagePicture>();
        PdfPictures.Extract(pdf, null, new PictureOptions(), (picture, _, _) => once.Add(picture));
        var every = new List<PagePicture>();
        PdfPictures.Extract(pdf, null, new PictureOptions(Once: false), (picture, _, _) => every.Add(picture));

        Assert.Equal([(0, 300), (1, 400)], once.Select(picture => (picture.Page, picture.Width)));
        Assert.Equal([(0, 300), (1, 300), (1, 400), (2, 300)], every.Select(picture => (picture.Page, picture.Width)));
        // Only the pages chosen are looked at.
        Assert.Equal([(2, 300)], PdfPictures.In(pdf, [2]).Select(picture => (picture.Page, picture.Width)));
    }

    [Fact]
    public void PicturesInsideNestedGraphicsAreFoundWhereTheyShow()
    {
        // The way many programs write their pages: everything wrapped in one graphic.
        var pdf = TestPdf.Nested(PageWithPictures());

        var pictures = PdfPictures.In(pdf, null, smallest: 100);

        Assert.Equal([(400, 300), (200, 200)], pictures.Select(picture => (picture.Width, picture.Height)));
        Assert.Equal(100, pictures[0].Bounds.Left, 1);
        Assert.Equal(250, pictures[0].Bounds.Bottom, 1);
        using var pixels = PdfPictures.Pixels(pdf, pictures[0]);
        Assert.NotNull(pixels);
        Assert.Equal((400, 300), (pixels.Width, pixels.Height));
        Assert.Equal(".jpg", PdfPictures.AsFile(pdf, pictures[0], new PictureOptions())?.Ending);
    }

    [Fact]
    public void OfAScannedPageThePictureIsTheScanItself()
    {
        using var scan = TestPdf.FakeScan(1240, 1754);
        var pdf = TestPdf.FromImage(scan, 150);

        var picture = Assert.Single(PdfPictures.In(pdf, null, smallest: 50));

        // Every pixel the scanner made, not the few a screen shows of them.
        Assert.Equal((1240, 1754), (picture.Width, picture.Height));
        Assert.Equal(0, picture.Bounds.Left, 0.5);
        Assert.Equal(1240 * 72 / 150.0, picture.Bounds.Right, 0.5);
    }

    [AvaloniaFact]
    public async Task ThePicturesToolMarksCountsAndSavesAndHandsOutOneByItself()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PdfModifier.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var (window, vm) = Start();
            SKBitmap? copied = null;
            vm.CopyPicture = picture =>
            {
                copied = picture.Copy();
                return Task.CompletedTask;
            };
            vm.Session.Open(PdfPages.Merge([PageWithPictures(), TestPdf.WithText("No picture on this page")]), Path.Combine(folder, "brochure.pdf"));
            var tool = Tool<PicturesTool>(vm);
            var onPage = Field<PieceListField>(tool, "PicturesOnPage");
            var found = tool.Fields.OfType<InfoField>().Single();
            await Settle(() => onPage.Items.Count == 2 && vm.Overlays.Count == 2 && vm.ApplySummary.Length > 0);

            // The ornament is left out as the tool starts; the two pictures are marked on the page and shown small.
            Assert.Equal(2, onPage.Items.Count);
            Assert.All(onPage.Items, item => Assert.NotNull(item.Picture));
            Assert.Equal(["400 × 300", "200 × 200"], vm.Overlays.Select(overlay => overlay.Text));
            Assert.Equal(string.Format(Strings.English["PicturesFound"], 2, 2), found.Text);
            Assert.Equal(string.Format(Strings.English["SummaryPictures"], 2, 1), vm.ApplySummary);
            Assert.False(Field<ActionField>(tool, "PicturesSaveOne").IsVisible);
            Capture(window, "64-pictures");

            // With no size left out, the ornament counts as well.
            Field<SliderField>(tool, "PicturesSmallest").Set(0);
            await Settle(() => onPage.Items.Count == 3 && vm.ApplySummary.Contains('3'));
            Assert.Equal(string.Format(Strings.English["SummaryPictures"], 3, 1), vm.ApplySummary);

            // One of them picked, on the page or among the small ones, can be copied by itself at its full size.
            await vm.OverlayClicked(vm.Overlays[0]);
            Assert.True(Field<ActionField>(tool, "PicturesCopyOne").IsVisible);
            Assert.Equal(string.Format(Strings.English["PicturesPicked"], 400, 300), vm.StatusText);
            Press(tool, "PicturesCopyOne");
            await Settle(() => copied != null && !vm.IsBusy);
            Assert.NotNull(copied);
            Assert.Equal((400, 300), (copied.Width, copied.Height));
            Assert.Equal(string.Format(Strings.English["CopyPictureCopied"], 400, 300), vm.StatusText);
            copied.Dispose();

            // Saved, they are named after the document, their page and their place on it.
            var written = 0;
            await vm.RunBusy(async context => written = await PicturesTool.SaveTo(context, context.Pdf, null, new PictureOptions(Smallest: 100), folder));
            Assert.Equal(2, written);
            Assert.Equal(["brochure.pdf", "brochure_p001_1.jpg", "brochure_p001_2.png"],
                Directory.GetFiles(folder).Select(Path.GetFileName).Append("brochure.pdf").Distinct().Order(StringComparer.Ordinal));

            // A page without pictures says so, and has nothing marked.
            vm.GoToPage(1);
            await Settle(() => vm.Overlays.Count == 0 && found.Text.StartsWith(string.Format(Strings.English["PicturesFound"], 0, 3)[..16]));
            Assert.Empty(vm.Overlays);
            Assert.False(onPage.IsVisible);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public async Task TheCopyToolTakesTheTextOfThePageOrOfTheFrameDrawnOnIt()
    {
        var (window, vm) = Start();
        var texts = new List<string>();
        SKBitmap? copied = null;
        vm.CopyText = text =>
        {
            texts.Add(text);
            return Task.CompletedTask;
        };
        vm.CopyPicture = picture =>
        {
            copied = picture.Copy();
            return Task.CompletedTask;
        };
        vm.Session.Open(TestPdf.WithText("Invoice 2026-104 for Mr Smith", "Second page"), null);
        var tool = Tool<CopyTool>(vm);
        var box = Field<TextField>(tool, "CopyText");
        await Settle(() => box.Value.Length > 0 && vm.Overlays.Count == 1);

        // Without a frame the whole page is taken, and the line it is from is marked.
        Assert.Equal("Invoice 2026-104 for Mr Smith", box.Value);
        Assert.Single(vm.Overlays);
        Assert.False(Field<ActionField>(tool, "CopyWholePage").IsVisible);
        Press(tool, "CopyDoText");
        await Settle(() => texts.Count == 1);
        Assert.Equal(["Invoice 2026-104 for Mr Smith"], texts);
        Assert.Equal(string.Format(Strings.English["CopyTextCopied"], 29), vm.StatusText);

        // A frame around the last two words takes just those, and stays on the page.
        var words = vm.Session.View!.GetLines(0)[0].Words;
        var frame = new RectD(words[3].Bounds.Left - 3, words[3].Bounds.Top - 6, words[4].Bounds.Right + 3, words[4].Bounds.Bottom + 6);
        await vm.RegionSelected(frame);
        await Settle(() => box.Value == "Mr Smith");
        Assert.Equal("Mr Smith", box.Value);
        Assert.Equal(frame, vm.Region);
        Assert.True(Field<ActionField>(tool, "CopyWholePage").IsVisible);
        Capture(window, "65-copy");

        // What stands in the box can be put right before it is copied.
        box.Value = "Mr. Smith";
        Press(tool, "CopyDoText");
        await Settle(() => texts.Count == 2);
        Assert.Equal("Mr. Smith", texts[1]);

        // As a picture the frame comes out at the resolution asked for.
        Field<SliderField>(tool, "ImageDpi").Set(144);
        Press(tool, "CopyDoPicture");
        await Settle(() => copied != null && !vm.IsBusy);
        Assert.NotNull(copied);
        Assert.Equal(frame.Width * 2, copied.Width, 1.5);
        Assert.Equal(frame.Height * 2, copied.Height, 1.5);
        Assert.True(TestPdf.InkRatio(copied) > 0.05, "the picture shows the words");
        copied.Dispose();

        // The frame goes with the button, and with the page.
        Press(tool, "CopyWholePage");
        await Settle(() => box.Value.StartsWith("Invoice"));
        Assert.Null(vm.Region);
        await vm.RegionSelected(frame);
        vm.GoToPage(1);
        await Settle(() => box.Value == "Second page");
        Assert.Equal("Second page", box.Value);
        Assert.Null(vm.Region);

        // The text of whole pages, page after page.
        (string Text, int Unread, string? WhyNot) all = default;
        await vm.RunBusy(async context => all = await tool.TextOfPages(context));
        Assert.Equal(("Invoice 2026-104 for Mr Smith\n\nSecond page", 0), (all.Text, all.Unread));
        Assert.Equal(string.Format(Strings.English["SummaryCopyText"], 2, "1-2"), vm.ApplySummary);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OnAScanTheTextIsReadOffThePictureOrSaidNotToHaveBeen()
    {
        var (window, vm) = Start();
        using var scan = TestPdf.FakeScan(1240, 1754);
        vm.Session.Open(TestPdf.FromImage(scan, 150), null);
        var tool = Tool<CopyTool>(vm);
        var box = Field<TextField>(tool, "CopyText");
        var note = tool.Fields.OfType<InfoField>().Skip(1).First();
        await Settle(() => box.Value.Length > 0 || note.HasText, 60);

        if (!CanRead)
        {
            // Nothing to read with here: the tool says why the box is empty, and offers what would fill it.
            Assert.Equal("", box.Value);
            Assert.True(note.Warns);
            Assert.StartsWith(Strings.English["CopyScanUnread"][..30], note.Text);
            window.Close();
            return;
        }

        Assert.Contains("The quick brown fox jumps over the lazy dog", box.Value);
        Assert.True(box.Value.Split('\n').Length >= 15, "line by line");
        Assert.All(vm.Overlays, overlay => Assert.True(overlay.IsScan));
        Capture(window, "66-copy-scan");

        // A frame around the first line only gives that line.
        var first = vm.Overlays.OrderBy(overlay => overlay.Bounds.Top).First().Bounds;
        await vm.RegionSelected(new RectD(first.Left - 4, first.Top - 4, first.Right + 4, first.Bottom + 4));
        await Settle(() => !box.Value.Contains('\n'));
        Assert.StartsWith("The quick brown fox", box.Value);
        Assert.DoesNotContain("\n", box.Value);

        // With reading switched off the scan gives nothing, and the tool says what would change that.
        Field<BoolField>(tool, "CopyScans").Value = false;
        await Settle(() => box.Value.Length == 0);
        Assert.Equal(Strings.English["CopyScanOff"], note.Text);
        window.Close();
    }
}
