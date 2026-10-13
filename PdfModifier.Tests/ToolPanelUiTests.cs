using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfModifier.Controls;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Localization;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using PdfModifier.Views;
using SkiaSharp;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>
/// The tools as they are used from their panels: the limit and the doubts of the page cutter, a folder to compress,
/// the recipes for unclear pages, careful reading with marked passages, and the list of images used before.
/// </summary>
public class ToolPanelUiTests
{
    private static readonly SKColor Table = new(46, 44, 48);

    private static PageCanvas Canvas(MainWindow window) => window.FindControl<PageCanvas>("PageView")!;

    private static Point At(MainWindow window, MainViewModel vm, double x, double y) =>
        Canvas(window).TranslatePoint(new Point(x * vm.PointScale, y * vm.PointScale), window)!.Value;

    private static void Drag(MainWindow window, MainViewModel vm, double x0, double y0, double x1, double y1)
    {
        var from = At(window, vm, x0, y0);
        var by = new Point((x1 - x0) * vm.PointScale, (y1 - y0) * vm.PointScale);
        window.MouseDown(from, MouseButton.Left);
        for (var step = 1; step <= 4; step++) window.MouseMove(new Point(from.X + by.X * step / 4, from.Y + by.Y * step / 4));
        window.MouseUp(new Point(from.X + by.X, from.Y + by.Y), MouseButton.Left);
    }

    private static void FitPage(MainWindow window, MainViewModel vm)
    {
        var viewer = window.FindControl<ScrollViewer>("Viewer")!;
        vm.ZoomToFit(viewer.Bounds.Width, viewer.Bounds.Height);
    }

    private static bool OcrReady => PdfOcr.EngineStatus().Available && TessData.Missing(["en"]).Count == 0;

    private static List<SheetArea> Frames(MainViewModel vm) => vm.Objects.Select(o => o.Model).OfType<SheetArea>().ToList();

    private static string Note(Tool tool) => tool.Fields.OfType<InfoField>().First().Text;

    private static byte[] ParagraphPage(float gap)
    {
        using var page = SheetTests.Paragraphs(gap: gap);
        return TestPdf.FromImage(page, 150);
    }

    private static byte[] TwoOnATable()
    {
        using var sheet = SheetTests.Sheet(2000, 1400, Table,
            (new SKRect(120, 130, 940, 1280), null), (new SKRect(1060, 110, 1880, 1260), null));
        return TestPdf.FromImage(sheet, 150);
    }

    // ---- the page cutter: its limit and its doubts ------------------------------------------------------------------

