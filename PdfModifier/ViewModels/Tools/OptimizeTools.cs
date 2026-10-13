using Avalonia.Input;
using PdfModifier.Core;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using PdfModifier.Localization;

namespace PdfModifier.ViewModels.Tools;

/// <summary>Forwards progress on the calling thread, unlike <see cref="Progress{T}"/>, which posts it elsewhere.</summary>
internal sealed class DirectProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}

/// <summary>
/// Makes files smaller by recompressing their images: the open document, with the effect shown on the page before it
/// is applied, or every PDF of a folder, with a list of what each file weighed before and after.
/// </summary>
public sealed class CompressTool : Tool
{
    /// <summary>Where the compressed files of a folder go unless another place is chosen.</summary>
    internal const string OutputFolderName = "compressed";

    private readonly ModeField _target;
    private readonly FolderField _folder;
    private readonly BoolField _subfolders;
    private readonly ChoiceField _output;
    private readonly ChoiceField _preset;
    private readonly NumberField _dpi, _quality;
    private readonly BoolField _grayscale, _removeMetadata;
    private readonly ActionField _measure;
    private readonly InfoField _report;

    /// <summary>What became of one file of the folder.</summary>
    private sealed record Outcome(string Name, long Before, long After, string? Problem);

    public CompressTool(MainViewModel main) : base("Compress", "GroupOptimize")
    {
        _target = Add(new ModeField("CompressTarget", new Choice("document", "CompressTargetDocument"), new Choice("folder", "CompressTargetFolder")));
        _folder = Add(new FolderField("CompressFolder").WithPicker(main));
        _subfolders = Add(new BoolField("CompressSubfolders"));
        _output = Add(new ChoiceField("CompressOutput",
            new Choice("sub", "CompressOutputSub"), new Choice("other", "CompressOutputOther"), new Choice("replace", "CompressOutputReplace")));
        _preset = Add(new ChoiceField("CompressPreset",
            new Choice("balanced", "CompressBalanced"), new Choice("strong", "CompressStrong"),
            new Choice("light", "CompressLight"), new Choice("custom", "CompressCustom")));
        _dpi = Add(new NumberField("CompressDpi", 150, 36, 600, 10));
        _quality = Add(new SliderField("ImageQuality", 70, 10, 100, 1));
        _grayscale = Add(new BoolField("CompressGrayscale"));
        _removeMetadata = Add(new BoolField("CompressRemoveMetadata"));
        Add(new InfoField()).SetKey("CompressNote");
        // The preview of a folder: what every file would weigh, without writing anything.
        _measure = Add(new ActionField("CompressMeasure", () => main.RunBusy(context => RunFolder(context, write: false), false)));
        _report = Add(new InfoField());
        _preset.PropertyChanged += (_, _) => Update();
        _target.PropertyChanged += (_, _) => Update();
        Update();
    }

    private bool WholeFolder => _target.Key == "folder";

    public override bool NeedsDocument => !WholeFolder;
    public override bool HasPreview => !WholeFolder;
    public override string ApplyLabel => Loc.Get(WholeFolder ? "CompressFolderRun" : "Apply");

