using System.Diagnostics;
using System.Text;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Text;
using PdfModifier.Localization;
using PdfModifier.Services;
using SkiaSharp;

namespace PdfModifier.ViewModels.Tools;

public sealed class CompareTool : Tool
{
    private readonly FileField _other;
    private readonly BoolField _show;
    private readonly InfoField _result;
    private byte[]? _otherBytes;
    private byte[]? _comparedWith;

    public CompareTool(MainViewModel main) : base("Compare", "GroupReview")
    {
        _other = Add(new FileField("CompareOther").WithPicker(main));
        _show = Add(new BoolField("CompareShow", true));
        _result = Add(new InfoField());
        _result.SetKey("CompareHint");
        _show.PropertyChanged += (_, _) => _ = main.RenderPage();
    }

    public override string ApplyLabel => Loc.Get("CompareRun");

    public override async Task RunAsync(ToolContext context)
    {
        if (_other.Path.Length == 0) throw new UserException(Loc.Get("ErrChooseFile"));
        var first = context.Pdf;
        var second = await FieldSetup.ReadPdf(context, _other.Path);
        var result = await context.Run(() => PdfCompare.Compare(first, second, context.Progress, context.Cancel));
        (_otherBytes, _comparedWith) = (second, first);
        _result.SetText(Summarise(result));
        context.Status(result.Identical ? Loc.Get("CompareIdentical") : Loc.Format("CompareDifferent", result.DifferentPages));
        await context.Main.RenderPage();
    }

    private static string Summarise(ComparisonResult result)
    {
        if (result.Identical) return Loc.Get("CompareIdentical");
        var text = new StringBuilder();
        text.AppendLine(Loc.Format("CompareDifferent", result.DifferentPages));
        if (result.FirstPageCount != result.SecondPageCount)
            text.AppendLine(Loc.Format("ComparePageCounts", result.FirstPageCount, result.SecondPageCount));
        text.AppendLine(Loc.Get("CompareLegend"));

        foreach (var page in result.Pages.Where(p => !p.Identical).Take(200))
        {
            text.AppendLine();
            if (!page.InSecond) text.AppendLine(Loc.Format("ComparePageOnlyFirst", page.PageIndex + 1));
            else if (!page.InFirst) text.AppendLine(Loc.Format("ComparePageOnlySecond", page.PageIndex + 1));
            else text.AppendLine(Loc.Format("ComparePage", page.PageIndex + 1, $"{page.PixelDifference:P1}"));
            foreach (var change in page.TextChanges.Take(40))
            {
                var words = change.Text.Length > 160 ? change.Text[..160] + "…" : change.Text;
                text.AppendLine((change.Kind == TextChangeKind.Removed ? "− " : "+ ") + words);
            }
        }

        return text.ToString().TrimEnd();
    }

    public override SKBitmap? RenderPage(MainViewModel main, int pageIndex, double scale)
    {
        // The overlay is only meaningful for the exact document that was compared.
        if (!_show.Value || _otherBytes is not { } other || _comparedWith is not { } compared) return null;
        if (!ReferenceEquals(main.Session.Bytes, compared) || !ReferenceEquals(main.SelectedTool, this)) return null;
        return PdfCompare.RenderDifference(compared, other, pageIndex, scale);
    }
}

public sealed class ProtectTool : Tool
{
    private readonly TextField _password, _confirm;

