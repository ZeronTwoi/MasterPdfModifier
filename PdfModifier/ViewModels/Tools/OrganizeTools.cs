using PdfModifier.Core;
using PdfModifier.Core.Operations;
using PdfModifier.Localization;
using PdfModifier.Services;

namespace PdfModifier.ViewModels.Tools;

internal static class Units
{
    public const double PointsPerMm = 72 / 25.4;

    public static double Mm(this NumberField field) => field.Number * PointsPerMm;

    /// <summary>Named paper sizes in points, portrait.</summary>
    public static (double Width, double Height)? Paper(string key) => key switch
    {
        "a3" => (841.89, 1190.55),
        "a4" => (595.28, 841.89),
        "a5" => (419.53, 595.28),
        "letter" => (612, 792),
        "legal" => (612, 1008),
        _ => null
    };

    public static Choice[] PaperChoices(params Choice[] extra) =>
    [
        ..extra,
        new("a4", "A4", true), new("a3", "A3", true), new("a5", "A5", true),
        new("letter", "Letter", true), new("legal", "Legal", true)
    ];

    public static string DocumentName(DocumentSession session) =>
        session.FilePath != null ? Path.GetFileNameWithoutExtension(session.FilePath) : Loc.Get("Untitled");

    public static string Megabytes(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes / 1048576.0:0.0} MB";
}

internal static class FieldSetup
{
    public static FileField WithPicker(this FileField field, MainViewModel main)
    {
        field.Browse = async f =>
        {
            var files = await main.Dialogs.OpenFiles(f.Label, false, f.Images ? Dialogs.ImageFiles : Dialogs.PdfFiles);
            if (files.Count == 0) return;
            // Remembered first, so the file is already marked as the one in use when it shows up among the recent ones.
            if (f.Images) main.RememberImage(files[0]);
            f.Path = files[0];
        };
        // Images are offered again under the field once they were used: a logo or a signature is rarely needed just once.
        if (field.Images) main.TrackRecentImages(field);
        return field;
    }

    public static FolderField WithPicker(this FolderField field, MainViewModel main)
    {
        field.Browse = async f =>
        {
            if (await main.Dialogs.PickFolder(f.Label) is { } folder) f.Path = folder;
        };
        return field;
    }

    /// <summary>Lets the colour be taken from a spot on the page.</summary>
    public static ColorField WithPicker(this ColorField field, MainViewModel main)
    {
        field.PickFromPage = main.PickColor;
        return field;
    }

    public static FileListField WithPicker(this FileListField field, MainViewModel main)
    {
        field.Browse = async f =>
        {
            foreach (var file in await main.Dialogs.OpenFiles(f.Label, true, f.Images ? Dialogs.ImageFiles : Dialogs.PdfFiles))
                if (!f.Files.Contains(file))
                    f.Files.Add(file);
        };
        return field;
    }

    /// <summary>Reads a PDF from disk, asking for its password when it has one.</summary>
    public static async Task<byte[]> ReadPdf(ToolContext context, string path)
    {
        var bytes = await context.Run(() => File.ReadAllBytes(path));
        if (!await context.Run(() => PdfPages.NeedsPassword(bytes))) return bytes;
        var password = await context.Dialogs.AskPassword(Loc.Format("AskPassword", Path.GetFileName(path)))
                       ?? throw new OperationCanceledException();
        return await context.Run(() => PdfPages.Unlock(bytes, password));
    }
}

public sealed class MergeTool : Tool
{
    private readonly FileListField _files;
    private readonly BoolField _includeCurrent;

    public MergeTool(MainViewModel main) : base("Merge", "GroupOrganize")
    {
        _includeCurrent = Add(new BoolField("MergeIncludeCurrent", true));
        _files = Add(new FileListField("MergeFiles").WithPicker(main));
    }

    public override bool NeedsDocument => false;