    private void Update()
    {
        _dpi.IsVisible = _quality.IsVisible = _preset.Key == "custom";
        _folder.IsVisible = _subfolders.IsVisible = _output.IsVisible = _measure.IsVisible = WholeFolder;
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(ApplyLabel));
        OnPropertyChanged(nameof(NeedsDocument));
    }

    public CompressOptions Options()
    {
        var (dpi, quality) = _preset.Key switch
        {
            "strong" => (100, 50),
            "light" => (200, 85),
            "custom" => ((int)_dpi.Number, (int)_quality.Number),
            _ => (150, 70)
        };
        return new CompressOptions { MaxImageDpi = dpi, JpegQuality = quality, Grayscale = _grayscale.Value, RemoveMetadata = _removeMetadata.Value };
    }

    /// <summary>Shows what the images of the page look like after compression, which is what the setting is weighed against.</summary>
    public override PreviewResult Preview(PreviewRequest request)
    {
        var result = PdfCompress.Compress(request.Page, Options() with { RemoveMetadata = false }, cancel: request.Cancel);
        return result.NewSize >= result.OriginalSize
            ? new PreviewResult(null, Loc.Get("CompressPageNoGain"))
            : new PreviewResult(result.Pdf, Loc.Format("CompressPage", Units.Megabytes(result.OriginalSize), Units.Megabytes(result.NewSize)));
    }

    public override async Task RunAsync(ToolContext context)
    {
        if (WholeFolder)
        {
            await RunFolder(context, write: true);
            return;
        }

        var pdf = context.Pdf;
        var options = Options();
        var result = await context.Run(() => PdfCompress.Compress(pdf, options, context.Progress, context.Cancel));
        if (result.NewSize >= result.OriginalSize)
        {
            context.Status(Loc.Get("CompressNoGain"));
            return;
        }

        context.Commit(result.Pdf);
        context.Status(Loc.Format("CompressDone", Units.Megabytes(result.OriginalSize), Units.Megabytes(result.NewSize), $"{result.SavedRatio:P0}"));
    }

    /// <summary>
    /// Compresses every PDF of the chosen folder, or with <paramref name="write"/> off only works out what each would
    /// weigh. Either way the list of sizes before and after is shown.
    /// </summary>
    private async Task RunFolder(ToolContext context, bool write)
    {
        var folder = _folder.Path.Trim().Trim('"');
        if (folder.Length == 0 || !Directory.Exists(folder)) throw new UserException(Loc.Get("ErrChooseFolder"));
        folder = Path.GetFullPath(folder);
        var options = Options();
        var replace = _output.Key == "replace";
        var own = Path.Combine(folder, OutputFolderName);
        string? target = null;
        if (write && !replace)
        {
            target = _output.Key == "other" ? await context.Dialogs.PickFolder(Loc.Get("PickOutputFolder")) : own;
            if (target == null) return;
            target = Path.GetFullPath(target);
            // Written over their own sources the files would be replaced after all, without the question that goes with it.
            if (target.Equals(folder, StringComparison.OrdinalIgnoreCase)) throw new UserException(Loc.Get("ErrCompressSameFolder"));
        }

        var search = _subfolders.Value ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = await context.Run(() => Directory.EnumerateFiles(folder, "*.pdf", search)
            .Where(file => Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            // Not what an earlier run of this very tool has put there.
            .Where(file => !file.StartsWith(own + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(file => target == null || !file.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList());
        if (files.Count == 0) throw new UserException(Loc.Get("ErrCompressNoFiles"));
        if (write && replace && !await context.Dialogs.Confirm(Loc.Format("CompressReplaceConfirm", files.Count))) return;

        var outcomes = await context.Run(() =>
        {
            var done = new List<Outcome>();
            for (var i = 0; i < files.Count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                var index = i;
                var name = Path.GetRelativePath(folder, files[i]);
                long size = 0;
                try
                {
                    var bytes = File.ReadAllBytes(files[i]);
                    size = bytes.Length;
                    if (PdfPages.NeedsPassword(bytes))
                    {
                        done.Add(new Outcome(name, size, size, Loc.Get("CompressFileLocked")));
                        continue;
                    }

                    var result = PdfCompress.Compress(bytes, options, new DirectProgress(p => context.Progress.Report((index + p) / files.Count)), context.Cancel);
                    if (write && replace && result.NewSize < result.OriginalSize)
                    {
                        // Written next to the original first: a file cut short by a full disk must not take its place.
                        var fresh = files[i] + ".compressing";
                        File.WriteAllBytes(fresh, result.Pdf);
                        File.Move(fresh, files[i], true);
                    }
                    else if (write && !replace)
                    {
                        // Files that could not be made smaller go along as they are, so the folder written is complete.
                        var to = Path.Combine(target!, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.WriteAllBytes(to, result.Pdf);
                    }

                    done.Add(new Outcome(name, result.OriginalSize, result.NewSize, null));
                }
                catch (Exception e) when (e is PdfException or IOException or UnauthorizedAccessException)
                {
                    // One unreadable file should not stop the rest of the folder.
                    done.Add(new Outcome(name, size, size, e.Message));
                }

                context.Progress.Report((i + 1.0) / files.Count);
            }

            return done;
        });

        _report.SetText(Describe(outcomes, write, replace ? folder : target));
        long before = outcomes.Sum(o => o.Before), after = outcomes.Sum(o => o.After);
        context.Status(Loc.Format(write ? "CompressFolderDone" : "CompressFolderMeasured",
            outcomes.Count, Units.Megabytes(before), Units.Megabytes(after), Saved(before, after)));
    }

    private static string Saved(long before, long after) => before <= 0 ? "0 %" : $"{Math.Max(0, 1 - (double)after / before):P0}";

    /// <summary>The sizes before and after, file by file: the total first, then the files that gain most.</summary>
    private static string Describe(List<Outcome> outcomes, bool written, string? where)
    {
        long before = outcomes.Sum(o => o.Before), after = outcomes.Sum(o => o.After);
        var lines = new List<string>
        {
            Loc.Format(written ? "CompressFolderDone" : "CompressFolderMeasured", outcomes.Count, Units.Megabytes(before), Units.Megabytes(after), Saved(before, after))
        };
        if (written && where != null) lines.Add(Loc.Format("CompressFolderWhere", where));
        lines.Add("");
        const int most = 300;
        foreach (var outcome in outcomes.OrderByDescending(o => o.Before - o.After).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).Take(most))
            lines.Add(outcome switch
            {
                { Problem: { } problem } => $"{outcome.Name}: {problem}",
                _ when outcome.After >= outcome.Before => $"{outcome.Name}: {Units.Megabytes(outcome.Before)} · {Loc.Get("CompressFileNoGain")}",
                _ => $"{outcome.Name}: {Units.Megabytes(outcome.Before)} → {Units.Megabytes(outcome.After)} (−{Saved(outcome.Before, outcome.After)})"
            });
        if (outcomes.Count > most) lines.Add("…");
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Cleans up scanned pages. What is wrong with a page is chosen from a short list (an ordinary scan, blurry print,
/// faint or broken print, a photograph with shadows), which sets the single steps; each of them can then be adjusted,
/// with the page on view showing the result.
/// </summary>
public sealed class EnhanceTool : Tool
{
    private readonly ChoiceField _recipe;
    private readonly PagesField _pages;
    private readonly BoolField _deskew, _evenLight, _whiten, _sharpen, _denoise, _upscale, _binarize, _grayscale, _darken;
    private readonly SliderField _strength, _deblur, _thicken;
    private bool _applying;

    /// <summary>The settings a recipe stands for. Black-and-white and grayscale are a matter of taste and left as they are.</summary>
    private sealed record Recipe(bool EvenLight, int Whiten, int Deblur, int Thicken, bool Sharpen, bool Denoise, bool Upscale);

    private static readonly Dictionary<string, Recipe> Recipes = new()
    {
        ["standard"] = new Recipe(false, 30, 0, 0, true, false, false),
        // Focus first, and only a light hand on the paper: the thin strokes of blurred print are barely darker than it.
        ["blurry"] = new Recipe(false, 10, 45, 20, false, false, true),
        ["faint"] = new Recipe(true, 20, 0, 60, true, false, false),
        ["photo"] = new Recipe(true, 35, 15, 0, true, true, false)
    };

    public EnhanceTool() : base("Enhance", "GroupOptimize")
    {
        _recipe = Add(new ChoiceField("EnhanceRecipe",
            new Choice("standard", "EnhanceRecipeStandard"), new Choice("blurry", "EnhanceRecipeBlurry"),
            new Choice("faint", "EnhanceRecipeFaint"), new Choice("photo", "EnhanceRecipePhoto"), new Choice("custom", "EnhanceRecipeCustom")));
        _pages = Add(new PagesField());
        _deskew = Add(new BoolField("EnhanceDeskew", true));
        _evenLight = Add(new BoolField("EnhanceEvenLight"));
        _whiten = Add(new BoolField("EnhanceWhiten", true));
        _strength = Add(new SliderField("EnhanceStrength", 30, 0, 100, 1));
        _deblur = Add(new SliderField("EnhanceDeblur", 0, 0, 100, 5));
        _thicken = Add(new SliderField("EnhanceThicken", 0, 0, 100, 5));
        _sharpen = Add(new BoolField("EnhanceSharpen", true));
        _denoise = Add(new BoolField("EnhanceDenoise"));
        _upscale = Add(new BoolField("EnhanceUpscale"));
        _grayscale = Add(new BoolField("EnhanceGrayscale"));
        _binarize = Add(new BoolField("EnhanceBinarize"));
        _darken = Add(new BoolField("EnhanceDarkenText", true));
        Add(new InfoField()).SetKey("EnhanceNote");
        _whiten.PropertyChanged += (_, _) => _strength.IsVisible = _whiten.Value;
        _recipe.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChoiceField.Selected) && !_applying) Use(_recipe.Key);
        };
        // A step changed by hand makes the settings one's own.
        foreach (var field in new Field[] { _evenLight, _whiten, _strength, _deblur, _thicken, _sharpen, _denoise, _upscale })
            field.Edited += () =>
            {
                if (_applying || _recipe.Key == "custom") return;
                _applying = true;
                _recipe.Select("custom");
                _applying = false;
            };
    }

    private void Use(string key)
    {
        if (!Recipes.TryGetValue(key, out var recipe)) return;
        _applying = true;
        try
        {
            _evenLight.Value = recipe.EvenLight;
            _whiten.Value = true;
            _strength.Set(recipe.Whiten);
            _deblur.Set(recipe.Deblur);
            _thicken.Set(recipe.Thicken);
            _sharpen.Value = recipe.Sharpen;
            _denoise.Value = recipe.Denoise;
            _upscale.Value = recipe.Upscale;
        }
        finally
        {
            _applying = false;
        }
    }

    public EnhanceOptions Options() => new()
    {
        Deskew = _deskew.Value,
        EvenLight = _evenLight.Value,
        WhitenBackground = _whiten.Value,
        WhitenStrength = _strength.Number / 100,
        Deblur = _deblur.Number / 100,
        Thicken = _thicken.Number / 100,
        Sharpen = _sharpen.Value,
        Denoise = _denoise.Value,
        Upscale = _upscale.Value,
        Grayscale = _grayscale.Value,
        Binarize = _binarize.Value,
        DarkenText = _darken.Value
    };

    public override bool HasPreview => true;

    public override PreviewResult Preview(PreviewRequest request)
    {
        if (!request.Includes(_pages)) return new PreviewResult(null, Loc.Get("PreviewOutside"));
        var result = PdfEnhance.Enhance(request.Page, null, Options(), cancel: request.Cancel);
        return result.ImagesEnhanced + result.TextObjectsDarkened == 0
            ? new PreviewResult(null, Loc.Get("EnhanceNothing"))
            : new PreviewResult(result.Pdf);
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var options = Options();
        var result = await context.Run(() => PdfEnhance.Enhance(pdf, pages, options, context.Progress, context.Cancel));
        if (result.ImagesEnhanced + result.TextObjectsDarkened == 0)
        {
            context.Status(Loc.Get("EnhanceNothing"));
            return;
        }

        context.Commit(result.Pdf);
        context.Status(Loc.Format("EnhanceDone", result.ImagesEnhanced, result.TextObjectsDarkened));
    }
}

/// <summary>
/// Makes scanned pages searchable. Before the whole document is run, the page on view can be read to see what would
/// go in: lines that were more guessed at than read show in red. For those there are two remedies: reading everything
/// with care, which is slower, and marking the passages concerned so that just they are read with all the care there is.
/// </summary>
public sealed class OcrTool : Tool
{
    private readonly MainViewModel _main;
    private readonly PagesField _pages;
    private readonly BoolField _force, _careful;
    private readonly InfoField _status;
    private readonly ActionField _mark, _unmark;

    // Passages marked for a careful reading, by page, as frames on the page view. They belong to one state of the
    // document: any change to it may move the pages they are on.
    private readonly Dictionary<int, List<CanvasObject>> _areas = [];
    private byte[]? _document;
    private bool _drawing, _syncing;

    public OcrTool(MainViewModel main) : base("Ocr", "GroupOptimize")
    {
        _main = main;
        _pages = Add(new PagesField());
        _force = Add(new BoolField("OcrForce"));
        _careful = Add(new BoolField("OcrCareful", main.Settings.OcrCareful));
        _status = Add(new InfoField());
        Add(new ActionField("OcrShow", () => main.RunBusy(ShowFound)));
        Add(new InfoField()).SetKey("OcrAreaHint");
        _mark = new ActionField("OcrAreaAdd", () =>
        {
            StartDrawing();
            return Task.CompletedTask;
        });
        _unmark = new ActionField("OcrAreaRemove", () =>
        {
            RemoveSelected();
            return Task.CompletedTask;
        });
        Add(new RowField(_mark, _unmark));
        _careful.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BoolField.Value) || _syncing || main.Settings.OcrCareful == _careful.Value) return;
            main.Settings.OcrCareful = _careful.Value;
            main.Settings.Save();
        };
        _unmark.IsVisible = false;
    }

    public override CanvasMode CanvasMode => _drawing ? CanvasMode.Region : CanvasMode.Select;

    private IReadOnlyDictionary<int, IReadOnlyList<RectD>> Marked() => _areas
        .Where(pair => pair.Value.Count > 0)
        .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<RectD>)pair.Value.Select(frame => frame.Model.Bounds).ToList());

    /// <summary>
    /// Reads the page on view and marks every line that was found, without changing the document: a look at what
    /// making it searchable would put in, before running it over the whole document.
    /// </summary>
    private async Task ShowFound(ToolContext context)
    {
        if (OcrSetup.Problem(context.Settings) is { } problem) throw new UserException(problem);
        var view = context.Session.View!;
        var page = context.CurrentPage;
        var settings = OcrSetup.From(context.Settings);
        var force = _force.Value;
        var marked = Marked().GetValueOrDefault(page) ?? [];
        context.Status(Loc.Get("StatusDetecting"));
        var lines = await context.Run(() =>
        {
            var content = view.Analyse(page);
            var whole = force || PdfOcr.NeedsReading(content);
            if (!whole && marked.Count == 0) return null;
            var found = whole ? PdfOcr.RecognizePage(view, page, settings, context.Cancel).Lines.ToList() : [];
            foreach (var area in marked)
            {
                found.RemoveAll(line => area.Contains(line.Bounds.CenterX, line.Bounds.CenterY));
                found.AddRange(PdfOcr.RecognizeArea(view, page, area, settings, context.Cancel).Lines);
            }

            return found;
        });

        var main = context.Main;
        main.Overlays.Clear();
        if (lines == null)
        {
            context.Status(Loc.Get("OcrPageHasText"));
            return;
        }

        foreach (var line in lines) main.Overlays.Add(OcrSetup.Highlight(line));
        var doubtful = main.Overlays.Count(overlay => overlay.Doubtful);
        context.Status(doubtful == 0 ? Loc.Format("OcrShown", lines.Count) : Loc.Format("OcrShownDoubt", lines.Count, doubtful));
    }

    public override Task RefreshAsync(MainViewModel main)
    {
        StopDrawing();
        _syncing = true;
        _careful.Value = main.Settings.OcrCareful;
        _syncing = false;
        _status.SetText(OcrSetup.Problem(main.Settings)
                        ?? Loc.Format("OcrLanguagesInUse", OcrSetup.LanguageNames(main.Settings.OcrLanguages)));
        if (!ReferenceEquals(main.Session.Bytes, _document))
        {
            _areas.Clear();
            _document = main.Session.Bytes;
        }

        main.Objects.Clear();
        foreach (var frame in _areas.GetValueOrDefault(main.CurrentPageIndex) ?? []) main.Objects.Add(frame);
        _mark.IsVisible = main.HasDocument;
        _unmark.IsVisible = false;
        return Task.CompletedTask;
    }

    public override void Deactivated(MainViewModel main) => StopDrawing();

    // ---- passages to read with care -----------------------------------------------------------------------------

    private void StartDrawing()
    {
        if (!_main.HasDocument) return;
        _drawing = true;
        OnPropertyChanged(nameof(CanvasMode));
        _main.StatusText = Loc.Get("OcrAreaDraw");
    }

    private void StopDrawing()
    {
        if (!_drawing) return;
        _drawing = false;
        OnPropertyChanged(nameof(CanvasMode));
    }

    public override Task RegionSelected(MainViewModel main, RectD region)
    {
        if (!_drawing) return Task.CompletedTask;
        StopDrawing();
        if (region.Width < 6 || region.Height < 6) return Task.CompletedTask;
        if (!_areas.TryGetValue(main.CurrentPageIndex, out var frames)) _areas[main.CurrentPageIndex] = frames = [];
        var frame = new CanvasObject(new SheetArea { Id = frames.Select(f => f.Id).DefaultIfEmpty().Max() + 1, Bounds = region });
        frames.Add(frame);
        main.Objects.Add(frame);
        main.SelectedObject = frame;
        // What was shown as found no longer says what this passage will give.
        main.Overlays.Clear();
        OnSettingsChanged();
        return Task.CompletedTask;
    }

    public override void ObjectSelected(MainViewModel main, CanvasObject? obj) => _unmark.IsVisible = obj?.Model is SheetArea;

    public override void ObjectTransformed(MainViewModel main, CanvasObject obj, RectD bounds, double rotation, double scale, bool done)
    {
        if (obj.Model is not SheetArea area) return;
        bounds = new RectD(Math.Max(0, bounds.Left), Math.Max(0, bounds.Top), Math.Min(main.PageWidth, bounds.Right), Math.Min(main.PageHeight, bounds.Bottom));
        if (bounds.Width >= 6 && bounds.Height >= 6) obj.Model = area with { Bounds = bounds };
    }

    private void RemoveSelected()
    {
        if (_main.SelectedObject is not { Model: SheetArea } selected) return;
        _areas.GetValueOrDefault(_main.CurrentPageIndex)?.Remove(selected);
        _main.Objects.Remove(selected);
        _main.SelectedObject = null;
        OnSettingsChanged();
    }

    public override bool KeyPressed(MainViewModel main, Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.Escape when _drawing:
                StopDrawing();
                return true;
            case Key.Delete or Key.Back when main.SelectedObject?.Model is SheetArea:
                RemoveSelected();
                return true;
            default:
                return false;
        }
    }

    public override string? Summary(MainViewModel main)
    {
        var marked = Marked();
        return marked.Count == 0 ? null : Loc.Format("OcrAreasSummary", marked.Values.Sum(areas => areas.Count), marked.Count);
    }

    public override async Task RunAsync(ToolContext context)
    {
        if (OcrSetup.Problem(context.Settings) is { } problem) throw new UserException(problem);
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var settings = OcrSetup.From(context.Settings);
        var force = _force.Value;
        var marked = ReferenceEquals(pdf, _document) ? Marked() : new Dictionary<int, IReadOnlyList<RectD>>();
        var (result, recognised) = await context.Run(() =>
            PdfOcr.MakeSearchable(pdf, pages, settings, force, context.Progress, context.Cancel, marked));
        if (recognised == 0)
        {
            context.Status(Loc.Get("OcrNothing"));
            return;
        }

        context.Commit(result);
        context.Status(Loc.Format("OcrDone", recognised));
    }
}

