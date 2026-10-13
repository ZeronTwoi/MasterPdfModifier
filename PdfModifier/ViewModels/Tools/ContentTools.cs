using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Text;
using PdfModifier.Localization;

namespace PdfModifier.ViewModels.Tools;

internal static class Parse
{
    /// <summary>The colour of a field, or a message asking for a proper one.</summary>
    public static uint Color(ColorField field) => field.Argb ?? throw new UserException(Loc.Get("ErrColor"));
}

internal static class OcrSetup
{
    public static OcrSettings From(Services.Settings settings) => new(settings.OcrLanguages, settings.AutoDetectLanguage)
    {
        Effort = settings.OcrCareful ? OcrEffort.Careful : OcrEffort.Standard
    };

    /// <summary>A highlight for a line that was read from the page image; red when it was more guessed at than read.</summary>
    public static Overlay Highlight(OcrLine line) =>
        new(line.Bounds, line.Text, line, true, PdfOcr.Certainty(line) < PdfOcr.DoubtfulBelow);

    /// <summary>
    /// The button a tool shows next to its note about what recognition lacks: it fetches that without a further
    /// question and has the tool look again.
    /// </summary>
    public static ActionField Button(MainViewModel main) => new ActionField("SetupButton", () => main.RunBusy(async context =>
    {
        await main.Supplies.Ensure(context, confirmed: true);
        main.RefreshWhenIdle();
    }, false));

    public static string LanguageNames(IEnumerable<string> codes) => string.Join(", ", codes.Select(code => Languages.Get(code).NativeName));
}

/// <summary>Reads files a preview needs once and keeps them, so dragging a slider does not read the disk again and again.</summary>
internal sealed class FileCache
{
    private string _path = "";
    private DateTime _written;
    private byte[]? _data;

    public byte[] Read(string path)
    {
        if (path.Length == 0) throw new UserException(Loc.Get("ErrChooseFile"));
        lock (this)
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (_data != null && path == _path && written == _written) return _data;
            (_path, _written, _data) = (path, written, File.ReadAllBytes(path));
            return _data;
        }
    }
}

/// <summary>
/// Stamps text or an image across pages: once, at a spot chosen on a small picture of a page or by a click on the
/// page itself, or repeated all over it. The page on view shows the result as the settings change.
/// </summary>
public sealed class WatermarkTool : Tool
{
    private readonly PagesField _pages;
    private readonly ModeField _type, _layout;
    private readonly PlaceField _place;
    private readonly TextField _text;
    private readonly ColorField _color;
    private readonly SliderField _size, _opacity, _rotation, _imageScale;
    private readonly ToggleField _bold;
    private readonly FileField _image;
    private readonly InfoField _placeHint;
    private readonly FileCache _files = new();
    private double _centerX = 0.5, _centerY = 0.5;

    public WatermarkTool(MainViewModel main) : base("Watermark", "GroupEdit")
    {
        _pages = Add(new PagesField());
        _type = Add(new ModeField("WatermarkType", new Choice("text", "WatermarkText"), new Choice("image", "WatermarkImage")));
        _text = Add(new TextField("WatermarkTextValue", "CONFIDENTIAL"));
        _size = new SliderField("FontSize", 54, 6, 300, 2);
        _bold = new ToggleField("Bold", "B", true);
        Add(new RowField(_size, _bold));
        _color = Add(new ColorField("TextColor", "#808080").WithPicker(main));
        _image = Add(new FileField("WatermarkImageFile", true).WithPicker(main));
        _imageScale = Add(new SliderField("WatermarkImageScale", 50, 2, 200, 1).WithStops(" %", 25, 50, 100));
        _opacity = Add(new SliderField("Opacity", 30, 2, 100, 1).WithStops(" %", 10, 30, 50, 100));
        _rotation = Add(new SliderField("Rotation", 45, -180, 180, 1).WithStops("°", 0, 45, -45, 90));
        _layout = Add(new ModeField("WatermarkLayout", new Choice("once", "WatermarkOnce"), new Choice("tile", "WatermarkRepeated")));
        _place = Add(new PlaceField("WatermarkWhere", "Center"));
        _placeHint = Add(new InfoField());
        _placeHint.SetKey("WatermarkWhereHint");
        _type.PropertyChanged += (_, _) => Update();
        _layout.PropertyChanged += (_, _) =>
        {
            Update();
            OnPropertyChanged(nameof(CanvasMode));
        };
        Update();
    }

    public override bool HasPreview => true;

