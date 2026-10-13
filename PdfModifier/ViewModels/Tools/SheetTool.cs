using System.ComponentModel;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Localization;
using PdfModifier.Services;

namespace PdfModifier.ViewModels.Tools;

/// <summary>
/// Cuts sheets that show several pages at once (a scan of an open book, a photograph of pages lying side by side,
/// a print with four pages per sheet) into pages of their own. The pages found are framed and numbered on the sheet
/// before anything is cut: every frame can be moved, resized, removed or drawn anew, and their order is either
/// worked out (printed page numbers first, reading order otherwise) or set by clicking them one after the other.
/// <para>
/// How readily a sheet is divided is set with a slider. Where the result is a close call the frames turn red, and so
/// does the mark the sheet carries in the page list, so that a look over the list shows what needs checking.
/// </para>
/// </summary>
public sealed class SheetTool : Tool
{
    private enum Numbers
    {
        Unread,
        Reading,
        Read,
        Unavailable
    }

    /// <summary>One page found on a sheet.</summary>
    private sealed class Piece(CanvasObject frame)
    {
        public CanvasObject Frame => frame;
        public RectD Area => frame.Model.Bounds;

        /// <summary>The page number printed on it, when one was read.</summary>
        public int? Number { get; set; }

        /// <summary>Its place among the pages the user has clicked so far.</summary>
        public int? Clicked { get; set; }
    }

    /// <summary>What is going to happen to one sheet.</summary>
    private sealed class Plan(int page)
    {
        public int Page => page;
        public List<Piece> Pieces { get; } = [];
        public Numbers Numbers { get; set; }
        public CancellationTokenSource? Reading { get; set; }

        /// <summary>True while the pages were found by a close call that nobody has looked at yet.</summary>
        public bool Doubtful { get; set; }

        /// <summary>True for a sheet that is to stay as it is, whatever was found on it.</summary>
        public bool Skip { get; set; }

        /// <summary>A small picture of the sheet, from which the pictures of its pages are cut.</summary>
        public Bitmap? Picture { get; set; }
    }

    private sealed record Division(string Key, int Columns, int Rows, double Sensitivity);

    private readonly MainViewModel _main;
    private readonly PagesField _pages;
    private readonly ChoiceField _layout, _order;
    private readonly SliderField _sensitivity;
    private readonly NumberField _columns, _rows;
    private readonly InfoField _found, _hint;
    private readonly PieceListField _result;
    private readonly ActionField _add, _remove, _again, _restart, _confirm, _leave, _take;
    private readonly BoolField _keep;

    // Plans are made for one state of the document; any change to it makes the page numbers they go by meaningless.
    private readonly Dictionary<int, Plan> _plans = [];

    // What was found on sheets that have not been on view: enough to mark them in the page list and to cut them.
    private readonly Dictionary<int, SheetFinding> _findings = [];
    private CancellationTokenSource? _survey, _settling;
    private byte[]? _document;
    private Plan? _plan;
    private int _ticket;
    private bool _drawing;