/// <summary>
/// Runs a chain of clean-up steps unattended, either on pages of the open document or on a whole batch of files.
/// </summary>
public sealed class AutomateTool : Tool
{
    private readonly ChoiceField _target;
    private readonly PagesField _pages;
    private readonly FileListField _files;
    private readonly BoolField _autoCrop, _enhance, _binarize, _watermarks, _flatten, _ocr, _compress, _metadata;

    public AutomateTool(MainViewModel main) : base("Automate", "GroupOptimize")
    {
        _target = Add(new ChoiceField("AutoTarget", new Choice("current", "AutoCurrent"), new Choice("files", "AutoFiles")));
        _pages = Add(new PagesField());
        _files = Add(new FileListField("AutoInputFiles").WithPicker(main));
        Add(new InfoField()).SetKey("AutoSteps");
        _enhance = Add(new BoolField("AutoEnhance", true));
        _binarize = Add(new BoolField("EnhanceBinarize"));
        _watermarks = Add(new BoolField("AutoWatermarks"));
        _autoCrop = Add(new BoolField("AutoCrop"));
        _flatten = Add(new BoolField("AutoFlatten"));
        _ocr = Add(new BoolField("AutoOcr"));
        _compress = Add(new BoolField("AutoCompress"));
        _metadata = Add(new BoolField("AutoMetadata"));
        _target.PropertyChanged += (_, _) => Update();
        _enhance.PropertyChanged += (_, _) => Update();
        Update();
    }

