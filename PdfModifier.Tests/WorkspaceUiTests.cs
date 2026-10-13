using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
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
/// The window as a place to work in: the list of pages (adding files, moving, deleting, taking pages out as
/// pictures), the tool that cuts sheets with several pages apart, and the tool list.
/// </summary>
public class WorkspaceUiTests
{
    private static readonly SKColor Table = new(46, 44, 48);

    private static PageCanvas Canvas(MainWindow window) => window.FindControl<PageCanvas>("PageView")!;

    private static Point At(MainWindow window, MainViewModel vm, double x, double y) =>
        Canvas(window).TranslatePoint(new Point(x * vm.PointScale, y * vm.PointScale), window)!.Value;

    private static void Click(MainWindow window, MainViewModel vm, double x, double y)
    {
        window.MouseDown(At(window, vm, x, y), MouseButton.Left);
        window.MouseUp(At(window, vm, x, y), MouseButton.Left);
    }

    private static void Drag(MainWindow window, MainViewModel vm, double x0, double y0, double x1, double y1)
    {
        var from = At(window, vm, x0, y0);
        var by = new Point((x1 - x0) * vm.PointScale, (y1 - y0) * vm.PointScale);
        window.MouseDown(from, MouseButton.Left);
        for (var step = 1; step <= 4; step++) window.MouseMove(new Point(from.X + by.X * step / 4, from.Y + by.Y * step / 4));
        window.MouseUp(new Point(from.X + by.X, from.Y + by.Y), MouseButton.Left);
    }

    private static bool OcrReady => PdfOcr.EngineStatus().Available && TessData.Missing(["en"]).Count == 0;

    /// <summary>Four pages printed on one sheet, numbered 3, 4 in the upper row and 1, 2 in the lower one.</summary>
    private static byte[] FourOnOneSheet()
    {
        using var sheet = SheetTests.Sheet(2400, 3200, SKColors.White,
            (new SKRect(0, 0, 1200, 1600), 3), (new SKRect(1200, 0, 2400, 1600), 4),
            (new SKRect(0, 1600, 1200, 3200), 1), (new SKRect(1200, 1600, 2400, 3200), 2));
        return TestPdf.FromImage(sheet, 300);
    }

    private static byte[] TwoOnATable()
    {
        using var sheet = SheetTests.Sheet(2000, 1400, Table,
            (new SKRect(120, 130, 940, 1280), null), (new SKRect(1060, 110, 1880, 1260), null));
        return TestPdf.FromImage(sheet, 150);
    }

    private static List<SheetArea> Frames(MainViewModel vm) => vm.Objects.Select(o => o.Model).OfType<SheetArea>().ToList();

    private static async Task<(MainWindow, MainViewModel, SheetTool)> Cutting(byte[] pdf, int frames)
    {
        var (window, vm) = Start();
        vm.Session.Open(pdf, null);
        var tool = Tool<SheetTool>(vm);
        await Settle(() => vm.PageBitmap != null);
        vm.ZoomToFit(window.FindControl<ScrollViewer>("Viewer")!.Bounds.Width, window.FindControl<ScrollViewer>("Viewer")!.Bounds.Height);
        await Settle(() => vm.Objects.Count == frames && vm.PageBitmap != null);
        return (window, vm, tool);
    }

    // ---- sheets ---------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task ThePagesOnASheetAreFramedNumberedAndCutInTheOrderOfTheirPageNumbers()
    {
        var (window, vm, tool) = await Cutting(FourOnOneSheet(), 4);
        var result = Field<PieceListField>(tool, "SheetResult");
        // Once the numbers printed on the pages are read, the frames say where each page will go.
        if (OcrReady) await Settle(() => Frames(vm).Select(f => f.Label).SequenceEqual(["3", "4", "1", "2"]), 20);
        await Settle(() => result.Items.Count == 4 && result.Items.All(item => item.Picture != null));

        Assert.Equal(4, vm.Objects.Count);
        Assert.Equal(OcrReady ? ["3", "4", "1", "2"] : ["1", "2", "3", "4"], Frames(vm).Select(f => f.Label));
        Assert.Equal(["1", "2", "3", "4"], result.Items.Select(item => item.Label));
        Assert.Equal(Loc.Format("SummarySheet", 4), vm.ApplySummary);
        Capture(window, "21-sheet-found");

        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && vm.Pages.Count == 4 && vm.PageBitmap != null);

