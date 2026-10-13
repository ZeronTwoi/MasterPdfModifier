using System.Runtime.CompilerServices;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using PdfModifier.Core.Text;
using PdfModifier.Localization;
using PdfModifier.Services;
using SkiaSharp;

namespace PdfModifier.ViewModels.Tools;

/// <summary>
/// Interactive editing. What the document already contains is found automatically, as text where it is text and by
/// recognising the page image where it is a scan, and changed line by line. What is added (text boxes, images,
/// patches that paint something over) stays an object: it can be moved, resized, turned, restyled and removed again,
/// also after the file was saved and reopened.
/// <para>
/// Objects are shown live on top of the page while they are worked on and written into the document a moment after
/// the last change, so nothing has to wait for the document while the mouse is moving or text is being typed.
/// </para>
/// </summary>
public sealed class EditTool : Tool
{
    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(600);

    private readonly MainViewModel _main;

    private readonly ModeField _mode, _align;
    private readonly InfoField _hint, _status;
    private readonly ChoiceField _source, _font;
    private readonly ActionField _detect, _download, _applyLine, _deleteLine, _liftLine, _readArea;
    private readonly BoolField _careful;
    private readonly TextField _lineText, _text;
    private readonly NumberField _size;
    private readonly ToggleField _bold, _italic;
    private readonly ColorField _color, _eraseColor, _patchColor;
    private readonly SliderField _rotation, _opacity;
    private readonly FileField _image;
    private readonly CheckListField _eraseWhat;
    private readonly RowField _style, _objectActions, _orderActions;

    // The objects of the page on view. This list, not the document, is what is true while they are being worked on.
    private readonly List<CanvasObject> _objects = [];
    private byte[]? _layerDocument;
    private int _layerPage = -1;
    private bool _dirty;
    private readonly List<RectD> _erase = [];
    private readonly SemaphoreSlim _writing = new(1, 1);
    private CancellationTokenSource? _writeDelay;

    private CanvasObject? _selected;
    private Overlay? _line;
    private bool _loadingFields;
    private double? _resizeFrom;
    private int _selectAfterRefresh;

    // Text recognised on pages already visited. It survives this tool's own edits but not changes made elsewhere.
    private readonly Dictionary<int, List<OcrLine>> _recognised = new();

    // Passages that were read once more with care, by page. What was found there stands in for whatever else the page
    // has for them: a search layer that is wrong, text inside a nested graphic.
    private readonly Dictionary<int, List<RectD>> _reread = new();
    private bool _reading, _syncing;
    private int _recognisedEpoch;
    private bool _ownCommit;
    private int _detection;

    private (int Page, SKBitmap Picture)? _snapshot;
    private ImagePixels? _armed;
    private readonly ConditionalWeakTable<ImagePixels, Bitmap> _pictures = new();

