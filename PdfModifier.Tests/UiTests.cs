using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Localization;
using PdfModifier.Services;
using PdfModifier.Tests;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using PdfModifier.Views;

[assembly: AvaloniaTestApplication(typeof(UiTestApp))]

namespace PdfModifier.Tests;

public static class UiTestApp
{
    // Real Skia drawing (instead of the headless stub) so frames can be captured as pictures.
    public static AppBuilder BuildAvaloniaApp()
    {
        // Tests must never read or write the preferences of the person running them.
        Settings.FilePath = Path.Combine(Path.GetTempPath(), "PdfModifier.Tests", Guid.NewGuid().ToString("N"), "settings.json");
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>
/// Drives the real window without a screen. Set PDFMODIFIER_SCREENSHOTS to a folder to keep pictures of each state.
/// </summary>
public class UiTests
{
    internal static readonly string? ScreenshotFolder = Environment.GetEnvironmentVariable("PDFMODIFIER_SCREENSHOTS");

    internal static (MainWindow Window, MainViewModel ViewModel) Start(string language = "en")
    {
        Loc.SetLanguage(language);
        var viewModel = new MainViewModel(new Settings { UiLanguage = language });
        var window = new MainWindow { DataContext = viewModel, Width = 1440, Height = 900 };
        window.Show();
        return (window, viewModel);
    }

    /// <summary>Lets background rendering finish and the results arrive on the UI thread.</summary>
    internal static async Task Settle(Func<bool>? until = null, int seconds = 6)
    {
        for (var i = 0; i < seconds * 25; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(40);
            if (i > 4 && (until?.Invoke() ?? true)) break;
        }

        Dispatcher.UIThread.RunJobs();
    }

    internal static void Capture(MainWindow window, string name)
    {
        if (string.IsNullOrEmpty(ScreenshotFolder)) return;
        Directory.CreateDirectory(ScreenshotFolder);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ScreenshotFolder, name + ".png"));
    }

    internal static T Tool<T>(MainViewModel vm) where T : Tool
    {
        var tool = vm.Tools.OfType<T>().Single();
        vm.SelectedTool = tool;
        return tool;
    }

    /// <summary>All fields of a tool, including those that share a row.</summary>
    internal static IEnumerable<Field> AllFields(Tool tool) =>
        tool.Fields.SelectMany(field => field is RowField row ? row.Fields : [field]);

    /// <summary>The field of a tool that is labelled with the given text key. Tests run in English.</summary>
    internal static T Field<T>(Tool tool, string labelKey, int which = 0) where T : Field =>
        AllFields(tool).OfType<T>().Where(f => f.Label == Strings.English[labelKey]).ElementAt(which);

    internal static void Press(Tool tool, string labelKey) => Field<ActionField>(tool, labelKey).Command.Execute(null);

    [AvaloniaFact]
    public async Task StartsEmptyAndListsEveryTool()
    {
        var (window, vm) = Start();
        await Settle();

        Assert.False(vm.HasDocument);
        Assert.Equal(23, vm.Tools.Count);
        Assert.Equal(5, vm.Tools.Count(t => t.StartsGroup));
        Capture(window, "01-empty");
        window.Close();
    }