    public override async Task RunAsync(ToolContext context)
    {
        var useCurrent = _includeCurrent.Value && context.Session.HasDocument;
        if (_files.Files.Count + (useCurrent ? 1 : 0) < 2) throw new UserException(Loc.Get("ErrMergeNeedsTwo"));

        var sources = new List<byte[]>();
        if (useCurrent) sources.Add(context.Pdf);
        foreach (var file in _files.Files) sources.Add(await FieldSetup.ReadPdf(context, file));

        var merged = await context.Run(() => PdfPages.Merge(sources));
        if (useCurrent) context.Commit(merged);
        else await context.Main.OpenNew(merged);
        context.Status(Loc.Format("StatusMerged", sources.Count));
    }
}

public sealed class SplitTool : Tool
{
    private readonly ChoiceField _mode;
    private readonly NumberField _every;
    private readonly TextField _ranges;

    public SplitTool() : base("Split", "GroupOrganize")
    {
        _mode = Add(new ChoiceField("SplitMode", new Choice("every", "SplitEvery"), new Choice("ranges", "SplitRanges"), new Choice("single", "SplitSingle")));
        _every = Add(new NumberField("SplitPagesPerFile", 1, 1, 10000));
        _ranges = Add(new TextField("SplitRangesList", "1-2; 3-"));
        _mode.PropertyChanged += (_, _) => Update();
        Update();
    }

    private void Update()
    {
        _every.IsVisible = _mode.Key == "every";
        _ranges.IsVisible = _mode.Key == "ranges";
    }

    private List<IReadOnlyList<int>> Groups(int pageCount) => _mode.Key switch
    {
        "ranges" => PageRange.ParseGroups(_ranges.Value, pageCount).Select(g => (IReadOnlyList<int>)g).ToList(),
        "single" => PdfPages.GroupsOf(pageCount, 1).Select(g => (IReadOnlyList<int>)g).ToList(),
        _ => PdfPages.GroupsOf(pageCount, (int)_every.Number).Select(g => (IReadOnlyList<int>)g).ToList()
    };

    public override string Summary(MainViewModel main)
    {
        var groups = Groups(main.Pages.Count);
        // The first few files spelled out, so a slip in the ranges shows before anything is written.
        var listed = string.Join("; ", groups.Take(6).Select(PageRange.Format)) + (groups.Count > 6 ? "; …" : "");
        return Loc.Format("SummarySplit", groups.Count, listed);
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var groups = Groups(context.PageCount);
        if (groups.Count == 0) throw new UserException(Loc.Get("ErrPageRange"));

        var folder = await context.Dialogs.PickFolder(Loc.Get("PickOutputFolder"));
        if (folder == null) return;
        var name = Units.DocumentName(context.Session);
        await context.Run(() =>
        {
            for (var i = 0; i < groups.Count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                File.WriteAllBytes(Path.Combine(folder, $"{name}_{i + 1}.pdf"), PdfPages.Extract(pdf, groups[i]));
                context.Progress.Report((i + 1.0) / groups.Count);
            }

            return true;
        });
        context.Status(Loc.Format("StatusFilesWritten", groups.Count, folder));
    }
}

public sealed class RotateTool : Tool
{
    private readonly PagesField _pages;
    private readonly ChoiceField _angle;

    public RotateTool() : base("Rotate", "GroupOrganize")
    {
        _pages = Add(new PagesField());
        _pages.Scope = _pages.Scopes[1];
        _angle = Add(new ChoiceField("RotateAngle", new Choice("90", "RotateRight"), new Choice("-90", "RotateLeft"), new Choice("180", "RotateHalf")));
    }

    public override bool HasPreview => true;

    public override PreviewResult Preview(PreviewRequest request) =>
        request.Includes(_pages)
            ? new PreviewResult(PdfPages.Rotate(request.Page, null, int.Parse(_angle.Key)))
            : new PreviewResult(null, Loc.Get("PreviewOutside"));

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var degrees = int.Parse(_angle.Key);
        context.Commit(await context.Run(() => PdfPages.Rotate(pdf, pages, degrees)));
    }
}

public sealed class DeleteTool : Tool
{
    private readonly PagesField _pages;

    public DeleteTool() : base("Delete", "GroupOrganize")
    {
        _pages = Add(new PagesField());
        _pages.Scope = _pages.Scopes[1];
    }