    public ProtectTool() : base("Protect", "GroupReview")
    {
        _password = Add(new TextField("ProtectPassword") { IsPassword = true });
        _confirm = Add(new TextField("ProtectConfirm") { IsPassword = true });
        // For whoever wants to see what was typed before a file is locked with it.
        var show = Add(new BoolField("ProtectShow"));
        show.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BoolField.Value)) _password.Reveal = _confirm.Reveal = show.Value;
        };
        Add(new InfoField()).SetKey("ProtectNote");
    }

    public override string ApplyLabel => Loc.Get("ProtectSave");

    public override async Task RunAsync(ToolContext context)
    {
        if (_password.Value.Length == 0) throw new UserException(Loc.Get("ErrPasswordEmpty"));
        if (_password.Value != _confirm.Value) throw new UserException(Loc.Get("ErrPasswordMismatch"));
        var pdf = context.Pdf;
        var password = _password.Value;
        var path = await context.Dialogs.SaveFile(Loc.Get("ToolProtect"), Units.DocumentName(context.Session) + "_protected.pdf", Dialogs.PdfFiles);
        if (path == null) return;
        await context.Run(() =>
        {
            File.WriteAllBytes(path, PdfSecurity.Protect(pdf, password));
            return true;
        });
        _password.Value = _confirm.Value = "";
        context.Status(Loc.Format("StatusSaved", Path.GetFileName(path)));
    }
}

public sealed class SettingsTool : Tool
{
    private readonly ChoiceField _language;
    private readonly CheckListField _ocrLanguages;
    private readonly BoolField _autoDetect, _notify;
    private readonly InfoField _engine, _update;
    private readonly ActionField _setup, _menuAdd, _menuRemove, _getUpdate;
    private readonly MainViewModel _main;
    private bool _syncing;