    [AvaloniaFact]
    public async Task APageOfParagraphsIsLeftWholeUntilTheLimitIsRaised()
    {
        var (window, vm) = Start();
        vm.Session.Open(ParagraphPage(110), null);
        var tool = Tool<SheetTool>(vm);
        await Settle(() => vm.Objects.Count == 1 && vm.PageBitmap != null);
        FitPage(window, vm);
        await Settle();

        // The gaps between its paragraphs do not make it two pages.
        var frame = Assert.Single(Frames(vm));
        Assert.False(frame.Doubtful);
        Assert.Equal(Loc.Get("SheetFoundOne"), Note(tool));
        Assert.Equal(Loc.Get("SummarySheetWhole"), vm.ApplySummary);
        Assert.Null(vm.Pages[0].Badge);
        var limit = Field<SliderField>(tool, "SheetSensitivity");
        Assert.Equal(50, limit.Number);
        Assert.True(limit.IsVisible);

        // Someone who knows the sheet holds two pages raises the limit; where that only just tips the scale the
        // frames are red, and so is the mark on the page in the list.
        var first = 0.0;
        for (var value = 55; value <= 100 && first == 0; value += 5)
        {
            limit.Set(value);
            await Settle(() => vm.Objects.Count > 0 && !Note(tool).Equals(Loc.Get("SheetLooking")));
            if (vm.Objects.Count == 2) first = value;
        }

        Assert.InRange(first, 60, 95);
        Assert.All(Frames(vm), area => Assert.True(area.Doubtful));
        Assert.Equal(Loc.Format("SheetDoubtSplit", 2), Note(tool));
        Assert.Equal(new PageBadge("2?", true, Loc.Get("SheetBadgeDoubt")), vm.Pages[0].Badge);
        Assert.True(Field<ActionField>(tool, "SheetConfirm").IsVisible);
        await Settle();
        Capture(window, "31-sheet-doubtful");

        // Looked at and found right: nothing is red any more.
        Press(tool, "SheetConfirm");
        Assert.All(Frames(vm), area => Assert.False(area.Doubtful));
        Assert.Equal(new PageBadge("2", false, Loc.Format("SheetBadgeCut", 2)), vm.Pages[0].Badge);
        Assert.False(Field<ActionField>(tool, "SheetConfirm").IsVisible);

        // Or the sheet is left out altogether.
        Press(tool, "SheetLeave");
        Assert.Empty(vm.Objects);
        Assert.Equal(Loc.Get("SheetSkipped"), Note(tool));
        Assert.Null(vm.Pages[0].Badge);
        Assert.Equal(Loc.Get("SheetSkipped"), vm.ApplySummary);
        Press(tool, "SheetTake");
        Assert.Equal(2, vm.Objects.Count);

        // With another way of dividing chosen there is no limit to set.
        Field<ChoiceField>(tool, "SheetLayout").Select("two");
        await Settle(() => vm.Objects.Count == 2 && !Frames(vm)[0].Doubtful);
        Assert.False(limit.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SheetsThatAreNotCertainAreMarkedRedInThePageList()
    {
        // A page of paragraphs, a sheet with two pages, and the page of paragraphs again.
        var (window, vm) = Start();
        vm.Session.Open(PdfPages.Merge([ParagraphPage(110), TwoOnATable(), ParagraphPage(110)]), null);
        var tool = Tool<SheetTool>(vm);
        Field<PagesField>(tool, "Pages").Scope = Field<PagesField>(tool, "Pages").Scopes[0];
        // Until the page on view has its frame and the others have been looked at in the background.
        await Settle(() => vm.Pages[1].Badge != null && vm.Objects.Count == 1 && vm.ApplySummary.Length > 0);

        // At the usual limit only the real sheet is going to be cut, and that is certain.
        Assert.Null(vm.Pages[0].Badge);
        Assert.Equal(new PageBadge("2", false, Loc.Format("SheetBadgeCut", 2)), vm.Pages[1].Badge);
        Assert.Null(vm.Pages[2].Badge);
        Assert.Equal(Loc.Format("SummarySheetsSkip", 3), vm.ApplySummary);

        // Raised to where the paragraphs just count as a division, those two pages are marked as doubtful.
        var limit = Field<SliderField>(tool, "SheetSensitivity");
        // (On the way there they are first marked with a question mark alone: left whole, but nearly not.)
        for (var value = 55; value <= 100 && vm.Pages[0].Badge?.Text != "2?"; value += 5)
        {
            limit.Set(value);
            await Settle(() => vm.Pages[1].Badge != null && vm.Objects.Count > 0, 4);
        }

        await Settle(() => vm.Pages[2].Badge != null);
        Assert.Equal("2?", vm.Pages[0].Badge?.Text);
        Assert.True(vm.Pages[2].Badge is { Warns: true });
        Assert.Equal(new PageBadge("2", false, Loc.Format("SheetBadgeCut", 2)), vm.Pages[1].Badge);
        Assert.Equal(Loc.Format("SummarySheets", 3, 2) + " " + Loc.Format("SummarySheetsDoubt", 2), vm.ApplySummary);
        Capture(window, "32-sheets-marked");

        // Taken out of the selection, a doubtful page is no longer part of what Apply does.
        Field<PagesField>(tool, "Pages").Scope = Field<PagesField>(tool, "Pages").Scopes[1];
        vm.SetSelection([1, 2], 1);
        await Settle(() => vm.Pages[0].Badge == null && vm.Objects.Count == 2);
        Assert.Null(vm.Pages[0].Badge);
        Assert.Equal(Loc.Format("SummarySheets", 2, 2) + " " + Loc.Format("SummarySheetsDoubt", 1), vm.ApplySummary);

        // What was cut by a close call is selected afterwards, to be looked at first.
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && vm.Pages.Count == 5);
        Assert.Equal(5, vm.Pages.Count);
        Assert.Equal([3, 4], vm.SelectedPageIndexes);
        Assert.Equal(Loc.Format("StatusSheetsCutDoubt", 2, 4, 1), vm.StatusText);
        Assert.All(vm.Pages, page => Assert.Null(page.Badge));
        window.Close();
    }

    // ---- compressing a folder ------------------------------------------------------------------------------------------

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PdfModifier.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [AvaloniaFact]
    public async Task AFolderIsMeasuredAndCompressedWithTheSizesListedFileByFile()
    {
        var folder = TempFolder();
        using (var scan = TestPdf.FakeScan(1654, 2339))
        {
            // Scans stored without loss: the kind of file that has most to gain.
            File.WriteAllBytes(Path.Combine(folder, "scan.pdf"), PdfPages.Merge([TestPdf.FromImage(scan, 200), TestPdf.FromImage(scan, 200)]));
            Directory.CreateDirectory(Path.Combine(folder, "older"));
            File.WriteAllBytes(Path.Combine(folder, "older", "report.pdf"), TestPdf.FromImage(scan, 200));
        }

        File.WriteAllBytes(Path.Combine(folder, "letter.pdf"), TestPdf.WithText("A letter of plain text"));
        File.WriteAllBytes(Path.Combine(folder, "locked.pdf"), PdfSecurity.Protect(TestPdf.WithText("Secret"), "pw"));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a PDF");
        var scanSize = new FileInfo(Path.Combine(folder, "scan.pdf")).Length;

        var (window, vm) = Start();
        var tool = Tool<CompressTool>(vm);
        await Settle();
        Assert.True(tool.NeedsDocument);
        Field<ModeField>(tool, "CompressTarget").Select("folder");
        Assert.False(tool.NeedsDocument);
        Assert.False(vm.CanPreview);
        Assert.Equal(Loc.Get("CompressFolderRun"), tool.ApplyLabel);
        var folderField = Field<FolderField>(tool, "CompressFolder");
        Assert.True(folderField.IsVisible);
        folderField.Path = folder;
        var report = tool.Fields.OfType<InfoField>().Last();

        // Measuring says what would be gained and writes nothing.
        Press(tool, "CompressMeasure");
        await Settle(() => !vm.IsBusy && report.Text.Length > 0, 60);
        Assert.StartsWith(Loc.Get("CompressFolderMeasured").Split(':')[0].Replace("{0}", "3"), report.Text);
        Assert.Contains("scan.pdf: ", report.Text);
        Assert.Contains(" → ", report.Text);
        Assert.Contains("letter.pdf: ", report.Text);
        Assert.Contains(Loc.Get("CompressFileNoGain"), report.Text);
        Assert.Contains("locked.pdf: " + Loc.Get("CompressFileLocked"), report.Text);
        Assert.DoesNotContain("report.pdf", report.Text);
        Assert.False(Directory.Exists(Path.Combine(folder, CompressTool.OutputFolderName)));
        Capture(window, "33-compress-folder");

        // Run with the folders inside it included: the compressed files go into a folder of their own, complete.
        Field<BoolField>(tool, "CompressSubfolders").Value = true;
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy, 60);
        var written = Path.Combine(folder, CompressTool.OutputFolderName);
        Assert.Contains(Loc.Format("CompressFolderWhere", written), report.Text);
        Assert.Contains(Path.Combine("older", "report.pdf") + ": ", report.Text);
        Assert.True(new FileInfo(Path.Combine(written, "scan.pdf")).Length < scanSize * 0.7);
        Assert.Equal(2, TestPdf.PageCount(File.ReadAllBytes(Path.Combine(written, "scan.pdf"))));
        Assert.True(File.Exists(Path.Combine(written, "older", "report.pdf")));
        Assert.Equal("A letter of plain text", TestPdf.Text(File.ReadAllBytes(Path.Combine(written, "letter.pdf")), 0));
        Assert.False(File.Exists(Path.Combine(written, "locked.pdf")));
        // The originals are as they were.
        Assert.Equal(scanSize, new FileInfo(Path.Combine(folder, "scan.pdf")).Length);

        // A second run does not take what the first one wrote for more files to compress.
        Press(tool, "CompressMeasure");
        await Settle(() => !vm.IsBusy, 60);
        Assert.DoesNotContain(CompressTool.OutputFolderName + Path.DirectorySeparatorChar, report.Text);
        window.Close();
        Directory.Delete(folder, true);
    }