    /// <summary>
    /// A single watermark can be put anywhere with a click on the page; the page is still moved by dragging it.
    /// </summary>
    public override CanvasMode CanvasMode => _layout.Key == "once" ? CanvasMode.Click : CanvasMode.Select;

    private void Update()
    {
        var text = _type.Key == "text";
        _text.IsVisible = _size.IsVisible = _color.IsVisible = _bold.IsVisible = text;
        _image.IsVisible = _imageScale.IsVisible = !text;
        _place.IsVisible = _placeHint.IsVisible = _layout.Key == "once";
    }

    public override Task PageClicked(MainViewModel main, double x, double y)
    {
        if (_layout.Key != "once" || main.PageWidth <= 0 || main.PageHeight <= 0) return Task.CompletedTask;
        // Kept as a share of the page, so the mark lands in the same spot on pages of another size.
        (_centerX, _centerY) = (Math.Clamp(x / main.PageWidth, 0, 1), Math.Clamp(y / main.PageHeight, 0, 1));
        // None of the ready-made spots any more.
        _place.Key = null;
        OnSettingsChanged();
        return Task.CompletedTask;
    }

    /// <summary>Where the middle of a single watermark goes, as shares of the page's width and height.</summary>
    private (double X, double Y) Centre()
    {
        if (_place.Key == null) return (_centerX, _centerY);
        // The spots towards the edges lie a little way in, so that a mark placed there is still on the page.
        var (across, down) = _place.Share;
        return (0.2 + 0.6 * across, 0.14 + 0.72 * down);
    }

    private WatermarkOptions Options()
    {
        var (x, y) = _layout.Key == "once" ? Centre() : (0.5, 0.5);
        var options = new WatermarkOptions
        {
            Opacity = _opacity.Number / 100,
            RotationDegrees = _rotation.Number,
            Layout = _layout.Key == "tile" ? WatermarkLayout.Tile : WatermarkLayout.Center,
            CenterX = x,
            CenterY = y
        };

        if (_type.Key != "text")
            return options with { ImageData = _files.Read(_image.Path), ImageScale = _imageScale.Number / 100 };
        if (string.IsNullOrWhiteSpace(_text.Value)) throw new UserException(Loc.Get("EditTypeTextFirst"));
        return options with { Text = _text.Value, FontSize = _size.Number, Color = Parse.Color(_color), Bold = _bold.Value };
    }

    public override PreviewResult Preview(PreviewRequest request) =>
        request.Includes(_pages)
            ? new PreviewResult(PdfStamps.AddWatermark(request.Page, null, Options()))
            : new PreviewResult(null, Loc.Get("PreviewOutside"));

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var options = await context.Run(Options);
        context.Commit(await context.Run(() => PdfStamps.AddWatermark(pdf, pages, options)));
    }
}

/// <summary>
/// Removes watermarks of both kinds: those that are objects of their own on the page, which can be found and taken
/// off, and those that are part of the picture of a scanned page, which have to be taken out of the picture.
/// </summary>
public sealed class RemoveWatermarkTool : Tool
{
    private readonly PagesField _pages;
    private readonly ModeField _method;
    private readonly ActionField _find;
    private readonly CheckListField _found;
    private readonly InfoField _note, _pageKind;
    private readonly SliderField _strength, _tolerance;
    private readonly ColorField _markColor;
    private readonly BoolField _limit;

    private byte[]? _analysed;
    private RectD? _area;
    private (byte[] Document, List<string> Keys)? _certain;
    private byte[]? _suggestedFor;
    private bool _methodChosen, _suggesting;

