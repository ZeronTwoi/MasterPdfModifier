using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfModifier.Core;
using PdfModifier.Core.Operations;
using PdfModifier.Localization;
using PdfModifier.Services;
using SkiaSharp;

namespace PdfModifier.ViewModels;

/// <summary>A problem to show to the user as is; the message is already localised.</summary>
public sealed class UserException(string message) : Exception(message);

/// <summary>How the page view reacts to the mouse while a tool is active.</summary>
public enum CanvasMode
{
    /// <summary>
    /// Highlighted areas and objects can be clicked; objects can be moved, resized and turned. Dragging anywhere else
    /// moves the page under the mouse.
    /// </summary>
    Select,

    /// <summary>A click anywhere on the page is reported.</summary>
    Click,

    /// <summary>The user drags out a rectangle, which is reported once and not kept.</summary>
    Region,

    /// <summary>The user drags out a rectangle that stays on the page and can be moved and resized by its handles.</summary>
    Area,

    /// <summary>The next click takes the colour under the pointer.</summary>
    Pick
}

/// <summary>A highlighted, clickable area on the page (visual coordinates in points).</summary>
public sealed class Overlay(RectD bounds, string text, object source, bool isScan, bool doubtful = false)
{
    /// <summary>True for text that was read with little certainty: shown in red, as something to check.</summary>
    public bool Doubtful => doubtful;

    public RectD Bounds => bounds;
    public string Text => text;

    /// <summary>The detected item behind the highlight (a text run or a recognised line).</summary>
    public object Source => source;

    /// <summary>True when the text was read from the page image rather than from the document's text.</summary>
    public bool IsScan => isScan;
}

/// <summary>
/// An object on the page that can be selected, moved, resized and turned in the page view: the picture of a
/// <see cref="PageObject"/> as it will look in the document.
/// </summary>
public sealed partial class CanvasObject(PageObject model) : ObservableObject
{
    [ObservableProperty] private PageObject _model = model;

    /// <summary>What to draw for it: the rendered text of a box or the pixels of an image. Patches need none.</summary>
    [ObservableProperty] private IImage? _picture;

    /// <summary>Points of margin the picture has around the box on every side (text only).</summary>
    public double PicturePadding { get; set; }

    public int Id => Model.Id;
}

/// <summary>
/// A frame around one of several pages found on a sheet, with its place in their order written on it. It can be
/// moved and resized like any object, but it is only a mark on the page view: nothing of it is stored in the document.
/// </summary>
public sealed record SheetArea : PageObject
{
    /// <summary>The number shown on the frame; empty while the page has not been given a place yet.</summary>
    public string Label { get; init; } = "";

    /// <summary>Drawn in red: the frame was set by a close call and wants a look before it is relied on.</summary>
    public bool Doubtful { get; init; }
}

/// <summary>
/// A mark a tool puts on a page in the page list: a few characters, in red when the page needs attention, and what
/// to say about it when the pointer rests there.
/// </summary>
public sealed record PageBadge(string Text, bool Warns, string Tip);

/// <summary>What a tool gets to work with when it runs.</summary>
public sealed class ToolContext(MainViewModel main, IProgress<double> progress, CancellationToken cancel)
{
    public MainViewModel Main => main;
    public DocumentSession Session => main.Session;
    public Dialogs Dialogs => main.Dialogs;
    public Settings Settings => main.Settings;
    public IProgress<double> Progress => progress;
    public CancellationToken Cancel => cancel;

    public byte[] Pdf => Session.Bytes ?? throw new UserException(Loc.Get("ErrNoDocument"));
    public int PageCount => Session.View?.PageCount ?? 0;
    public int CurrentPage => main.CurrentPageIndex;
    public IReadOnlyList<int> SelectedPages => main.SelectedPageIndexes;

    public List<int>? Resolve(PagesField field) => field.Resolve(SelectedPages, CurrentPage, PageCount);

    /// <summary>Replaces the document with the result of an operation (undoable).</summary>
    public void Commit(byte[] pdf) => main.Commit(pdf);

    public void Status(string message) => main.StatusText = message;

    /// <summary>Runs CPU-heavy work off the UI thread.</summary>
    public Task<T> Run<T>(Func<T> work) => Task.Run(work, cancel);
}

/// <summary>
/// What a tool needs to show its effect before it is applied. Previews are made from a copy of the page on view only,
/// so they take about as long for a document of a thousand pages as for one of a single page.
/// </summary>
public sealed class PreviewRequest(byte[] document, Lazy<byte[]> page, int pageIndex, int pageCount, IReadOnlyList<int> selectedPages,
    CancellationToken cancel)
{
    /// <summary>The whole document, for the few tools that have to look across pages.</summary>
    public byte[] Document => document;

    /// <summary>A one-page document holding just the page on view.</summary>
    public byte[] Page => page.Value;

    public int PageIndex => pageIndex;
    public int PageCount => pageCount;
    public CancellationToken Cancel => cancel;

    public List<int>? Resolve(PagesField field) => field.Resolve(selectedPages, pageIndex, pageCount);

    /// <summary>True when the page on view is one of the pages the tool is set to change.</summary>
    public bool Includes(PagesField field) => Resolve(field) is not { } pages || pages.Contains(pageIndex);
}