    public EditTool(MainViewModel main) : base("Edit", "GroupEdit")
    {
        _main = main;
        _mode = Add(new ModeField("",
            new Choice("select", "EditModeSelect"), new Choice("text", "EditModeAddText"),
            new Choice("image", "EditModeAddImage"), new Choice("erase", "EditModeErase")));
        _hint = Add(new InfoField());

        // Text the document already has, or that was found in the page image.
        _source = Add(new ChoiceField("EditSource",
            new Choice("auto", "EditSourceAuto"), new Choice("text", "EditSourceText"), new Choice("scan", "EditSourceScan")));
        _careful = Add(new BoolField("OcrCareful", main.Settings.OcrCareful));
        _status = Add(new InfoField());
        _download = Add(new ActionField("EditDownloadData", () => main.RunBusy(DownloadLanguages, false)));
        _detect = Add(new ActionField("EditDetectAgain", () =>
        {
            _recognised.Remove(main.CurrentPageIndex);
            _reread.Remove(main.CurrentPageIndex);
            return main.RefreshTool();
        }));
        _readArea = Add(new ActionField("EditReadArea", () =>
        {
            StartReadingArea();
            return Task.CompletedTask;
        }));
        _lineText = Add(new TextField("EditSelectedText", multiline: true));
        _applyLine = Add(new ActionField("EditApplyChange", () => ChangeLine(false)));
        _deleteLine = Add(new ActionField("EditDelete", () => ChangeLine(true)));
        _liftLine = Add(new ActionField("EditMakeMovable", LiftLine));

        // A text box: the one selected, or the next one to be drawn.
        _text = Add(new TextField("EditSelectedText", multiline: true));
        _font = Add(new ChoiceField("FontFamily", new Choice("", "FontDefault")));
        _size = new NumberField("FontSize", 12, 2, 800, 1, "0.#");
        _bold = new ToggleField("Bold", "B");
        _italic = new ToggleField("Italic", "I");
        _style = Add(new RowField(_size, _bold, _italic));
        _align = Add(new ModeField("TextAlign",
            new Choice("left", "AlignLeft"), new Choice("center", "AlignCenter"), new Choice("right", "AlignRight")));
        _color = Add(new ColorField("TextColor").WithPicker(main));

        _image = Add(new FileField("EditImageFile", true).WithPicker(main));
        _opacity = Add(new SliderField("Opacity", 100, 5, 100, 5));
        _rotation = Add(new SliderField("Rotation", 0, -180, 180, 1));

        _eraseWhat = Add(new CheckListField("EditEraseWhat"));
        _eraseColor = Add(new ColorField("EditEraseColor", "#FFFFFF", allowAutomatic: true).WithPicker(main));
        _patchColor = Add(new ColorField("EditPatchColor", "#FFFFFF").WithPicker(main));

        _objectActions = Add(new RowField(
            new ActionField("EditObjectDelete", () => Act(Remove)), new ActionField("EditObjectDuplicate", () => Act(Duplicate))));
        _orderActions = Add(new RowField(
            new ActionField("EditToFront", () => Act(obj => Restack(obj, true))), new ActionField("EditToBack", () => Act(obj => Restack(obj, false)))));

        FillEraseKinds();
        Loc.Changed += FillEraseKinds;

        _mode.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChoiceField.Selected)) return;
            if (_mode.Key != "select") main.SelectedObject = null;
            Update();
            OnPropertyChanged(nameof(CanvasMode));
            if (IsActive) _ = main.RefreshTool();
        };
        _source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChoiceField.Selected) && IsActive) _ = main.RefreshTool();
        };
        _image.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileField.Path)) _ = Arm(_image.Path);
        };
        _careful.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BoolField.Value) || _syncing || main.Settings.OcrCareful == _careful.Value) return;
            main.Settings.OcrCareful = _careful.Value;
            main.Settings.Save();
            // What was read the other way is read anew.
            _recognised.Clear();
            _reread.Clear();
            if (IsActive) _ = main.RefreshTool();
        };

        // Every style field shapes the selected object; with nothing selected it sets up the next one.
        foreach (var field in new Field[] { _text, _font, _size, _bold, _italic, _align, _color, _rotation, _opacity, _patchColor })
            field.Edited += ApplyFields;

        main.PropertyChanged += (_, e) =>
        {
            // Text is drawn for the zoom it is shown at; at another zoom it would be blurred or jagged.
            if (e.PropertyName == nameof(MainViewModel.PointScale) && IsActive)
                foreach (var obj in _objects.Where(o => o.Model is PageText))
                    Draw(obj);
        };

        // Listing the installed fonts means looking into each of them, which is not worth holding up the start for.
        Task.Run(PdfPageObjects.FontFamilies).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            _loadingFields = true;
            _font.SetOptions([new Choice("", "FontDefault"), ..task.Result.Select(family => new Choice(family, family, true))]);
            _loadingFields = false;
        }), TaskContinuationOptions.OnlyOnRanToCompletion);

        Update();
    }

    public override bool HasApply => false;
    public override bool HasPendingChanges => _dirty;

    public override CanvasMode CanvasMode => _mode.Key == "select" && !_reading ? CanvasMode.Select : CanvasMode.Region;

    private bool IsActive => ReferenceEquals(_main.SelectedTool, this);

    private void FillEraseKinds()
    {
        var chosen = _eraseWhat.Items.Count == 0 ? ["page", "text", "images"] : _eraseWhat.CheckedKeys.ToList();
        _eraseWhat.Items.Clear();
        foreach (var (key, label) in new[] { ("page", "EraseOriginal"), ("text", "EraseAddedText"), ("images", "EraseAddedImages") })
            _eraseWhat.Items.Add(new CheckItem(key, Loc.Get(label), chosen.Contains(key)));
    }

    // ---- what the panel shows -----------------------------------------------------------------------------------

    private void Update()
    {
        var mode = _mode.Key;
        var selecting = mode == "select";
        var model = selecting ? _selected?.Model : null;
        var line = selecting && model == null ? _line : null;
        var idle = selecting && model == null && line == null;

        _source.IsVisible = _detect.IsVisible = _status.IsVisible = _careful.IsVisible = _readArea.IsVisible = idle;
        if (!idle) _download.IsVisible = false;
        _lineText.IsVisible = _applyLine.IsVisible = _deleteLine.IsVisible = line != null;
        _liftLine.IsVisible = line?.Source is TextRun { Upright: true };

        var styling = model is PageText || mode == "text";
        _text.IsVisible = model is PageText;
        _font.IsVisible = _size.IsVisible = _bold.IsVisible = _italic.IsVisible = _align.IsVisible = _color.IsVisible = styling;

        _image.IsVisible = mode == "image";
        _opacity.IsVisible = model is PageImage || mode == "image";
        _rotation.IsVisible = model != null;
        _eraseWhat.IsVisible = _eraseColor.IsVisible = mode == "erase";
        _patchColor.IsVisible = model is PagePatch;
        foreach (var action in _objectActions.Fields.Concat(_orderActions.Fields)) action.IsVisible = model != null;

        _hint.SetKey(mode switch
        {
            "text" => "EditHintAddText",
            "image" => "EditHintAddImage",
            "erase" => "EditHintErase",
            _ => model != null ? "EditHintObject" : "EditHintSelect"
        });
    }

    public override void Deactivated(MainViewModel main)
    {
        _selected = null;
        _line = null;
        _writeDelay?.Cancel();
        StopReadingArea();
        Update();
    }

    /// <summary>The page as it was before anything was added: the objects are drawn on top of it live.</summary>
    public override SKBitmap? RenderPage(MainViewModel main, int pageIndex, double scale) =>
        main.Session.View?.Render(pageIndex, scale, withoutEditable: true);

    public override async Task RefreshAsync(MainViewModel main)
    {
        var document = main.Session.Bytes;
        var page = main.CurrentPageIndex;
        if (document == null)
        {
            _objects.Clear();
            (_layerDocument, _layerPage, _dirty) = (null, -1, false);
            Update();
            return;
        }

        if (!ReferenceEquals(document, _layerDocument) || page != _layerPage)
        {
            // Edits still waiting to be written belong to the page that was on view until now.
            await FlushAsync(main);
            if (main.Session.Bytes is not { } current) return;
            (document, page) = (current, main.CurrentPageIndex);

            // Somebody else changed the document: what was read from its pages may no longer be what is on them.
            if (!_ownCommit)
            {
                _recognised.Clear();
                _reread.Clear();
                _recognisedEpoch++;
            }

            _ownCommit = false;
            DropSnapshot();
            IReadOnlyList<PageObject> stored;
            try
            {
                stored = await Task.Run(() => PdfPageObjects.Read(current, page));
            }
            catch (ArgumentOutOfRangeException)
            {
                return; // the page went away meanwhile; the refresh for what replaced it is on its way
            }

            // Another page was chosen while this one was being read; the call for that page does the rest.
            if (!ReferenceEquals(main.Session.Bytes, document) || main.CurrentPageIndex != page) return;
            foreach (var old in _objects) Discard(old);
            _objects.Clear();
            _objects.AddRange(stored.Select(model => new CanvasObject(model)));
            foreach (var obj in _objects) Draw(obj);
            (_layerDocument, _layerPage, _dirty) = (document, page, false);
            _erase.Clear();
        }

        Show(main);
        StopReadingArea();
        // The other tool that reads scans may have changed how carefully that is done.
        _syncing = true;
        _careful.Value = main.Settings.OcrCareful;
        _syncing = false;
        _line = null;
        main.SelectedOverlay = null;
        if (_selectAfterRefresh != 0 && _objects.FirstOrDefault(o => o.Id == _selectAfterRefresh) is { } added) main.SelectedObject = added;
        _selectAfterRefresh = 0;
        Update();
        await Detect(main, document, page);
    }

    /// <summary>Puts the objects of the page into the page view, unless they are there already.</summary>
    private void Show(MainViewModel main)
    {
        if (main.Objects.SequenceEqual(_objects)) return;
        main.Objects.Clear();
        foreach (var obj in _objects) main.Objects.Add(obj);
    }

    // ---- finding the text of the page ---------------------------------------------------------------------------

    private async Task Detect(MainViewModel main, byte[] document, int page)
    {
        var detection = ++_detection;
        _download.IsVisible = false;
        if (_mode.Key != "select")
        {
            main.Overlays.Clear();
            return;
        }

        bool Current() => detection == _detection && IsActive && main.CurrentPageIndex == page;

        IReadOnlyList<TextRun> runs;
        PageContent content;
        try
        {
            if (main.Session.View is not { } view) return;
            (runs, content) = await Task.Run(() => (PdfTextEdit.GetTextRuns(document, page), view.Analyse(page)));
        }
        catch (Exception e) when (e is ObjectDisposedException or PdfException or ArgumentOutOfRangeException)
        {
            return; // the document was replaced meanwhile; whatever replaced it asks again
        }

        if (!Current()) return;
        var source = _source.Key;
        var found = runs.Select(run => new Overlay(run.Bounds, run.Text, run, false)).ToList();

        void Offer(IEnumerable<OcrLine> lines)
        {
            foreach (var line in lines)
            {
                // Not what a patch already hides (it was erased or retyped), and not what is on offer as text already.
                if (_objects.Any(o => o.Model is PagePatch patch && patch.Bounds.Contains(line.Bounds.CenterX, line.Bounds.CenterY))) continue;
                if (found.Any(o => o.Bounds.Contains(line.Bounds.CenterX, line.Bounds.CenterY))) continue;
                found.Add(OcrSetup.Highlight(line));
            }
        }

        // Passages read again on request come first: they were asked for because what else there is for them is wrong.
        var again = _reread.GetValueOrDefault(page) ?? [];
        bool ReadAgain(OcrLine line) => again.Any(area => area.Contains(line.Bounds.CenterX, line.Bounds.CenterY));
        if (again.Count > 0 && _recognised.TryGetValue(page, out var careful)) Offer(careful.Where(ReadAgain));

        // Text inside nested graphics is real text that cannot be changed in place: it is retyped over, like a scan.
        Offer(content.NestedText.Where(line => !ReadAgain(line)));
        // A scan that was made searchable before brings its text along; reading it again would only be slower.
        var layer = source == "auto" && content.HiddenText.Count > 0;
        if (layer) Offer(content.HiddenText.Where(line => !ReadAgain(line)));
        var read = source == "scan" || (source == "auto" && !layer && (content.HasPictures || found.Count == 0));

        void Publish(string? status)
        {
            main.Overlays.Clear();
            foreach (var overlay in found) main.Overlays.Add(overlay);
            if (status != null) _status.SetText(status);
        }

        string Counted()
        {
            var doubtful = found.Count(o => o.Doubtful);
            return doubtful == 0 ? Loc.Format("StatusTextFound", found.Count) : Loc.Format("StatusTextFoundDoubt", found.Count, doubtful);
        }

        if (!read || _recognised.ContainsKey(page))
        {
            if (read) Offer(_recognised[page]);
            Publish(found.Count == 0 ? Loc.Get("EditNothingFound") : Counted());
            return;
        }

        if (OcrSetup.Problem(main.Settings) is { } problem)
        {
            Publish(problem);
            // Missing language data can be fetched right here; there is no need to go looking for it in the settings.
            _download.IsVisible = _status.IsVisible && OcrSetup.CanDownload(main.Settings);
            return;
        }

        Publish(Loc.Get("StatusDetecting"));
        var epoch = _recognisedEpoch;
        var settings = OcrSetup.From(main.Settings);

        // What a first reading found is shown at once; if the page needs a closer look, that goes on in the
        // background and takes the place of the first result when it is done.
        var early = new List<Overlay>();

        void ShowEarly(OcrPageResult first)
        {
            if (!Current()) return;
            var before = found.Count;
            Offer(first.Lines);
            early.AddRange(found.Skip(before));
            foreach (var overlay in early) main.Overlays.Add(overlay);
            _status.SetText(Loc.Get("StatusDetectingMore"));
        }

        OcrPageResult? result = null;
        for (var attempt = 0; attempt < 3 && result == null; attempt++)
        {
            try
            {
                if (main.Session.View is not { } view) return;
                result = await Task.Run(() => PdfOcr.RecognizePage(view, page, settings, default,
                    first => Dispatcher.UIThread.Post(() => ShowEarly(first))));
            }
            catch (ObjectDisposedException)
            {
                // An edit of this tool was written while the page was being read; the page itself is unchanged.
            }
            catch (Exception e) when (e is OcrException or PdfException)
            {
                if (Current()) _status.SetText(MainViewModel.Describe(e));
                return;
            }
        }

        if (result == null) return;
        // Kept for when the page is visited again, unless the document has been changed from elsewhere since.
        if (epoch == _recognisedEpoch) _recognised[page] = result.Lines.ToList();
        if (!Current()) return;

        // The early lines make way for the final ones, except one that is being worked on right now.
        foreach (var overlay in early.Where(o => !ReferenceEquals(o, _line)))
        {
            found.Remove(overlay);
            main.Overlays.Remove(overlay);
        }

        var known = found.Count;
        Offer(result.Lines);
        foreach (var overlay in found.Skip(known)) main.Overlays.Add(overlay);
        var unsure = found.Count(o => o.Doubtful);
        _status.SetText(found.Count == 0 ? Loc.Get("EditNothingFound")
            : unsure == 0 ? Loc.Format("StatusDetected", found.Count(o => o.IsScan), OcrSetup.LanguageNames(result.DetectedLanguages))
            : Loc.Format("StatusDetectedDoubt", found.Count(o => o.IsScan), OcrSetup.LanguageNames(result.DetectedLanguages), unsure));
    }

    // ---- reading a passage again, with care ---------------------------------------------------------------------

    private void StartReadingArea()
    {
        if (!_main.HasDocument || _mode.Key != "select") return;
        _reading = true;
        OnPropertyChanged(nameof(CanvasMode));
        _status.SetText(Loc.Get("EditReadAreaHint"));
    }

    private void StopReadingArea()
    {
        if (!_reading) return;
        _reading = false;
        OnPropertyChanged(nameof(CanvasMode));
    }

    /// <summary>
    /// Reads what is inside an area once more, at a higher resolution and in every way there is. What it finds takes
    /// the place of the lines the area had before.
    /// </summary>
    private async Task ReadArea(MainViewModel main, RectD area)
    {
        StopReadingArea();
        if (area.Width < 6 || area.Height < 6 || main.Session.View is not { } view) return;
        if (OcrSetup.Problem(main.Settings) is { } problem)
        {
            _status.SetText(problem);
            _download.IsVisible = _status.IsVisible && OcrSetup.CanDownload(main.Settings);
            return;
        }

        var page = main.CurrentPageIndex;
        var epoch = _recognisedEpoch;
        var settings = OcrSetup.From(main.Settings);
        _status.SetText(Loc.Get("StatusReadingArea"));
        OcrPageResult? result = null;
        for (var attempt = 0; attempt < 3 && result == null; attempt++)
        {
            try
            {
                if (main.Session.View is not { } current) return;
                result = await Task.Run(() => PdfOcr.RecognizeArea(current, page, area, settings));
            }
            catch (ObjectDisposedException)
            {
                // An edit of this tool was written while the passage was being read; the page itself is unchanged.
            }
            catch (Exception e) when (e is OcrException or PdfException)
            {
                _status.SetText(MainViewModel.Describe(e));
                return;
            }
        }

        if (result == null || epoch != _recognisedEpoch) return;
        var lines = (_recognised.GetValueOrDefault(page) ?? []).Where(line => !area.Contains(line.Bounds.CenterX, line.Bounds.CenterY)).ToList();
        lines.AddRange(result.Lines);
        _recognised[page] = lines;
        if (!_reread.TryGetValue(page, out var areas)) _reread[page] = areas = [];
        areas.Add(area);
        if (!IsActive || main.CurrentPageIndex != page) return;
        await main.RefreshTool();
        var doubtful = result.Lines.Count(line => PdfOcr.Certainty(line) < PdfOcr.DoubtfulBelow);
        _status.SetText(result.Lines.Count == 0 ? Loc.Get("StatusAreaNothing")
            : doubtful == 0 ? Loc.Format("StatusAreaRead", result.Lines.Count)
            : Loc.Format("StatusAreaReadDoubt", result.Lines.Count, doubtful));
    }

    private async Task DownloadLanguages(ToolContext context)
    {
        var missing = TessData.Missing(context.Settings.OcrLanguages);
        for (var i = 0; i < missing.Count; i++)
        {
            var index = i;
            context.Status(Loc.Format("SettingsDownloading", Languages.Get(missing[i]).NativeName));
            var progress = new Progress<double>(p => context.Progress.Report((index + p) / missing.Count));
            await TessData.DownloadAsync(missing[i], progress, context.Cancel);
        }

        context.Status(Loc.Get("SettingsDownloaded"));
        context.Main.RefreshWhenIdle();
    }

    // ---- changing text the page already has ----------------------------------------------------------------------

    public override Task OverlaySelected(MainViewModel main, Overlay? overlay)
    {
        _line = overlay;
        if (overlay != null) main.SelectedObject = null;
        _lineText.Value = overlay?.Text ?? "";
        Update();
        return Task.CompletedTask;
    }

    private Task ChangeLine(bool delete)
    {
        var selected = _line ?? throw new UserException(Loc.Get("EditHintSelect"));
        var text = delete ? "" : _lineText.Value;
        return selected.Source is TextRun run ? ChangeRun(run, text) : Retype(selected, (OcrLine)selected.Source, text);
    }

    /// <summary>Real text is changed where it stands, in the document's own font as far as that has the letters.</summary>
    private Task ChangeRun(TextRun run, string text) => _main.RunBusy(async context =>
    {
        var pdf = context.Pdf;
        var page = context.CurrentPage;
        var result = await context.Run(() =>
        {
            var target = Locate(pdf, page, run);
            return PdfTextEdit.ApplyEdits(pdf, page, [text.Length == 0 ? new DeleteTextEdit(target.Id) : new ReplaceTextEdit(target.Id, text)]);
        });
        _ownCommit = true;
        context.Commit(result);
    });

    /// <summary>
    /// The run as it is numbered in the document as it is now. Writing waiting edits can renumber the runs (an erased
    /// line is gone from the list), so the one that was clicked is found again by what it says and where it stands.
    /// </summary>
    private static TextRun Locate(byte[] pdf, int page, TextRun wanted) =>
        PdfTextEdit.GetTextRuns(pdf, page)
            .Where(run => run.Text == wanted.Text)
            .OrderBy(run => Math.Abs(run.Bounds.Left - wanted.Bounds.Left) + Math.Abs(run.Bounds.Top - wanted.Bounds.Top))
            .FirstOrDefault() ?? throw new UserException(Loc.Get("EditHintSelect"));

    /// <summary>
    /// Text that is part of a picture (or of a nested graphic) cannot be changed, only replaced: the old line is
    /// painted over in the colour of the paper and the new text set on top, as two objects that stay adjustable.
    /// The size and colour of the new text are taken from the old; they can be corrected afterwards.
    /// </summary>
    private async Task Retype(Overlay overlay, OcrLine line, string text)
    {
        var main = _main;
        var area = new RectD(line.Bounds.Left - 1.5, line.Bounds.Top - 1.5, line.Bounds.Right + 1.5, line.Bounds.Bottom + 1.5);
        var (paper, ink) = await Sample(main, area);
        if (!ReferenceEquals(_line, overlay)) return;

        AddPatch(main, area, paper);
        if (_recognised.TryGetValue(_layerPage, out var known)) known.Remove(line);
        main.Overlays.Remove(overlay);
        _line = null;
        main.SelectedOverlay = null;

        if (text.Trim().Length > 0)
        {
            var box = new PageText { Id = NextId(), Text = text, FontFamily = Family(), Color = ink, Bold = _bold.Value, Italic = _italic.Value };
            // As tall as the old line, and narrower letters rather than a line that runs past where the old one ended.
            var size = Math.Max(3, line.Bounds.Height * 0.85);
            var natural = PdfPageObjects.NaturalWidth(box with { FontSize = size });
            if (natural > line.Bounds.Width && natural > 0) size *= Math.Max(0.6, line.Bounds.Width / natural);
            box = box with { FontSize = size };
            var width = PdfPageObjects.NaturalWidth(box) + 2;
            box = box with { Bounds = new RectD(line.Bounds.Left, 0, line.Bounds.Left + width, 1) };
            // The letters stand on the line the old ones stood on, about a fifth above the bottom of their box.
            var top = line.Bounds.Bottom - line.Bounds.Height * 0.2 - PdfPageObjects.BaselineOffset(box);
            box = PdfPageObjects.Fit(box with { Bounds = new RectD(line.Bounds.Left, top, line.Bounds.Left + width, top + 1) });
            main.SelectedObject = AddObject(main, box);
        }

        MarkDirty(main);
        Update();
    }

    /// <summary>
    /// Takes a line of real text out of the page content and puts a text box with the same words, look and place in
    /// its stead, which can then be moved and restyled like anything else that was added.
    /// </summary>
    private Task LiftLine()
    {
        if (_line?.Source is not TextRun { Upright: true } run) return Task.CompletedTask;
        return _main.RunBusy(async context =>
        {
            var pdf = context.Pdf;
            var page = context.CurrentPage;
            var box = new PageText
            {
                Id = NextId(), Text = run.Text, FontFamily = run.Family == "sans-serif" ? null : run.Family, FontSize = run.FontSize,
                Color = run.Color | 0xFF000000, Bold = run.Bold, Italic = run.Italic
            };
            box = box with { Bounds = new RectD(run.StartX, 0, run.StartX + PdfPageObjects.NaturalWidth(box) + 2, 1) };
            var top = run.Baseline - PdfPageObjects.BaselineOffset(box);
            box = PdfPageObjects.Fit(box with { Bounds = new RectD(box.Bounds.Left, top, box.Bounds.Right, top + 1) });
            var objects = _objects.Select(o => o.Model).Append(box).ToList();

            var result = await context.Run(() =>
            {
                var without = PdfTextEdit.ApplyEdits(pdf, page, [new DeleteTextEdit(Locate(pdf, page, run).Id)]);
                return PdfPageObjects.Write(without, page, objects);
            });
            (_ownCommit, _selectAfterRefresh) = (true, box.Id);
            context.Commit(result);
        });
    }

    // ---- objects ------------------------------------------------------------------------------------------------

    private int NextId() => _objects.Select(o => o.Id).DefaultIfEmpty(0).Max() + 1;

    private string? Family() => _font.Key.Length == 0 ? null : _font.Key;

    private uint TextColor() => _color.Argb ?? 0xFF000000;

    private CanvasObject AddObject(MainViewModel main, PageObject model, int? at = null)
    {
        var obj = new CanvasObject(model);
        Draw(obj);
        var index = Math.Clamp(at ?? _objects.Count, 0, _objects.Count);
        _objects.Insert(index, obj);
        main.Objects.Insert(Math.Min(index, main.Objects.Count), obj);
        return obj;
    }

    /// <summary>Patches go underneath everything else that was added: they are there to hide the page, not the additions.</summary>
    private void AddPatch(MainViewModel main, RectD area, uint color)
    {
        var under = _objects.TakeWhile(o => o.Model is PagePatch).Count();
        AddObject(main, new PagePatch { Id = NextId(), Bounds = area, Color = color | 0xFF000000 }, under);
        // Text of the page under the patch is taken out for good, or it would still be found by a search.
        _erase.Add(area);
    }

    /// <summary>Gives an object a new definition, redraws it if it looks different now, and notes that there is something to write.</summary>
    private void Change(MainViewModel main, CanvasObject obj, PageObject model, bool write = true)
    {
        if (model is PageText text) model = PdfPageObjects.Fit(text);
        var before = obj.Model;
        obj.Model = model;
        if (LooksDifferent(before, model)) Draw(obj);
        if (write) MarkDirty(main);
    }

    private static bool LooksDifferent(PageObject before, PageObject after) => (before, after) switch
    {
        (PageText a, PageText b) => a with { Bounds = b.Bounds, Rotation = b.Rotation } != b || Math.Abs(a.Bounds.Width - b.Bounds.Width) > 1e-6,
        (PageImage a, PageImage b) => !ReferenceEquals(a.Pixels, b.Pixels),
        _ => false
    };

    /// <summary>Makes the picture the page view shows for an object.</summary>
    private void Draw(CanvasObject obj)
    {
        switch (obj.Model)
        {
            case PageText text:
                var picture = PdfPageObjects.RenderText(text, _main.PointScale * _main.RenderScaling);
                var previous = obj.Picture as Bitmap;
                obj.PicturePadding = picture.Padding;
                obj.Picture = BitmapConverter.ToAvalonia(picture.Bitmap);
                picture.Bitmap.Dispose();
                previous?.Dispose();
                break;
            case PageImage image:
                // Copies of an image share its pixels and so its picture; a camera photo is shown reduced.
                obj.Picture = _pictures.GetValue(image.Pixels, pixels => BitmapConverter.ToAvalonia(pixels.Bitmap, 2400));
                break;
        }
    }

    private static void Discard(CanvasObject obj)
    {
        if (obj.Model is PageText && obj.Picture is Bitmap picture) picture.Dispose();
    }

    private void Remove(CanvasObject obj)
    {
        _objects.Remove(obj);
        _main.Objects.Remove(obj);
        if (ReferenceEquals(_main.SelectedObject, obj)) _main.SelectedObject = null;
        Discard(obj);
        MarkDirty(_main);
    }

    private void Duplicate(CanvasObject obj)
    {
        var b = obj.Model.Bounds;
        var copy = obj.Model with { Id = NextId(), Bounds = new RectD(b.Left + 12, b.Top + 12, b.Right + 12, b.Bottom + 12) };
        _main.SelectedObject = AddObject(_main, copy);
        MarkDirty(_main);
    }

    private void Restack(CanvasObject obj, bool toFront)
    {
        var target = toFront ? _objects.Count - 1 : 0;
        if (_objects.IndexOf(obj) == target) return;
        _objects.Remove(obj);
        _objects.Insert(target, obj);
        _main.Objects.Move(_main.Objects.IndexOf(obj), target);
        MarkDirty(_main);
    }

    private Task Act(Action<CanvasObject> action)
    {
        if (_selected is { } obj) action(obj);
        return Task.CompletedTask;
    }

    public override void ObjectSelected(MainViewModel main, CanvasObject? obj)
    {
        // A text box that was drawn but never typed into leaves nothing behind.
        if (_selected is { Model: PageText { Text: var typed } } left && !ReferenceEquals(left, obj) && string.IsNullOrWhiteSpace(typed) &&
            _objects.Remove(left))
        {
            main.Objects.Remove(left);
            Discard(left);
        }

        _selected = obj;
        _resizeFrom = null;
        if (obj != null)
        {
            _line = null;
            main.SelectedOverlay = null;
            LoadFields(obj.Model);
        }

        Update();
    }

    /// <summary>Shows the selected object's properties in the fields, without that counting as changing them.</summary>
    private void LoadFields(PageObject model)
    {
        _loadingFields = true;
        try
        {
            _rotation.Set(Math.Round(model.Rotation));
            switch (model)
            {
                case PageText text:
                    _text.Value = text.Text;
                    _font.Select(text.FontFamily ?? "");
                    _size.Set(text.FontSize);
                    _bold.Value = text.Bold;
                    _italic.Value = text.Italic;
                    _align.Select(text.Align switch { TextAlign.Center => "center", TextAlign.Right => "right", _ => "left" });
                    _color.Set(text.Color);
                    break;
                case PageImage image:
                    _opacity.Set(image.Opacity * 100);
                    break;
                case PagePatch patch:
                    _patchColor.Set(patch.Color);
                    break;
            }
        }
        finally
        {
            _loadingFields = false;
        }
    }

    /// <summary>Carries what the fields say over to the selected object.</summary>
    private void ApplyFields()
    {
        if (_loadingFields || _selected is not { } obj || !IsActive) return;
        var rotation = _rotation.Number;
        PageObject changed = obj.Model switch
        {
            PageText text => text with
            {
                Text = _text.Value,
                // A font the list does not have (the box came from another computer) stays until another is chosen.
                FontFamily = _font.Key.Length == 0 && _font.Options.All(o => o.Key != (text.FontFamily ?? "")) ? text.FontFamily : Family(),
                FontSize = _size.Number, Bold = _bold.Value, Italic = _italic.Value, Color = _color.Argb ?? text.Color,
                Align = _align.Key switch { "center" => TextAlign.Center, "right" => TextAlign.Right, _ => TextAlign.Left },
                Rotation = rotation
            },
            PageImage image => image with { Opacity = _opacity.Number / 100, Rotation = rotation },
            PagePatch patch => patch with { Color = _patchColor.Argb ?? patch.Color, Rotation = rotation },
            _ => obj.Model
        };
        if (changed != obj.Model) Change(_main, obj, changed);
    }

    public override void ObjectTransformed(MainViewModel main, CanvasObject obj, RectD bounds, double rotation, double scale, bool done)
    {
        if (done)
        {
            _resizeFrom = null;
            LoadFields(obj.Model);
            MarkDirty(main);
            return;
        }

        var model = obj.Model with { Bounds = bounds, Rotation = rotation };
        if (model is PageText text && Math.Abs(scale - 1) > 1e-9)
        {
            // Pulled by a corner, a text box grows as a whole: the letters with the box.
            _resizeFrom ??= ((PageText)obj.Model).FontSize;
            model = text with { FontSize = Math.Clamp(_resizeFrom.Value * scale, 2, 800) };
        }

        Change(main, obj, model, write: false);
    }

    /// <summary>A double click on a text box goes straight to typing.</summary>
    public override void ObjectActivated(MainViewModel main, CanvasObject obj)
    {
        if (obj.Model is PageText) _text.Focus();
    }

    public override bool KeyPressed(MainViewModel main, Key key, KeyModifiers modifiers)
    {
        if (key == Key.Escape && _reading)
        {
            StopReadingArea();
            _ = main.RefreshTool();
            return true;
        }

        if (_selected is not { } obj)
        {
            if (key != Key.Escape || _mode.Key == "select") return false;
            _mode.Select("select");
            return true;
        }

        var step = modifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        switch (key)
        {
            case Key.Delete or Key.Back:
                Remove(obj);
                return true;
            case Key.Escape:
                main.SelectedObject = null;
                return true;
            case Key.D when modifiers.HasFlag(KeyModifiers.Control):
                Duplicate(obj);
                return true;
            case Key.Left:
                Nudge(-step, 0);
                return true;
            case Key.Right:
                Nudge(step, 0);
                return true;
            case Key.Up:
                Nudge(0, -step);
                return true;
            case Key.Down:
                Nudge(0, step);
                return true;
            default:
                return false;
        }

        void Nudge(double dx, double dy)
        {
            var b = obj.Model.Bounds;
            Change(main, obj, obj.Model with { Bounds = new RectD(b.Left + dx, b.Top + dy, b.Right + dx, b.Bottom + dy) });
        }
    }

    // ---- adding and erasing -------------------------------------------------------------------------------------

    public override async Task RegionSelected(MainViewModel main, RectD region)
    {
        if (_reading)
        {
            await ReadArea(main, region);
            return;
        }

        switch (_mode.Key)
        {
            case "text":
                AddText(main, region.Left, region.Top, Math.Max(24, region.Width));
                break;
            case "image":
                var pixels = _armed ?? throw new UserException(Loc.Get("ErrChooseFile"));
                // The picture keeps its proportions and sits in the middle of the area that was marked for it.
                var fit = Math.Min(region.Width / pixels.Width, region.Height / pixels.Height);
                PlaceImage(main, pixels, region.CenterX, region.CenterY, pixels.Width * fit, pixels.Height * fit);
                break;
            case "erase":
                await Erase(main, region);
                break;
        }
    }

    public override Task PageClicked(MainViewModel main, double x, double y)
    {
        switch (_mode.Key)
        {
            case "text":
                // A click starts a line there; it reaches as far as the page does, less a margin.
                AddText(main, x, y - _size.Number * 0.6, Math.Clamp(main.PageWidth - x - 18, 60, 320));
                break;
            case "image":
                var pixels = _armed ?? throw new UserException(Loc.Get("ErrChooseFile"));
                // At its own size as far as that is known (96 pixels to the inch), but never bigger than half the page.
                var scale = Math.Min(0.75, Math.Min(main.PageWidth * 0.5 / pixels.Width, main.PageHeight * 0.5 / pixels.Height));
                PlaceImage(main, pixels, x, y, pixels.Width * scale, pixels.Height * scale);
                break;
        }

        return Task.CompletedTask;
    }

    private void AddText(MainViewModel main, double left, double top, double width)
    {
        var box = PdfPageObjects.Fit(new PageText
        {
            Id = NextId(), Bounds = new RectD(left, top, left + width, top + 1), FontFamily = Family(), FontSize = _size.Number,
            Color = TextColor(), Bold = _bold.Value, Italic = _italic.Value,
            Align = _align.Key switch { "center" => TextAlign.Center, "right" => TextAlign.Right, _ => TextAlign.Left }
        });
        var obj = AddObject(main, box);
        // Back to selecting, with the new box selected and ready to be typed into.
        _mode.Select("select");
        main.SelectedObject = obj;
        _text.Focus();
    }

    private void PlaceImage(MainViewModel main, ImagePixels pixels, double centerX, double centerY, double width, double height)
    {
        var bounds = new RectD(centerX - width / 2, centerY - height / 2, centerX + width / 2, centerY + height / 2);
        var obj = AddObject(main, new PageImage { Id = NextId(), Pixels = pixels, Bounds = bounds, Opacity = _opacity.Number / 100 });
        _mode.Select("select");
        main.SelectedObject = obj;
        MarkDirty(main);
    }

    /// <summary>Reads the chosen image file, so it is at hand when the place for it is marked.</summary>
    private async Task Arm(string path)
    {
        _armed = null;
        if (path.Length == 0) return;
        try
        {
            var pixels = await Task.Run(() => ImagePixels.FromFile(File.ReadAllBytes(path)));
            if (_image.Path == path) _armed = pixels;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfException)
        {
            _main.StatusText = Loc.Get("ErrImage");
        }
    }

    /// <summary>
    /// Erases inside an area, by kind: images that were added lose the part that lies in it, text boxes that reach
    /// into it are removed, and the page itself is painted over there. What is not ticked is left as it is.
    /// </summary>
    private async Task Erase(MainViewModel main, RectD region)
    {
        var changed = false;
        if (_eraseWhat.IsChecked("images"))
            foreach (var obj in _objects.Where(o => o.Model is PageImage).ToList())
            {
                var image = (PageImage)obj.Model;
                var cut = PdfPageObjects.CutOut(image, region);
                if (ReferenceEquals(cut, image)) continue;
                changed = true;
                if (cut == null) Remove(obj);
                else Change(main, obj, cut, write: false);
            }

        if (_eraseWhat.IsChecked("text"))
            foreach (var obj in _objects.Where(o => o.Model is PageText && Reaches(o.Model, region)).ToList())
            {
                changed = true;
                Remove(obj);
            }

        if (_eraseWhat.IsChecked("page"))
        {
            var color = _eraseColor.Automatic || _eraseColor.Argb is null ? (await Sample(main, region)).Paper : _eraseColor.Argb.Value;
            AddPatch(main, region, color);
            changed = true;
        }

        if (changed) MarkDirty(main);
    }

    /// <summary>True when an object, turned as it is, has any part inside an area.</summary>
    private static bool Reaches(PageObject model, RectD area)
    {
        var a = model.Rotation * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(a)), sin = Math.Abs(Math.Sin(a)), w = model.Bounds.Width, h = model.Bounds.Height;
        double halfWidth = (w * cos + h * sin) / 2, halfHeight = (w * sin + h * cos) / 2;
        return model.Bounds.CenterX + halfWidth > area.Left && model.Bounds.CenterX - halfWidth < area.Right &&
               model.Bounds.CenterY + halfHeight > area.Top && model.Bounds.CenterY - halfHeight < area.Bottom;
    }

    /// <summary>
    /// The colour of the paper around an area and of what is written in it, taken from the page without the objects
    /// that were added: it is the page that is being matched.
    /// </summary>
    private async Task<(uint Paper, uint Ink)> Sample(MainViewModel main, RectD area)
    {
        const double scale = 2;
        var page = main.CurrentPageIndex;
        if (_snapshot is not { } snapshot || snapshot.Page != page)
        {
            DropSnapshot();
            if (main.Session.View is not { } view) return (0xFFFFFFFF, 0xFF000000);
            try
            {
                snapshot = (page, await Task.Run(() => view.Render(page, scale, withoutEditable: true)));
            }
            catch (ObjectDisposedException)
            {
                return (0xFFFFFFFF, 0xFF000000);
            }

            _snapshot = snapshot;
        }

        return PdfPageObjects.SampleColors(snapshot.Picture, scale, area);
    }

    private void DropSnapshot()
    {
        _snapshot?.Picture.Dispose();
        _snapshot = null;
    }

    // ---- writing into the document ------------------------------------------------------------------------------

    private void MarkDirty(MainViewModel main)
    {
        _dirty = true;
        main.NotifyUndoState();
        _writeDelay?.Cancel();
        var delay = _writeDelay = new CancellationTokenSource();
        _ = WriteLater(main, delay.Token);
    }

    private async Task WriteLater(MainViewModel main, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(WriteDelay, cancel);
            await FlushAsync(main);
        }
        catch (OperationCanceledException)
        {
            // Another change followed; its own delay is running.
        }
        catch (Exception e) when (e is PdfException or IOException or InvalidOperationException)
        {
            main.StatusText = MainViewModel.Describe(e);
        }
    }

    public override async Task FlushAsync(MainViewModel main)
    {
        _writeDelay?.Cancel();
        await _writing.WaitAsync();
        try
        {
            if (!_dirty) return;
            _dirty = false;
            var document = main.Session.Bytes;
            // The document was replaced from elsewhere (undo, another tool): these edits were made to a page that no
            // longer exists in that form.
            if (document == null || !ReferenceEquals(document, _layerDocument))
            {
                _erase.Clear();
                return;
            }

            var page = _layerPage;
            var objects = _objects.Select(o => o.Model).Where(m => m is not PageText { Text: var t } || !string.IsNullOrWhiteSpace(t)).ToList();
            var erase = _erase.ToList();
            _erase.Clear();
            var result = await Task.Run(() => PdfPageObjects.Write(document, page, objects, erase));
            if (!ReferenceEquals(main.Session.Bytes, document) || ReferenceEquals(result, document)) return;

            _layerDocument = result;
            if (IsActive)
            {
                // The page view shows these objects already. The page under them is drawn again all the same: text
                // of the page that an erase took out must not come back when the patch over it is moved.
                main.CommitQuietly(result, page);
                if (erase.Count > 0 && main.CurrentPageIndex == page)
                {
                    DropSnapshot();
                    _ = main.RenderPage();
                }
            }
            else
            {
                main.Commit(result);
            }
        }
        finally
        {
            _writing.Release();
            main.NotifyUndoState();
        }
    }
}
