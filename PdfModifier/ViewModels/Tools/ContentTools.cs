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

public sealed class WatermarkTool : Tool
{
    private readonly PagesField _pages;
    private readonly ModeField _type;
    private readonly ChoiceField _layout;
    private readonly TextField _text;
    private readonly ColorField _color;
    private readonly NumberField _size;
    private readonly SliderField _opacity, _rotation, _imageScale;
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
        _size = new NumberField("FontSize", 54, 6, 400, 2);
        _bold = new ToggleField("Bold", "B", true);
        Add(new RowField(_size, _bold));
        _color = Add(new ColorField("TextColor", "#808080").WithPicker(main));
        _image = Add(new FileField("WatermarkImageFile", true).WithPicker(main));
        _imageScale = Add(new SliderField("WatermarkImageScale", 50, 2, 200, 1));
        _opacity = Add(new SliderField("Opacity", 30, 2, 100, 1));
        _rotation = Add(new SliderField("Rotation", 45, -180, 180, 1));
        _layout = Add(new ChoiceField("WatermarkLayout",
            new Choice("center", "WatermarkCenter"), new Choice("tile", "WatermarkTile"), new Choice("place", "WatermarkPlace")));
        _placeHint = Add(new InfoField());
        _placeHint.SetKey("WatermarkPlaceHint");
        _type.PropertyChanged += (_, _) => Update();
        _layout.PropertyChanged += (_, _) =>
        {
            Update();
            OnPropertyChanged(nameof(CanvasMode));
        };
        Update();
    }

    public override bool HasPreview => true;

    /// <summary>While the place is being chosen, a click on the page says where the middle of the watermark goes.</summary>
    public override CanvasMode CanvasMode => _layout.Key == "place" ? CanvasMode.Click : CanvasMode.Select;

    private void Update()
    {
        var text = _type.Key == "text";
        _text.IsVisible = _size.IsVisible = _color.IsVisible = _bold.IsVisible = text;
        _image.IsVisible = _imageScale.IsVisible = !text;
        _placeHint.IsVisible = _layout.Key == "place";
    }

    public override Task PageClicked(MainViewModel main, double x, double y)
    {
        if (_layout.Key != "place" || main.PageWidth <= 0 || main.PageHeight <= 0) return Task.CompletedTask;
        // Kept as a share of the page, so the mark lands in the same spot on pages of another size.
        (_centerX, _centerY) = (Math.Clamp(x / main.PageWidth, 0, 1), Math.Clamp(y / main.PageHeight, 0, 1));
        OnSettingsChanged();
        return Task.CompletedTask;
    }

    private WatermarkOptions Options()
    {
        var place = _layout.Key == "place";
        var options = new WatermarkOptions
        {
            Opacity = _opacity.Number / 100,
            RotationDegrees = _rotation.Number,
            Layout = _layout.Key == "tile" ? WatermarkLayout.Tile : WatermarkLayout.Center,
            CenterX = place ? _centerX : 0.5,
            CenterY = place ? _centerY : 0.5
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
    private readonly ChoiceField _method;
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
        _method = Add(new ChoiceField("WatermarkMethod",
            new Choice("objects", "WatermarkMethodObjects"), new Choice("light", "WatermarkMethodLight"),
            new Choice("color", "WatermarkMethodColor"), new Choice("cover", "WatermarkMethodCover")));
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

public sealed class PageNumbersTool : Tool
{
    private readonly PagesField _pages;
    private readonly TextField _format;
    private readonly ColorField _color;
    private readonly NumberField _start, _size, _margin;
    private readonly ChoiceField _position;

    public PageNumbersTool(MainViewModel main) : base("PageNumbers", "GroupEdit")
    {
        _pages = Add(new PagesField());
        _format = Add(new TextField("NumberFormat", "{n} / {total}"));
        Add(new InfoField()).SetKey("NumberFormatHint");
        _start = Add(new NumberField("NumberStart", 1, 0, 100000));
        _position = Add(new ChoiceField("Position",
            new Choice(nameof(StampPosition.BottomCenter), "PosBottomCenter"), new Choice(nameof(StampPosition.BottomRight), "PosBottomRight"),
            new Choice(nameof(StampPosition.BottomLeft), "PosBottomLeft"), new Choice(nameof(StampPosition.TopCenter), "PosTopCenter"),
            new Choice(nameof(StampPosition.TopRight), "PosTopRight"), new Choice(nameof(StampPosition.TopLeft), "PosTopLeft")));
        _size = Add(new NumberField("FontSize", 11, 4, 100, 1, "0.#"));
        _margin = Add(new NumberField("MarginMm", 10, 0, 200, 1, "0.#"));
        _color = Add(new ColorField("TextColor").WithPicker(main));
    }

    public override bool HasPreview => true;

    private PageNumberOptions Options() => new()
    {
        Format = _format.Value,
        StartNumber = (int)_start.Number,
        Position = Enum.Parse<StampPosition>(_position.Key),
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
