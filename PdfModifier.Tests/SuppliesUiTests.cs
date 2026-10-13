using Avalonia.Headless.XUnit;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Localization;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>
/// What text recognition lacks on a computer is fetched by the app, after asking. The tests stand in for the person
/// who answers and for the downloads and installers; what is lacking is made up, so that neither the data folder of
/// whoever runs the tests nor their computer is touched.
/// </summary>
public class SuppliesUiTests
{
    private static bool OcrReady => PdfOcr.EngineStatus().Available && TessData.Missing(["en"]).Count == 0;

    /// <summary>A window on a scanned page, with English made out to be lacking until it has been "downloaded".</summary>
    private static (Views.MainWindow Window, MainViewModel ViewModel, List<string> Questions, List<string> Fetched) OnAScan(bool answer)
    {
        var (window, vm) = Start();
        var lacking = true;
        var questions = new List<string>();
        var fetched = new List<string>();
        vm.Supplies.MissingLanguages = codes => lacking ? codes.ToList() : [];
        vm.Supplies.DownloadLanguage = (code, progress, _) =>
        {
            fetched.Add(code);
            lacking = false;
            progress?.Report(1);
            return Task.CompletedTask;
        };
        vm.Supplies.Ask = question =>
        {
            questions.Add(question);
            return Task.FromResult(answer);
        };
        using var scan = TestPdf.FakeScan(1240, 1754);
        vm.Session.Open(TestPdf.FromImage(scan, 150), null);
        return (window, vm, questions, fetched);
    }

    [AvaloniaFact]
    public async Task LanguageDataThatIsLackingIsAskedForFetchedAndThePageIsReadAfterAll()
    {
        if (!OcrReady) return;
        var (window, vm, questions, fetched) = OnAScan(answer: true);
        Tool<EditTool>(vm);
        await Settle(() => vm.Overlays.Count > 10, 90);

        // One question, naming what is to be downloaded; then the download, then the reading it was needed for.
        Assert.Contains("English", Assert.Single(questions));
        Assert.Equal(["en"], fetched);
        Assert.True(vm.Overlays.Count >= 15, $"{vm.Overlays.Count} lines detected");
        Assert.False(vm.IsBusy);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ANotNowIsTakenAtItsWordAndTheButtonFetchesWithoutAskingAgain()
    {
        if (!OcrReady) return;
        var (window, vm, questions, fetched) = OnAScan(answer: false);
        var edit = Tool<EditTool>(vm);
        await Settle(() => questions.Count > 0 && !vm.IsBusy, 20);

        Assert.Single(questions);
        Assert.Empty(fetched);
        Assert.Empty(vm.Overlays);
        var button = Field<ActionField>(edit, "SetupButton");
        Assert.True(button.IsVisible);

        // Coming upon the same lack again, by turning to another tool and back, does not bring the question back.
        Tool<OcrTool>(vm);
        await Settle();
        Tool<EditTool>(vm);
        await Settle();
        Assert.Single(questions);

        // The button says what it does, so pressing it is the yes.
        button.Command.Execute(null);
        await Settle(() => vm.Overlays.Count > 10, 90);
        Assert.Single(questions);
        Assert.Equal(["en"], fetched);
        Assert.True(vm.Overlays.Count >= 15, $"{vm.Overlays.Count} lines detected");
        Assert.False(button.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WhatTheEngineRunsOnIsInstalledAfterAskingAndThenTheLanguageData()
    {
        if (!OcrReady) return;
        var (window, vm, questions, fetched) = OnAScan(answer: true);
        var need = OcrNeed.Runtime;
        var installed = 0;
        vm.Supplies.Need = () => need;
        vm.Supplies.InstallRuntime = _ =>
        {
            installed++;
            need = OcrNeed.Nothing;
            return Task.FromResult(true);
        };
        Tool<EditTool>(vm);
        await Settle(() => vm.Overlays.Count > 10, 90);

        // First what the engine cannot run without, then the language: each asked for by name.
        Assert.Equal(2, questions.Count);
        Assert.Equal(Strings.English["SetupRuntimeAsk"], questions[0]);
        Assert.Contains("English", questions[1]);
        Assert.Equal(1, installed);
        Assert.Equal(["en"], fetched);
        Assert.True(vm.Overlays.Count >= 15, $"{vm.Overlays.Count} lines detected");
        window.Close();
    }

    [AvaloniaFact]
    public async Task WorkThatNeedsRecognitionSaysWhatIsLackingWhenItCannotBeHad()
    {
        var (window, vm) = Start();
        var questions = new List<string>();
        var context = new ToolContext(vm, new Progress<double>(), CancellationToken.None);
        vm.Supplies.Ask = question =>
        {
            questions.Add(question);
            return Task.FromResult(true);
        };

        // An installer that does not get through: the lack stays and is said in plain words.
        vm.Supplies.Need = () => OcrNeed.Program;
        vm.Supplies.InstallProgram = _ => Task.FromResult(false);
        var failed = await Assert.ThrowsAsync<UserException>(() => vm.Supplies.Ensure(context));
        Assert.Equal(Strings.English["SetupProgramFailed"], failed.Message);
        Assert.Equal(Strings.English["SetupProgramAsk"], Assert.Single(questions));

        // A no ends the work without a complaint.
        vm.Supplies.Ask = _ => Task.FromResult(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => vm.Supplies.Ensure(context));

        // With nobody there to ask, nothing is fetched and the reason is given.
        vm.Supplies.Ask = null;
        var unattended = await Assert.ThrowsAsync<UserException>(() => vm.Supplies.Ensure(context));
        Assert.Equal(Strings.English["SetupProgramMissing"], unattended.Message);
        window.Close();
    }
}
