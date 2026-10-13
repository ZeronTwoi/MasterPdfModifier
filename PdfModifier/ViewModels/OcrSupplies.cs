using Avalonia.Threading;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Text;
using PdfModifier.Localization;
using PdfModifier.ViewModels.Tools;

namespace PdfModifier.ViewModels;

/// <summary>
/// Something text recognition lacks on this computer: what to say about it, how to ask whether it should be fetched,
/// and how it is fetched. Without a way to fetch it there is nothing the app can do about it.
/// </summary>
public sealed record OcrLack(string Key, string Text, string Question, Func<ToolContext, Task>? Fetch)
{
    public bool CanFetch => Fetch != null;
}

/// <summary>
/// Gets what text recognition lacks: language data, and where the engine itself cannot run, what that is missing
/// (the Microsoft Visual C++ runtime on Windows, the Tesseract program on Linux). Nobody has to go and install
/// anything by hand, and nothing is fetched without asking first.
/// </summary>
public sealed class OcrSupplies(MainViewModel main)
{
    private readonly HashSet<string> _declined = [];
    private bool _offering;

    /// <summary>How the user is asked a question with a yes and a no. Null where nobody is there to answer.</summary>
    public Func<string, Task<bool>>? Ask { get; set; }

    // What is looked at and what is done. The tests put their own in place of the real thing.
    internal Func<OcrNeed> Need { get; set; } = OcrEngine.Need;
    internal Func<ToolContext, Task<bool>> InstallRuntime { get; set; } = InstallRuntimeFromMicrosoft;
    internal Func<CancellationToken, Task<bool>> InstallProgram { get; set; } = InstallTesseract;
    internal Func<IEnumerable<string>, IReadOnlyList<string>> MissingLanguages { get; set; } = PdfOcr.MissingLanguages;
    internal Func<string, IProgress<double>?, CancellationToken, Task> DownloadLanguage { get; set; } = TessData.DownloadAsync;

    /// <summary>What is lacking right now; null when recognition can run in the languages that are ticked.</summary>
    public OcrLack? Lack()
    {
        switch (Need())
        {
            case OcrNeed.Runtime:
                return new OcrLack("runtime", Loc.Get("SetupRuntimeMissing"), Loc.Get("SetupRuntimeAsk"), FetchRuntime);
            case OcrNeed.Program:
                return new OcrLack("program", Loc.Get("SetupProgramMissing"), Loc.Get("SetupProgramAsk"), FetchProgram);
        }

        var (available, detail) = PdfOcr.EngineStatus();
        if (!available) return new OcrLack("engine", Loc.Get("ErrOcrUnavailable") + "\n" + detail, "", null);
        var missing = MissingLanguages(main.Settings.OcrLanguages);
        if (missing.Count == 0) return null;
        var names = OcrSetup.LanguageNames(missing);
        return new OcrLack("languages:" + string.Join('+', missing), Loc.Format("ErrOcrLanguages", names),
            Loc.Format("SetupLanguagesAsk", names), context => FetchLanguages(context, missing));
    }

    /// <summary>
    /// Sees to it that recognition can run, for work the user has started: asks about what is lacking, fetches it and
    /// returns. When that is not to be, it says why (<see cref="UserException"/>), or that the answer was no
    /// (<see cref="OperationCanceledException"/>).
    /// </summary>
    /// <param name="confirmed">The user pressed a button that says what it fetches, so the question is not put again.</param>
    public async Task Ensure(ToolContext context, bool confirmed = false)
    {
        // Two things can be lacking one after the other: what the engine runs on, then the language data.
        for (var round = 0; round < 3 && Lack() is { } lack; round++)
        {
            if (lack.Fetch == null || (Ask == null && !confirmed)) throw new UserException(lack.Text);
            if (!confirmed && !await Ask!(lack.Question))
            {
                _declined.Add(lack.Key);
                throw new OperationCanceledException();
            }

            await lack.Fetch(context);
        }

        if (Lack() is { } still) throw new UserException(still.Text);
    }

    /// <summary>
    /// For reading that starts by itself, as when the editor comes upon a scan: asks about what is lacking, fetches it
    /// and has the tool look again. A "not now" is taken at its word until the app is closed; the buttons are there
    /// for a change of mind.
    /// </summary>
    public void Offer()
    {
        if (_offering || Ask == null || main.IsBusy) return;
        if (Lack() is not { CanFetch: true } lack || _declined.Contains(lack.Key)) return;
        _offering = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await main.RunBusy(async context =>
                {
                    await Ensure(context);
                    main.RefreshWhenIdle();
                }, false);
            }
            finally
            {
                _offering = false;
            }
        });
    }

    /// <summary>Fetches the data of the ticked languages that is not there yet. True when there was something to fetch.</summary>
    public async Task<bool> DownloadLanguages(ToolContext context)
    {
        var missing = MissingLanguages(main.Settings.OcrLanguages);
        if (missing.Count > 0) await FetchLanguages(context, missing);
        return missing.Count > 0;
    }

    private async Task FetchLanguages(ToolContext context, IReadOnlyList<string> missing)
    {
        for (var i = 0; i < missing.Count; i++)
        {
            var index = i;
            context.Status(Loc.Format("SettingsDownloading", Languages.Get(missing[i]).NativeName));
            var progress = new Progress<double>(p => context.Progress.Report((index + p) / missing.Count));
            await DownloadLanguage(missing[i], progress, context.Cancel);
        }

        context.Status(Loc.Get("SettingsDownloaded"));
    }

    private async Task FetchRuntime(ToolContext context)
    {
        context.Status(Loc.Get("SetupRuntimeDownloading"));
        if (!await InstallRuntime(context)) throw new UserException(Loc.Get("SetupRuntimeFailed"));
        OcrEngine.LookAgain();
        context.Status(Loc.Get("SetupDone"));
    }

    private async Task FetchProgram(ToolContext context)
    {
        context.Status(Loc.Get("SetupProgramInstalling"));
        if (!await InstallProgram(context.Cancel)) throw new UserException(Loc.Get("SetupProgramFailed"));
        OcrEngine.LookAgain();
        context.Status(Loc.Get("SetupDone"));
    }

    private static async Task<bool> InstallRuntimeFromMicrosoft(ToolContext context)
    {
        var installer = await VcRuntime.DownloadAsync(context.Progress, context.Cancel);
        if (!VcRuntime.IsFromMicrosoft(installer))
        {
            File.Delete(installer);
            throw new UserException(Loc.Get("SetupNotGenuine"));
        }

        context.Status(Loc.Get("SetupRuntimeInstalling"));
        return await VcRuntime.InstallAsync(installer, context.Cancel);
    }

    private static async Task<bool> InstallTesseract(CancellationToken cancel) =>
        LinuxPackages.ForTesseract() is { } command && await LinuxPackages.RunAsync(command, cancel);
}