    public override string Summary(MainViewModel main)
    {
        var pages = _pages.Resolve(main.SelectedPageIndexes, main.CurrentPageIndex, main.Pages.Count);
        return pages == null || pages.Count >= main.Pages.Count
            ? Loc.Get("ErrDeleteAll")
            : Loc.Format("SummaryDelete", pages.Count, main.Pages.Count, PageRange.Format(pages));
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages) ?? throw new UserException(Loc.Get("ErrDeleteAll"));
        if (pages.Count >= context.PageCount) throw new UserException(Loc.Get("ErrDeleteAll"));
        context.Commit(await context.Run(() => PdfPages.Delete(pdf, pages)));
        context.Status(Loc.Format("StatusPagesDeleted", pages.Count));
    }
}

public sealed class ReorderTool : Tool
{
    private readonly ChoiceField _action;

    public ReorderTool() : base("Reorder", "GroupOrganize")
    {
        _action = Add(new ChoiceField("ReorderAction",
            new Choice("up", "ReorderUp"), new Choice("down", "ReorderDown"),
            new Choice("first", "ReorderFirst"), new Choice("last", "ReorderLast"), new Choice("reverse", "ReorderReverse")));
        Add(new InfoField()).SetKey("ReorderNote");
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var count = context.PageCount;
        var selected = (context.SelectedPages.Count > 0 ? context.SelectedPages : [context.CurrentPage]).Order().ToList();
        var order = Enumerable.Range(0, count).ToList();

        switch (_action.Key)
        {
            case "reverse":
                order.Reverse();
                break;
            case "first":
                order = [..selected, ..order.Except(selected)];
                break;
            case "last":
                order = [..order.Except(selected), ..selected];
                break;
            case "up":
                // Swap each selected page with its predecessor unless that one is selected too.
                foreach (var page in selected)
                {
                    var at = order.IndexOf(page);
                    if (at > 0 && !selected.Contains(order[at - 1])) (order[at - 1], order[at]) = (order[at], order[at - 1]);
                }

                break;
            default:
                foreach (var page in selected.AsEnumerable().Reverse())
                {
                    var at = order.IndexOf(page);
                    if (at < count - 1 && !selected.Contains(order[at + 1])) (order[at + 1], order[at]) = (order[at], order[at + 1]);
                }

                break;
        }

        if (order.SequenceEqual(Enumerable.Range(0, count))) return;
        context.Commit(await context.Run(() => PdfPages.Reorder(pdf, order)));
        // Keep the moved pages selected so the action can be repeated.
        var moved = _action.Key == "reverse" ? [] : selected.Select(page => order.IndexOf(page)).ToList();
        if (moved.Count > 0) context.Main.RestoreSelection(moved);
    }
}

public sealed class InsertTool : Tool
{
    private readonly ChoiceField _what, _where, _blankSize, _imageSize;
    private readonly NumberField _count;
    private readonly FileField _pdf;
    private readonly TextField _pdfPages;
    private readonly FileListField _images;

    public InsertTool(MainViewModel main) : base("Insert", "GroupOrganize")
    {
        _what = Add(new ChoiceField("InsertWhat", new Choice("blank", "InsertBlank"), new Choice("pdf", "InsertPdf"), new Choice("images", "InsertImages")));
        _where = Add(new ChoiceField("InsertWhere",
            new Choice("after", "InsertAfter"), new Choice("before", "InsertBefore"),
            new Choice("start", "InsertStart"), new Choice("end", "InsertEnd")));
        _blankSize = Add(new ChoiceField("PageSize", Units.PaperChoices(new Choice("same", "SizeSameAsCurrent"))));
        _count = Add(new NumberField("InsertCount", 1, 1, 500));
        _pdf = Add(new FileField("InsertPdfFile").WithPicker(main));
        _pdfPages = Add(new TextField("InsertPdfPages"));
        _images = Add(new FileListField("InsertImageFiles", true).WithPicker(main));
        _imageSize = Add(new ChoiceField("PageSize", Units.PaperChoices(new Choice("image", "SizeOfImage"))));
        _what.PropertyChanged += (_, _) => Update();
        Update();
    }