    // ---- unclear pages ----------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task ARecipeSetsTheStepsForWhatIsWrongWithAPage()
    {
        // Without grain, so that how crisp the print is can be measured by how steeply its edges rise.
        using var poor = TestPdf.PoorScan(noise: 0);
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.FromImage(poor, 150), null);
        var tool = Tool<EnhanceTool>(vm);
        await Settle(() => vm.IsPreview);
        FitPage(window, vm);

        var recipe = Field<ChoiceField>(tool, "EnhanceRecipe");
        Assert.Equal("standard", recipe.Key);
        Assert.Equal(0, tool.Options().Deblur);

        recipe.Select("blurry");
        var options = tool.Options();
        Assert.Equal((0.45, 0.1, 0.2, false, true), (options.Deblur, options.WhitenStrength, options.Thicken, options.Sharpen, options.Upscale));
        Assert.Equal(45, Field<SliderField>(tool, "EnhanceDeblur").Number);
        Assert.Equal("blurry", recipe.Key);
        await Settle(() => vm.IsPreview && vm.PageBitmap != null);
        await Settle();
        Assert.True(vm.IsPreview);
        Capture(window, "34-clarity-blurry");

        recipe.Select("faint");
        Assert.True(tool.Options() is { EvenLight: true, Thicken: 0.6, Deblur: 0 });
        recipe.Select("photo");
        Assert.True(tool.Options() is { EvenLight: true, Denoise: true });