    public override bool NeedsDocument => false;
    public override string ApplyLabel => Loc.Get("AutoRun");
    public override bool HasPreview => _target.Key == "current";

    /// <summary>
    /// The steps that change how the page looks, run on the page on view. Recognition and metadata show nothing, and
    /// watermarks are only recognised across pages, so those are left to the real run.
    /// </summary>
    public override PreviewResult Preview(PreviewRequest request)
    {
        if (!request.Includes(_pages)) return new PreviewResult(null, Loc.Get("PreviewOutside"));
        var result = Process(request.Page, null, null, new SynchronousProgress(_ => { }), request.Cancel, visibleOnly: true);
        return new PreviewResult(result, _ocr.Value || _watermarks.Value || _metadata.Value ? Loc.Get("AutoPreviewNote") : null);
    }

    private void Update()
    {
        OnPropertyChanged(nameof(HasPreview));
        _pages.IsVisible = _target.Key == "current";
        _files.IsVisible = _target.Key == "files";
        _binarize.IsVisible = _enhance.Value;
    }

    public override async Task RunAsync(ToolContext context)
    {
        if (!new[] { _autoCrop, _enhance, _watermarks, _flatten, _ocr, _compress, _metadata }.Any(step => step.Value))
            throw new UserException(Loc.Get("AutoNoSteps"));
        if (_ocr.Value && OcrSetup.Problem(context.Settings) is { } problem) throw new UserException(problem);
        var ocrSettings = OcrSetup.From(context.Settings);

        if (_target.Key == "current")
        {
            var pdf = context.Pdf;
            var pages = context.Resolve(_pages);
            context.Commit(await context.Run(() => Process(pdf, pages, ocrSettings, context.Progress, context.Cancel)));
            context.Status(Loc.Get("AutoDone"));
            return;
        }

        if (_files.Files.Count == 0) throw new UserException(Loc.Get("ErrChooseFile"));
        var folder = await context.Dialogs.PickFolder(Loc.Get("PickOutputFolder"));
        if (folder == null) return;
        var files = _files.Files.ToList();
        var failed = new List<string>();
        await context.Run(() =>
        {
            for (var i = 0; i < files.Count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                var index = i;
                var step = new SynchronousProgress(p => context.Progress.Report((index + p) / files.Count));
                try
                {
                    var result = Process(File.ReadAllBytes(files[i]), null, ocrSettings, step, context.Cancel);
                    // Never write over an input file: outputs landing next to their source get a suffix.
                    var target = Path.Combine(folder, Path.GetFileName(files[i]));
                    if (Path.GetFullPath(target).Equals(Path.GetFullPath(files[i]), StringComparison.OrdinalIgnoreCase))
                        target = Path.Combine(folder, Path.GetFileNameWithoutExtension(files[i]) + "_processed.pdf");
                    File.WriteAllBytes(target, result);
                }
                catch (Exception e) when (e is Core.Pdfium.PdfException or IOException or UnauthorizedAccessException)
                {
                    // One unreadable or protected file should not stop the rest of the batch.
                    failed.Add(Path.GetFileName(files[i]));
                }
            }

            return true;
        });

        context.Status(failed.Count == 0
            ? Loc.Format("StatusFilesWritten", files.Count, folder)
            : Loc.Format("AutoSomeFailed", files.Count - failed.Count, files.Count, string.Join(", ", failed)));
    }