    public override bool NeedsDocument => false;

    private void Update()
    {
        var what = _what.Key;
        _blankSize.IsVisible = _count.IsVisible = what == "blank";
        _pdf.IsVisible = _pdfPages.IsVisible = what == "pdf";
        _images.IsVisible = _imageSize.IsVisible = what == "images";
    }

    public override async Task RunAsync(ToolContext context)
    {
        var current = context.Session.Bytes;
        var count = context.PageCount;
        var index = current == null ? 0 : _where.Key switch
        {
            "before" => context.CurrentPage,
            "start" => 0,
            "end" => count,
            _ => context.CurrentPage + 1
        };

        byte[] result;
        switch (_what.Key)
        {
            case "pdf":
                if (_pdf.Path.Length == 0) throw new UserException(Loc.Get("ErrChooseFile"));
                var other = await FieldSetup.ReadPdf(context, _pdf.Path);
                var range = _pdfPages.Value;
                result = await context.Run(() =>
                {
                    using var view = new PdfView(other);
                    var pages = PageRange.Parse(range, view.PageCount);
                    return current == null ? PdfPages.Extract(other, pages) : PdfPages.InsertPdf(current, other, pages, index);
                });
                break;

            case "images":
                if (_images.Files.Count == 0) throw new UserException(Loc.Get("ErrChooseFile"));
                var files = _images.Files.ToList();
                var size = Units.Paper(_imageSize.Key);
                result = await context.Run(() =>
                {
                    var images = files.Select(File.ReadAllBytes).ToList();
                    var options = size is { } s ? new ImagePageOptions(s.Width, s.Height, Margin: 18) : new ImagePageOptions();
                    return PdfImages.InsertImages(current, images, index, options);
                });
                break;

            default:
                if (current == null) throw new UserException(Loc.Get("ErrNoDocument"));
                var (width, height) = Units.Paper(_blankSize.Key) ?? (context.Main.PageWidth, context.Main.PageHeight);
                var copies = (int)_count.Number;
                result = await context.Run(() => PdfPages.InsertBlank(current, index, width, height, copies));
                break;
        }

        if (current == null) await context.Main.OpenNew(result);
        else context.Commit(result);
    }
}

public sealed class ExtractTool : Tool
{
    private readonly PagesField _pages;
    private readonly ChoiceField _format;
    private readonly NumberField _dpi, _quality;

    public ExtractTool() : base("Extract", "GroupOrganize")
    {
        _pages = Add(new PagesField());
        _pages.Scope = _pages.Scopes[1];
        _format = Add(new ChoiceField("ExtractFormat",
            new Choice("pdf", "PDF", true), new Choice("png", "PNG", true), new Choice("jpg", "JPEG", true), new Choice("webp", "WebP", true)));
        _dpi = Add(new NumberField("ImageDpi", 150, 36, 1200, 10));
        _quality = Add(new NumberField("ImageQuality", 90, 10, 100, 5));
        _format.PropertyChanged += (_, _) => Update();
        Update();
    }

    private void Update()
    {
        _dpi.IsVisible = _format.Key != "pdf";
        _quality.IsVisible = _format.Key is "jpg" or "webp";
    }