        Assert.Equal(4, vm.Pages.Count);
        Assert.Equal([0, 1, 2, 3], vm.SelectedPageIndexes);
        // The page numbered 1 was at the lower left of the sheet, where the third bar colour is.
        Assert.Equal(OcrReady ? [2, 3, 0, 1] : [0, 1, 2, 3], Enumerable.Range(0, 4).Select(i => SheetTests.Mark(vm.Session.Bytes!, i)));
        Assert.Equal(Loc.Format("StatusSheetsCut", 1, 4), vm.StatusText);
        Capture(window, "22-sheet-cut");

        await vm.UndoCommand.ExecuteAsync(null);
        await Settle(() => vm.Pages.Count == 1);
        Assert.Single(vm.Pages);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheOrderCanBeSetByClickingThePagesOneAfterTheOther()
    {
        var (window, vm, tool) = await Cutting(FourOnOneSheet(), 4);
        var order = Field<ChoiceField>(tool, "SheetOrder");
        order.Select("click");
        await Settle();
        Assert.All(Frames(vm), frame => Assert.Equal("", frame.Label));
        Assert.Equal(Loc.Format("SheetClickHint", 0, 4), tool.Fields.OfType<InfoField>().First().Text);

        // Lower right first, then upper left, then the other two: each click on the sheet gives the next place.
        var (w, h) = (vm.PageWidth, vm.PageHeight);
        Click(window, vm, w * 0.75, h * 0.75);
        Click(window, vm, w * 0.25, h * 0.25);
        Click(window, vm, w * 0.75, h * 0.25);
        await Settle();
        Assert.Equal(["2", "3", "", "1"], Frames(vm).Select(f => f.Label));
        Assert.Equal(Loc.Format("SheetClickHint", 3, 4), tool.Fields.OfType<InfoField>().First().Text);
        Capture(window, "23-sheet-click-order");

        // The last one is chosen from the pictures in the panel instead.
        var pictures = Field<PieceListField>(tool, "SheetResult");
        pictures.ChooseCommand.Execute(pictures.Items.Single(item => item.Label == "?"));
        Assert.Equal(["2", "3", "4", "1"], Frames(vm).Select(f => f.Label));

        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && vm.Pages.Count == 4);
        Assert.Equal([3, 0, 1, 2], Enumerable.Range(0, 4).Select(i => SheetTests.Mark(vm.Session.Bytes!, i)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task FramesCanBeMovedResizedRemovedAndDrawnByHand()
    {
        var (window, vm, tool) = await Cutting(TwoOnATable(), 2);
        var first = Frames(vm)[0].Bounds;
        Assert.InRange(first.Left, 50, 65);

        // Dragged by its middle the frame moves as a whole.
        Drag(window, vm, first.CenterX, first.CenterY, first.CenterX + 20, first.CenterY + 10);
        var moved = Frames(vm)[0].Bounds;
        Assert.Equal(first.Left + 20, moved.Left, 1.5);
        Assert.Equal(first.Width, moved.Width, 0.5);
        Assert.Same(vm.Objects[0], vm.SelectedObject);

        // Pulled by the handle on its right edge it gets wider.
        Drag(window, vm, moved.Right, moved.CenterY, moved.Right + 30, moved.CenterY);
        Assert.Equal(moved.Width + 30, Frames(vm)[0].Bounds.Width, 1.5);

        // Delete takes the selected frame away; what is left is one page, smaller than the sheet.
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Single(vm.Objects);
        Assert.Equal(Loc.Get("SummarySheetCrop"), vm.ApplySummary);

        // A new frame is drawn where the first page is.
        Press(tool, "SheetAdd");
        Assert.Equal(CanvasMode.Region, vm.CanvasMode);
        Drag(window, vm, 60, 64, 450, 612);
        await Settle();
        Assert.Equal(CanvasMode.Select, vm.CanvasMode);
        Assert.Equal(2, vm.Objects.Count);
        Assert.Equal(new RectD(60, 64, 450, 612).Width, Frames(vm)[1].Bounds.Width, 2);
        // In reading order the new one, on the left, is the first page again.
        Assert.Equal(["2", "1"], Frames(vm).Select(f => f.Label));
        Capture(window, "24-sheet-frames");

        Press(tool, "SheetAgain");
        await Settle(() => vm.Objects.Count == 2 && Frames(vm)[0].Label == "1");
        Assert.Equal(first.Left, Frames(vm)[0].Bounds.Left, 0.5);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ASheetCanBeDividedEvenlyWhenNothingIsFoundOnIt()
    {
        var (window, vm, tool) = await Cutting(TestPdf.WithText(842, 595, "One page, as far as can be told"), 1);
        Assert.Equal(Loc.Get("SheetFoundOne"), tool.Fields.OfType<InfoField>().First().Text);
        Assert.Equal(Loc.Get("SummarySheetWhole"), vm.ApplySummary);

        Field<ChoiceField>(tool, "SheetLayout").Select("two");
        await Settle(() => vm.Objects.Count == 2);
        Assert.Equal([421, 421], Frames(vm).Select(f => Math.Round(f.Bounds.Width)));

        Field<ChoiceField>(tool, "SheetLayout").Select("grid");
        await Settle(() => vm.Objects.Count == 6);
        Assert.Equal(6, vm.Objects.Count);
        Assert.True(Field<NumberField>(tool, "SheetColumns").IsVisible);

        Field<ChoiceField>(tool, "SheetOrder").Select("columns");
        await Settle();
        Assert.Equal(["1", "3", "5", "2", "4", "6"], Frames(vm).Select(f => f.Label));
        window.Close();
    }

    [AvaloniaFact]
    public async Task SeveralSheetsAreCutInOneGo()
    {
        var sheet = TwoOnATable();
        var (window, vm, tool) = await Cutting(PdfPages.Merge([sheet, TestPdf.WithText("An ordinary page"), sheet]), 2);
        Field<PagesField>(tool, "Pages").Scope = Field<PagesField>(tool, "Pages").Scopes[0];
        await Settle();
        Assert.Equal(Loc.Format("SummarySheets", 3, 2), vm.ApplySummary);
        Field<BoolField>(tool, "SheetKeep").Value = true;

        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && vm.Pages.Count == 7);

        // Sheet, its two pages, the ordinary page untouched, sheet, its two pages.
        Assert.Equal(7, vm.Pages.Count);
        Assert.Equal("An ordinary page", TestPdf.Text(vm.Session.Bytes!, 3));
        var sizes = PdfPages.GetPageInfos(vm.Session.Bytes!).Select(p => Math.Round(p.Width)).ToList();
        Assert.Equal(960, sizes[0]);
        Assert.InRange(sizes[1], 385, 400);
        Assert.Equal(960, sizes[4]);
        Assert.InRange(sizes[6], 385, 400);
        Assert.Equal([1, 2], vm.SelectedPageIndexes);
        window.Close();
    }

    // ---- the page list ----------------------------------------------------------------------------------------------

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PdfModifier.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Picture(string folder, string name, SKColor color, ImageFormat format, int width = 800, int height = 600)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bitmap.Erase(color);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, PdfImages.Encode(bitmap, format));
        return path;
    }