        // A step set by hand makes the settings one's own; what the recipe had set stays as the starting point.
        Field<SliderField>(tool, "EnhanceDeblur").Set(70);
        Assert.Equal("custom", recipe.Key);
        Assert.True(tool.Options() is { Deblur: 0.7, EvenLight: true });

        // The blurry page really is crisper after Apply.
        recipe.Select("blurry");
        using var before = TestPdf.Render(vm.Session.Bytes!, 0, 2);
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && vm.CanUndo);
        using var after = TestPdf.Render(vm.Session.Bytes!, 0, 2);
        Assert.True(Steepness(after) > Steepness(before) * 1.5, $"{Steepness(before):0.00} before, {Steepness(after):0.00} after");
        window.Close();
    }

    private static double Steepness(SKBitmap bitmap)
    {
        long sum = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        for (var x = 1; x < bitmap.Width; x++)
            sum += Math.Abs(bitmap.GetPixel(x, y).Red - bitmap.GetPixel(x - 1, y).Red);
        return (double)sum / (bitmap.Width * bitmap.Height / 2.0);
    }

    // ---- reading scans with care ---------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task APassageOfAPoorScanIsMarkedAndReadAgainInTheEditor()
    {
        using var poor = TestPdf.PoorScan();
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.FromImage(poor, 150), null);
        var edit = Tool<EditTool>(vm);
        await Settle(() => vm.PageBitmap != null);
        FitPage(window, vm);
        var careful = Field<BoolField>(edit, "OcrCareful");
        var readArea = Field<ActionField>(edit, "EditReadArea");
        Assert.True(readArea.IsVisible);
        Assert.False(careful.Value);
        if (!OcrReady)
        {
            window.Close();
            return;
        }

        string Status() => edit.Fields.OfType<InfoField>().ElementAt(1).Text;
        await Settle(() => vm.Overlays.Count >= 14 && Status() != Loc.Get("StatusDetecting") && Status() != Loc.Get("StatusDetectingMore"), 40);
        // On a scan like this many lines are more guessed at than read: they show in red and the panel says so.
        var doubtful = vm.Overlays.Count(o => o.Doubtful);
        Assert.True(doubtful >= 6, $"{doubtful} of {vm.Overlays.Count} lines in doubt");
        Assert.Equal(Loc.Format("StatusDetectedDoubt", vm.Overlays.Count, "English", doubtful), Status());
        var area = new RectD(30, 82, 420, 176);
        int Right() => TestPdf.WordsRight(vm.Overlays.Where(o => area.Contains(o.Bounds.CenterX, o.Bounds.CenterY))
            .Select(o => new OcrLine(o.Text, o.Bounds, [])));
        var before = Right();
        Capture(window, "35-scan-in-doubt");

        // Marking the passage reads it again on its own; what is found takes the place of what was there.
        readArea.Command.Execute(null);
        Assert.Equal(CanvasMode.Region, vm.CanvasMode);
        Drag(window, vm, area.Left, area.Top, area.Right, area.Bottom);
        await Settle(() => vm.CanvasMode == CanvasMode.Select && vm.Overlays.Count >= 14 && Status() != Loc.Get("StatusReadingArea"), 40);
        Assert.Equal(CanvasMode.Select, vm.CanvasMode);
        Assert.True(Right() >= before + 4, $"{before} words right before, {Right()} after reading the passage again");
        Assert.Equal(4, vm.Overlays.Count(o => area.Contains(o.Bounds.CenterX, o.Bounds.CenterY)));
        Capture(window, "36-passage-read-again");

        // Reading everything with care is a setting both tools that read scans share.
        careful.Value = true;
        Assert.True(vm.Settings.OcrCareful);
        var ocr = Tool<OcrTool>(vm);
        await Settle();
        Assert.True(Field<BoolField>(ocr, "OcrCareful").Value);
        Field<BoolField>(ocr, "OcrCareful").Value = false;
        Assert.False(vm.Settings.OcrCareful);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PassagesMarkedForMakingSearchableAreFramesOnThePage()
    {
        using var poor = TestPdf.PoorScan();
        var (window, vm) = Start();
        vm.Session.Open(PdfPages.Merge([TestPdf.FromImage(poor, 150), TestPdf.FromImage(poor, 150)]), null);
        var tool = Tool<OcrTool>(vm);
        await Settle(() => vm.PageBitmap != null);
        FitPage(window, vm);
        await Settle();
        Assert.Equal("", vm.ApplySummary);
        Assert.False(Field<ActionField>(tool, "OcrAreaRemove").IsVisible);

        Press(tool, "OcrAreaAdd");
        Assert.Equal(CanvasMode.Region, vm.CanvasMode);
        Drag(window, vm, 30, 82, 420, 176);
        await Settle();
        Assert.Equal(CanvasMode.Select, vm.CanvasMode);
        var frame = Assert.Single(Frames(vm));
        Assert.Equal(390, frame.Bounds.Width, 2.0);
        Assert.Equal(Loc.Format("OcrAreasSummary", 1, 1), vm.ApplySummary);
        Assert.True(Field<ActionField>(tool, "OcrAreaRemove").IsVisible);
        Capture(window, "37-ocr-passage");

        // The marks belong to their page: the next one has none, and coming back they are still there.
        vm.GoToPage(1);
        await Settle(() => vm.PageBitmap != null && vm.Objects.Count == 0);
        Assert.Empty(vm.Objects);
        vm.GoToPage(0);
        await Settle(() => vm.Objects.Count == 1);
        Assert.Single(vm.Objects);

        if (OcrReady)
        {
            var plain = PdfOcr.MakeSearchable(vm.Session.Bytes!, null, OcrSettings.DefaultFor("en")).Pdf;
            await vm.ApplyCommand.ExecuteAsync(null);
            await Settle(() => !vm.IsBusy && vm.CanUndo, 90);
            static int Words(byte[] pdf) => TestPdf.Text(pdf, 0).Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Count(word => TestPdf.Sentences.Any(s => s.Split(' ').Contains(word.Trim('.', ','), StringComparer.OrdinalIgnoreCase)));
            Assert.True(Words(vm.Session.Bytes!) >= Words(plain) + 3, $"{Words(plain)} words right without the mark, {Words(vm.Session.Bytes!)} with it");
            // The document is another now, and with it the marks are gone.
            Assert.Empty(vm.Objects);
            Assert.Equal("", vm.ApplySummary);
        }
        else
        {
            vm.SelectedObject = vm.Objects[0];
            Press(tool, "OcrAreaRemove");
            Assert.Empty(vm.Objects);
            Assert.Equal("", vm.ApplySummary);
        }

        window.Close();
    }

    // ---- images used before ---------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task ImagesUsedBeforeCanBePinnedAndTakenOffTheList()
    {
        var folder = TempFolder();
        string Picture(string name, SKColor color)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(60, 40, SKColorType.Bgra8888, SKAlphaType.Opaque));
            bitmap.Erase(color);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, PdfImages.Encode(bitmap, ImageFormat.Png));
            return path;
        }

        var (logo, stamp, sign) = (Picture("logo.png", SKColors.Red), Picture("stamp.png", SKColors.Green), Picture("sign.png", SKColors.Blue));
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.WithText("A page"), null);
        var edit = Tool<EditTool>(vm);
        var watermark = vm.Tools.OfType<WatermarkTool>().Single();
        await Settle();
        edit.Fields.OfType<ModeField>().First().Select("image");
        var images = AllFields(edit).OfType<FileField>().Single(f => f.Images);
        foreach (var path in new[] { logo, stamp, sign }) vm.RememberImage(path);
        Assert.Equal([sign, stamp, logo], images.Recent.Select(r => r.Path));
        Assert.All(images.Recent, recent => Assert.False(recent.IsPinned));
        await Settle(() => images.Recent.All(r => r.Thumbnail != null));
        Capture(window, "38-image-history");

        // Pinned, the logo moves to the front and stays there, in every tool that takes an image.
        images.Recent.Single(r => r.Path == logo).TogglePinCommand!.Execute(null);
        Assert.Equal([logo, sign, stamp], images.Recent.Select(r => r.Path));
        Assert.True(images.Recent[0].IsPinned);
        Assert.Equal([logo], vm.Settings.PinnedImages);
        var others = AllFields(watermark).OfType<FileField>().Single(f => f.Images);
        Assert.Equal([logo, sign, stamp], others.Recent.Select(r => r.Path));
        Assert.True(others.Recent[0].IsPinned);
        // With the pointer on an entry, the buttons for pinning and removing show; the pinned one carries its mark.
        await Settle();
        var tile = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("recent")).ElementAt(1);
        window.MouseMove(tile.TranslatePoint(new Point(30, 30), window)!.Value);
        await Settle();
        Assert.Equal(2, window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("mini") && b.IsEffectivelyVisible));
        Capture(window, "39-image-pinned");
        vm.RememberImage(stamp);
        Assert.Equal([logo, stamp, sign], images.Recent.Select(r => r.Path));

        // Taken off the list, an image is gone from it; the file itself is not touched.
        images.Recent.Single(r => r.Path == sign).ForgetCommand!.Execute(null);
        Assert.Equal([logo, stamp], images.Recent.Select(r => r.Path));
        Assert.True(File.Exists(sign));
        images.Recent[0].ForgetCommand!.Execute(null);
        Assert.Equal([stamp], images.Recent.Select(r => r.Path));
        Assert.Empty(vm.Settings.PinnedImages);

        // Let go of, a pinned image is an ordinary recent one again.
        images.Recent[0].TogglePinCommand!.Execute(null);
        images.Recent[0].TogglePinCommand!.Execute(null);
        Assert.False(images.Recent[0].IsPinned);
        Assert.Equal([stamp], vm.Settings.RecentImages);
        window.Close();
        Directory.Delete(folder, true);
    }
}