    public SheetTool(MainViewModel main) : base("Sheets", "GroupOrganize")
    {
        _main = main;
        _pages = Add(new PagesField());
        _pages.Scope = _pages.Scopes[1];
        _layout = Add(new ChoiceField("SheetLayout",
            new Choice("auto", "SheetLayoutAuto"), new Choice("two", "SheetLayoutTwo"), new Choice("stack", "SheetLayoutStack"),
            new Choice("four", "SheetLayoutFour"), new Choice("grid", "SheetLayoutGrid")));
        _sensitivity = Add(new SliderField("SheetSensitivity", 50, 0, 100, 5));
        _columns = new NumberField("SheetColumns", 3, 1, 12);
        _rows = new NumberField("SheetRows", 2, 1, 12);
        Add(new RowField(_columns, _rows));
        _order = Add(new ChoiceField("SheetOrder",
            new Choice("auto", "SheetOrderAuto"), new Choice("rows", "SheetOrderRows"), new Choice("rtl", "SheetOrderRtl"),
            new Choice("columns", "SheetOrderColumns"), new Choice("click", "SheetOrderClick")));
        _found = Add(new InfoField());
        _confirm = new ActionField("SheetConfirm", () => Do(Confirm));
        _leave = new ActionField("SheetLeave", () => Do(() => Skip(true)));
        _take = new ActionField("SheetTake", () => Do(() => Skip(false)));
        Add(new RowField(_confirm, _leave, _take));
        _result = Add(new PieceListField("SheetResult"));
        _result.Chosen = item => Chosen(item);
        _restart = Add(new ActionField("SheetRestart", () => Do(RestartOrder)));
        _keep = Add(new BoolField("SheetKeep"));
        _hint = Add(new InfoField());
        _hint.SetKey("SheetAdjustHint");
        _add = new ActionField("SheetAdd", () => Do(StartDrawing));
        _remove = new ActionField("SheetRemove", () => Do(RemoveSelected));
        Add(new RowField(_add, _remove));
        _again = Add(new ActionField("SheetAgain", () => FindAgain()));

        _layout.PropertyChanged += OnDivisionChanged;
        _columns.PropertyChanged += OnDivisionChanged;
        _rows.PropertyChanged += OnDivisionChanged;
        _sensitivity.PropertyChanged += OnDivisionChanged;
        _pages.PropertyChanged += (_, _) => Survey();
        _order.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChoiceField.Selected)) OrderChanged();
        };
        Update();
    }

    public override string ApplyLabel => Loc.Get("SheetApply");

    public override CanvasMode CanvasMode => _drawing ? CanvasMode.Region : CanvasMode.Select;

    private bool ByClicking => _order.Key == "click";

    private Division Dividing() => _layout.Key switch
    {
        "two" => new Division("grid", 2, 1, 0),
        "stack" => new Division("grid", 1, 2, 0),
        "four" => new Division("grid", 2, 2, 0),
        "grid" => new Division("grid", (int)_columns.Number, (int)_rows.Number, 0),
        _ => new Division("auto", 0, 0, _sensitivity.Number / 100)
    };

    private static Task Do(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private void Update()
    {
        var shown = _plan is { Skip: false };
        _columns.IsVisible = _rows.IsVisible = _layout.Key == "grid";
        _sensitivity.IsVisible = _layout.Key == "auto";
        _restart.IsVisible = shown && ByClicking && _plan is { Pieces.Count: > 1 };
        _remove.IsVisible = shown && _main.SelectedObject?.Model is SheetArea;
        _result.IsVisible = shown && _plan is { Pieces.Count: > 0 };
        _hint.IsVisible = _add.IsVisible = _again.IsVisible = shown;
        _confirm.IsVisible = _plan is { Doubtful: true, Skip: false };
        _leave.IsVisible = shown;
        _take.IsVisible = _plan is { Skip: true };
    }

    // ---- finding the pages of the sheet on view -----------------------------------------------------------------

    private static SheetFinding Find(PdfView view, int page, Division division) => division.Key == "auto"
        ? PdfSheets.Find(view, page, division.Sensitivity)
        : new SheetFinding(PdfSheets.Grid(view, page, division.Columns, division.Rows), 1, false);

    public override async Task RefreshAsync(MainViewModel main)
    {
        var ticket = ++_ticket;
        StopDrawing();
        main.Objects.Clear();
        main.SelectedObject = null;
        Show(null);
        if (main.Session.Bytes is not { } document || main.Session.View is not { } view)
        {
            DropPlans();
            Describe();
            return;
        }

        if (!ReferenceEquals(document, _document))
        {
            DropPlans();
            _document = document;
        }

        var page = main.CurrentPageIndex;
        if (!_plans.TryGetValue(page, out var plan))
        {
            if (!_findings.TryGetValue(page, out var finding))
            {
                _found.SetKey("SheetLooking");
                var division = Dividing();
                try
                {
                    finding = await Task.Run(() => Find(view, page, division));
                }
                catch (ObjectDisposedException)
                {
                    // The document was replaced meanwhile, which asks for all of this again.
                    return;
                }

                if (ticket != _ticket || !ReferenceEquals(main.Session.Bytes, document)) return;
            }

            plan = new Plan(page) { Doubtful = finding.Doubtful };
            foreach (var area in finding.Pages) plan.Pieces.Add(NewPiece(plan, area));
            _plans[page] = plan;
            _findings.Remove(page);
        }

        Show(plan);
        if (!plan.Skip)
            foreach (var piece in plan.Pieces)
                main.Objects.Add(piece.Frame);
        Renumber();
        if (plan.Picture == null) _ = LoadPicture(plan, view, document);
        if (!ByClicking && _order.Key == "auto") ReadNumbers(plan);
        Survey();
    }

    private static Piece NewPiece(Plan plan, RectD area) => new(new CanvasObject(new SheetArea
    {
        Id = plan.Pieces.Select(p => p.Frame.Id).DefaultIfEmpty().Max() + 1, Bounds = area, Doubtful = plan.Doubtful
    }));

    /// <summary>Makes a plan the one on view; the picture of the sheet shown before is let go.</summary>
    private void Show(Plan? plan)
    {
        if (ReferenceEquals(plan, _plan)) return;
        _result.Items.Clear();
        if (_plan is { } previous)
        {
            previous.Reading?.Cancel();
            previous.Picture?.Dispose();
            previous.Picture = null;
            if (previous.Numbers == Numbers.Reading) previous.Numbers = Numbers.Unread;
        }

        _plan = plan;
    }

    private void DropPlans()
    {
        _survey?.Cancel();
        _survey = null;
        Show(null);
        _plans.Clear();
        _findings.Clear();
        _document = null;
        _main.SetPageBadges(null);
    }

    public override void Deactivated(MainViewModel main)
    {
        _ticket++;
        _settling?.Cancel();
        StopDrawing();
        DropPlans();
    }

    private async Task LoadPicture(Plan plan, PdfView view, byte[] document)
    {
        try
        {
            var info = view.Pages[plan.Page];
            var scale = 720 / Math.Max(1, Math.Max(info.Width, info.Height));
            var picture = await Task.Run(() =>
            {
                using var rendered = view.Render(plan.Page, scale);
                return BitmapConverter.ToAvalonia(rendered);
            });
            if (!ReferenceEquals(_plan, plan) || !ReferenceEquals(_document, document))
            {
                picture.Dispose();
                return;
            }

            plan.Picture = picture;
            Renumber();
        }
        catch (ObjectDisposedException)
        {
            // The document was replaced while the picture was being made.
        }
    }

    /// <summary>Reads the page numbers printed on the pages of a sheet, in the background.</summary>
    private async void ReadNumbers(Plan plan)
    {
        // What could not be read for want of text recognition can be, once that has been set up.
        if (plan.Numbers == Numbers.Unavailable && _main.Supplies.Lack() == null) plan.Numbers = Numbers.Unread;
        if (plan.Numbers is Numbers.Read or Numbers.Reading or Numbers.Unavailable || plan.Pieces.Count < 2 || plan.Skip) return;
        if (_main.Session.View is not { } view) return;
        plan.Reading?.Cancel();
        var cancel = plan.Reading = new CancellationTokenSource();
        plan.Numbers = Numbers.Reading;
        Describe();
        var pieces = plan.Pieces.ToList();
        var areas = pieces.Select(p => p.Area).ToList();
        var languages = _main.Settings.OcrLanguages.ToList();
        try
        {
            var numbers = await Task.Run(() => PdfSheets.ReadPageNumbers(view, plan.Page, areas, languages, cancel.Token));
            if (cancel.IsCancellationRequested) return;
            for (var i = 0; i < pieces.Count; i++) pieces[i].Number = numbers[i];
            plan.Numbers = Numbers.Read;
        }
        catch (OcrException)
        {
            plan.Numbers = Numbers.Unavailable;
            _main.Supplies.Offer();
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        if (ReferenceEquals(_plan, plan)) Renumber();
    }

    // ---- the other sheets ---------------------------------------------------------------------------------------

    private List<int> Scope() =>
        _pages.Resolve(_main.SelectedPageIndexes, _main.CurrentPageIndex, _main.Pages.Count) ?? Enumerable.Range(0, _main.Pages.Count).ToList();

    public override void PagesSelected(MainViewModel main) => Survey();

    /// <summary>
    /// Looks at the sheets that are to be cut but have not been on view, in the background, so that the page list can
    /// say for each how many pages it holds and whether that is certain.
    /// </summary>
    private async void Survey()
    {
        _survey?.Cancel();
        _survey = null;
        if (!ReferenceEquals(_main.SelectedTool, this) || _main.Session.View is not { } view || _document is not { } document) return;
        List<int> pages;
        try
        {
            pages = Scope();
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
        {
            // A page range that is still being typed.
            return;
        }

        MarkPages(pages);
        var unseen = pages.Where(page => !_plans.ContainsKey(page) && !_findings.ContainsKey(page)).ToList();
        if (unseen.Count == 0) return;
        var cancel = _survey = new CancellationTokenSource();
        var division = Dividing();
        try
        {
            foreach (var page in unseen)
            {
                var finding = await Task.Run(() => Find(view, page, division), cancel.Token);
                if (cancel.IsCancellationRequested || !ReferenceEquals(_document, document)) return;
                if (!_plans.ContainsKey(page)) _findings[page] = finding;
                MarkPages(pages);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // Other settings, another selection or another document: whatever asked for this has asked anew.
        }

        // The line under Apply counts the sheets that are not certain.
        if (!cancel.IsCancellationRequested) OnSettingsChanged();
    }

    /// <summary>What is known about a sheet: how many pages it will be cut into (1: left whole) and whether that is doubtful.</summary>
    private (int Pages, bool Doubtful)? Known(int page)
    {
        var size = _main.Session.View is { } view && page < view.PageCount ? view.Pages[page] : default;
        if (_plans.TryGetValue(page, out var plan))
        {
            if (plan.Skip) return (1, false);
            return (Worth(plan.Pieces.Select(p => p.Area).ToList(), size) ? plan.Pieces.Count : 1, plan.Doubtful);
        }

        if (_findings.TryGetValue(page, out var finding)) return (Worth(finding.Pages, size) ? finding.Pages.Count : 1, finding.Doubtful);
        return null;
    }

    /// <summary>Marks the sheets in the page list: with the number of pages each becomes, in red where that is not certain.</summary>
    private void MarkPages(IReadOnlyList<int> pages)
    {
        var badges = new Dictionary<int, PageBadge>();
        foreach (var page in pages)
        {
            if (Known(page) is not var (count, doubtful)) continue;
            if (doubtful)
                badges[page] = new PageBadge(count > 1 ? $"{count}?" : "?", true, Loc.Get("SheetBadgeDoubt"));
            else if (count > 1)
                badges[page] = new PageBadge(count.ToString(), false, Loc.Format("SheetBadgeCut", count));
        }

        _main.SetPageBadges(badges);
    }

    // ---- order --------------------------------------------------------------------------------------------------

    private static SheetOrder OrderOf(string key) => key switch
    {
        "rows" => SheetOrder.Rows,
        "rtl" => SheetOrder.RowsRightToLeft,
        "columns" => SheetOrder.Columns,
        _ => SheetOrder.Automatic
    };

    /// <summary>The pages of a sheet in the order they will have in the document.</summary>
    private List<Piece> Ordered(Plan plan)
    {
        var areas = plan.Pieces.Select(p => p.Area).ToList();
        if (ByClicking)
        {
            // Those clicked so far, in the order of the clicks; the others after them, as they are read.
            var reading = PdfSheets.Order(areas, SheetOrder.Rows).Select(i => plan.Pieces[i]).ToList();
            return [..reading.Where(p => p.Clicked != null).OrderBy(p => p.Clicked), ..reading.Where(p => p.Clicked == null)];
        }

        return PdfSheets.Order(areas, OrderOf(_order.Key), plan.Pieces.Select(p => p.Number).ToList()).Select(i => plan.Pieces[i]).ToList();
    }

    /// <summary>Writes each page's place onto its frame and lines the small pictures up in the same order.</summary>
    private void Renumber()
    {
        if (_plan is not { } plan)
        {
            _result.Items.Clear();
            Describe();
            return;
        }

        var ordered = Ordered(plan);
        var items = new List<PieceItem>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var piece = ordered[i];
            var label = ByClicking && piece.Clicked == null ? "" : (i + 1).ToString();
            if (piece.Frame.Model is SheetArea area && (area.Label != label || area.Doubtful != plan.Doubtful))
                piece.Frame.Model = area with { Label = label, Doubtful = plan.Doubtful };
            var item = new PieceItem(piece) { Label = label.Length > 0 ? label : "?", Aspect = piece.Area.Width / Math.Max(1, piece.Area.Height) };
            if (plan.Picture is { } picture && _main.PageWidth > 0 && _main.PageHeight > 0)
            {
                double fx = picture.PixelSize.Width / _main.PageWidth, fy = picture.PixelSize.Height / _main.PageHeight;
                var part = new PixelRect((int)(piece.Area.Left * fx), (int)(piece.Area.Top * fy),
                    Math.Max(1, (int)(piece.Area.Width * fx)), Math.Max(1, (int)(piece.Area.Height * fy)));
                part = part.Intersect(new PixelRect(picture.PixelSize));
                if (part.Width > 0 && part.Height > 0) item.Picture = new CroppedBitmap(picture, part);
            }

            items.Add(item);
        }

        _result.Items.Clear();
        foreach (var item in items) _result.Items.Add(item);
        Describe();
    }

    private void Describe()
    {
        Update();
        // The line under Apply says what will happen; it follows every change to the frames.
        OnSettingsChanged();
        if (_plan is not { } plan)
        {
            if (!_main.HasDocument) _found.SetKey("ErrNoDocument");
            return;
        }

        if (_document != null && ReferenceEquals(_main.SelectedTool, this))
            try
            {
                MarkPages(Scope());
            }
            catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
            {
                // A page range that is still being typed says nothing about which sheets to mark.
            }

        var count = plan.Pieces.Count;
        if (plan.Skip) _found.SetKey("SheetSkipped");
        else if (count == 0) _found.SetKey("SheetNone");
        else if (plan.Doubtful) _found.SetText(count == 1 ? Loc.Get("SheetDoubtWhole") : Loc.Format("SheetDoubtSplit", count));
        else if (count == 1) _found.SetKey("SheetFoundOne");
        else if (ByClicking) _found.SetText(Loc.Format("SheetClickHint", plan.Pieces.Count(p => p.Clicked != null), count));
        else if (_order.Key != "auto") _found.SetText(Loc.Format("SheetFound", count));
        else
            _found.SetText(plan.Numbers switch
            {
                Numbers.Reading or Numbers.Unread => Loc.Format("SheetReading", count),
                Numbers.Unavailable => Loc.Format("SheetNoOcr", count),
                _ when plan.Pieces.All(p => p.Number == null) => Loc.Format("SheetNoNumbers", count),
                _ => Loc.Format("SheetByNumbers", count, string.Join(", ", Ordered(plan).Select(p => p.Number?.ToString() ?? "–")))
            });
    }

    private void OrderChanged()
    {
        if (_plan is not { } plan) return;
        Renumber();
        if (_order.Key == "auto") ReadNumbers(plan);
    }

    private void RestartOrder()
    {
        if (_plan is not { } plan) return;
        foreach (var piece in plan.Pieces) piece.Clicked = null;
        Renumber();
    }

    private async void OnDivisionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ChoiceField.Selected) or nameof(NumberField.Value))) return;
        Update();
        // A slider passes through many values on its way; the sheets are looked at again once it has come to rest.
        _settling?.Cancel();
        var settling = _settling = new CancellationTokenSource();
        try
        {
            if (ReferenceEquals(sender, _sensitivity)) await Task.Delay(180, settling.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Another way of dividing the sheets applies to all of them, also those looked at already.
        DropPlans();
        if (ReferenceEquals(_main.SelectedTool, this)) await _main.RefreshTool();
    }

    private Task FindAgain()
    {
        if (_plan is { } plan) _plans.Remove(plan.Page);
        return _main.RefreshTool();
    }

    /// <summary>The result was looked at and is right: nothing is red about this sheet any more.</summary>
    private void Confirm()
    {
        if (_plan is not { } plan) return;
        plan.Doubtful = false;
        Renumber();
    }

    /// <summary>Leaves the sheet on view as it is, or takes it up again.</summary>
    private void Skip(bool skip)
    {
        if (_plan is not { } plan || plan.Skip == skip) return;
        plan.Skip = skip;
        StopDrawing();
        _main.SelectedObject = null;
        _main.Objects.Clear();
        if (!skip)
            foreach (var piece in plan.Pieces)
                _main.Objects.Add(piece.Frame);
        Renumber();
        if (!skip && _order.Key == "auto") ReadNumbers(plan);
    }

    // ---- the frames on the page ---------------------------------------------------------------------------------

    private Piece? PieceOf(CanvasObject obj) => _plan?.Pieces.FirstOrDefault(p => ReferenceEquals(p.Frame, obj));

    public override void ObjectSelected(MainViewModel main, CanvasObject? obj) => Update();

    public override void ObjectTransformed(MainViewModel main, CanvasObject obj, RectD bounds, double rotation, double scale, bool done)
    {
        if (obj.Model is not SheetArea area || _plan is not { } plan) return;
        double width = main.PageWidth, height = main.PageHeight;
        var moved = Math.Abs(bounds.Width - area.Bounds.Width) < 0.01 && Math.Abs(bounds.Height - area.Bounds.Height) < 0.01;
        if (moved)
        {
            // Pushed against the edge of the sheet a frame stops there; it is not squeezed.
            var dx = Math.Max(0, -bounds.Left) - Math.Max(0, bounds.Right - width);
            var dy = Math.Max(0, -bounds.Top) - Math.Max(0, bounds.Bottom - height);
            bounds = new RectD(bounds.Left + dx, bounds.Top + dy, bounds.Right + dx, bounds.Bottom + dy);
        }

        bounds = new RectD(Math.Max(0, bounds.Left), Math.Max(0, bounds.Top), Math.Min(width, bounds.Right), Math.Min(height, bounds.Bottom));
        if (bounds.Width >= 8 && bounds.Height >= 8) obj.Model = area with { Bounds = bounds };
        if (!done) return;
        Changed(plan);
    }

    /// <summary>A frame was moved, added or removed: the order may be another now, and so may the numbers read.</summary>
    private void Changed(Plan plan)
    {
        plan.Reading?.Cancel();
        // Frames set by hand are what their owner wants: there is nothing left to doubt.
        plan.Doubtful = false;
        if (plan.Numbers != Numbers.Unavailable) plan.Numbers = Numbers.Unread;
        foreach (var piece in plan.Pieces) piece.Number = null;
        Renumber();
        if (_order.Key == "auto") ReadNumbers(plan);
    }

    public override void ObjectClicked(MainViewModel main, CanvasObject obj)
    {
        if (PieceOf(obj) is { } piece) Click(piece);
    }

    private void Chosen(PieceItem item)
    {
        if (item.Source is not Piece piece) return;
        _main.SelectedObject = piece.Frame;
        Click(piece);
    }

    /// <summary>When the order is set by clicking: gives the page the next place.</summary>
    private void Click(Piece piece)
    {
        if (!ByClicking || _plan is not { } plan) return;
        if (piece.Clicked != null)
        {
            // Clicking again once all have a place starts over with this page as the first.
            if (plan.Pieces.Any(p => p.Clicked == null)) return;
            foreach (var other in plan.Pieces) other.Clicked = null;
        }

        piece.Clicked = plan.Pieces.Max(p => p.Clicked ?? 0) + 1;
        Renumber();
    }

    private void StartDrawing()
    {
        if (_plan is not { Skip: false }) return;
        _drawing = true;
        OnPropertyChanged(nameof(CanvasMode));
        _main.StatusText = Loc.Get("SheetAddHint");
    }

    private void StopDrawing()
    {
        if (!_drawing) return;
        _drawing = false;
        OnPropertyChanged(nameof(CanvasMode));
    }

    public override Task RegionSelected(MainViewModel main, RectD region)
    {
        if (!_drawing || _plan is not { } plan) return Task.CompletedTask;
        StopDrawing();
        if (region.Width < 8 || region.Height < 8) return Task.CompletedTask;
        var piece = NewPiece(plan, region);
        plan.Pieces.Add(piece);
        main.Objects.Add(piece.Frame);
        main.SelectedObject = piece.Frame;
        Changed(plan);
        return Task.CompletedTask;
    }

    private void RemoveSelected()
    {
        if (_plan is not { } plan || _main.SelectedObject is not { } selected || PieceOf(selected) is not { } piece) return;
        plan.Pieces.Remove(piece);
        _main.Objects.Remove(selected);
        _main.SelectedObject = null;
        Changed(plan);
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

    // ---- cutting ------------------------------------------------------------------------------------------------

    /// <summary>
    /// True when cutting to the areas changes the sheet: more than one page on it, or one that is smaller than the
    /// sheet (a single page photographed on a table).
    /// </summary>
    private static bool Worth(IReadOnlyList<RectD> areas, PageInfo sheet) =>
        areas.Count > 1 || (areas.Count == 1 && areas[0].Width * areas[0].Height < sheet.Width * sheet.Height * 0.97);

    public override string? Summary(MainViewModel main)
    {
        if (_plan is not { } plan || main.Session.View is not { } view || plan.Page >= view.PageCount) return null;
        var pages = _pages.Resolve(main.SelectedPageIndexes, main.CurrentPageIndex, main.Pages.Count) ?? Enumerable.Range(0, main.Pages.Count).ToList();
        if (!pages.Contains(plan.Page)) return Loc.Get("PreviewOutside");
        var worth = !plan.Skip && Worth(plan.Pieces.Select(p => p.Area).ToList(), view.Pages[plan.Page]);
        if (pages.Count > 1)
        {
            var text = worth ? Loc.Format("SummarySheets", pages.Count, plan.Pieces.Count) : Loc.Format("SummarySheetsSkip", pages.Count);
            var doubtful = pages.Count(page => Known(page) is (_, true));
            return doubtful > 0 ? text + " " + Loc.Format("SummarySheetsDoubt", doubtful) : text;
        }

        if (plan.Skip) return Loc.Get("SheetSkipped");
        if (!worth) return Loc.Get("SummarySheetWhole");
        return plan.Pieces.Count == 1 ? Loc.Get("SummarySheetCrop") : Loc.Format("SummarySheet", plan.Pieces.Count);
    }

    /// <summary>The areas a sheet is cut into, in their order, for a sheet that was not on view.</summary>
    private static List<RectD> Work(PdfView view, int page, Division division, SheetOrder order, IReadOnlyList<string> languages,
        IReadOnlyList<RectD>? areas, CancellationToken cancel)
    {
        areas ??= Find(view, page, division).Pages;
        IReadOnlyList<int?>? numbers = null;
        if (order == SheetOrder.Automatic && areas.Count > 1)
            try
            {
                numbers = PdfSheets.ReadPageNumbers(view, page, areas, languages, cancel);
            }
            catch (OcrException)
            {
                // Without text recognition the pages are taken as they are read.
            }

        return PdfSheets.Order(areas, order, numbers).Select(i => areas[i]).ToList();
    }

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var view = context.Session.View!;
        var pages = context.Resolve(_pages) ?? Enumerable.Range(0, context.PageCount).ToList();
        var division = Dividing();
        var order = OrderOf(_order.Key);
        var languages = context.Settings.OcrLanguages.ToList();
        var keep = _keep.Value;

        // Sheets that were looked at are cut the way they were shown; the others the way they were found, or are
        // found now. One whose page numbers are still being read is read once more here, so the order does not depend
        // on timing.
        var shown = new Dictionary<int, List<RectD>>();
        var unread = new Dictionary<int, IReadOnlyList<RectD>>();
        var skipped = new HashSet<int>();
        var doubtful = new HashSet<int>();
        if (ReferenceEquals(pdf, _document))
            foreach (var page in pages)
            {
                if (_plans.TryGetValue(page, out var plan))
                {
                    if (plan.Skip) skipped.Add(page);
                    else if (!ByClicking && order == SheetOrder.Automatic && plan.Numbers is Numbers.Reading or Numbers.Unread && plan.Pieces.Count > 1)
                        unread[page] = plan.Pieces.Select(p => p.Area).ToList();
                    else
                        shown[page] = Ordered(plan).Select(p => p.Area).ToList();
                    if (plan.Doubtful) doubtful.Add(page);
                }
                else if (_findings.TryGetValue(page, out var finding))
                {
                    unread[page] = finding.Pages;
                    if (finding.Doubtful) doubtful.Add(page);
                }
            }

        var cuts = new Dictionary<int, IReadOnlyList<RectD>>();
        await context.Run(() =>
        {
            for (var i = 0; i < pages.Count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                var page = pages[i];
                if (skipped.Contains(page)) continue;
                var areas = shown.TryGetValue(page, out var known)
                    ? known
                    : Work(view, page, division, order, languages, unread.GetValueOrDefault(page), context.Cancel);
                if (Worth(areas, view.Pages[page])) cuts[page] = areas;
                context.Progress.Report((i + 1.0) / pages.Count * 0.5);
            }

            return true;
        });

        if (cuts.Count == 0) throw new UserException(Loc.Get("ErrSheetNothing"));
        var progress = new Progress<double>(value => context.Progress.Report(0.5 + value * 0.5));
        var result = await context.Run(() => PdfSheets.Split(pdf, cuts, keep, progress, context.Cancel));
        var made = cuts.Values.Sum(areas => areas.Count);

        // Where the pages of each sheet have ended up in the document.
        var places = new Dictionary<int, List<int>>();
        var shift = 0;
        foreach (var page in cuts.Keys.Order())
        {
            var first = page + shift + (keep ? 1 : 0);
            places[page] = Enumerable.Range(first, cuts[page].Count).ToList();
            shift += cuts[page].Count - 1 + (keep ? 1 : 0);
        }

        context.Commit(result);
        // What most needs a look is shown and selected: the pages of sheets that were a close call, or else those of
        // the first sheet that was cut.
        var uncertain = cuts.Keys.Where(doubtful.Contains).Order().ToList();
        context.Main.RestoreSelection(uncertain.Count > 0 ? uncertain.SelectMany(page => places[page]).ToList() : places[cuts.Keys.Min()]);
        context.Status(uncertain.Count > 0
            ? Loc.Format("StatusSheetsCutDoubt", cuts.Count, made, uncertain.Count)
            : Loc.Format("StatusSheetsCut", cuts.Count, made));
    }
}