/// <summary>
/// The outcome of a preview: the page as it would become (a one-page document) and, optionally, a line of text about it.
/// A null <see cref="Pdf"/> shows the page as it is.
/// </summary>
public sealed record PreviewResult(byte[]? Pdf, string? Note = null);

/// <summary>One entry in the tool list: its options (<see cref="Fields"/>) and what happens on Apply.</summary>
public abstract class Tool(string id, string groupKey) : LocalizedObject
{
    public string Id => id;
    public string Title => Loc.Get("Tool" + id);
    public string Description => Loc.Get("Tool" + id + "Hint");
    public string Group => Loc.Get(groupKey);
    public string GroupKey => groupKey;

    /// <summary>The small drawing shown next to the title.</summary>
    public Geometry Icon => Controls.Icons.ForTool(id);

    private bool _isSelected, _isListed = true;

    /// <summary>True for the tool whose options are on view. Set by the main view model.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>False while the tool is left out of the list because it does not match what is searched for.</summary>
    public bool IsListed
    {
        get => _isListed;
        set => SetProperty(ref _isListed, value);
    }

    /// <summary>Set by the main view model: true for the first tool of each group, which shows the group heading.</summary>
    public bool StartsGroup { get; set; }

    public List<Field> Fields { get; } = [];

    /// <summary>False for tools that work without an open document (merge, batch, settings).</summary>
    public virtual bool NeedsDocument => true;

    /// <summary>False for tools whose panel has its own buttons instead of the main Apply button.</summary>
    public virtual bool HasApply => true;

    public virtual string ApplyLabel => Loc.Get("Apply");

    public virtual CanvasMode CanvasMode => CanvasMode.Select;

    /// <summary>True for tools that can show their effect on the page before they are applied.</summary>
    public virtual bool HasPreview => false;

    /// <summary>True while the tool holds edits it has not written into the document yet (see <see cref="FlushAsync"/>).</summary>
    public virtual bool HasPendingChanges => false;

    /// <summary>Raised when an option was changed, so that the preview no longer shows what Apply would do.</summary>
    public event Action? SettingsChanged;

    protected void OnSettingsChanged() => SettingsChanged?.Invoke();

    /// <summary>What the Apply button does. Tools without one (<see cref="HasApply"/> false) leave this empty.</summary>
    public virtual Task RunAsync(ToolContext context) => Task.CompletedTask;

    /// <summary>Called when the tool is shown and whenever the document or the current page changes while it is shown.</summary>
    public virtual Task RefreshAsync(MainViewModel main) => Task.CompletedTask;

    public virtual void Deactivated(MainViewModel main)
    {
    }

    /// <summary>
    /// Writes into the document whatever the tool still holds back (edits it collects before saving them in one go).
    /// Called before anything else reads or replaces the document.
    /// </summary>
    public virtual Task FlushAsync(MainViewModel main) => Task.CompletedTask;

    /// <summary>
    /// The page on view with the tool's current settings applied. Runs on a worker thread; throwing a
    /// <see cref="UserException"/> shows its message instead of a preview.
    /// </summary>
    public virtual PreviewResult? Preview(PreviewRequest request) => null;

    /// <summary>
    /// One line saying what Apply will do with the settings as they are, for tools whose effect cannot be shown on
    /// a page (which pages go, how many files are written). Null when there is nothing to say.
    /// </summary>
    public virtual string? Summary(MainViewModel main) => null;

    public virtual Task OverlaySelected(MainViewModel main, Overlay? overlay) => Task.CompletedTask;

    public virtual Task PageClicked(MainViewModel main, double x, double y) => Task.CompletedTask;

    public virtual Task RegionSelected(MainViewModel main, RectD region) => Task.CompletedTask;

    /// <summary>Other pages were selected in the page list.</summary>
    public virtual void PagesSelected(MainViewModel main)
    {
    }

    /// <summary>The selection in the page view changed (null: nothing is selected any more).</summary>
    public virtual void ObjectSelected(MainViewModel main, CanvasObject? obj)
    {
    }

    /// <summary>
    /// An object is being dragged, resized or turned. <paramref name="scale"/> is how much it grew when it is resized
    /// as a whole (by a corner) and 1 otherwise; <paramref name="done"/> is true when the mouse button was released.
    /// </summary>
    public virtual void ObjectTransformed(MainViewModel main, CanvasObject obj, RectD bounds, double rotation, double scale, bool done)
    {
    }

    /// <summary>An object was clicked without being moved.</summary>
    public virtual void ObjectClicked(MainViewModel main, CanvasObject obj)
    {
    }

    /// <summary>An object was double-clicked, which asks for its content to be edited.</summary>
    public virtual void ObjectActivated(MainViewModel main, CanvasObject obj)
    {
    }

    /// <summary>A key was pressed in the page view. Returns true when the tool used it.</summary>
    public virtual bool KeyPressed(MainViewModel main, Key key, KeyModifiers modifiers) => false;

    /// <summary>Lets a tool replace the page picture (e.g. with a comparison overlay). Null keeps the normal rendering.</summary>
    public virtual SKBitmap? RenderPage(MainViewModel main, int pageIndex, double scale) => null;

    protected T Add<T>(T field) where T : Field
    {
        Fields.Add(field);
        field.Edited += OnSettingsChanged;
        return field;
    }
}