    public override string Summary(MainViewModel main)
    {
        var pages = _pages.Resolve(main.SelectedPageIndexes, main.CurrentPageIndex, main.Pages.Count) ?? Enumerable.Range(0, main.Pages.Count).ToList();
        return Loc.Format("SummaryExtract", pages.Count, PageRange.Format(pages));
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var name = Units.DocumentName(context.Session);

        if (_format.Key == "pdf")
        {
            var path = await context.Dialogs.SaveFile(Loc.Get("ToolExtract"), name + "_pages.pdf", Dialogs.PdfFiles);
            if (path == null) return;
            var list = pages ?? Enumerable.Range(0, context.PageCount).ToList();
            await context.Run(() =>
            {
                File.WriteAllBytes(path, PdfPages.Extract(pdf, list));
                return true;
            });
            context.Status(Loc.Format("StatusSaved", Path.GetFileName(path)));
            return;
        }

        var folder = await context.Dialogs.PickFolder(Loc.Get("PickOutputFolder"));
        if (folder == null) return;
        var format = _format.Key switch { "jpg" => ImageFormat.Jpeg, "webp" => ImageFormat.Webp, _ => ImageFormat.Png };
        double dpi = _dpi.Number;
        var quality = (int)_quality.Number;
        var written = await context.Run(() =>
        {
            var files = 0;
            PdfImages.ExportPages(pdf, pages, dpi, format, quality, (page, data) =>
            {
                File.WriteAllBytes(Path.Combine(folder, $"{name}_{page + 1:D3}{PdfImages.Extension(format)}"), data);
                files++;
            }, context.Progress, context.Cancel);
            return files;
        });
        context.Status(Loc.Format("StatusFilesWritten", written, folder));
    }
}

public sealed class ResizeTool : Tool
{
    private readonly PagesField _pages;
    private readonly ChoiceField _target;
    private readonly NumberField _width, _height, _percent;
    private readonly BoolField _keepOrientation;

    public ResizeTool() : base("Resize", "GroupOrganize")
    {
        _pages = Add(new PagesField());
        _target = Add(new ChoiceField("ResizeTarget", Units.PaperChoices().Concat([new Choice("custom", "SizeCustom"), new Choice("percent", "ResizePercent")]).ToArray()));
        _width = Add(new NumberField("WidthMm", 210, 10, 5000, 1, "0.#"));
        _height = Add(new NumberField("HeightMm", 297, 10, 5000, 1, "0.#"));
        _percent = Add(new NumberField("ScalePercent", 50, 5, 1000, 5));
        _keepOrientation = Add(new BoolField("ResizeKeepOrientation", true));
        _target.PropertyChanged += (_, _) => Update();
        Update();
    }

    private void Update()
    {
        _width.IsVisible = _height.IsVisible = _target.Key == "custom";
        _percent.IsVisible = _target.Key == "percent";
        _keepOrientation.IsVisible = _target.Key != "percent";
    }

    public override bool HasPreview => true;

    public override PreviewResult Preview(PreviewRequest request)
    {
        if (!request.Includes(_pages)) return new PreviewResult(null, Loc.Get("PreviewOutside"));
        var info = PdfPages.GetPageInfos(request.Page)[0];
        return new PreviewResult(Resize(request.Page, null, [info]));
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var infos = context.Session.View!.Pages;
        context.Commit(await context.Run(() => Resize(pdf, pages, infos)));
    }

    private byte[] Resize(byte[] pdf, List<int>? pages, IReadOnlyList<PageInfo> infos)
    {
        if (_target.Key == "percent") return PdfPages.Scale(pdf, pages, ScaleMode.Factor, _percent.Number / 100);

        var (width, height) = Units.Paper(_target.Key) ?? (_width.Mm(), _height.Mm());
        if (!_keepOrientation.Value) return PdfPages.Scale(pdf, pages, ScaleMode.FitToSize, targetWidth: width, targetHeight: height);

        // Landscape pages get the landscape version of the target so they are not shrunk into a portrait sheet.
        var all = pages ?? Enumerable.Range(0, infos.Count).ToList();
        var landscape = all.Where(i => infos[i].Width > infos[i].Height).ToList();
        var portrait = all.Except(landscape).ToList();
        double w = Math.Min(width, height), h = Math.Max(width, height);
        var result = pdf;
        if (portrait.Count > 0) result = PdfPages.Scale(result, portrait, ScaleMode.FitToSize, targetWidth: w, targetHeight: h);
        if (landscape.Count > 0) result = PdfPages.Scale(result, landscape, ScaleMode.FitToSize, targetWidth: h, targetHeight: w);
        return result;
    }
}

