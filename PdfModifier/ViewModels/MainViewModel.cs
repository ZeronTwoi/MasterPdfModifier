using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using PdfModifier.Core.Text;
using PdfModifier.Localization;
using PdfModifier.Services;
using PdfModifier.ViewModels.Tools;

namespace PdfModifier.ViewModels;

public partial class PageItem(int index, double width, double height) : ObservableObject
{
    public const double ThumbnailWidth = 132;

    [ObservableProperty] private Bitmap? _thumbnail;

    /// <summary>A mark the active tool puts on the page: what it will do with it, or that it needs a look.</summary>
    [ObservableProperty] private PageBadge? _badge;

    public bool HasBadge => Badge != null;

    partial void OnBadgeChanged(PageBadge? value) => OnPropertyChanged(nameof(HasBadge));

    public int Index => index;
    public string Number => (index + 1).ToString();
    public double DisplayWidth => ThumbnailWidth;
    public double DisplayHeight => Math.Clamp(ThumbnailWidth * height / Math.Max(1, width), 40, 320);
    internal bool Requested { get; set; }

    internal bool HasSize(double otherWidth, double otherHeight) =>
        Math.Abs(width - otherWidth) < 0.01 && Math.Abs(height - otherHeight) < 0.01;
}

/// <summary>A heading in the tool list with the tools under it.</summary>
public sealed class ToolGroup(string key, IReadOnlyList<Tool> tools) : LocalizedObject
{
    private bool _isListed = true;

    public string Title => Loc.Get(key);
    public IReadOnlyList<Tool> Tools => tools;

    /// <summary>False while none of its tools matches what is searched for.</summary>
    public bool IsListed
    {
        get => _isListed;
        set => SetProperty(ref _isListed, value);
    }
}

public partial class MainViewModel : LocalizedObject
{
    private const double MinZoom = 0.1, MaxZoom = 8;

    /// <summary>
    /// The space the window leaves around a page that is shown whole: a margin on every side, and at the bottom the
    /// bar with the page and zoom controls that floats over the view.
    /// </summary>
    private const double RoomAcross = 48, RoomDown = 88;

    private CancellationTokenSource? _cancel;
    private int _renderVersion;
    private int _documentVersion;
    private int _generation = -1;
    private bool _refreshWhenIdle;

    // A change the active tool already shows itself: the document is replaced without redrawing anything but the
    // thumbnail of the page concerned.
    private bool _quietChange;
    private int? _changedPage;

    private ColorField? _picking;

    // Preview of what Apply would do to the page on view.
    private CancellationTokenSource? _previewDelay;
    private PdfView? _previewView;
    private bool _previewHeld;
    private readonly object _pageCopyGate = new();
    private (byte[] Document, int Page, byte[] Copy)? _pageCopy;

    private readonly List<FileField> _imageFields = [];
    private readonly Dictionary<string, Bitmap?> _thumbnails = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private int _currentPageIndex;
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private Bitmap? _pageBitmap;
    [ObservableProperty] private double _pageWidth;
    [ObservableProperty] private double _pageHeight;
    [ObservableProperty] private Overlay? _selectedOverlay;
    [ObservableProperty] private CanvasObject? _selectedObject;
    [ObservableProperty] private RectD? _region;
    [ObservableProperty] private bool _dimOutsideRegion;
    [ObservableProperty] private Tool _selectedTool;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _showPreview = true;
    [ObservableProperty] private bool _isPreview;
    [ObservableProperty] private string _previewNote = "";
    [ObservableProperty] private string _applySummary = "";
    [ObservableProperty] private string _toolSearch = "";
    [ObservableProperty] private bool _noToolListed;

    /// <summary>The number of a newer version of the app, once the look at start-up has found one.</summary>
    [ObservableProperty] private string? _newVersion;

    private readonly string _dragFolder = Path.Combine(Path.GetTempPath(), "PdfModifier", "pages-" + Guid.NewGuid().ToString("N"));
    private int _dragCount;

    public MainViewModel(Settings settings)
    {
        Settings = settings;
        Supplies = new OcrSupplies(this);
        Tools = ToolCatalog.Create(this);
        string? group = null;
        foreach (var tool in Tools)
        {
            tool.StartsGroup = tool.GroupKey != group;
            group = tool.GroupKey;
        }

        ToolGroups = Tools.GroupBy(tool => tool.GroupKey).Select(g => new ToolGroup(g.Key, g.ToList())).ToList();
        _selectedTool = Tools[0];
        _selectedTool.IsSelected = true;
        Attach(_selectedTool);
        Session.Changed += OnDocumentChanged;
        StatusText = Loc.Get("StatusReady");
    }

    public DocumentSession Session { get; } = new();
    public Settings Settings { get; }

    /// <summary>Set by the window once it exists.</summary>
    public Dialogs Dialogs { get; set; } = null!;

    /// <summary>Fetches what text recognition lacks on this computer, after asking.</summary>
    public OcrSupplies Supplies { get; }

    /// <summary>Looks for a newer version of the app, for a user who wants to be told.</summary>
    public UpdateCheck Updates { get; } = new();

    /// <summary>Puts that newer version in the place of this one, when the button for it is pressed.</summary>
    public Updater Updater { get; } = new();

    /// <summary>Closes the app. Set by the program once the window exists; an update ends with it.</summary>
    public Action? Quit { get; set; }

    public bool HasNewVersion => NewVersion != null;

    /// <summary>What the settings button says of itself: with a newer version out, that comes first.</summary>
    public string SettingsTip => NewVersion is { } version ? Loc.Format("UpdateAvailable", version, AppInfo.Version) : Loc.Get("ToolSettings");