    public RemoveWatermarkTool(MainViewModel main) : base("RemoveWatermark", "GroupEdit")
    {
        _pages = Add(new PagesField());
        _pageKind = Add(new InfoField());
        _method = Add(new ModeField("WatermarkMethod",
            new Choice("objects", "WatermarkMethodObjects"), new Choice("light", "WatermarkMethodLight"),
            new Choice("color", "WatermarkMethodColor"), new Choice("cover", "WatermarkMethodCover")) { Columns = 1 });
        _find = Add(new ActionField("WatermarkDetect", () => main.RunBusy(Detect)));
        _found = Add(new CheckListField("WatermarkFound"));
        _strength = Add(new SliderField("WatermarkStrength", 50, 0, 100, 1));
        _markColor = Add(new ColorField("WatermarkMarkColor", "#C0C0C0").WithPicker(main));
        _tolerance = Add(new SliderField("WatermarkTolerance", 50, 0, 100, 1));
        _limit = Add(new BoolField("WatermarkLimitArea"));
        _note = Add(new InfoField());
        _found.IsVisible = false;

        _method.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChoiceField.Selected)) return;
            // Chosen by hand, the method is no longer changed to suit the page.
            if (!_suggesting) _methodChosen = true;
            Update();
            ShowArea(main);
        };
        _limit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BoolField.Value)) return;
            Update();
            ShowArea(main);
        };
        Update();
    }

    public override bool HasPreview => true;

    private bool UsesArea => _method.Key == "cover" || (_method.Key is "light" or "color" && _limit.Value);

    public override CanvasMode CanvasMode => UsesArea ? CanvasMode.Area : CanvasMode.Select;

    private void Update()
    {
        var method = _method.Key;
        _find.IsVisible = method == "objects";
        _found.IsVisible = method == "objects" && _found.Items.Count > 0;
        _strength.IsVisible = method == "light";
        _markColor.IsVisible = _tolerance.IsVisible = method == "color";
        _limit.IsVisible = method is "light" or "color";
        _note.SetKey(method switch
        {
            "light" => "WatermarkNoteLight",
            "color" => "WatermarkNoteColor",
            "cover" => "WatermarkNoteCover",
            _ => "WatermarkRemoveNote"
        });
        OnPropertyChanged(nameof(CanvasMode));
    }

    /// <summary>Shows the marked area on the page while a method that works on an area is chosen.</summary>
    private void ShowArea(MainViewModel main)
    {
        if (!ReferenceEquals(main.SelectedTool, this)) return;
        main.Region = UsesArea ? _area : null;
    }

    public override Task RegionSelected(MainViewModel main, RectD region)
    {
        _area = region;
        OnSettingsChanged();
        return Task.CompletedTask;
    }

    private async Task Detect(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var candidates = await context.Run(() => PdfWatermarkRemoval.Detect(pdf, pages));
        _found.Items.Clear();
        foreach (var c in candidates)
        {
            var what = c.Kind switch
            {
                WatermarkKind.Annotation => Loc.Get("WatermarkKindAnnotation"),
                WatermarkKind.Tagged => Loc.Get("WatermarkKindTagged"),
                _ when c.Description == "Image" => Loc.Get("WatermarkKindImage"),
                _ when c.Description == "Graphic" => Loc.Get("WatermarkKindGraphic"),
                _ => "“" + c.Description + "”"
            };
            _found.Items.Add(new CheckItem(c.Key, Loc.Format("WatermarkCandidate", what, c.PageCount, $"{c.Confidence:P0}"),
                c.Confidence >= PdfWatermarkRemoval.AutoThreshold));
        }

        _analysed = pdf;
        Update();
        context.Status(candidates.Count == 0 ? Loc.Get("WatermarkNoneFound") : Loc.Format("WatermarkFoundCount", candidates.Count));
    }

    public override async Task RefreshAsync(MainViewModel main)
    {
        var document = main.Session.Bytes;
        // Detection results describe one exact document; they are void once it changes.
        if (!ReferenceEquals(document, _analysed))
        {
            _found.Items.Clear();
            _analysed = null;
            Update();
        }

        ShowArea(main);
        if (document == null || main.Session.View is not { } view)
        {
            _pageKind.SetText("");
            return;
        }

        // Whether the page on view is a picture decides which kind of watermark it can have at all.
        var page = main.CurrentPageIndex;
        PageContent content;
        try
        {
            content = await Task.Run(() => view.Analyse(page));
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (!ReferenceEquals(main.Session.Bytes, document) || main.CurrentPageIndex != page) return;
        _pageKind.SetKey(content.HasPictures ? "WatermarkPageScan" : "WatermarkPageObjects");
        if (_methodChosen || ReferenceEquals(_suggestedFor, document)) return;
        // Once per document, and only until a method is chosen by hand: start with the one that suits the page.
        _suggestedFor = document;
        var separate = await Task.Run(() => PdfWatermarkRemoval.Detect(document, [page]).Count > 0);
        if (_methodChosen || !ReferenceEquals(main.Session.Bytes, document)) return;
        _suggesting = true;
        _method.Select(content.HasPictures && !separate ? "light" : "objects");
        _suggesting = false;
    }

    private ScanMarkOptions ScanOptions()
    {
        var area = UsesArea ? _area : null;
        return _method.Key switch
        {
            "color" => new ScanMarkOptions { Kind = ScanMarkKind.Colored, Color = Parse.Color(_markColor), Strength = _tolerance.Number / 100, Area = area },
            "cover" => new ScanMarkOptions { Kind = ScanMarkKind.Cover, Area = area ?? throw new UserException(Loc.Get("WatermarkNoteCover")) },
            _ => new ScanMarkOptions { Kind = ScanMarkKind.Light, Strength = _strength.Number / 100, Area = area }
        };
    }

    /// <summary>
    /// What Apply would remove without "Find watermarks" having been used: the candidates that are certain. They are
    /// looked for in the whole document, since it is the repetition from page to page that gives a watermark away.
    /// </summary>
    private List<string> CertainKeys(byte[] document, IReadOnlyCollection<int>? pages)
    {
        lock (this)
        {
            if (_certain is { } known && ReferenceEquals(known.Document, document)) return known.Keys;
            var keys = PdfWatermarkRemoval.Detect(document, pages)
                .Where(c => c.Confidence >= PdfWatermarkRemoval.AutoThreshold).Select(c => c.Key).ToList();
            _certain = (document, keys);
            return keys;
        }
    }

    public override PreviewResult Preview(PreviewRequest request)
    {
        if (!request.Includes(_pages)) return new PreviewResult(null, Loc.Get("PreviewOutside"));
        if (_method.Key != "objects")
        {
            var (cleaned, pages) = PdfWatermarkRemoval.RemoveFromScans(request.Page, null, ScanOptions(), cancel: request.Cancel);
            return pages == 0 ? new PreviewResult(null, Loc.Get("WatermarkNoPicture")) : new PreviewResult(cleaned);
        }

        var keys = ReferenceEquals(request.Document, _analysed) ? _found.CheckedKeys.ToList() : CertainKeys(request.Document, request.Resolve(_pages));
        var (result, removed) = PdfWatermarkRemoval.Remove(request.Page, null, keys);
        return removed == 0 ? new PreviewResult(null, Loc.Get("WatermarkNoneFound")) : new PreviewResult(result);
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        if (_method.Key != "objects")
        {
            var options = ScanOptions();
            var (cleaned, changed) = await context.Run(() => PdfWatermarkRemoval.RemoveFromScans(pdf, pages, options, context.Progress, context.Cancel));
            if (changed == 0)
            {
                context.Status(Loc.Get("WatermarkNoPicture"));
                return;
            }

            context.Commit(cleaned);
            context.Status(Loc.Format("WatermarkScanDone", changed));
            return;
        }

        // Without a prior "Find watermarks" the confident matches are removed; with one, exactly what is ticked.
        var keys = ReferenceEquals(pdf, _analysed) ? _found.CheckedKeys.ToList() : null;
        var (result, removed) = await context.Run(() => PdfWatermarkRemoval.Remove(pdf, pages, keys));
        if (removed == 0)
        {
            context.Status(Loc.Get("WatermarkNoneFound"));
            return;
        }

        context.Commit(result);
        context.Status(Loc.Format("WatermarkRemovedCount", removed));
    }
}

