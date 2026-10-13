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
    private readonly ModeField _mode;
    private readonly NumberField _every;
    private readonly TextField _ranges;
    private readonly InfoField _atHint;

    public SplitTool() : base("Split", "GroupOrganize")
    {
        _mode = Add(new ModeField("SplitMode",
            new Choice("every", "SplitEvery"), new Choice("ranges", "SplitRanges"),
            new Choice("single", "SplitSingle"), new Choice("selected", "SplitAtSelected")) { Columns = 2 });
        _every = Add(new NumberField("SplitPagesPerFile", 1, 1, 10000));
        _ranges = Add(new TextField("SplitRangesList", "1-2; 3-"));
        _atHint = Add(new InfoField());
        _atHint.SetKey("SplitAtSelectedHint");
        _mode.PropertyChanged += (_, _) => Update();
        Update();
    }

    private void Update()
    {
        _every.IsVisible = _mode.Key == "every";
        _ranges.IsVisible = _mode.Key == "ranges";
        _atHint.IsVisible = _mode.Key == "selected";
    }

    private List<IReadOnlyList<int>> Groups(int pageCount, IReadOnlyList<int> selected) => _mode.Key switch
    {
        "ranges" => PageRange.ParseGroups(_ranges.Value, pageCount).Select(g => (IReadOnlyList<int>)g).ToList(),
        "single" => PdfPages.GroupsOf(pageCount, 1).Select(g => (IReadOnlyList<int>)g).ToList(),
        "selected" => At(pageCount, selected),
        _ => PdfPages.GroupsOf(pageCount, (int)_every.Number).Select(g => (IReadOnlyList<int>)g).ToList()
    };

    /// <summary>The document divided in front of every selected page: each of them starts a file.</summary>
    private static List<IReadOnlyList<int>> At(int pageCount, IReadOnlyList<int> selected)
    {
        var starts = selected.Where(page => page > 0 && page < pageCount).Append(0).Distinct().Order().ToList();
        return starts.Select((start, i) => (IReadOnlyList<int>)Enumerable.Range(start, (i + 1 < starts.Count ? starts[i + 1] : pageCount) - start).ToList())
            .Where(group => group.Count > 0).ToList();
    }

    public override string Summary(MainViewModel main)
    {
        var groups = Groups(main.Pages.Count, main.SelectedPageIndexes);
        // The first few files spelled out, so a slip in the ranges shows before anything is written.
        var listed = string.Join("; ", groups.Take(6).Select(PageRange.Format)) + (groups.Count > 6 ? "; …" : "");
        return Loc.Format("SummarySplit", groups.Count, listed);
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var groups = Groups(context.PageCount, context.SelectedPages);
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
    private readonly MainViewModel _main;
    private readonly PagesField _pages;
    private readonly SliderField _readiness;
    private readonly InfoField _noReading, _found, _thisPage;
    private readonly ActionField _setup, _confirm, _leave, _take;

    // What was found, for one state of the document: what speaks for each page being turned, what that comes to at
    // the limit now set, the pages somebody said to leave as they are, and the close calls somebody has looked at.
    private readonly Dictionary<int, PageTurn> _evidence = [], _turns = [];
    private readonly HashSet<int> _left = [], _settled = [];
    private byte[]? _document;

    public RotateTool(MainViewModel main) : base("Rotate", "GroupOrganize")
    {
        _main = main;
        _pages = Add(new PagesField());
        _pages.Scope = _pages.Scopes[1];
        Add(new RowField(
            new ActionField("RotateLeftShort", () => main.RunBusy(context => Turn(context, -90))) { Icon = Controls.Icons.RotateLeft, Large = true },
            new ActionField("RotateRightShort", () => main.RunBusy(context => Turn(context, 90))) { Icon = Controls.Icons.RotateRight, Large = true },
            new ActionField("RotateHalfShort", () => main.RunBusy(context => Turn(context, 180))) { Icon = Controls.Icons.RotateHalf, Large = true }));
        Add(new RowField(
            new ActionField("MirrorAcross", () => main.RunBusy(context => Mirror(context, false))) { Icon = Controls.Icons.MirrorAcross, Large = true },
            new ActionField("MirrorDown", () => main.RunBusy(context => Mirror(context, true))) { Icon = Controls.Icons.MirrorDown, Large = true }));
        Add(new InfoField()).SetKey("RotateActNow");

        // Pages that are turned, found by the app.
        _noReading = Add(new InfoField());
        _noReading.SetKey("RotateNoReading");
        _setup = Add(OcrSetup.Button(main));
        _readiness = Add(new SliderField("RotateReadiness", 50, 0, 100, 5));
        Add(new ActionField("RotateFind", () => main.RunBusy(Detect)) { Icon = Controls.Icons.Search });
        _found = Add(new InfoField());
        _thisPage = Add(new InfoField());
        _confirm = new ActionField("SheetConfirm", () => Do(() => _settled.Add(main.CurrentPageIndex)));
        _leave = new ActionField("RotateLeave", () => Do(() => _left.Add(main.CurrentPageIndex)));
        _take = new ActionField("RotateTake", () => Do(() => _left.Remove(main.CurrentPageIndex)));
        Add(new RowField(_confirm, _leave, _take));
        // Another limit judges the same evidence anew; the pages need not be looked at again for that.
        _readiness.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(NumberField.Value) || _evidence.Count == 0) return;
            foreach (var (page, found) in _evidence) _turns[page] = PdfOrientation.At(found, _readiness.Number / 100);
            Show();
        };
        _noReading.IsVisible = _setup.IsVisible = _confirm.IsVisible = _leave.IsVisible = _take.IsVisible = false;
    }

    public override bool HasApply => ToTurn().Count > 0;
    public override string ApplyLabel => Loc.Get("RotateApply");
    public override bool HasPreview => true;

    private Task Do(Action action)
    {
        action();
        Show();
        return Task.CompletedTask;
    }

    // ---- by hand ------------------------------------------------------------------------------------------------

    /// <summary>The pages to show as selected after a change, the one on view first so that it stays on view.</summary>
    private static List<int> Selection(ToolContext context)
    {
        var selected = context.SelectedPages.Count > 0 ? context.SelectedPages.ToList() : [context.CurrentPage];
        return selected.Contains(context.CurrentPage) ? [context.CurrentPage, ..selected.Where(page => page != context.CurrentPage)] : selected;
    }

    private async Task Turn(ToolContext context, int degrees)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var selection = Selection(context);
        var count = pages?.Count ?? context.PageCount;
        var result = await context.Run(() => PdfPages.Rotate(pdf, pages, degrees));
        // What was found for these pages is still true of them, less the turn they have now been given.
        foreach (var page in pages ?? Enumerable.Range(0, context.PageCount).ToList())
            if (_evidence.TryGetValue(page, out var found))
            {
                int Less(int turn) => ((turn - degrees) % 360 + 360) % 360;
                _evidence[page] = found with { Degrees = Less(found.Degrees), Leaning = Less(found.Leaning) };
                _turns[page] = _turns[page] with { Degrees = Less(_turns[page].Degrees), Leaning = Less(_turns[page].Leaning), Doubtful = false };
                _settled.Add(page);
            }

        Replace(context, pdf, result, selection);
        context.Status(Loc.Format("StatusPagesTurned", count));
    }

    private async Task Mirror(ToolContext context, bool topToBottom)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var selection = Selection(context);
        var count = pages?.Count ?? context.PageCount;
        var result = await context.Run(() => PdfPages.Mirror(pdf, pages, topToBottom));
        // A mirrored page is another page: which way it stands would have to be looked at again.
        foreach (var page in pages ?? Enumerable.Range(0, context.PageCount).ToList())
        {
            _evidence.Remove(page);
            _turns.Remove(page);
        }

        Replace(context, pdf, result, selection);
        context.Status(Loc.Format("StatusPagesMirrored", count));
    }

    /// <summary>Puts the changed document in place, keeping what was found for the pages of the one before.</summary>
    private void Replace(ToolContext context, byte[] before, byte[] result, List<int> selection)
    {
        if (ReferenceEquals(_document, before)) _document = result;
        context.Commit(result);
        context.Main.RestoreSelection(selection);
        Show();
    }

    // ---- found by the app ---------------------------------------------------------------------------------------

    private void Forget()
    {
        _evidence.Clear();
        _turns.Clear();
        _left.Clear();
        _settled.Clear();
        _document = null;
        Show();
    }

    private bool InDoubt(int page) => _turns.TryGetValue(page, out var turn) && turn.Doubtful && !_settled.Contains(page) && !_left.Contains(page);

    /// <summary>The pages that Apply will turn, with the turn each gets.</summary>
    private List<(int Page, int Degrees)> ToTurn() => _turns
        .Where(found => found.Value.IsTurned && !_left.Contains(found.Key))
        .Select(found => (found.Key, found.Value.Degrees)).OrderBy(found => found.Key).ToList();

    private async Task Detect(ToolContext context)
    {
        var pdf = context.Pdf;
        var view = context.Session.View!;
        var pages = context.Resolve(_pages) ?? Enumerable.Range(0, context.PageCount).ToList();
        var languages = context.Settings.OcrLanguages.ToList();
        var readiness = _readiness.Number / 100;
        var canRead = context.Main.Supplies.Lack() == null;
        context.Status(Loc.Get("StatusLookingAtPages"));

        Dictionary<int, PageTurn> Look(IReadOnlyList<int> which, bool read, double from, double share) => which.Select((page, i) =>
        {
            context.Cancel.ThrowIfCancellationRequested();
            var turn = PdfOrientation.Find(view, page, languages, read, readiness, context.Cancel);
            context.Progress.Report(from + share * (i + 1) / which.Count);
            return (page, turn);
        }).ToDictionary(found => found.page, found => found.turn);

        var found = await context.Run(() => Look(pages, canRead, 0, canRead ? 1 : 0.5));
        // Scans that could only be looked at, with text recognition lacking: reading them tells much more, so what
        // it lacks is offered now, when there is something to read. A no leaves the findings as they are.
        var scans = found.Where(turn => turn.Value.Evidence is TurnEvidence.Look or TurnEvidence.None).Select(turn => turn.Key).ToList();
        if (!canRead && scans.Count > 0)
            try
            {
                await context.Main.Supplies.Ensure(context);
                context.Status(Loc.Get("StatusLookingAtPages"));
                foreach (var (page, turn) in await context.Run(() => Look(scans, true, 0.5, 0.5))) found[page] = turn;
            }
            catch (Exception e) when (e is UserException || (e is OperationCanceledException && !context.Cancel.IsCancellationRequested))
            {
                // Not to be had, or not wanted: the look of the print has to do.
            }

        _evidence.Clear();
        _turns.Clear();
        _left.Clear();
        _settled.Clear();
        foreach (var (page, turn) in found) _evidence[page] = _turns[page] = turn;
        _document = pdf;
        Show();
        var turned = ToTurn().Count;
        context.Status(turned == 0 ? Loc.Format("RotateFoundNone", pages.Count) : Loc.Format("StatusTurnedFound", turned, pages.Count));

        // The first page that needs a look, or else the first that will be turned, is put on view. The selection
        // stays what it is: it may be what says which pages are meant.
        var first = _turns.Keys.Where(InDoubt).Order().Cast<int?>().FirstOrDefault() ?? ToTurn().Select(turn => (int?)turn.Page).FirstOrDefault();
        if (first is not { } show || show == context.CurrentPage) return;
        var selected = context.SelectedPages.ToList();
        if (selected.Contains(show)) context.Main.RestoreSelection([show, ..selected.Where(page => page != show)]);
        else if (_pages.Scope.Key != "selected") context.Main.GoToPage(show);
    }

    public override Task RefreshAsync(MainViewModel main)
    {
        // What was found describes one state of the document; any change from elsewhere makes it void.
        if (!ReferenceEquals(main.Session.Bytes, _document) && _turns.Count > 0) Forget();
        Show();
        return Task.CompletedTask;
    }

    public override void Deactivated(MainViewModel main) => main.SetPageBadges(null);

    private static string Words(int degrees, string right, string left, string half) => degrees switch
    {
        90 => Loc.Get(right),
        270 => Loc.Get(left),
        _ => Loc.Get(half)
    };

    /// <summary>Brings the panel, the marks in the page list and the line under Apply in step with what was found.</summary>
    private void Show()
    {
        var lack = _main.Supplies.Lack();
        _noReading.IsVisible = lack != null;
        _setup.IsVisible = lack is { CanFetch: true };

        var active = ReferenceEquals(_main.SelectedTool, this);
        var page = _main.CurrentPageIndex;
        var has = _turns.TryGetValue(page, out var turn);
        var left = _left.Contains(page);
        var doubt = InDoubt(page);
        if (!has || turn is { IsTurned: false, Doubtful: false }) _thisPage.SetText("");
        else if (left) _thisPage.SetKey("RotateThisLeftAlone");
        else if (turn!.IsTurned)
            _thisPage.SetText(Words(turn.Degrees, "RotateThisRight", "RotateThisLeft", "RotateThisHalf") + (doubt ? " " + Loc.Get("RotateNotCertain") : ""));
        else _thisPage.SetText(doubt ? Loc.Get("RotateThisMaybe") : "");
        _confirm.IsVisible = has && doubt;
        _leave.IsVisible = has && turn!.IsTurned && !left;
        _take.IsVisible = has && left;

        if (_turns.Count == 0) _found.SetText("");
        else
        {
            var turned = ToTurn();
            var lines = new List<string>
            {
                turned.Count == 0
                    ? Loc.Format("RotateFoundNone", _turns.Count)
                    : Loc.Format("RotateFound", turned.Count, _turns.Count, PageRange.Format(turned.Select(found => found.Page)))
            };
            var doubtful = _turns.Keys.Count(InDoubt);
            if (doubtful > 0) lines.Add(Loc.Format("RotateFoundDoubt", doubtful));
            var blank = _turns.Values.Count(found => found.Evidence == TurnEvidence.None);
            if (blank > 0) lines.Add(Loc.Format("RotateUnknown", blank));
            _found.SetText(string.Join(" ", lines));
        }

        if (active)
        {
            var badges = new Dictionary<int, PageBadge>();
            foreach (var (index, found) in _turns)
            {
                if (_left.Contains(index)) continue;
                var red = InDoubt(index);
                if (found.IsTurned)
                    badges[index] = new PageBadge(found.Degrees switch { 90 => "90°", 270 => "−90°", _ => "180°" } + (red ? "?" : ""), red,
                        red ? Loc.Get("RotateBadgeNotCertain") : Words(found.Degrees, "RotateBadgeRight", "RotateBadgeLeft", "RotateBadgeHalf"));
                else if (red)
                    badges[index] = new PageBadge("?", true, Loc.Get("RotateBadgeMaybe"));
            }

            _main.SetPageBadges(badges);
        }

        OnPropertyChanged(nameof(HasApply));
        OnSettingsChanged();
    }

    public override string? Summary(MainViewModel main)
    {
        var turned = ToTurn();
        if (turned.Count == 0) return null;
        var text = Loc.Format("SummaryRotate", turned.Count, PageRange.Format(turned.Select(found => found.Page)));
        var doubtful = _turns.Keys.Count(InDoubt);
        return doubtful > 0 ? text + " " + Loc.Format("RotateFoundDoubt", doubtful) : text;
    }

    /// <summary>The page on view as it will stand once the pages are set upright.</summary>
    public override PreviewResult? Preview(PreviewRequest request)
    {
        if (!ReferenceEquals(request.Document, _document) || _left.Contains(request.PageIndex)) return null;
        return _turns.TryGetValue(request.PageIndex, out var turn) && turn.IsTurned
            ? new PreviewResult(PdfPages.Rotate(request.Page, null, turn.Degrees))
            : null;
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        if (!ReferenceEquals(pdf, _document)) return;
        var turned = ToTurn();
        if (turned.Count == 0) return;
        var doubtful = turned.Where(turn => InDoubt(turn.Page)).Select(turn => turn.Page).ToList();
        var result = await context.Run(() =>
        {
            var document = pdf;
            foreach (var degrees in turned.GroupBy(turn => turn.Degrees))
                document = PdfPages.Rotate(document, degrees.Select(turn => turn.Page).ToList(), degrees.Key);
            return document;
        });
        Forget();
        context.Commit(result);
        // What was turned on a close call is selected afterwards, to be looked at first; else all that was turned.
        context.Main.RestoreSelection(doubtful.Count > 0 ? doubtful : turned.Select(turn => turn.Page).ToList());
        context.Status(doubtful.Count > 0
            ? Loc.Format("StatusTurnedUprightDoubt", turned.Count, doubtful.Count)
            : Loc.Format("StatusTurnedUpright", turned.Count));
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

/// <summary>
/// Moves the pages that are selected in the page list: a place up or down, to the beginning or to the end. Every
/// button acts at once and leaves the moved pages selected, so it can be pressed again until they are where they
/// belong.
/// </summary>
public sealed class ReorderTool : Tool
{
    private readonly InfoField _chosen;

    public ReorderTool(MainViewModel main) : base("Reorder", "GroupOrganize")
    {
        Add(new InfoField()).SetKey("ReorderHint");
        _chosen = Add(new InfoField());
        Add(new RowField(Button("up", "ReorderUp", Controls.Icons.Up), Button("down", "ReorderDown", Controls.Icons.Down)));
        Add(new RowField(Button("first", "ReorderFirst", Controls.Icons.ToStart), Button("last", "ReorderLast", Controls.Icons.ToEnd)));
        Add(new ActionField("ReorderReverse", () => main.RunBusy(context => Move(context, "reverse"))) { Icon = Controls.Icons.Reverse });

        ActionField Button(string action, string label, Avalonia.Media.Geometry icon) =>
            new(label, () => main.RunBusy(context => Move(context, action))) { Icon = icon, Large = true };
    }

    public override bool HasApply => false;

    public override Task RefreshAsync(MainViewModel main)
    {
        Describe(main);
        return Task.CompletedTask;
    }

    public override void PagesSelected(MainViewModel main) => Describe(main);

    /// <summary>Says which pages the buttons will move.</summary>
    private void Describe(MainViewModel main)
    {
        if (!main.HasDocument)
        {
            _chosen.SetText("");
            return;
        }

        var pages = (main.SelectedPageIndexes.Count > 0 ? main.SelectedPageIndexes : [main.CurrentPageIndex]).Order().ToList();
        _chosen.SetText(pages.Count == 1 ? Loc.Format("PagesChosenOne", pages[0] + 1) : Loc.Format("PagesChosen", pages.Count, PageRange.Format(pages)));
    }

    private static async Task Move(ToolContext context, string action)
    {
        var pdf = context.Pdf;
        var count = context.PageCount;
        var selected = (context.SelectedPages.Count > 0 ? context.SelectedPages : [context.CurrentPage]).Order().ToList();
        var order = Enumerable.Range(0, count).ToList();

        switch (action)
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
        // The moved pages stay selected so the button can be pressed again.
        var moved = action == "reverse" ? [] : selected.Select(page => order.IndexOf(page)).ToList();
        if (moved.Count > 0) context.Main.RestoreSelection(moved);
        context.Status(Loc.Format("StatusPagesMoved", action == "reverse" ? count : selected.Count));
    }
}

public sealed class InsertTool : Tool
{
    private readonly ModeField _what, _where;
    private readonly ChoiceField _blankSize, _imageSize;
    private readonly NumberField _count;
    private readonly FileField _pdf;
    private readonly TextField _pdfPages;
    private readonly FileListField _images;

    public InsertTool(MainViewModel main) : base("Insert", "GroupOrganize")
    {
        _what = Add(new ModeField("InsertWhat", new Choice("blank", "InsertBlank"), new Choice("pdf", "InsertPdf"), new Choice("images", "InsertImages")));
        _where = Add(new ModeField("InsertWhere",
            new Choice("after", "InsertAfter"), new Choice("before", "InsertBefore"),
            new Choice("start", "InsertStart"), new Choice("end", "InsertEnd")) { Columns = 2 });
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
    private readonly ModeField _format;
    private readonly SliderField _dpi, _quality;

    public ExtractTool() : base("Extract", "GroupOrganize")
    {
        _pages = Add(new PagesField());
        _pages.Scope = _pages.Scopes[1];
        _format = Add(new ModeField("ExtractFormat",
            new Choice("pdf", "PDF", true), new Choice("png", "PNG", true), new Choice("jpg", "JPEG", true), new Choice("webp", "WebP", true)));
        _dpi = Add(new SliderField("ImageDpi", 150, 40, 1200, 10).WithStops("", 100, 150, 300, 600));
        _quality = Add(new SliderField("ImageQuality", 90, 10, 100, 5));
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
        _width = new NumberField("WidthMm", 210, 10, 5000, 1, "0.#");
        _height = new NumberField("HeightMm", 297, 10, 5000, 1, "0.#");
        Add(new RowField(_width, _height));
        _percent = Add(new SliderField("ScalePercent", 50, 5, 400, 5).WithStops(" %", 25, 50, 75, 150, 200));
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
/// Crops pages. The part that will be kept is shown on the page before anything is cut, with the rest darkened:
/// found by the app around what is on the page, or set by hand, by pulling the frame on the page or typing the
/// margins. Set by hand, the same margins come off every chosen page.
/// </summary>
public sealed class CropTool : Tool
{
    private readonly PagesField _pages;
    private readonly ModeField _mode;
    private readonly SliderField _padding;
    private readonly NumberField _left, _top, _right, _bottom;
    private readonly InfoField _hint;
    private int _shown;

    public CropTool(MainViewModel main) : base("Crop", "GroupOrganize")
    {
        _pages = Add(new PagesField());
        _mode = Add(new ModeField("CropMode", new Choice("auto", "CropAuto"), new Choice("hand", "CropByHand")));
        _padding = Add(new SliderField("CropPadding", 3, 0, 30, 0.5m, "0.#"));
        _hint = Add(new InfoField());
        _left = new NumberField("MarginLeft", 10, 0, 1000, 1, "0.#");
        _right = new NumberField("MarginRight", 10, 0, 1000, 1, "0.#");
        _top = new NumberField("MarginTop", 10, 0, 1000, 1, "0.#");
        _bottom = new NumberField("MarginBottom", 10, 0, 1000, 1, "0.#");
        Add(new RowField(_left, _right));
        Add(new RowField(_top, _bottom));
        _mode.PropertyChanged += (_, _) => Update();
        // The marked part follows the settings as they are changed.
        SettingsChanged += () => _ = ShowKept(main);
        Update();
    }

    private bool ByHand => _mode.Key == "hand";

    /// <summary>By hand, the frame around what is kept can be pulled by its edges and corners, and moved as a whole.</summary>
    public override CanvasMode CanvasMode => ByHand ? CanvasMode.Area : CanvasMode.Select;

    private void Update()
    {
        _padding.IsVisible = !ByHand;
        _left.IsVisible = _top.IsVisible = _right.IsVisible = _bottom.IsVisible = ByHand;
        _hint.SetKey(ByHand ? "CropHandHint" : "CropKeptHint");
        OnPropertyChanged(nameof(CanvasMode));
    }

    /// <summary>The frame was pulled on the page: the margins are what it leaves around itself.</summary>
    public override Task RegionSelected(MainViewModel main, RectD region)
    {
        if (!ByHand || main.PageWidth <= 0 || main.PageHeight <= 0) return Task.CompletedTask;
        _left.Set(region.Left / Units.PointsPerMm);
        _top.Set(region.Top / Units.PointsPerMm);
        _right.Set((main.PageWidth - region.Right) / Units.PointsPerMm);
        _bottom.Set((main.PageHeight - region.Bottom) / Units.PointsPerMm);
        return Task.CompletedTask;
    }

    public override Task RefreshAsync(MainViewModel main) => ShowKept(main);

    /// <summary>What the margins leave of a page of this size.</summary>
    private RectD Kept(double width, double height) => new(_left.Mm(), _top.Mm(), Math.Max(_left.Mm() + 1, width - _right.Mm()),
        Math.Max(_top.Mm() + 1, height - _bottom.Mm()));

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

        if (ByHand)
        {
            var kept = Kept(main.PageWidth, main.PageHeight);
            main.Region = kept;
            _hint.SetText(Loc.Get("CropHandHint") + " " +
                          Loc.Format("CropKeeps", $"{kept.Width / Units.PointsPerMm:0} × {kept.Height / Units.PointsPerMm:0} mm"));
            return;
        }

        var page = main.CurrentPageIndex;
        var padding = _padding.Mm();
        var found = await Task.Run(() => PdfPages.AutoCropArea(document, page, padding));
        if (shown == _shown && ReferenceEquals(main.Session.Bytes, document) && main.CurrentPageIndex == page) main.Region = found;
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        if (ByHand)
        {
            double l = _left.Mm(), t = _top.Mm(), r = _right.Mm(), b = _bottom.Mm();
            context.Commit(await context.Run(() => PdfPages.CropMargins(pdf, pages, l, t, r, b)));
            return;
        }

        var padding = _padding.Mm();
        context.Commit(await context.Run(() => PdfPages.AutoCrop(pdf, pages, padding)));
    }
}

/// <summary>
/// Puts several pages on each sheet, scaled down. The sheet the page on view will be on is shown before anything
/// is changed, and the line above Apply says how many sheets the pages come to and how large they are.
/// </summary>
public sealed class PerSheetTool : Tool
{
    private readonly PagesField _pages;
    private readonly ModeField _count, _turn, _order;
    private readonly ChoiceField _paper;
    private readonly SliderField _margin, _gap;
    private readonly BoolField _frame;

    public PerSheetTool() : base("PerSheet", "GroupOrganize")
    {
        _pages = Add(new PagesField());
        _count = Add(new ModeField("PerSheetCount", new Choice("2", "2", true), new Choice("4", "4", true), new Choice("6", "6", true),
            new Choice("8", "8", true), new Choice("9", "9", true), new Choice("16", "16", true)));
        _paper = Add(new ChoiceField("PerSheetPaper", Units.PaperChoices(new Choice("same", "PerSheetSame"))));
        _turn = Add(new ModeField("PerSheetTurn",
            new Choice("auto", "PerSheetTurnAuto"), new Choice("upright", "PerSheetUpright"), new Choice("sideways", "PerSheetSideways")));
        _order = Add(new ModeField("PerSheetOrder",
            new Choice("rows", "SheetOrderRows") { Icon = Controls.Icons.OrderRows }, new Choice("rtl", "SheetOrderRtl") { Icon = Controls.Icons.OrderRowsBack },
            new Choice("columns", "SheetOrderColumns") { Icon = Controls.Icons.OrderColumns }));
        _margin = Add(new SliderField("PerSheetMargin", 6, 0, 30, 1).WithStops(" mm", 0, 5, 10, 15));
        _gap = Add(new SliderField("PerSheetGap", 3, 0, 20, 1).WithStops(" mm", 0, 3, 5, 10));
        _frame = Add(new BoolField("PerSheetFrame"));
    }

    public override bool HasPreview => true;

    /// <summary>The sheet as the settings make it, for pages of the given size (that of the first page that goes on a sheet).</summary>
    private SheetPlan Plan(double pageWidth, double pageHeight)
    {
        var (paperWidth, paperHeight) = Units.Paper(_paper.Key) ?? (pageWidth, pageHeight);
        var turn = _turn.Key switch { "upright" => SheetTurn.Upright, "sideways" => SheetTurn.Sideways, _ => SheetTurn.Automatic };
        return PdfPagesPerSheet.Plan(int.Parse(_count.Key), paperWidth, paperHeight, turn, pageWidth, pageHeight, _margin.Mm(), _gap.Mm()) with
        {
            Order = _order.Key switch { "rtl" => SheetOrder.RowsRightToLeft, "columns" => SheetOrder.Columns, _ => SheetOrder.Rows },
            Frame = _frame.Value
        };
    }

    public override PreviewResult Preview(PreviewRequest request)
    {
        var pages = request.Resolve(_pages);
        var (width, height) = PdfPagesPerSheet.SizeOf(request.Document, pages is { Count: > 0 } ? pages[0] : 0);
        return PdfPagesPerSheet.SheetWith(request.Document, pages, request.PageIndex, Plan(width, height), request.Cancel) is { } found
            ? new PreviewResult(found.Sheet, Loc.Format("PerSheetPreview", found.Number, found.Of))
            : new PreviewResult(null, Loc.Get("PreviewOutside"));
    }

    public override string? Summary(MainViewModel main)
    {
        if (main.Session.View is not { } view) return null;
        var pages = _pages.Resolve(main.SelectedPageIndexes, main.CurrentPageIndex, main.Pages.Count) ?? Enumerable.Range(0, main.Pages.Count).ToList();
        if (pages.Count == 0) return null;
        var plan = Plan(view.Pages[pages[0]].Width, view.Pages[pages[0]].Height);
        return Loc.Format("SummaryPerSheet", pages.Count, PdfPagesPerSheet.SheetCount(pages.Count, plan),
            $"{plan.Width / Units.PointsPerMm:0} × {plan.Height / Units.PointsPerMm:0} mm", $"{plan.ScaleOf(view.Pages[pages[0]].Width, view.Pages[pages[0]].Height):P0}");
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var first = pages is { Count: > 0 } ? pages[0] : 0;
        var count = pages?.Count ?? context.PageCount;
        var size = context.Session.View!.Pages[first];
        var plan = Plan(size.Width, size.Height);
        context.Commit(await context.Run(() => PdfPagesPerSheet.Arrange(pdf, pages, plan, context.Progress, context.Cancel)));
        var sheets = PdfPagesPerSheet.SheetCount(count, plan);
        // The sheets are what is new; they are selected so the next step can go on with them.
        context.Main.RestoreSelection(Enumerable.Range(first, sheets).ToList());
        context.Status(Loc.Format("StatusPerSheet", count, sheets));
    }
}