/// <summary>
/// Crops pages. Whatever the method, the part that will be kept is shown on the page before anything is cut: found
/// automatically, worked out from the margins, or drawn by hand and adjusted by its handles.
/// </summary>
public sealed class CropTool : Tool
{
    private readonly PagesField _pages;
    private readonly ChoiceField _mode;
    private readonly NumberField _padding, _left, _top, _right, _bottom;
    private readonly InfoField _area;
    private RectD? _region;
    private int _shown;

    public CropTool(MainViewModel main) : base("Crop", "GroupOrganize")
    {
        _pages = Add(new PagesField());
        _mode = Add(new ChoiceField("CropMode", new Choice("auto", "CropAuto"), new Choice("margins", "CropMargins"), new Choice("area", "CropArea")));
        _padding = Add(new NumberField("CropPadding", 3, 0, 100, 1, "0.#"));
        _left = Add(new NumberField("MarginLeft", 10, 0, 1000, 1, "0.#"));
        _top = Add(new NumberField("MarginTop", 10, 0, 1000, 1, "0.#"));
        _right = Add(new NumberField("MarginRight", 10, 0, 1000, 1, "0.#"));
        _bottom = Add(new NumberField("MarginBottom", 10, 0, 1000, 1, "0.#"));
        _area = Add(new InfoField());
        _mode.PropertyChanged += (_, _) => Update();
        // The marked part follows the settings as they are changed.
        SettingsChanged += () => _ = ShowKept(main);
        Update();
    }

    public override CanvasMode CanvasMode => _mode.Key == "area" ? CanvasMode.Area : CanvasMode.Select;

    private void Update()
    {
        _padding.IsVisible = _mode.Key == "auto";
        _left.IsVisible = _top.IsVisible = _right.IsVisible = _bottom.IsVisible = _mode.Key == "margins";
        Describe();
        OnPropertyChanged(nameof(CanvasMode));
    }

    private void Describe()
    {
        if (_mode.Key == "area" && _region is { } region)
            _area.SetText(Loc.Format("CropAreaChosen", $"{region.Width / Units.PointsPerMm:0} × {region.Height / Units.PointsPerMm:0} mm"));
        else
            _area.SetKey(_mode.Key == "area" ? "CropAreaHint" : "CropKeptHint");
    }

    public override Task RegionSelected(MainViewModel main, RectD region)
    {
        _region = region;
        Describe();
        return Task.CompletedTask;
    }

    public override Task RefreshAsync(MainViewModel main) => ShowKept(main);

    /// <summary>Marks on the page what would be left of it, with the rest darkened.</summary>
    private async Task ShowKept(MainViewModel main)
    {
        if (!ReferenceEquals(main.SelectedTool, this)) return;
        var shown = ++_shown;
        main.DimOutsideRegion = true;
        if (main.Session.Bytes is not { } document)
        {
            main.Region = null;
            return;
        }

        switch (_mode.Key)
        {
            case "area":
                main.Region = _region;
                break;
            case "margins":
                main.Region = new RectD(_left.Mm(), _top.Mm(), Math.Max(_left.Mm() + 1, main.PageWidth - _right.Mm()),
                    Math.Max(_top.Mm() + 1, main.PageHeight - _bottom.Mm()));
                break;
            default:
                var page = main.CurrentPageIndex;
                var padding = _padding.Mm();
                var kept = await Task.Run(() => PdfPages.AutoCropArea(document, page, padding));
                if (shown == _shown && ReferenceEquals(main.Session.Bytes, document) && main.CurrentPageIndex == page) main.Region = kept;
                break;
        }
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        switch (_mode.Key)
        {
            case "margins":
                double l = _left.Mm(), t = _top.Mm(), r = _right.Mm(), b = _bottom.Mm();
                context.Commit(await context.Run(() => PdfPages.CropMargins(pdf, pages, l, t, r, b)));
                break;
            case "area":
                var area = _region ?? throw new UserException(Loc.Get("CropAreaHint"));
                _region = null;
                context.Commit(await context.Run(() => PdfPages.Crop(pdf, pages, _ => area)));
                Update();
                break;
            default:
                var padding = _padding.Mm();
                context.Commit(await context.Run(() => PdfPages.AutoCrop(pdf, pages, padding)));
                break;
        }
    }
}