/// <summary>
/// Numbers the pages. How the number is written is chosen from the usual ways or typed, where it stands is clicked
/// on a small picture of a page, and the page on view shows the result.
/// </summary>
public sealed class PageNumbersTool : Tool
{
    private readonly PagesField _pages;
    private readonly ModeField _style;
    private readonly TextField _format;
    private readonly InfoField _formatHint;
    private readonly ColorField _color;
    private readonly NumberField _start;
    private readonly SliderField _size, _margin;
    private readonly PlaceField _position;

    public PageNumbersTool(MainViewModel main) : base("PageNumbers", "GroupEdit")
    {
        _pages = Add(new PagesField());
        _style = Add(new ModeField("NumberStyle",
            new Choice("n", "1", true), new Choice("of", "1 / 9", true), new Choice("dash", "– 1 –", true),
            new Choice("page", "NumberSamplePage"), new Choice("pageOf", "NumberSamplePageOf"), new Choice("own", "NumberStyleOwn")) { Columns = 3 });
        _style.Select("of");
        _format = Add(new TextField("NumberFormat", "{n} / {total}"));
        _formatHint = Add(new InfoField());
        _formatHint.SetKey("NumberFormatHint");
        _start = Add(new NumberField("NumberStart", 1, 0, 100000));
        _position = Add(new PlaceField("Position", nameof(StampPosition.BottomCenter),
            nameof(StampPosition.TopLeft), nameof(StampPosition.TopCenter), nameof(StampPosition.TopRight),
            nameof(StampPosition.BottomLeft), nameof(StampPosition.BottomCenter), nameof(StampPosition.BottomRight)));
        _size = Add(new SliderField("FontSize", 11, 6, 48, 1, "0.#"));
        _margin = Add(new SliderField("MarginMm", 10, 0, 50, 1, "0.#"));
        _color = Add(new ColorField("TextColor").WithPicker(main));
        _style.PropertyChanged += (_, _) => Update();
        Update();
    }