    private byte[] Process(byte[] pdf, IReadOnlyCollection<int>? pages, OcrSettings? ocr, IProgress<double> progress, CancellationToken cancel,
        bool visibleOnly = false)
    {
        var steps = new List<Func<byte[], IProgress<double>, byte[]>>();
        if (_watermarks.Value && !visibleOnly) steps.Add((d, _) => PdfWatermarkRemoval.Remove(d, pages).Pdf);
        if (_enhance.Value)
        {
            var options = new EnhanceOptions { Binarize = _binarize.Value };
            steps.Add((d, p) => PdfEnhance.Enhance(d, pages, options, p, cancel).Pdf);
        }

        if (_flatten.Value) steps.Add((d, _) => PdfPages.Flatten(d, pages));
        // Recognition reads the cleaned-up picture, and cropping comes after it so the text layer needs no adjusting.
        if (_ocr.Value && ocr != null) steps.Add((d, p) => PdfOcr.MakeSearchable(d, pages, ocr, false, p, cancel).Pdf);
        if (_autoCrop.Value) steps.Add((d, _) => PdfPages.AutoCrop(d, pages));
        if (_compress.Value) steps.Add((d, p) => PdfCompress.Compress(d, null, p, cancel).Pdf);
        if (_metadata.Value && !visibleOnly) steps.Add((d, _) => PdfMetadataOps.RemoveAll(d));

        for (var i = 0; i < steps.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var index = i;
            pdf = steps[i](pdf, new SynchronousProgress(p => progress.Report((index + p) / steps.Count)));
            progress.Report((i + 1.0) / steps.Count);
        }

        return pdf;
    }

    /// <summary>Forwards progress on the calling thread, unlike <see cref="Progress{T}"/>, which posts it elsewhere.</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