    [AvaloniaFact]
    public async Task OpeningADocumentShowsItsPagesAndDetectsTheText()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.WithText("Invoice 2026-104", "Second page", "Third page"), null);
        await Settle(() => vm.PageBitmap != null && vm.Overlays.Count > 0 && vm.Pages[0].Thumbnail != null);

        Assert.Equal(3, vm.Pages.Count);
        Assert.Equal("1 / 3", vm.PageLabel);
        Assert.IsType<EditTool>(vm.SelectedTool);
        Assert.Equal("Invoice 2026-104", Assert.Single(vm.Overlays).Text);
        Assert.NotNull(vm.Pages[0].Thumbnail);
        Capture(window, "02-document-edit");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClickingDetectedTextAndApplyingChangesTheDocument()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.WithText("Hello world"), null);
        var edit = Tool<EditTool>(vm);
        await Settle(() => vm.Overlays.Count > 0);

        await vm.OverlayClicked(vm.Overlays[0]);
        var text = Field<TextField>(edit, "EditSelectedText");
        Assert.True(text.IsVisible);
        Assert.Equal("Hello world", text.Value);
        Capture(window, "03-text-selected");

        text.Value = "Hello Avalonia, привет!";
        Press(edit, "EditApplyChange");
        await Settle(() => !vm.IsBusy && vm.Overlays.Count > 0 && vm.CanUndo);

        Assert.Equal("Hello Avalonia, привет!", TestPdf.Text(vm.Session.Bytes!, 0));
        Assert.True(vm.Session.IsDirty);

        await vm.UndoCommand.ExecuteAsync(null);
        await Settle(() => vm.Overlays.Count > 0);
        Assert.Equal("Hello world", TestPdf.Text(vm.Session.Bytes!, 0));
        Assert.True(vm.CanRedo);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ToolsApplyToTheSelectedPagesAndCanBeUndone()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(4), null);
        await Settle(() => vm.PageBitmap != null);

        var rotate = Tool<RotateTool>(vm);
        vm.SetSelection([1, 2], 1);
        Press(rotate, "RotateRightShort");
        await Settle(() => !vm.IsBusy && vm.CanUndo);
        Assert.Equal([0, 90, 90, 0], PdfPages.GetPageInfos(vm.Session.Bytes!).Select(p => p.RotationDegrees));

        Tool<DeleteTool>(vm);
        vm.SetSelection([0], 0);
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy);
        Assert.Equal(3, vm.Pages.Count);
        Assert.Equal("Page 2", TestPdf.Text(vm.Session.Bytes!, 0));

        await vm.UndoCommand.ExecuteAsync(null);
        await vm.UndoCommand.ExecuteAsync(null);
        await Settle();
        Assert.Equal(4, vm.Pages.Count);
        Assert.All(PdfPages.GetPageInfos(vm.Session.Bytes!), p => Assert.Equal(0, p.RotationDegrees));
        window.Close();
    }

    [AvaloniaFact]
    public async Task WatermarkCanBeAddedFoundAndRemovedThroughTheTools()
    {
        var (window, vm) = Start();
        var original = TestPdf.Numbered(2);
        vm.Session.Open(original, null);
        await Settle(() => vm.PageBitmap != null);

        Tool<WatermarkTool>(vm);
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy);
        Assert.Contains("CONFIDENTIAL", TestPdf.Text(vm.Session.Bytes!, 1));
        Capture(window, "04-watermark");

        var remove = Tool<RemoveWatermarkTool>(vm);
        Press(remove, "WatermarkDetect");
        await Settle(() => !vm.IsBusy && remove.Fields.OfType<CheckListField>().First().Items.Count > 0);
        var found = remove.Fields.OfType<CheckListField>().First();
        Assert.True(Assert.Single(found.Items).IsChecked);
        Capture(window, "05-watermark-found");

        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy);
        Assert.Equal(0, TestPdf.Difference(original, vm.Session.Bytes!), 0.0001);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ScannedPagesAreReadAutomaticallyAndCanBeRetyped()
    {
        if (!PdfOcr.EngineStatus().Available || TessData.Missing(["en"]).Count > 0) return;
        var (window, vm) = Start();
        using var scan = TestPdf.FakeScan(1240, 1754);
        vm.Session.Open(TestPdf.FromImage(scan, 150), null);
        var edit = Tool<EditTool>(vm);
        await Settle(() => vm.Overlays.Count > 10, 90);

        Assert.True(vm.Overlays.Count >= 15, $"{vm.Overlays.Count} lines detected");
        Assert.All(vm.Overlays, o => Assert.True(o.IsScan));
        Assert.Contains("quick brown fox", vm.Overlays[0].Text);
        // Reading the page does not stop the window from being used.
        Assert.False(vm.IsBusy);
        Capture(window, "06-scan-detected");

        var before = vm.Overlays.Count;
        var line = vm.Overlays[0];
        await vm.OverlayClicked(line);
        Field<TextField>(edit, "EditSelectedText").Value = "This line was retyped";
        Press(edit, "EditApplyChange");
        await Settle(() => vm.Objects.Count == 2, 30);

        // The old line is painted over and the new one set on top; both stay objects that can be adjusted.
        var patch = Assert.IsType<PagePatch>(vm.Objects[0].Model);
        var typed = Assert.IsType<PageText>(vm.Objects[1].Model);
        Assert.Equal("This line was retyped", typed.Text);
        Assert.InRange(typed.FontSize, 12, 24); // 36 px letters at 150 dpi are about 17 pt
        Assert.True((typed.Color & 0xFF) < 90, $"ink colour {typed.Color:X8} should be dark like the scanned text");
        Assert.True(patch.Bounds.Contains(line.Bounds.CenterX, line.Bounds.CenterY));
        Assert.Same(vm.Objects[1], vm.SelectedObject);
        Assert.Equal(before - 1, vm.Overlays.Count);
        Capture(window, "07-scan-edited");

        await vm.FlushTool();
        Assert.Contains("This line was retyped", TestPdf.Text(vm.Session.Bytes!, 0));
        Assert.Equal(2, PdfPageObjects.Read(vm.Session.Bytes!, 0).Count);
        // Writing it did not disturb what is on screen: same objects, still selected, the other lines still on offer.
        Assert.Equal(2, vm.Objects.Count);
        Assert.Same(vm.Objects[1], vm.SelectedObject);
        Assert.Equal(before - 1, vm.Overlays.Count);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("tr")]
    public async Task TheInterfaceSwitchesLanguage(string language)
    {
        var (window, vm) = Start(language);
        vm.Session.Open(TestPdf.Numbered(2), null);
        var settings = Tool<SettingsTool>(vm);
        await Settle(() => vm.PageBitmap != null);

        Assert.Equal(Strings.For(language)["ToolSettings"], settings.Title);
        Assert.Equal(Strings.For(language)["Open"], Application.Current!.Resources["T_Open"]);
        Assert.NotEqual(Strings.English["ToolMerge"], vm.Tools.OfType<MergeTool>().Single().Title);
        Capture(window, "08-settings-" + language);

        Loc.SetLanguage("en");
        Assert.Equal("Settings", settings.Title);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PreferencesAreSavedOnlyWhenTheyChange()
    {
        var (window, vm) = Start();
        var settings = Tool<SettingsTool>(vm);
        await Settle();
        if (File.Exists(Settings.FilePath)) File.Delete(Settings.FilePath);

        // Switching the interface language re-announces every field; that alone must not write anything.
        Loc.SetLanguage("de");
        Loc.SetLanguage("en");
        Assert.False(File.Exists(Settings.FilePath));

        var languages = settings.Fields.OfType<CheckListField>().Single();
        languages.Items.Single(i => i.Key == "ru").IsChecked = true;
        Assert.Equal(["en", "ru"], vm.Settings.OcrLanguages);
        Assert.Equal(["en", "ru"], Settings.Load().OcrLanguages);

        // Unticking everything falls back to English rather than leaving no language at all.
        foreach (var item in languages.Items) item.IsChecked = false;
        Assert.Equal(["en"], Settings.Load().OcrLanguages);
        Assert.StartsWith(Path.GetTempPath(), Settings.FilePath);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EveryToolPanelCanBeShown()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(2), null);
        await Settle(() => vm.PageBitmap != null);

        foreach (var tool in vm.Tools)
        {
            vm.SelectedTool = tool;
            await Settle(() => !vm.IsBusy);
            Assert.False(string.IsNullOrWhiteSpace(tool.Title));
            Assert.DoesNotContain("Tool" + tool.Id, tool.Title);
            Assert.DoesNotContain("Hint", tool.Description[^Math.Min(4, tool.Description.Length)..]);
            // Every field has a proper label, not the name of a text that is missing.
            Assert.All(AllFields(tool), field => Assert.DoesNotMatch("^[A-Z][a-z]+[A-Z][A-Za-z]+$", field.Label));
            Capture(window, "tool-" + tool.Id);
        }

        window.Close();
    }
}