    partial void OnNewVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(HasNewVersion));
        OnPropertyChanged(nameof(SettingsTip));
    }

    /// <summary>
    /// The one look for a newer version that a start of the app makes, if the user wants to be told. What it finds
    /// shows as a warning sign on the settings button and as a note in the settings.
    /// </summary>
    public async Task LookForNewVersion()
    {
        NewVersion = await Updates.LookOnce(Settings, AppInfo.Version);
        if (SelectedTool is SettingsTool) await RefreshTool();
    }

    /// <summary>Device pixels per layout unit of the screen the window is on; pages are rendered that much sharper.</summary>
    public double RenderScaling { get; set; } = 1;

    public IReadOnlyList<Tool> Tools { get; }

    /// <summary>The same tools under their headings, as the list shows them.</summary>
    public IReadOnlyList<ToolGroup> ToolGroups { get; }
    public ObservableCollection<PageItem> Pages { get; } = [];
    public ObservableCollection<Overlay> Overlays { get; } = [];

    /// <summary>The movable objects the active tool shows on top of the page, bottom-most first.</summary>
    public ObservableCollection<CanvasObject> Objects { get; } = [];

    public IReadOnlyList<int> SelectedPageIndexes { get; private set; } = [];

    /// <summary>Raised after the page list was rebuilt, so the view can restore the selection.</summary>
    public event Action? PagesRebuilt;

    /// <summary>Raised when a different document was opened, so the view can fit it into the window.</summary>
    public event Action? DocumentOpened;

    /// <summary>Raised when another page is put on view; the argument says whether to show its end rather than its start.</summary>
    public event Action<bool>? PageTurned;

    public bool HasDocument => Session.HasDocument;
    public bool CanUndo => (Session.CanUndo || SelectedTool.HasPendingChanges) && !IsBusy;
    public bool CanRedo => Session.CanRedo && !IsBusy;
    public bool IsIdle => !IsBusy;
    public bool ShowApply => SelectedTool.HasApply;
    public bool CanPreview => SelectedTool.HasPreview;
    public string ZoomLabel => $"{Zoom * 100:0}%";
    public string PageLabel => HasDocument ? $"{CurrentPageIndex + 1} / {Pages.Count}" : "";

    /// <summary>The name of the open file, for the head of the window.</summary>
    public string DocumentName => !HasDocument ? "" : Session.FilePath != null ? Path.GetFileName(Session.FilePath) : Loc.Get("Untitled");

    /// <summary>True while the document has changes that are not saved yet.</summary>
    public bool IsDirty => HasDocument && Session.IsDirty;

    /// <summary>The heading of the page list, with the number of pages once there are some.</summary>
    public string PagesTitle => HasDocument ? $"{Loc.Get("GroupOrganize")} · {Pages.Count}" : Loc.Get("GroupOrganize");

    /// <summary>The size of the page on view in millimetres, for the status bar.</summary>
    public string PageSizeLabel => HasDocument && PageWidth > 0
        ? $"{PageWidth / Units.PointsPerMm:0} × {PageHeight / Units.PointsPerMm:0} mm"
        : "";

    /// <summary>How the page view reacts to the mouse right now: as the tool wants, unless a colour is being picked.</summary>
    public CanvasMode CanvasMode => _picking != null ? CanvasMode.Pick : SelectedTool.CanvasMode;

    public string Title
    {
        get
        {
            if (!HasDocument) return AppInfo.Name;
            var name = Session.FilePath != null ? Path.GetFileName(Session.FilePath) : Loc.Get("Untitled");
            return $"{name}{(Session.IsDirty ? " *" : "")} — {AppInfo.Name}";
        }
    }

    /// <summary>Screen units per PDF point at the current zoom (100% shows a page at its physical size).</summary>
    public double PointScale => Zoom * 96 / 72;

    // ---- document lifecycle -------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task Open()
    {
        // A picture can be opened like a document: it becomes one, of a single page.
        var files = await Dialogs.OpenFiles(Loc.Get("Open"), false, Dialogs.PdfFiles, Dialogs.PageFiles);
        if (files.Count > 0) await OpenFile(files[0]);
    }

    public async Task OpenFile(string path)
    {
        await FlushTool();
        if (!await ConfirmDiscard()) return;
        await RunBusy(async context =>
        {
            var picture = PdfImages.IsImageFile(path);
            var bytes = await context.Run(() => picture
                ? PdfImages.InsertImageFiles(null, [path], 0, ImagePageOptions.Natural)
                : File.ReadAllBytes(path));
            var unlocked = false;
            while (await context.Run(() => PdfPages.NeedsPassword(bytes)))
            {
                var password = await Dialogs.AskPassword(Loc.Format("AskPassword", Path.GetFileName(path)));
                if (password == null) return;
                try
                {
                    var captured = bytes;
                    bytes = await context.Run(() => PdfPages.Unlock(captured, password));
                    unlocked = true;
                }
                catch (PdfPasswordException)
                {
                    await Dialogs.Message(Loc.Get("ErrPassword"));
                }
            }

            // An unlocked copy must not silently overwrite the protected original, so it starts without a file name;
            // so does the document made from a picture, which is not that picture's file.
            Session.Open(bytes, unlocked || picture ? null : path);
            CurrentPageIndex = 0;
            DocumentOpened?.Invoke();
            StatusText = Loc.Format("StatusOpened", Path.GetFileName(path), Pages.Count);
        }, needsDocument: false);
    }

    /// <summary>Starts a new, unsaved document from generated bytes (e.g. a merge result).</summary>
    public async Task OpenNew(byte[] pdf)
    {
        if (!await ConfirmDiscard()) return;
        Session.Open(pdf, null);
        CurrentPageIndex = 0;
        DocumentOpened?.Invoke();
    }

    [RelayCommand]
    private Task Save() => Session.FilePath == null ? SaveAs() : SaveTo(Session.FilePath);

    [RelayCommand]
    private async Task SaveAs()
    {
        if (!HasDocument) return;
        var suggested = Session.FilePath != null ? Path.GetFileName(Session.FilePath) : Loc.Get("Untitled") + ".pdf";
        var path = await Dialogs.SaveFile(Loc.Get("SaveAs"), suggested, Dialogs.PdfFiles);
        if (path != null) await SaveTo(path);
    }

    private Task SaveTo(string path) => RunBusy(async context =>
    {
        var bytes = context.Pdf;
        await context.Run(() =>
        {
            File.WriteAllBytes(path, bytes);
            return true;
        });
        Session.MarkSaved(path);
        StatusText = Loc.Format("StatusSaved", Path.GetFileName(path));
    });

    /// <summary>Asks before throwing away unsaved changes. True when it is fine to continue.</summary>
    public async Task<bool> ConfirmDiscard() =>
        !Session.IsDirty || !Session.HasDocument || await Dialogs.Confirm(Loc.Get("ConfirmDiscard"));

    /// <summary>Has the active tool write what it still holds back into the document.</summary>
    public Task FlushTool() => Guarded(() => SelectedTool.FlushAsync(this));

    [RelayCommand]
    private async Task Undo()
    {
        // An edit the tool has not written yet is the latest change: it has to be in the document to be undone.
        await FlushTool();
        if (Session.CanUndo && !IsBusy) Guard(Session.Undo);
    }

    [RelayCommand]
    private async Task Redo()
    {
        await FlushTool();
        if (CanRedo) Guard(Session.Redo);
    }

    public void Commit(byte[] pdf) => Session.Commit(pdf);

    /// <summary>
    /// Replaces the document after a change to one page that the active tool is showing already: nothing is redrawn
    /// except that page's thumbnail, and whatever is selected stays selected.
    /// </summary>
    public void CommitQuietly(byte[] pdf, int page)
    {
        (_quietChange, _changedPage) = (true, page);
        try
        {
            Session.Commit(pdf);
        }
        finally
        {
            (_quietChange, _changedPage) = (false, null);
        }
    }

    private void OnDocumentChanged()
    {
        _documentVersion++;
        lock (_pageCopyGate) _pageCopy = null;
        var view = Session.View;
        var sameDocument = _generation == Session.Generation;
        _generation = Session.Generation;
        var samePages = sameDocument && view != null && view.PageCount == Pages.Count &&
                        Pages.All(p => p.HasSize(view.Pages[p.Index].Width, view.Pages[p.Index].Height));
        if (samePages)
        {
            // The list, its selection and the old pictures stay until fresh ones are drawn: nothing flickers.
            foreach (var page in Pages.Where(p => p.Requested && (_changedPage == null || p.Index == _changedPage)))
            {
                page.Requested = false;
                RequestThumbnail(page);
            }
        }
        else
        {
            foreach (var page in Pages) page.Thumbnail?.Dispose();
            Pages.Clear();
            if (view != null)
                for (var i = 0; i < view.PageCount; i++)
                    Pages.Add(new PageItem(i, view.Pages[i].Width, view.Pages[i].Height));
        }

        if (_quietChange)
        {
            NotifyState();
            return;
        }

        var clamped = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, Pages.Count - 1));
        if (clamped != CurrentPageIndex) CurrentPageIndex = clamped; // re-renders through the change handler
        else _ = PageChanged();
        if (!samePages)
        {
            SelectedPageIndexes = Pages.Count > 0 ? [CurrentPageIndex] : [];
            PagesRebuilt?.Invoke();
        }

        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(PagesTitle));
        OnPropertyChanged(nameof(PageSizeLabel));
    }

    partial void OnPageHeightChanged(double value) => OnPropertyChanged(nameof(PageSizeLabel));

    partial void OnToolSearchChanged(string value)
    {
        // Every word typed has to turn up in the name of the tool or in what it says it does.
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var group in ToolGroups)
        {
            foreach (var tool in group.Tools)
                tool.IsListed = words.All(word => tool.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
                                                  tool.Description.Contains(word, StringComparison.CurrentCultureIgnoreCase));
            group.IsListed = group.Tools.Any(tool => tool.IsListed);
        }

        NoToolListed = ToolGroups.All(group => !group.IsListed);
    }

    [RelayCommand]
    private void SelectTool(Tool tool) => SelectedTool = tool;

    [RelayCommand]
    private void ShowSettings() => SelectedTool = Tools.OfType<SettingsTool>().First();

    /// <summary>
    /// Marks pages in the page list on behalf of the active tool; pages not named lose their mark. Null clears all.
    /// </summary>
    public void SetPageBadges(IReadOnlyDictionary<int, PageBadge>? badges)
    {
        foreach (var page in Pages) page.Badge = badges?.GetValueOrDefault(page.Index);
    }

    /// <summary>Tells the window that what can be undone has changed (a tool took up or wrote an edit).</summary>
    public void NotifyUndoState() => OnPropertyChanged(nameof(CanUndo));

    // ---- pages and rendering ------------------------------------------------------------------------------------

    /// <summary>
    /// Tells the fields of the active tool that choose pages what is selected, so each can say which pages its
    /// choice comes to.
    /// </summary>
    private void ShowPageChoice()
    {
        foreach (var field in SelectedTool.Fields.SelectMany(field => field is RowField row ? row.Fields : [field]).OfType<PagesField>())
            field.Show(SelectedPageIndexes, CurrentPageIndex, Pages.Count);
    }

    public void SetSelection(IReadOnlyList<int> indexes, int? current)
    {
        SelectedPageIndexes = indexes;
        if (current is { } index && index != CurrentPageIndex) CurrentPageIndex = index;
        else InvalidatePreview();
        ShowPageChoice();
        // What Apply will do depends on the selection; saying so does not have to wait for the page to be drawn.
        ApplySummary = Summarise(SelectedTool);
        Guard(() => SelectedTool.PagesSelected(this));
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PageLabel));
        ShowPageChoice();
        ApplySummary = Summarise(SelectedTool);
        _ = PageChanged();
    }

    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomLabel));
        OnPropertyChanged(nameof(PointScale));
        _ = RenderPage();
    }

    private async Task PageChanged()
    {
        SelectedOverlay = null;
        SelectedObject = null;
        Overlays.Clear();
        Objects.Clear();
        Region = null;
        DimOutsideRegion = false;
        DropPreview();
        await RenderPage();
        await RefreshTool();
        InvalidatePreview();
    }

    /// <summary>Redraws the current page, e.g. after a tool changed what it wants to show.</summary>
    public async Task RenderPage()
    {
        var version = ++_renderVersion;
        var view = Session.View;
        if (view == null || Pages.Count == 0)
        {
            PageBitmap = null;
            PageWidth = PageHeight = 0;
            return;
        }

        var index = Math.Clamp(CurrentPageIndex, 0, view.PageCount - 1);
        // A preview is a one-page document of its own, and may have another size than the page it is made from.
        var preview = _previewView;
        var info = preview != null ? preview.Pages[0] : view.Pages[index];
        (PageWidth, PageHeight) = (info.Width, info.Height);
        var tool = SelectedTool;
        // Sharp at the screen's pixel density, but bounded so a deep zoom on a large sheet cannot exhaust memory.
        var scale = PointScale * RenderScaling;
        scale = Math.Min(scale, Math.Sqrt(60_000_000 / Math.Max(1, info.Width * info.Height)));

        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var rendered = preview != null
                    ? preview.Render(0, scale)
                    : tool.RenderPage(this, index, scale) ?? view.Render(index, scale);
                return BitmapConverter.ToAvalonia(rendered);
            });
            if (version != _renderVersion)
            {
                bitmap.Dispose();
                return;
            }

            var previous = PageBitmap;
            PageBitmap = bitmap;
            previous?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // The document was replaced while rendering; the newer request takes over.
        }
        catch (PdfException e)
        {
            StatusText = Describe(e);
        }
    }

    /// <summary>Loads a page thumbnail the first time its list entry scrolls into view.</summary>
    public void RequestThumbnail(PageItem item)
    {
        if (item.Requested || Session.View is not { } view) return;
        item.Requested = true;
        var version = _documentVersion;
        var scale = PageItem.ThumbnailWidth * 1.5 / Math.Max(1, view.Pages[item.Index].Width);
        Task.Run(() =>
        {
            try
            {
                using var rendered = view.Render(item.Index, scale);
                var bitmap = BitmapConverter.ToAvalonia(rendered);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (version != _documentVersion)
                    {
                        bitmap.Dispose();
                        return;
                    }

                    var previous = item.Thumbnail;
                    item.Thumbnail = bitmap;
                    previous?.Dispose();
                });
            }
            catch (Exception e) when (e is ObjectDisposedException or PdfException)
            {
                // The document changed again, which asks for the picture anew, or the page is unreadable.
            }
        });
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(MaxZoom, Math.Round(Zoom * 1.25, 2));

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(MinZoom, Math.Round(Zoom / 1.25, 2));

    [RelayCommand]
    private void ZoomActualSize() => Zoom = 1;

    /// <summary>Zooms so the page fits the given viewport (screen units).</summary>
    public void ZoomToFit(double viewportWidth, double viewportHeight)
    {
        if (PageWidth <= 0 || PageHeight <= 0 || viewportWidth <= RoomAcross + 20 || viewportHeight <= RoomDown + 20) return;
        var fit = Math.Min((viewportWidth - RoomAcross) / PageWidth, (viewportHeight - RoomDown) / PageHeight) * 72 / 96;
        // Rounded down: rounded up, the page would be a hair too large for the room and bring scroll bars with it.
        Zoom = Math.Clamp(Math.Floor(fit * 100) / 100, MinZoom, MaxZoom);
    }

    /// <summary>Zooms so the page is as wide as the given viewport (screen units).</summary>
    public void ZoomToWidth(double viewportWidth)
    {
        // Room for the scroll bar that a page of this width will need for its height.
        var room = viewportWidth - RoomAcross - 20;
        if (PageWidth <= 0 || room <= 20) return;
        Zoom = Math.Clamp(Math.Floor(room / PageWidth * 72 / 96 * 100) / 100, MinZoom, MaxZoom);
    }

    [RelayCommand]
    private void PreviousPage() => TurnPage(-1);

    [RelayCommand]
    private void NextPage() => TurnPage(1);

    [RelayCommand]
    private void FirstPage() => GoToPage(0);

    [RelayCommand]
    private void LastPage() => GoToPage(Pages.Count - 1);

    /// <summary>
    /// Goes one page back or forth. Returns false at either end of the document. Going back with
    /// <paramref name="showEnd"/> arrives at the bottom of the previous page, as when scrolling up through a document.
    /// </summary>
    public bool TurnPage(int direction, bool showEnd = false)
    {
        var target = CurrentPageIndex + Math.Sign(direction);
        if (target < 0 || target >= Pages.Count) return false;
        GoToPage(target, showEnd);
        return true;
    }

    public void GoToPage(int index, bool showEnd = false)
    {
        if (index < 0 || index >= Pages.Count || index == CurrentPageIndex) return;
        RestoreSelection([index]);
        PageTurned?.Invoke(showEnd);
    }

    /// <summary>Selects the given pages in the page list and shows the first of them.</summary>
    public void RestoreSelection(IReadOnlyList<int> indexes)
    {
        var valid = indexes.Where(i => i >= 0 && i < Pages.Count).ToList();
        if (valid.Count == 0) return;
        CurrentPageIndex = valid[0];
        SelectedPageIndexes = valid;
        PagesRebuilt?.Invoke();
        ShowPageChoice();
    }

    // ---- the page list: adding, moving, removing and taking out pages ----------------------------------------------

    private static bool IsPdfFile(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a file that can be added to the document as pages: an image or a PDF.</summary>
    public static bool CanBecomePages(string path) => PdfImages.IsImageFile(path) || IsPdfFile(path);

    /// <summary>The pages the commands of the page list act on: those selected, or else the one on view.</summary>
    private List<int> ChosenPages() =>
        (SelectedPageIndexes.Count > 0 ? SelectedPageIndexes : [CurrentPageIndex]).Where(i => i >= 0 && i < Pages.Count).Distinct().Order().ToList();

    [RelayCommand]
    private async Task AddPages()
    {
        var files = await Dialogs.OpenFiles(Loc.Get("PagesAdd"), true, Dialogs.PageFiles);
        if (files.Count > 0) await AddFiles(files, HasDocument ? ChosenPages().DefaultIfEmpty(Pages.Count - 1).Max() + 1 : 0);
    }

    /// <summary>
    /// Adds files as pages in front of the page at <paramref name="insertAt"/>: every image becomes a page, a PDF
    /// brings all of its pages. Without an open document the files become one.
    /// </summary>
    public async Task AddFiles(IReadOnlyList<string> paths, int insertAt)
    {
        var files = paths.Where(CanBecomePages).ToList();
        if (files.Count == 0)
        {
            StatusText = Loc.Get("ErrNothingToAdd");
            return;
        }

        // A single PDF put into an empty window is simply opened: it keeps its name and can be saved back.
        if (!HasDocument && files.Count == 1 && IsPdfFile(files[0]))
        {
            await OpenFile(files[0]);
            return;
        }

        await RunBusy(async context =>
        {
            var result = Session.Bytes;
            var index = result == null ? 0 : Math.Clamp(insertAt, 0, Pages.Count);
            var first = index;
            for (var i = 0; i < files.Count;)
            {
                context.Cancel.ThrowIfCancellationRequested();
                var before = result;
                var at = index;
                if (IsPdfFile(files[i]))
                {
                    var other = await FieldSetup.ReadPdf(context, files[i]);
                    index += await context.Run(() => PdfPages.CountPages(other));
                    result = await context.Run(() => before == null ? other : PdfPages.InsertPdf(before, other, null, at));
                    i++;
                }
                else
                {
                    // Images that follow each other go in together.
                    var images = files.Skip(i).TakeWhile(file => !IsPdfFile(file)).ToList();
                    result = await context.Run(() => PdfImages.InsertImageFiles(before, images, at, ImagePageOptions.Natural));
                    index += images.Count;
                    i += images.Count;
                }

                context.Progress.Report((double)i / files.Count);
            }

            var added = index - first;
            if (Session.Bytes == null)
            {
                Session.Open(result!, null);
                CurrentPageIndex = 0;
                DocumentOpened?.Invoke();
            }
            else
            {
                Commit(result!);
                RestoreSelection(Enumerable.Range(first, added).ToList());
            }

            StatusText = Loc.Format("StatusPagesAdded", added);
        }, needsDocument: false);
    }

    /// <summary>
    /// Files dropped onto the window rather than into the page list. A PDF is opened as the document, as ever; images
    /// become a document when none is open, and pages after the one on view otherwise.
    /// </summary>
    public Task DropFiles(IReadOnlyList<string> paths)
    {
        var files = paths.Where(CanBecomePages).ToList();
        if (HasDocument && files.Count > 0 && files.All(IsPdfFile)) return OpenFile(files[0]);
        return AddFiles(paths, HasDocument ? CurrentPageIndex + 1 : 0);
    }

    /// <summary>Moves pages so that they come to stand, in their own order, in front of the page now at <paramref name="insertAt"/>.</summary>
    public Task MovePages(IReadOnlyList<int> pages, int insertAt)
    {
        var count = Pages.Count;
        var moved = pages.Where(i => i >= 0 && i < count).Distinct().Order().ToList();
        if (moved.Count == 0) return Task.CompletedTask;
        var rest = Enumerable.Range(0, count).Except(moved).ToList();
        var place = Math.Clamp(insertAt, 0, count) - moved.Count(i => i < insertAt);
        List<int> order = [..rest.Take(place), ..moved, ..rest.Skip(place)];
        if (order.SequenceEqual(Enumerable.Range(0, count))) return Task.CompletedTask;
        return RunBusy(async context =>
        {
            var pdf = context.Pdf;
            Commit(await context.Run(() => PdfPages.Reorder(pdf, order)));
            RestoreSelection(Enumerable.Range(place, moved.Count).ToList());
            StatusText = Loc.Format("StatusPagesMoved", moved.Count);
        });
    }

    public Task DeletePages(IReadOnlyList<int> pages)
    {
        var doomed = pages.Where(i => i >= 0 && i < Pages.Count).Distinct().ToList();
        if (doomed.Count == 0) return Task.CompletedTask;
        if (doomed.Count >= Pages.Count)
        {
            StatusText = Loc.Get("ErrDeleteAll");
            return Task.CompletedTask;
        }

        return RunBusy(async context =>
        {
            var pdf = context.Pdf;
            var next = Math.Min(doomed.Min(), Pages.Count - doomed.Count - 1);
            Commit(await context.Run(() => PdfPages.Delete(pdf, doomed)));
            RestoreSelection([next]);
            StatusText = Loc.Format("StatusPagesDeleted", doomed.Count);
        });
    }

    public Task RotatePages(IReadOnlyList<int> pages, int degrees) => RunBusy(async context =>
    {
        var pdf = context.Pdf;
        var turned = pages.ToList();
        Commit(await context.Run(() => PdfPages.Rotate(pdf, turned, degrees)));
        RestoreSelection(turned);
    });

    [RelayCommand]
    private Task DeleteSelectedPages() => DeletePages(ChosenPages());

    [RelayCommand]
    private Task RotateSelectedPages(string degrees) => RotatePages(ChosenPages(), int.Parse(degrees));

    /// <summary>Saves the chosen pages as PNG pictures into a folder the user picks.</summary>
    [RelayCommand]
    private Task ExportSelectedPages() => RunBusy(async context =>
    {
        var pages = ChosenPages();
        var folder = await Dialogs.PickFolder(Loc.Get("PickOutputFolder"));
        if (folder == null) return;
        var pdf = context.Pdf;
        var name = Units.DocumentName(Session);
        var written = await context.Run(() => WritePictures(pdf, pages, folder, name, context.Progress, context.Cancel));
        StatusText = Loc.Format("StatusFilesWritten", written.Count, folder);
    });

    /// <summary>
    /// Pictures of pages as PNG files in a scratch folder, for dragging them out of the window: dropped on a folder
    /// or the desktop they are copied there, dropped on another program they are opened by it.
    /// </summary>
    public async Task<IReadOnlyList<string>> PagePictures(IReadOnlyList<int> pages)
    {
        // The pictures show the document as it is, with every edit a tool still holds back.
        await FlushTool();
        if (Session.Bytes is not { } pdf || pages.Count == 0) return [];
        var name = Units.DocumentName(Session);
        var folder = Path.Combine(_dragFolder, (++_dragCount).ToString());
        try
        {
            // Whatever was dragged before has long been copied to where it was dropped.
            if (Directory.Exists(_dragFolder)) Directory.Delete(_dragFolder, true);
            return await Task.Run(() => WritePictures(pdf, pages, folder, name, null, CancellationToken.None));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfException or ObjectDisposedException)
        {
            StatusText = Describe(e);
            return [];
        }
    }

    /// <summary>Where the pictures of pages being dragged out are kept until the window closes.</summary>
    internal string ScratchFolder => _dragFolder;

    /// <summary>Removes the scratch files made for dragging pages out. Called when the window closes.</summary>
    public void DeleteScratchFiles()
    {
        try
        {
            if (Directory.Exists(_dragFolder)) Directory.Delete(_dragFolder, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leftover temp files are cleaned up by the system eventually.
        }
    }

    private static List<string> WritePictures(byte[] pdf, IReadOnlyList<int> pages, string folder, string name,
        IProgress<double>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        using var view = new PdfView(pdf);
        var files = new List<string>();
        for (var i = 0; i < pages.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var info = view.Pages[pages[i]];
            // 200 dots per inch; less for sheets so large that the picture would run to tens of megapixels.
            var scale = Math.Min(200 / 72.0, Math.Sqrt(24_000_000 / Math.Max(1, info.Width * info.Height)));
            using var bitmap = view.Render(pages[i], scale);
            var file = Path.Combine(folder, $"{name}_{pages[i] + 1:D3}.png");
            File.WriteAllBytes(file, PdfImages.Encode(bitmap, ImageFormat.Png));
            files.Add(file);
            progress?.Report((i + 1.0) / pages.Count);
        }

        return files;
    }

    // ---- preview ------------------------------------------------------------------------------------------------

    partial void OnShowPreviewChanged(bool value)
    {
        _previewHeld = false;
        InvalidatePreview();
    }

    /// <summary>
    /// Asks for the preview to be made again, shortly: changes that follow each other quickly (typing, dragging a
    /// slider) are answered once.
    /// </summary>
    public void InvalidatePreview()
    {
        _previewDelay?.Cancel();
        _previewDelay = null;
        var tool = SelectedTool;
        // Whatever asks for a new preview (another page, another selection, another tool) may also have changed
        // which pages the tool is going to work on.
        ShowPageChoice();
        ApplySummary = Summarise(tool);
        if (!ShowPreview || _previewHeld || !tool.HasPreview || !HasDocument)
        {
            if (!DropPreview()) return;
            _ = RenderPage();
            return;
        }

        var delay = _previewDelay = new CancellationTokenSource();
        _ = UpdatePreview(tool, delay.Token);
    }

    private string Summarise(Tool tool)
    {
        if (!HasDocument) return "";
        try
        {
            return tool.Summary(this) ?? "";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // What is wrong with the settings (a page range that cannot be read) is worth knowing before Apply too.
            return Describe(e);
        }
    }

    /// <summary>Stops showing a preview. True when one was on view, so the page has to be drawn again.</summary>
    private bool DropPreview()
    {
        _previewDelay?.Cancel();
        _previewDelay = null;
        PreviewNote = "";
        if (_previewView == null && !IsPreview) return false;
        _previewView?.Dispose();
        _previewView = null;
        IsPreview = false;
        return true;
    }

    private async Task UpdatePreview(Tool tool, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(180, cancel);
            if (Session.Bytes is not { } document) return;
            var index = CurrentPageIndex;
            var copy = new Lazy<byte[]>(() => PageCopy(document, index));
            var request = new PreviewRequest(document, copy, index, Pages.Count, SelectedPageIndexes, cancel);

            PreviewResult? result = null;
            string? problem = null;
            try
            {
                result = await Task.Run(() => tool.Preview(request), cancel);
            }
            catch (UserException e)
            {
                problem = e.Message;
            }
            catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
            {
                problem = Describe(e);
            }

            if (cancel.IsCancellationRequested || !ReferenceEquals(Session.Bytes, document) || CurrentPageIndex != index ||
                !ReferenceEquals(SelectedTool, tool)) return;

            // A result that is the page itself means the tool would leave this page as it is.
            var changed = result?.Pdf is { } pdf && !(copy.IsValueCreated && ReferenceEquals(pdf, copy.Value)) ? pdf : null;
            var previous = _previewView;
            _previewView = changed != null ? new PdfView(changed) : null;
            previous?.Dispose();
            IsPreview = _previewView != null;
            PreviewNote = problem ?? result?.Note ?? "";
            await RenderPage();
        }
        catch (OperationCanceledException)
        {
            // A newer request has taken over.
        }
    }

    /// <summary>The page on view as a document of its own. Made once per page and kept until the document changes.</summary>
    private byte[] PageCopy(byte[] document, int index)
    {
        lock (_pageCopyGate)
        {
            if (_pageCopy is { } cached && ReferenceEquals(cached.Document, document) && cached.Page == index) return cached.Copy;
            var copy = PdfPages.Extract(document, [index]);
            _pageCopy = (document, index, copy);
            return copy;
        }
    }

    // ---- tools --------------------------------------------------------------------------------------------------

    partial void OnSelectedToolChanging(Tool value)
    {
        var leaving = SelectedTool;
        leaving.IsSelected = false;
        Detach(leaving);
        // What the tool still holds back is written before the next one looks at the document.
        _ = Guarded(() => leaving.FlushAsync(this));
        leaving.Deactivated(this);
    }

    partial void OnSelectedToolChanged(Tool value)
    {
        value.IsSelected = true;
        Attach(value);
        EndPicking();
        SelectedOverlay = null;
        SelectedObject = null;
        Overlays.Clear();
        Objects.Clear();
        Region = null;
        DimOutsideRegion = false;
        _previewHeld = false;
        DropPreview();
        OnPropertyChanged(nameof(ShowApply));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanvasMode));
        OnPropertyChanged(nameof(CanUndo));
        // A tool may draw the page differently (comparison overlay), so the picture is refreshed as well.
        _ = RenderPage();
        _ = RefreshTool();
        InvalidatePreview();
    }

    private void Attach(Tool tool)
    {
        tool.PropertyChanged += OnToolPropertyChanged;
        tool.SettingsChanged += OnToolSettingsChanged;
    }

    private void Detach(Tool tool)
    {
        tool.PropertyChanged -= OnToolPropertyChanged;
        tool.SettingsChanged -= OnToolSettingsChanged;
    }

    private void OnToolPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Tool.CanvasMode) or "" or null) OnPropertyChanged(nameof(CanvasMode));
        // A tool may have something to apply only once it has found something (pages that are turned).
        if (e.PropertyName is nameof(Tool.HasApply) or "" or null) OnPropertyChanged(nameof(ShowApply));
        // A tool may be able to preview with some settings and not with others (a batch of files has no page to show).
        if (e.PropertyName != nameof(Tool.HasPreview)) return;
        OnPropertyChanged(nameof(CanPreview));
        InvalidatePreview();
    }

    private void OnToolSettingsChanged()
    {
        // A changed setting is a wish to see what it does, also right after the last one was applied.
        _previewHeld = false;
        InvalidatePreview();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    partial void OnSelectedObjectChanged(CanvasObject? value) => SelectedTool.ObjectSelected(this, value);

    public async Task RefreshTool()
    {
        try
        {
            await SelectedTool.RefreshAsync(this);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            StatusText = Describe(e);
        }
    }

    [RelayCommand]
    private async Task Apply()
    {
        var before = Session.Bytes;
        await RunBusy(SelectedTool.RunAsync, SelectedTool.NeedsDocument);
        if (ReferenceEquals(before, Session.Bytes)) return;
        // The page now shows what was applied. Previewing on top of that would show the effect twice.
        _previewHeld = true;
        InvalidatePreview();
    }

    [RelayCommand]
    private void Cancel() => _cancel?.Cancel();

    public Task OverlayClicked(Overlay? overlay)
    {
        SelectedOverlay = overlay;
        return Guarded(() => SelectedTool.OverlaySelected(this, overlay));
    }

    public Task PageClicked(double x, double y)
    {
        if (_picking is not { } field) return Guarded(() => SelectedTool.PageClicked(this, x, y));
        if (PageWidth > 0 && PageHeight > 0 && BitmapConverter.PixelAt(PageBitmap, x / PageWidth, y / PageHeight) is { } color) field.Set(color);
        EndPicking();
        return Task.CompletedTask;
    }

    public Task RegionSelected(RectD region) => Guarded(() => SelectedTool.RegionSelected(this, region));

    public void ObjectTransformed(CanvasObject obj, RectD bounds, double rotation, double scale, bool done) =>
        Guard(() => SelectedTool.ObjectTransformed(this, obj, bounds, rotation, scale, done));

    public void ObjectActivated(CanvasObject obj) => Guard(() => SelectedTool.ObjectActivated(this, obj));

    public void ObjectClicked(CanvasObject obj) => Guard(() => SelectedTool.ObjectClicked(this, obj));

    /// <summary>A key pressed while the page view has the attention. True when something was done with it.</summary>
    public bool KeyPressed(Key key, KeyModifiers modifiers)
    {
        if (key == Key.Escape && _picking != null)
        {
            EndPicking();
            return true;
        }

        try
        {
            return SelectedTool.KeyPressed(this, key, modifiers);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            StatusText = Describe(e);
            return true;
        }
    }

    /// <summary>Starts taking a colour from the page for <paramref name="field"/>; asked again, stops doing so.</summary>
    public void PickColor(ColorField field)
    {
        var again = ReferenceEquals(_picking, field);
        EndPicking();
        if (again || !HasDocument) return;
        _picking = field;
        field.IsPicking = true;
        StatusText = Loc.Get("StatusPickColor");
        OnPropertyChanged(nameof(CanvasMode));
    }

    private void EndPicking()
    {
        if (_picking == null) return;
        _picking.IsPicking = false;
        _picking = null;
        OnPropertyChanged(nameof(CanvasMode));
    }

    // ---- recently used images -----------------------------------------------------------------------------------

    /// <summary>Makes a file field offer the images used before, and keep doing so as more are used.</summary>
    public void TrackRecentImages(FileField field)
    {
        _imageFields.Add(field);
        field.Pin = (file, pinned) => ChangeImageHistory(() => Settings.PinImage(file.Path, pinned));
        field.Forget = file => ChangeImageHistory(() => Settings.ForgetImage(file.Path));
        field.WithRecent(RecentImages());
    }

    private void ChangeImageHistory(Action change)
    {
        change();
        foreach (var field in _imageFields) field.WithRecent(RecentImages());
    }

    /// <summary>Notes that an image file was used, which puts it first among the recent ones everywhere.</summary>
    public void RememberImage(string path)
    {
        if (Settings.RecentImages.FirstOrDefault() == path || Settings.IsPinned(path)) return;
        Settings.RememberImage(path);
        foreach (var field in _imageFields) field.WithRecent(RecentImages());
    }

    private List<RecentFile> RecentImages()
    {
        var files = new List<RecentFile>();
        foreach (var (path, pinned) in Settings.ImageHistory().Where(entry => File.Exists(entry.Path)))
        {
            var file = new RecentFile(path) { IsPinned = pinned };
            files.Add(file);
            if (_thumbnails.TryGetValue(path, out var known))
            {
                file.Thumbnail = known;
                continue;
            }

            Task.Run(() => BitmapConverter.Thumbnail(path, 96)).ContinueWith(task =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!_thumbnails.TryGetValue(path, out var thumbnail)) _thumbnails[path] = thumbnail = task.Result;
                    file.Thumbnail = thumbnail;
                }), TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        return files;
    }

    // ---- running operations -------------------------------------------------------------------------------------

    /// <summary>
    /// Runs an operation with the busy indicator, progress and cancellation, and reports any failure to the user.
    /// </summary>
    public async Task RunBusy(Func<ToolContext, Task> action, bool needsDocument = true)
    {
        if (IsBusy) return;
        if (needsDocument && !HasDocument)
        {
            StatusText = Loc.Get("ErrNoDocument");
            return;
        }

        IsBusy = true;
        Progress = 0;
        _cancel = new CancellationTokenSource();
        var progress = new Progress<double>(value => Progress = value);
        try
        {
            // The operation has to see every edit made so far, also those a tool has not written into the document yet.
            await SelectedTool.FlushAsync(this);
            await action(new ToolContext(this, progress, _cancel.Token));
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Get("StatusCancelled");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            StatusText = Describe(e);
            await Dialogs.Message(StatusText);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsBusy = false;
            NotifyState();
            if (_refreshWhenIdle)
            {
                _refreshWhenIdle = false;
                _ = RefreshTool();
            }
        }
    }

    /// <summary>Asks for the active tool to be refreshed once the running operation has finished.</summary>
    public void RefreshWhenIdle() => _refreshWhenIdle = true;

    private async Task Guarded(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            StatusText = Describe(e);
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            StatusText = Describe(e);
        }
    }

    /// <summary>Turns an exception into a message in the interface language.</summary>
    public static string Describe(Exception e) => e switch
    {
        UserException => e.Message,
        PdfPasswordException => Loc.Get("ErrPassword"),
        OcrLanguageMissingException missing => Loc.Format("ErrOcrLanguages",
            string.Join(", ", missing.Languages.Select(code => Languages.Get(code).NativeName))),
        OcrException => Loc.Get("ErrOcrUnavailable") + "\n" + e.Message,
        FormatException or ArgumentOutOfRangeException => Loc.Get("ErrPageRange"),
        PdfException => Loc.Format("ErrPdf", e.Message),
        IOException or UnauthorizedAccessException => Loc.Format("ErrFile", e.Message),
        HttpRequestException => Loc.Format("ErrDownload", e.Message),
        _ => Loc.Format("ErrUnexpected", e.Message)
    };
}
