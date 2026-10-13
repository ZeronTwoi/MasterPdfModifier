using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PdfModifier.Controls;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using PdfModifier.Localization;
using PdfModifier.Services;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using PdfModifier.Views;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>
/// The settings of the tools as they are worked with: buttons that act at once, choices that are all on view, spots
/// that are clicked on a small page, values offered under sliders, and a line that says which pages are meant.
/// </summary>
public class ToolSettingsUiTests
{
    private static readonly string[] Lines =
    [
        "Invoices are payable within thirty days of receipt", "The committee approved the budget for next year",
        "Please return the signed form to our office"
    ];

    /// <summary>A page of a few lines of text, shown turned clockwise by <paramref name="turned"/> degrees.</summary>
    private static byte[] Letter(int turned = 0, string? heading = null)
    {
        var pdf = TestPdf.Compose((page, fonts) =>
        {
            fonts.PrepareAll([..Lines, heading ?? ""], null);
            if (heading != null) PageDraw.DrawText(page, fonts, heading, new TextStyle(20), 60, 70);
            for (var i = 0; i < Lines.Length; i++) PageDraw.DrawText(page, fonts, Lines[i], new TextStyle(13), 60, 120 + i * 24);
        });
        return turned == 0 ? pdf : PdfPages.Rotate(pdf, null, turned);
    }

    private static List<int> Rotations(MainViewModel vm) => PdfPages.GetPageInfos(vm.Session.Bytes!).Select(page => page.RotationDegrees).ToList();

    private static void FitPage(MainWindow window, MainViewModel vm)
    {
        var viewer = window.FindControl<ScrollViewer>("Viewer")!;
        vm.ZoomToFit(viewer.Bounds.Width, viewer.Bounds.Height);
    }

    // ---- which pages --------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task TheChoiceOfPagesSaysWhichPagesItComesTo()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(12), null);
        var tool = Tool<FlattenTool>(vm);
        await Settle(() => vm.PageBitmap != null);
        var pages = tool.Fields.OfType<PagesField>().Single();

        Assert.Equal(Loc.Format("PagesAllCount", 12), pages.Summary);
        pages.Scope = pages.Scopes[1];
        Assert.Equal(Loc.Format("PagesChosenOne", 1), pages.Summary);
        vm.SetSelection([1, 2, 3, 6, 9], 2);
        Assert.Equal(Loc.Format("PagesChosen", 5, "2-4, 7, 10"), pages.Summary);
        Assert.False(pages.IsInvalid);

        // A range is read as it is typed, and what cannot be read is said so on the spot.
        pages.Scope = pages.Scopes[2];
        pages.Range = "3-5, 11-";
        Assert.Equal(Loc.Format("PagesChosen", 5, "3-5, 11-12"), pages.Summary);
        pages.Range = "three";
        Assert.Equal((Loc.Get("ErrPageRange"), true), (pages.Summary, pages.IsInvalid));
        pages.Range = "4";
        Assert.Equal((Loc.Format("PagesChosenOne", 4), false), (pages.Summary, pages.IsInvalid));

        // The line follows the language like everything else.
        Loc.SetLanguage("de");
        Assert.Equal(Strings.For("de")["PagesChosenOne"].Replace("{0}", "4"), pages.Summary);
        Loc.SetLanguage("en");