    private static SKColor Middle(byte[] pdf, int page)
    {
        using var rendered = TestPdf.Render(pdf, page, 0.5);
        return rendered.GetPixel(rendered.Width / 2, rendered.Height / 2);
    }

    [AvaloniaFact]
    public async Task FilesDroppedIntoThePageListBecomePagesWhereTheyAreDropped()
    {
        var folder = TempFolder();
        var red = Picture(folder, "red.png", new SKColor(220, 30, 30), ImageFormat.Png);
        var green = Picture(folder, "green.jpg", new SKColor(30, 160, 60), ImageFormat.Jpeg, 600, 900);
        var other = Path.Combine(folder, "other.pdf");
        File.WriteAllBytes(other, TestPdf.WithText("From another file", "And its second page"));
        var notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(notes, "not a page");

        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(3), null);
        await Settle(() => vm.PageBitmap != null);

        await vm.AddFiles([red, notes, other, green], 1);
        await Settle(() => !vm.IsBusy && vm.Pages.Count == 7);

        var pdf = vm.Session.Bytes!;
        Assert.Equal(7, vm.Pages.Count);
        Assert.Equal("Page 1", TestPdf.Text(pdf, 0));
        Assert.Equal(new SKColor(220, 30, 30), Middle(pdf, 1));
        Assert.Equal("From another file", TestPdf.Text(pdf, 2));
        Assert.Equal("And its second page", TestPdf.Text(pdf, 3));
        Assert.InRange(Middle(pdf, 4).Green, 150, 170);
        Assert.Equal("Page 2", TestPdf.Text(pdf, 5));
        // What was added is selected and on view, and can be taken back in one step.
        Assert.Equal([1, 2, 3, 4], vm.SelectedPageIndexes);
        Assert.Equal(1, vm.CurrentPageIndex);
        Assert.Equal(Loc.Format("StatusPagesAdded", 4), vm.StatusText);
        // A portrait picture becomes a portrait page of about the size of a sheet of paper.
        var infos = PdfPages.GetPageInfos(pdf);
        Assert.True(infos[4].Height > infos[4].Width);
        Assert.InRange(infos[4].Height, 600, 900);
        Capture(window, "25-pages-added");