    public SettingsTool(MainViewModel main) : base("Settings", "GroupApp")
    {
        _main = main;
        var settings = main.Settings;

        // Only there when a newer version is out: what it is, and the way to it.
        _update = Add(new InfoField { Warns = true });
        _getUpdate = Add(new ActionField("UpdateGet", () =>
        {
            Process.Start(new ProcessStartInfo(AppInfo.LatestVersionPage) { UseShellExecute = true });
            return Task.CompletedTask;
        }));
        _getUpdate.IsVisible = false;

        _language = Add(new ChoiceField("SettingsLanguage", Languages.All.Select(l => new Choice(l.Code, l.NativeName, true)).ToArray()));
        _language.Select(settings.UiLanguage);
        _language.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChoiceField.Selected) || _language.Key == Loc.Language) return;
            settings.UiLanguage = _language.Key;
            settings.Save();
            Loc.SetLanguage(_language.Key);
            RefreshLanguageList();
            RefreshUpdateNote();
        };

        _ocrLanguages = Add(new CheckListField("SettingsOcrLanguages"));
        foreach (var language in Languages.All)
        {
            var item = new CheckItem(language.Code, language.NativeName, settings.OcrLanguages.Contains(language.Code));
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(CheckItem.IsChecked)) return;
                var chosen = _ocrLanguages.CheckedKeys.ToList();
                // At least one language is always needed; English is the default.
                settings.OcrLanguages = chosen.Count > 0 ? chosen : [Languages.Default];
                settings.Save();
            };
            _ocrLanguages.Items.Add(item);
        }

        _autoDetect = Add(new BoolField("SettingsAutoDetect", settings.AutoDetectLanguage));
        _autoDetect.PropertyChanged += (_, e) =>
        {
            // Fields also announce language changes; only a changed value is worth saving.
            if (e.PropertyName != nameof(BoolField.Value) || settings.AutoDetectLanguage == _autoDetect.Value) return;
            settings.AutoDetectLanguage = _autoDetect.Value;
            settings.Save();
        };

        Add(new ActionField("SettingsDownload", () => main.RunBusy(Download, false)));
        _engine = Add(new InfoField());
        _setup = Add(OcrSetup.Button(main));
        _menuAdd = Add(new ActionField("MenuAdd", () => ChangeMenuEntry(true)));
        _menuRemove = Add(new ActionField("MenuRemove", () => ChangeMenuEntry(false)));
        _notify = Add(new BoolField("UpdateNotify", settings.UpdateNotice == true));
        _notify.PropertyChanged += (_, e) =>
        {
            // Ticked or cleared by hand, this is the user's answer; the app goes by it from its next start on.
            if (e.PropertyName != nameof(BoolField.Value) || _syncing || (settings.UpdateNotice == true) == _notify.Value) return;
            settings.UpdateNotice = _notify.Value;
            settings.Save();
        };
        Add(new InfoField()).SetText($"{AppInfo.Name} {AppInfo.Version}");
        Add(new InfoField()).SetKey("AboutText");
        Add(new ActionField("AboutSupport", () =>
        {
            Process.Start(new ProcessStartInfo(AppInfo.DonationUrl) { UseShellExecute = true });
            return Task.CompletedTask;
        })).IsVisible = AppInfo.DonationUrl.Length > 0;
    }

    public override bool NeedsDocument => false;
    public override bool HasApply => false;

    public override Task RefreshAsync(MainViewModel main)
    {
        RefreshLanguageList();
        RefreshUpdateNote();
        // The answer may have been given since, to the question the app asks at its first start.
        _syncing = true;
        _notify.Value = main.Settings.UpdateNotice == true;
        _syncing = false;
        return Task.CompletedTask;
    }

    private void RefreshUpdateNote()
    {
        _update.SetText(_main.NewVersion is { } version ? Loc.Format("UpdateAvailable", version, AppInfo.Version) : "");
        _getUpdate.IsVisible = _main.HasNewVersion;
    }

    private void RefreshLanguageList()
    {
        var (available, detail) = PdfOcr.EngineStatus();
        foreach (var item in _ocrLanguages.Items)
        {
            // Without an engine there is nothing to say about a language yet.
            var name = Languages.Get(item.Key).NativeName;
            item.Label = !available ? name
                : PdfOcr.MissingLanguages([item.Key]).Count == 0 ? Loc.Format("SettingsLanguageReady", name)
                : Loc.Format("SettingsLanguageMissing", name);
        }

        // What the engine itself lacks is said in plain words, with a button that fetches it.
        var lack = available ? null : _main.Supplies.Lack();
        _engine.SetText(available ? Loc.Format("SettingsEngineOk", detail) : lack?.Text ?? Loc.Get("ErrOcrUnavailable") + "\n" + detail);
        _setup.IsVisible = lack is { CanFetch: true };
        _menuAdd.IsVisible = DesktopEntry.IsSupported && !DesktopEntry.Exists();
        _menuRemove.IsVisible = DesktopEntry.IsSupported && DesktopEntry.IsOwn;
    }

    private async Task Download(ToolContext context)
    {
        // The engine comes first: without it no language can be read, downloaded or not.
        if (_main.Supplies.Lack() is { Key: "runtime" or "program" }) await _main.Supplies.Ensure(context, confirmed: true);
        if (!await _main.Supplies.DownloadLanguages(context)) context.Status(Loc.Get("SettingsNothingToDownload"));
        context.Main.RefreshWhenIdle();
    }

    /// <summary>Linux: puts the app into the application menu, or takes out the entry it put there.</summary>
    private Task ChangeMenuEntry(bool add)
    {
        try
        {
            if (!add) DesktopEntry.Remove();
            else if (DesktopEntry.Program is { } program) DesktopEntry.Add(program);
            _main.StatusText = Loc.Format(add ? "MenuAdded" : "MenuRemoved", AppInfo.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _main.StatusText = MainViewModel.Describe(e);
        }

        RefreshLanguageList();
        return Task.CompletedTask;
    }
}

public static class ToolCatalog
{
    public static IReadOnlyList<Tool> Create(MainViewModel main) =>
    [
        new EditTool(main),
        new WatermarkTool(main),
        new RemoveWatermarkTool(main),
        new PageNumbersTool(main),
        new FlattenTool(),
        new MetadataTool(main),

        new MergeTool(main),
        new SplitTool(),
        new SheetTool(main),
        new RotateTool(main),
        new DeleteTool(),
        new ReorderTool(main),
        new InsertTool(main),
        new ExtractTool(),
        new ResizeTool(),
        new CropTool(main),

        new CompressTool(main),
        new EnhanceTool(),
        new OcrTool(main),
        new AutomateTool(main),

        new CompareTool(main),
        new ProtectTool(),

        new SettingsTool(main)
    ];
}