    public override bool HasPreview => true;

    private void Update() => _format.IsVisible = _formatHint.IsVisible = _style.Key == "own";

    /// <summary>How the number is written: {n} stands for the number of the page, {total} for the last number.</summary>
    private string Format() => _style.Key switch
    {
        "n" => "{n}",
        "of" => "{n} / {total}",
        "dash" => "– {n} –",
        "page" => Loc.Get("NumberStylePage"),
        "pageOf" => Loc.Get("NumberStylePageOf"),
        _ => _format.Value
    };

    private PageNumberOptions Options() => new()
    {
        Format = Format(),
        StartNumber = (int)_start.Number,
        Position = Enum.Parse<StampPosition>(_position.Key ?? nameof(StampPosition.BottomCenter)),
        FontSize = _size.Number,
        Margin = _margin.Mm(),
        Color = Parse.Color(_color)
    };

    public override PreviewResult Preview(PreviewRequest request)
    {
        // The number a page gets depends on where it stands among the pages that are numbered.
        var numbered = request.Resolve(_pages) ?? Enumerable.Range(0, request.PageCount).ToList();
        var place = numbered.IndexOf(request.PageIndex);
        if (place < 0) return new PreviewResult(null, Loc.Get("PreviewOutside"));
        var options = Options();
        var label = options.Format
            .Replace("{n}", (options.StartNumber + place).ToString())
            .Replace("{total}", (options.StartNumber + numbered.Count - 1).ToString());
        return new PreviewResult(PdfStamps.AddPageNumbers(request.Page, null, options with { Format = label }));
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var options = Options();
        context.Commit(await context.Run(() => PdfStamps.AddPageNumbers(pdf, pages, options)));
    }
}

public sealed class FlattenTool : Tool
{
    private readonly PagesField _pages;

    public FlattenTool() : base("Flatten", "GroupEdit") => _pages = Add(new PagesField());

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        context.Commit(await context.Run(() => PdfPages.Flatten(pdf, pages)));
        context.Status(Loc.Get("StatusFlattened"));
    }
}

public sealed class MetadataTool : Tool
{
    private readonly TextField _title, _author, _subject, _keywords, _creator;
    private readonly InfoField _readOnly;
    private byte[]? _loaded;

    public MetadataTool(MainViewModel main) : base("Metadata", "GroupEdit")
    {
        _title = Add(new TextField("MetaTitle"));
        _author = Add(new TextField("MetaAuthor"));
        _subject = Add(new TextField("MetaSubject"));
        _keywords = Add(new TextField("MetaKeywords"));
        _creator = Add(new TextField("MetaCreator"));
        _readOnly = Add(new InfoField());
        Add(new ActionField("MetaRemoveAll", () => main.RunBusy(async context =>
        {
            var pdf = context.Pdf;
            context.Commit(await context.Run(() => PdfMetadataOps.RemoveAll(pdf)));
            context.Status(Loc.Get("StatusMetadataRemoved"));
        })));
    }

    public override string ApplyLabel => Loc.Get("MetaSave");

    public override async Task RefreshAsync(MainViewModel main)
    {
        var document = main.Session.Bytes;
        // Reload only for a different document, so moving between pages does not discard what was typed.
        if (ReferenceEquals(document, _loaded)) return;
        _loaded = document;
        var meta = document == null ? new PdfMetadata() : await Task.Run(() => PdfMetadataOps.Read(document));
        (_title.Value, _author.Value, _subject.Value, _keywords.Value, _creator.Value) =
            (meta.Title, meta.Author, meta.Subject, meta.Keywords, meta.Creator);
        _readOnly.SetText(Loc.Format("MetaReadOnly", Shown(meta.Producer), Shown(meta.CreationDate), Shown(meta.ModificationDate)));

        static string Shown(string value) => value.Length == 0 ? "—" : value;
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var meta = new PdfMetadata
        {
            Title = _title.Value, Author = _author.Value, Subject = _subject.Value, Keywords = _keywords.Value, Creator = _creator.Value
        };
        context.Commit(await context.Run(() => PdfMetadataOps.Write(pdf, meta)));
        context.Status(Loc.Get("StatusMetadataSaved"));
    }
}