        await vm.UndoCommand.ExecuteAsync(null);
        await Settle(() => vm.Pages.Count == 3);
        Assert.Equal(3, vm.Pages.Count);

        await vm.AddFiles([notes], 0);
        Assert.Equal(Loc.Get("ErrNothingToAdd"), vm.StatusText);
        Assert.Equal(3, vm.Pages.Count);
        window.Close();
        Directory.Delete(folder, true);
    }

    [AvaloniaFact]
    public async Task PicturesDroppedIntoAnEmptyWindowBecomeADocument()
    {
        var folder = TempFolder();
        var first = Picture(folder, "1.png", new SKColor(20, 60, 200), ImageFormat.Png);
        var second = Picture(folder, "2.png", new SKColor(200, 180, 20), ImageFormat.Png);
        var (window, vm) = Start();
        await Settle();

        await vm.DropFiles([first, second]);
        await Settle(() => !vm.IsBusy && vm.Pages.Count == 2 && vm.PageBitmap != null);

        Assert.True(vm.HasDocument);
        Assert.Equal(2, vm.Pages.Count);
        Assert.Equal(new SKColor(20, 60, 200), Middle(vm.Session.Bytes!, 0));
        Assert.Equal(Loc.Get("Untitled"), vm.DocumentName);
        Assert.True(vm.IsDirty);

        // With a document open, a picture dropped on the page view goes in after the page on view.
        await vm.DropFiles([second]);
        await Settle(() => !vm.IsBusy && vm.Pages.Count == 3);
        Assert.Equal(new SKColor(200, 180, 20), Middle(vm.Session.Bytes!, 1));
        window.Close();
        Directory.Delete(folder, true);
    }

    [AvaloniaFact]
    public async Task PagesAreMovedRotatedAndDeletedFromThePageList()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(5), null);
        await Settle(() => vm.PageBitmap != null);
        string[] Texts() => Enumerable.Range(0, vm.Pages.Count).Select(i => TestPdf.Text(vm.Session.Bytes!, i)).ToArray();

        // Pages 2 and 4 dragged to the very front...
        await vm.MovePages([1, 3], 0);
        await Settle(() => !vm.IsBusy);
        Assert.Equal(["Page 2", "Page 4", "Page 1", "Page 3", "Page 5"], Texts());
        Assert.Equal([0, 1], vm.SelectedPageIndexes);
        // ...then the first of them to the end, and one dropped where it already is, which changes nothing.
        await vm.MovePages([0], 5);
        await Settle(() => !vm.IsBusy);
        Assert.Equal(["Page 4", "Page 1", "Page 3", "Page 5", "Page 2"], Texts());
        Assert.Equal([4], vm.SelectedPageIndexes);
        var before = vm.Session.Bytes;
        await vm.MovePages([2], 3);
        await vm.MovePages([2], 2);
        Assert.Same(before, vm.Session.Bytes);

        await vm.RotatePages([0], 90);
        await Settle(() => !vm.IsBusy);
        var turned = PdfPages.GetPageInfos(vm.Session.Bytes!)[0];
        Assert.True(turned.Width > turned.Height);

        // Delete, with the keyboard in the page list.
        var list = window.FindControl<ListBox>("PageList")!;
        list.SelectedItems!.Clear();
        list.SelectedItems.Add(vm.Pages[1]);
        list.SelectedItems.Add(vm.Pages[2]);
        list.ContainerFromIndex(1)!.Focus();
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        await Settle(() => !vm.IsBusy && vm.Pages.Count == 3);
        Assert.Equal(["Page 4", "Page 5", "Page 2"], Texts());
        Assert.Equal(Loc.Format("StatusPagesDeleted", 2), vm.StatusText);

        // Not all of them: a document has at least one page.
        await vm.DeletePages([0, 1, 2]);
        Assert.Equal(3, vm.Pages.Count);
        Assert.Equal(Loc.Get("ErrDeleteAll"), vm.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task APageDraggedOutOfTheWindowIsAPngPicture()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(3), null);
        await Settle(() => vm.PageBitmap != null);

        var files = await vm.PagePictures([0, 2]);

        Assert.Equal(["Untitled_001.png", "Untitled_003.png"], files.Select(Path.GetFileName));
        using var picture = SKBitmap.Decode(files[1]);
        // An A4 page at 200 dots per inch.
        Assert.InRange(picture.Width, 1650, 1657);
        Assert.InRange(picture.Height, 2335, 2342);
        Assert.True(TestPdf.InkRatio(picture) > 0.0003, "the page number is on the picture");

        // The scratch pictures are not left behind: the window removes them when it closes.
        vm.DeleteScratchFiles();
        Assert.False(File.Exists(files[0]));
        window.Close();
    }

    private static Point MiddleOf(MainWindow window, int page)
    {
        var item = window.FindControl<ListBox>("PageList")!.ContainerFromIndex(page)!;
        return item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
    }

    [AvaloniaFact]
    public async Task PressingOnOneOfSeveralSelectedPagesKeepsThemTogetherForDragging()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(3), null);
        await Settle(() => vm.Pages.All(p => p.Thumbnail != null));
        var list = window.FindControl<ListBox>("PageList")!;
        list.SelectedItems!.Add(vm.Pages[1]);
        Assert.Equal([0, 1], vm.SelectedPageIndexes.Order());

        // The press alone does not let go of the other page: they may be about to be dragged together...
        window.MouseDown(MiddleOf(window, 0), MouseButton.Left);
        Assert.Equal(2, list.SelectedItems.Count);
        // ...and a plain click, as it turns out to be, then selects just the page that was clicked.
        window.MouseUp(MiddleOf(window, 0), MouseButton.Left);
        Assert.Equal([0], vm.SelectedPageIndexes);

        // Dragging a page makes the picture of it that leaves the window, and leaves the document alone. (Without a
        // screen there is nowhere to drop it; where it would go in the list is covered by the drop mark below.)
        var before = vm.Session.Bytes;
        var from = MiddleOf(window, 2);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X, from.Y - 20), RawInputModifiers.LeftMouseButton);
        var picture = Path.Combine(vm.ScratchFolder, "1", "Untitled_003.png");
        await Settle(() => File.Exists(picture));
        window.MouseUp(new Point(from.X, from.Y - 20), MouseButton.Left);
        await Settle();
        Assert.True(File.Exists(picture));
        Assert.Same(before, vm.Session.Bytes);
        Assert.False(window.FindControl<Border>("DropMark")!.IsVisible);
        vm.DeleteScratchFiles();
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDropMarkShowsBetweenWhichPagesSomethingGoes()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(3), null);
        await Settle(() => vm.Pages.All(p => p.Thumbnail != null));
        var list = window.FindControl<ListBox>("PageList")!;
        var panel = window.FindControl<Panel>("PagePanel")!;

        double Top(int index) => list.ContainerFromIndex(index)!.TranslatePoint(default, panel)!.Value.Y;
        double Height(int index) => list.ContainerFromIndex(index)!.Bounds.Height;

        Assert.Equal(0, window.DropPlace(new Point(60, Top(0) + 10)).Index);
        Assert.Equal(1, window.DropPlace(new Point(60, Top(0) + Height(0) - 10)).Index);
        Assert.Equal(1, window.DropPlace(new Point(60, Top(1) + 10)).Index);
        Assert.Equal(2, window.DropPlace(new Point(60, Top(1) + Height(1) * 0.7)).Index);
        var end = window.DropPlace(new Point(60, panel.Bounds.Height - 4));
        Assert.Equal(3, end.Index);
        // The mark is drawn in the gap between the two pages, not across one of them.
        Assert.InRange(window.DropPlace(new Point(60, Top(1) + 10)).Y, Top(0) + Height(0), Top(1));
        Assert.InRange(end.Y, Top(2) + Height(2), Top(2) + Height(2) + 6);
        window.Close();
    }

    // ---- finding one's way ------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task ToolsAreFoundByTyping()
    {
        var (window, vm) = Start();
        await Settle();
        Assert.Equal(5, vm.ToolGroups.Count);
        Assert.Equal(vm.Tools, vm.ToolGroups.SelectMany(group => group.Tools));
        Assert.True(vm.SelectedTool.IsSelected);

        vm.ToolSearch = "water";
        Assert.Equal(["Watermark", "RemoveWatermark"], vm.Tools.Where(t => t.IsListed).Select(t => t.Id));
        Assert.Single(vm.ToolGroups, group => group.IsListed);
        Capture(window, "26-tool-search");

        // Words are looked for in what a tool says it does, too: "photo" is only in the hint of the sheet tool.
        vm.ToolSearch = "photo";
        Assert.Equal(["Sheets"], vm.Tools.Where(t => t.IsListed).Select(t => t.Id));

        vm.ToolSearch = "zzz";
        Assert.True(vm.NoToolListed);
        vm.ToolSearch = "";
        Assert.All(vm.Tools, tool => Assert.True(tool.IsListed));

        vm.SelectToolCommand.Execute(vm.Tools.OfType<CropTool>().Single());
        Assert.IsType<CropTool>(vm.SelectedTool);
        Assert.Single(vm.Tools, tool => tool.IsSelected);
        vm.ShowSettingsCommand.Execute(null);
        Assert.IsType<SettingsTool>(vm.SelectedTool);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheWindowHasADarkSide()
    {
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        try
        {
            var (window, vm) = Start();
            Capture(window, "27-dark-empty");
            vm.Session.Open(TwoOnATable(), null);
            Tool<SheetTool>(vm);
            await Settle(() => vm.Objects.Count == 2 && vm.Pages[0].Thumbnail != null);
            vm.ZoomToFit(window.FindControl<ScrollViewer>("Viewer")!.Bounds.Width, window.FindControl<ScrollViewer>("Viewer")!.Bounds.Height);
            await Settle(() => vm.PageBitmap != null);
            Assert.Equal(2, vm.Objects.Count);
            Capture(window, "28-dark-sheet");
            Tool<WatermarkTool>(vm);
            await Settle();
            Capture(window, "29-dark-watermark");
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Default;
        }
    }
}