        // A long list of single pages is cut short; the count says how many there are.
        vm.Session.Open(TestPdf.Numbered(80), null);
        await Settle(() => vm.Pages.Count == 80);
        pages.Scope = pages.Scopes[1];
        vm.SetSelection(Enumerable.Range(0, 40).Select(i => i * 2).ToList(), 0);
        Assert.StartsWith("40 pages: 1, 3, 5, 7,", pages.Summary);
        Assert.EndsWith(", …", pages.Summary);
        Assert.True(pages.Summary.Length < 80);
        Capture(window, "50-pages-choice");
        window.Close();
    }

    // ---- turning and mirroring ----------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task TurningAndMirroringAreButtonsThatActAtOnceOnTheChosenPages()
    {
        var (window, vm) = Start();
        vm.Session.Open(PdfPages.Merge([Letter(), Letter(), Letter(), Letter()]), null);
        var tool = Tool<RotateTool>(vm);
        await Settle(() => vm.PageBitmap != null);
        FitPage(window, vm);
        await Settle();

        // Nothing to apply: the buttons are what the tool is, and each says what it does.
        Assert.False(vm.ShowApply);
        var buttons = AllFields(tool).OfType<ActionField>().Where(field => field.Large).ToList();
        Assert.Equal(5, buttons.Count);
        Assert.All(buttons, button => Assert.True(button.HasIcon));
        Capture(window, "51-rotate");

        vm.SetSelection([1, 2], 1);
        Press(tool, "RotateRightShort");
        await Settle(() => !vm.IsBusy && vm.CanUndo);
        Assert.Equal([0, 90, 90, 0], Rotations(vm));
        Assert.Equal(Loc.Format("StatusPagesTurned", 2), vm.StatusText);
        // The same pages are still selected, the one that was on view still on view: the button can be pressed again.
        Assert.Equal([1, 2], vm.SelectedPageIndexes.Order());
        Assert.Equal(1, vm.CurrentPageIndex);
        Press(tool, "RotateRightShort");
        await Settle(() => !vm.IsBusy && Rotations(vm)[1] == 180);
        Press(tool, "RotateLeftShort");
        await Settle(() => !vm.IsBusy && Rotations(vm)[1] == 90);
        Press(tool, "RotateHalfShort");
        await Settle(() => !vm.IsBusy && Rotations(vm)[1] == 270);
        Assert.Equal([0, 270, 270, 0], Rotations(vm));

        // A mirror image, of all pages when that is what is chosen.
        var pages = tool.Fields.OfType<PagesField>().Single();
        pages.Scope = pages.Scopes[0];
        using var before = TestPdf.Render(vm.Session.Bytes!, 0);
        Press(tool, "MirrorAcross");
        await Settle(() => !vm.IsBusy && vm.StatusText == Loc.Format("StatusPagesMirrored", 4));
        using var after = TestPdf.Render(vm.Session.Bytes!, 0);
        // The lines stood at the left; mirrored, their place is at the right.
        Assert.True(TestPdf.InkRatio(SheetPictures.Part(before, 0, 0, 297, 842)) > TestPdf.InkRatio(SheetPictures.Part(before, 298, 0, 595, 842)) * 2);
        Assert.True(TestPdf.InkRatio(SheetPictures.Part(after, 298, 0, 595, 842)) > TestPdf.InkRatio(SheetPictures.Part(after, 0, 0, 297, 842)) * 2);
        Press(tool, "MirrorDown");
        await Settle(() => !vm.IsBusy);

        // Each press is one step back with Undo.
        var steps = 0;
        while (vm.CanUndo && steps++ < 10) await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(6, steps);
        Assert.Equal([0, 0, 0, 0], Rotations(vm));
        window.Close();
    }

    [AvaloniaFact]
    public async Task PagesThatAreTurnedAreFoundMarkedShownAndSetUpright()
    {
        // Five pages: upright, on its right side, on its head, upright, on its left side.
        var (window, vm) = Start();
        vm.Session.Open(PdfPages.Merge([Letter(), Letter(90), Letter(180), Letter(), Letter(270)]), null);
        var tool = Tool<RotateTool>(vm);
        var pages = tool.Fields.OfType<PagesField>().Single();
        pages.Scope = pages.Scopes[0];
        await Settle(() => vm.PageBitmap != null);
        FitPage(window, vm);
        Assert.False(vm.ShowApply);
        Assert.False(vm.IsPreview);

        Press(tool, "RotateFind");
        await Settle(() => !vm.IsBusy && vm.Pages[1].Badge != null);

        // Each turned page carries the turn it needs; the upright ones carry nothing.
        Assert.Null(vm.Pages[0].Badge);
        Assert.Equal(new PageBadge("−90°", false, Loc.Get("RotateBadgeLeft")), vm.Pages[1].Badge);
        Assert.Equal(new PageBadge("180°", false, Loc.Get("RotateBadgeHalf")), vm.Pages[2].Badge);
        Assert.Null(vm.Pages[3].Badge);
        Assert.Equal(new PageBadge("90°", false, Loc.Get("RotateBadgeRight")), vm.Pages[4].Badge);
        var found = tool.Fields.OfType<InfoField>().Single(field => field.Text.StartsWith("3 of 5"));
        Assert.Equal(Loc.Format("RotateFound", 3, 5, "2-3, 5"), found.Text);
        Assert.True(vm.ShowApply);
        Assert.Equal(Loc.Get("RotateApply"), tool.ApplyLabel);
        Assert.Equal(Loc.Format("SummaryRotate", 3, "2-3, 5"), vm.ApplySummary);

        // The first of them is put on view, shown as it will stand: upright, so taller than wide again.
        Assert.Equal(1, vm.CurrentPageIndex);
        await Settle(() => vm.IsPreview && vm.PageHeight > vm.PageWidth);
        Assert.True(vm.IsPreview);
        Assert.Equal((595.0, 842.0), (Math.Round(vm.PageWidth), Math.Round(vm.PageHeight)));
        var thisPage = tool.Fields.OfType<InfoField>().Single(field => field.Text == Loc.Get("RotateThisLeft"));
        Assert.True(thisPage.IsVisible);
        await Settle();
        Capture(window, "52-rotate-found");

        // A page can be left out, and taken up again.
        Press(tool, "RotateLeave");
        Assert.Null(vm.Pages[1].Badge);
        Assert.Equal(Loc.Format("SummaryRotate", 2, "3, 5"), vm.ApplySummary);
        await Settle(() => !vm.IsPreview);
        Assert.True(Field<ActionField>(tool, "RotateTake").IsVisible);
        Press(tool, "RotateTake");
        Assert.Equal("−90°", vm.Pages[1].Badge?.Text);

        // Turned by hand meanwhile, a page needs that much less; what was found for the others stands.
        vm.GoToPage(2);
        await Settle(() => vm.CurrentPageIndex == 2 && vm.PageBitmap != null);
        pages.Scope = pages.Scopes[1];
        Press(tool, "RotateRightShort");
        await Settle(() => !vm.IsBusy && Rotations(vm)[2] == 270);
        Assert.Equal("90°", vm.Pages[2].Badge?.Text);
        Assert.Equal("−90°", vm.Pages[1].Badge?.Text);
        Assert.Equal(Loc.Format("SummaryRotate", 3, "2-3, 5"), vm.ApplySummary);

        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && Rotations(vm).All(rotation => rotation == 0));
        Assert.Equal([0, 0, 0, 0, 0], Rotations(vm));
        Assert.Equal(Loc.Format("StatusTurnedUpright", 3), vm.StatusText);
        Assert.Equal([1, 2, 4], vm.SelectedPageIndexes.Order());
        Assert.All(vm.Pages, page => Assert.Null(page.Badge));
        Assert.False(vm.ShowApply);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ACloseCallAboutATurnIsRedAndFollowsTheLimit()
    {
        // A page with more of its text sideways than upright, but not by much; and one that is plainly on its side.
        var mixed = TestPdf.Compose((page, fonts) =>
        {
            const string line = "Delivery is expected on the fourteenth of March";
            fonts.Prepare(line, null);
            for (var i = 0; i < 4; i++) PageDraw.DrawText(page, fonts, line, new TextStyle(11), 60, 80 + i * 18);
            for (var i = 0; i < 6; i++) PageDraw.DrawText(page, fonts, line, new TextStyle(11), 380 + i * 18, 760, 90);
        });
        var (window, vm) = Start();
        vm.Session.Open(PdfPages.Merge([mixed, Letter(90)]), null);
        var tool = Tool<RotateTool>(vm);
        var pages = tool.Fields.OfType<PagesField>().Single();
        pages.Scope = pages.Scopes[0];
        await Settle(() => vm.PageBitmap != null);
        FitPage(window, vm);

        Press(tool, "RotateFind");
        await Settle(() => !vm.IsBusy && vm.Pages[1].Badge != null);

        // At the usual limit the first page is left as it is, with a red mark to have a look.
        Assert.Equal(new PageBadge("?", true, Loc.Get("RotateBadgeMaybe")), vm.Pages[0].Badge);
        Assert.Equal("−90°", vm.Pages[1].Badge?.Text);
        Assert.Equal(0, vm.CurrentPageIndex);
        Assert.Contains(tool.Fields.OfType<InfoField>(), field => field.Text == Loc.Get("RotateThisMaybe"));
        Assert.True(Field<ActionField>(tool, "SheetConfirm").IsVisible);
        Assert.Equal(Loc.Format("SummaryRotate", 1, "2") + " " + Loc.Format("RotateFoundDoubt", 1), vm.ApplySummary);
        await Settle();
        Capture(window, "53-rotate-doubt");

        // A higher limit takes it for turned, still in red; nothing is looked at again for that.
        var limit = Field<SliderField>(tool, "RotateReadiness");
        limit.Set(100);
        Assert.Equal(new PageBadge("90°?", true, Loc.Get("RotateBadgeNotCertain")), vm.Pages[0].Badge);
        Assert.False(vm.IsBusy);
        Assert.Contains(tool.Fields.OfType<InfoField>(), field => field.Text == Loc.Get("RotateThisRight") + " " + Loc.Get("RotateNotCertain"));
        limit.Set(0);
        Assert.Equal("?", vm.Pages[0].Badge?.Text);
        Assert.Equal("−90°", vm.Pages[1].Badge?.Text);

        // Looked at and found right as it is: the mark goes.
        Press(tool, "SheetConfirm");
        Assert.Null(vm.Pages[0].Badge);
        Assert.Equal(Loc.Format("SummaryRotate", 1, "2"), vm.ApplySummary);

        // A change from elsewhere makes what was found void.
        Tool<DeleteTool>(vm);
        Assert.All(vm.Pages, page => Assert.Null(page.Badge));
        vm.SetSelection([0], 0);
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && vm.Pages.Count == 1);
        Tool<RotateTool>(vm);
        await Settle();
        Assert.All(vm.Pages, page => Assert.Null(page.Badge));
        Assert.False(vm.ShowApply);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AScanThatLiesOnItsSideIsFoundToo()
    {
        using var upright = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1240, 1754, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque));
        using (var canvas = new SkiaSharp.SKCanvas(upright))
        {
            canvas.Clear(new SkiaSharp.SKColor(232, 230, 224));
            using var paint = new SkiaSharp.SKPaint
            {
                Color = new SkiaSharp.SKColor(40, 40, 40), IsAntialias = true, TextSize = 30, Typeface = SkiaSharp.SKTypeface.FromFamilyName("Times New Roman")
            };
            for (var i = 0; i < 28; i++)
                canvas.DrawText(TestPdf.Sentences[i % 16] + ", " + TestPdf.Sentences[(i * 7 + 3) % 16].ToLowerInvariant(), 110, 170 + i * 51, paint);
        }

        using var lying = OrientationTests.Turned(upright, 1);
        var (window, vm) = Start();
        vm.Session.Open(PdfPages.Merge([TestPdf.FromImage(upright, 150), TestPdf.FromImage(lying, 150)]), null);
        var tool = Tool<RotateTool>(vm);
        var pages = tool.Fields.OfType<PagesField>().Single();
        pages.Scope = pages.Scopes[0];
        await Settle(() => vm.PageBitmap != null);
        var canRead = PdfOcr.EngineStatus().Available && TessData.Missing(["en"]).Count == 0;
        // Without text recognition the panel says what that means for scans.
        Assert.Equal(!canRead, tool.Fields.OfType<InfoField>().Single(field => field.Text == Loc.Get("RotateNoReading")).IsVisible);

        Press(tool, "RotateFind");
        await Settle(() => !vm.IsBusy && vm.Pages[1].Badge != null, 60);

        Assert.Null(vm.Pages[0].Badge);
        // Read, the turn is certain; judged by the look of its print alone, it is marked for a look.
        Assert.Equal(canRead ? "−90°" : "−90°?", vm.Pages[1].Badge?.Text);
        Assert.Equal(!canRead, vm.Pages[1].Badge?.Warns);
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && Rotations(vm)[1] == 270);
        Assert.Equal([0, 270], Rotations(vm));
        window.Close();
    }

    // ---- moving pages -------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task MovingPagesIsAMatterOfPressingAButtonAgain()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(5), null);
        var tool = Tool<ReorderTool>(vm);
        await Settle(() => vm.PageBitmap != null);
        Assert.False(vm.ShowApply);

        string Order() => string.Join(" ", Enumerable.Range(0, vm.Pages.Count).Select(i => TestPdf.Text(vm.Session.Bytes!, i).Replace("Page ", "")));

        // The panel says which pages the buttons will move.
        vm.SetSelection([3, 4], 3);
        Assert.Contains(tool.Fields.OfType<InfoField>(), field => field.Text == Loc.Format("PagesChosen", 2, "4-5"));
        Capture(window, "54-reorder");

        Press(tool, "ReorderUp");
        await Settle(() => !vm.IsBusy && Order() == "1 2 4 5 3");
        // Still selected where they are now, so the same button takes them further.
        Assert.Equal([2, 3], vm.SelectedPageIndexes.Order());
        Press(tool, "ReorderUp");
        await Settle(() => !vm.IsBusy && Order() == "1 4 5 2 3");
        Press(tool, "ReorderFirst");
        await Settle(() => !vm.IsBusy && Order() == "4 5 1 2 3");
        Press(tool, "ReorderDown");
        await Settle(() => !vm.IsBusy && Order() == "1 4 5 2 3");
        Press(tool, "ReorderLast");
        await Settle(() => !vm.IsBusy && Order() == "1 2 3 4 5");
        Assert.Equal("1 2 3 4 5", Order());
        Press(tool, "ReorderReverse");
        await Settle(() => !vm.IsBusy && Order() == "5 4 3 2 1");
        Assert.Equal("5 4 3 2 1", Order());
        Assert.Equal(Loc.Format("StatusPagesMoved", 5), vm.StatusText);
        window.Close();
    }

    // ---- choices on view, spots to click, values under sliders ---------------------------------------------------

    [AvaloniaFact]
    public async Task PageNumbersAreWrittenAndPlacedByClickingWhatIsWanted()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.WithText("alpha", "beta", "gamma"), null);
        vm.Zoom = 0.75;
        var tool = Tool<PageNumbersTool>(vm);
        await Settle(() => vm.IsPreview && vm.PageBitmap != null);

        // The usual ways of writing a number are all on view; the box for typing one's own only shows when asked for.
        var style = Field<ModeField>(tool, "NumberStyle");
        Assert.Equal(6, style.Options.Count);
        Assert.Equal(("of", 3), (style.Key, style.Columns));
        var format = Field<TextField>(tool, "NumberFormat");
        Assert.False(format.IsVisible);
        var place = tool.Fields.OfType<PlaceField>().Single();
        Assert.Equal("BottomCenter", place.Key);
        // The middle row of the small page is not on offer: a page number stands at the head or the foot.
        Assert.Equal(6, place.Spots.Count(spot => spot.IsOffered));
        Capture(window, "55-page-numbers");

        async Task<string> Applied()
        {
            var before = vm.Session.Bytes;
            await vm.ApplyCommand.ExecuteAsync(null);
            await Settle(() => !vm.IsBusy && !ReferenceEquals(before, vm.Session.Bytes));
            var text = TestPdf.Text(vm.Session.Bytes!, 1).Replace("beta", "").Trim();
            await vm.UndoCommand.ExecuteAsync(null);
            await Settle(() => !vm.IsBusy);
            return text;
        }

        Assert.Equal("2 / 3", await Applied());
        style.Select("pageOf");
        Assert.Equal("Page 2 of 3", await Applied());
        style.Select("dash");
        Assert.Equal("– 2 –", await Applied());
        style.Select("own");
        Assert.True(format.IsVisible);
        format.Value = "[{n}]";
        Assert.Equal("[2]", await Applied());

        // Where the number stands is a click on the small page.
        place.ChooseCommand.Execute(place.Spots.Single(spot => spot.Key == "TopRight"));
        var before = vm.Session.Bytes;
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && !ReferenceEquals(before, vm.Session.Bytes));
        var number = PdfTextEdit.GetTextRuns(vm.Session.Bytes!, 0).Single(run => run.Text == "[1]");
        Assert.True(number.Bounds.Left > 500 && number.Bounds.Top < 60, $"the number stands at {number.Bounds}");
        // A spot that is not on offer cannot be chosen.
        place.ChooseCommand.Execute(place.Spots.Single(spot => spot.Key == "Center"));
        Assert.Equal("TopRight", place.Key);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ALevelOfCompressionIsWhatItsTwoSlidersSay()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(1), null);
        var tool = Tool<CompressTool>(vm);
        await Settle(() => vm.PageBitmap != null);
        var level = Field<ModeField>(tool, "CompressPreset");
        var dpi = Field<SliderField>(tool, "CompressDpi");
        var quality = Field<SliderField>(tool, "ImageQuality");

        // The sliders are always there and show what the level stands for.
        Assert.Equal(("balanced", 150.0, 70.0), (level.Key, dpi.Number, quality.Number));
        Assert.True(dpi.IsVisible && quality.IsVisible);
        level.Select("strong");
        Assert.Equal((100, 50), (tool.Options().MaxImageDpi, tool.Options().JpegQuality));
        Assert.Equal((100.0, 50.0), (dpi.Number, quality.Number));
        level.Select("light");
        Assert.Equal((200.0, 85.0), (dpi.Number, quality.Number));
        Assert.Equal("light", level.Key);

        // Moved by hand, a slider makes the settings one's own, starting from where the level had left them.
        quality.Set(60);
        Assert.Equal("custom", level.Key);
        Assert.Equal((200, 60), (tool.Options().MaxImageDpi, tool.Options().JpegQuality));
        level.Select("balanced");
        Assert.Equal((150.0, 70.0), (dpi.Number, quality.Number));
        Capture(window, "56-compress");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ValuesThatAreWantedOftenAreOneClickUnderTheirSlider()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(2), null);
        var watermark = Tool<WatermarkTool>(vm);
        await Settle(() => vm.PageBitmap != null);

        var rotation = Field<SliderField>(watermark, "Rotation");
        Assert.Equal(["0°", "45°", "-45°", "90°"], rotation.Stops.Select(stop => stop.Label));
        rotation.UseCommand.Execute(rotation.Stops[2]);
        Assert.Equal(-45, rotation.Number);
        var opacity = Field<SliderField>(watermark, "Opacity");
        opacity.UseCommand.Execute(opacity.Stops.Single(stop => stop.Label == "100 %"));
        Assert.Equal(100, opacity.Number);
        Capture(window, "57-watermark");

        var extract = Tool<ExtractTool>(vm);
        var dpi = Field<SliderField>(extract, "ImageDpi");
        Assert.False(dpi.IsVisible);
        Field<ModeField>(extract, "ExtractFormat").Select("jpg");
        Assert.True(dpi.IsVisible && Field<SliderField>(extract, "ImageQuality").IsVisible);
        dpi.UseCommand.Execute(dpi.Stops.Single(stop => stop.Value == 300));
        Assert.Equal(300, dpi.Number);
        // A slider with nothing offered under it shows no row of buttons.
        Assert.False(Field<SliderField>(extract, "ImageQuality").HasStops);
        await Settle();
        Capture(window, "58-extract");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ADocumentIsSplitInFrontOfTheSelectedPages()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(9), null);
        var tool = Tool<SplitTool>(vm);
        await Settle(() => vm.PageBitmap != null);
        var mode = Field<ModeField>(tool, "SplitMode");
        Assert.Equal((4, 2), (mode.Options.Count, mode.Columns));

        mode.Select("selected");
        Assert.Contains(tool.Fields.OfType<InfoField>(), field => field.IsVisible && field.Text == Loc.Get("SplitAtSelectedHint"));
        vm.SetSelection([3, 6], 3);
        Assert.Equal(Loc.Format("SummarySplit", 3, "1-3; 4-6; 7-9"), vm.ApplySummary);
        // The first page starts a file anyway; selecting it changes nothing.
        vm.SetSelection([0, 5], 5);
        Assert.Equal(Loc.Format("SummarySplit", 2, "1-5; 6-9"), vm.ApplySummary);
        Capture(window, "59-split");
        window.Close();
    }

    [AvaloniaFact]
    public async Task PasswordsCanBeShownWhileTheyAreTyped()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(1), null);
        var tool = Tool<ProtectTool>(vm);
        await Settle();
        var password = Field<TextField>(tool, "ProtectPassword");
        var again = Field<TextField>(tool, "ProtectConfirm");
        Assert.Equal(('•', '•'), (password.PasswordChar, again.PasswordChar));

        Field<BoolField>(tool, "ProtectShow").Value = true;
        Assert.Equal((default(char), default(char)), (password.PasswordChar, again.PasswordChar));
        Field<BoolField>(tool, "ProtectShow").Value = false;
        Assert.Equal('•', password.PasswordChar);
        window.Close();
    }

    [AvaloniaFact]
    public async Task HowASheetIsDividedAndOrderedIsChosenFromDrawings()
    {
        var (window, vm) = Start();
        using var sheet = SheetTests.FourOnOneSheet(1, 2, 3, 4);
        vm.Session.Open(TestPdf.FromImage(sheet, 150), null);
        var tool = Tool<SheetTool>(vm);
        await Settle(() => vm.Objects.Count == 4 && vm.PageBitmap != null);
        FitPage(window, vm);

        var layout = Field<ModeField>(tool, "SheetLayout");
        var order = Field<ModeField>(tool, "SheetOrder");
        Assert.All(layout.Options.Concat(order.Options), option => Assert.True(option.HasIcon));
        // What the drawing that is pressed stands for is written out under the row of them.
        Assert.Equal(Loc.Get("SheetLayoutAuto"), layout.Caption);
        layout.Select("two");
        Assert.Equal(Loc.Get("SheetLayoutTwo"), layout.Caption);
        await Settle(() => vm.Objects.Count == 2);
        order.Select("rtl");
        Assert.Equal(Loc.Get("SheetOrderRtl"), order.Caption);
        await Settle();
        Capture(window, "60-sheet-drawings");
        window.Close();
    }

    [AvaloniaFact]
    public async Task EveryPanelIsBuiltFromControlsThatFitIt()
    {
        // The panels of all tools with a document open, as pictures to look at, in the light and in the dark.
        var (window, vm) = Start();
        using var book = SheetPictures.BookOnScanner(left: 90, top: 60, right: 130, bottom: 150, block: 14, noise: 6);
        vm.Session.Open(PdfPages.Merge([TestPdf.FromImage(book, 150), Letter(heading: "A letter")]), null);
        await Settle(() => vm.PageBitmap != null);
        FitPage(window, vm);
        var options = window.FindControl<ScrollViewer>("OptionsViewer")!;
        foreach (var tool in vm.Tools)
        {
            vm.SelectedTool = tool;
            // A tool's options start at their head, wherever those of the tool before were scrolled to.
            Assert.Equal(0, options.Offset.Y);
            await Settle(() => !vm.IsBusy);
            options.Offset = new Avalonia.Vector(0, 240);
            // Nothing in a panel is a choice hidden behind a click where it could be shown: boxes that drop down
            // are left to the lists that are long.
            foreach (var choice in AllFields(tool).OfType<ChoiceField>().Where(field => field is not ModeField))
                Assert.True(choice.Options.Count >= 5 || choice.Options.Any(option => option.Label.Length > 24),
                    $"{tool.Id}: “{choice.Label}” has {choice.Options.Count} short choices and could show them all");
            options.Offset = default;
            await Settle();
            Capture(window, "panel-" + tool.Id);
            options.Offset = new Avalonia.Vector(0, 240);
        }

        window.Close();
    }
}
