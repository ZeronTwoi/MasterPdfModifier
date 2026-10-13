using Avalonia.Input;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using PdfModifier.Localization;
using PdfModifier.Services;

namespace PdfModifier.ViewModels.Tools;

/// <summary>
/// What the pages of one state of a document say, kept for as long as the document stays as it is: looking for
/// something a second time is then a matter of moments, and a scan is read only once.
/// </summary>
internal sealed class PageWordsCache
{
    // A page read off its picture and the same page taken by its text alone are two different answers.
    private readonly Dictionary<(int Page, bool Read), PageWords> _pages = [];
    private byte[]? _document;

    /// <summary>What a page says; a page that is a scan is read when <paramref name="reading"/> says how.</summary>
    public PageWords Of(byte[] document, PdfView view, int page, OcrSettings? reading, CancellationToken cancel)
    {
        var key = (page, reading != null);
        lock (_pages)
        {
            if (!ReferenceEquals(document, _document)) _pages.Clear();
            _document = document;
            if (_pages.TryGetValue(key, out var known)) return known;
        }

        var words = PdfText.Of(view, page, reading, cancel);
        lock (_pages)
        {
            if (ReferenceEquals(document, _document)) _pages[key] = words;
        }

        return words;
    }

    /// <summary>
    /// Looks through pages for the two kinds that matter here: those worth reading (part of what they say may be
    /// there only as a picture) and, among them, the scans, of which alone it can be said that something was passed
    /// over when they were not read. Reports its progress as the first <paramref name="share"/> of the whole job.
    /// </summary>
    public static (HashSet<int> WorthReading, HashSet<int> Scans) Kinds(PdfView view, IReadOnlyList<int> pages, ToolContext context, double share)
    {
        var (worth, scans) = (new HashSet<int>(), new HashSet<int>());
        for (var i = 0; i < pages.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var kind = PdfText.Kind(view, pages[i]);
            if (kind.WorthReading) worth.Add(pages[i]);
            if (kind.Scan) scans.Add(pages[i]);
            context.Progress.Report((i + 1.0) / pages.Count * share);
        }

        return (worth, scans);
    }

    /// <summary>Says something in the status line from work that runs beside the window.</summary>
    public static void Say(ToolContext context, string text) => Avalonia.Threading.Dispatcher.UIThread.Post(() => context.Status(text));
}

/// <summary>
/// Finds a word or a phrase: in the open document, where every place it was found at is marked on the page, counted
/// in the page list and listed with the words around it; or in a whole folder of PDFs, where the files that have it
/// are listed and open with a click. Pages that are scans are read for it; what was read with little certainty is
/// marked red, so nobody relies on a place that was half guessed.
/// </summary>
public sealed class SearchTool : Tool
{
    private readonly MainViewModel _main;
    private readonly TextField _phrase;
    private readonly ModeField _where;
    private readonly FolderField _folder;
    private readonly BoolField _subfolders, _matchCase, _wholeWords, _scans;
    private readonly InfoField _status, _found;
    private readonly ActionField _setup, _previous, _next;
    private readonly ListField _files, _places;
    private readonly PageWordsCache _words = new();

    // What was found in the open document, for one state of it, and the place in hand.
    private byte[]? _document;
    private List<TextHit> _hits = [];
    private int _current = -1;
    private bool _focused;

    /// <summary>What was found in one file of a folder.</summary>
    private sealed record FileFound(string Path, int Places, IReadOnlyList<int> Pages, string First, int Unread, bool Doubtful);

    public SearchTool(MainViewModel main) : base("Find", "GroupFind")
    {
        _main = main;
        _phrase = Add(new TextField("SearchPhrase"));
        _phrase.Submit = () => main.ApplyCommand.Execute(null);
        _where = Add(new ModeField("SearchWhere", new Choice("document", "SearchInDocument"), new Choice("folder", "SearchInFolder")));
        _folder = Add(new FolderField("SearchFolder").WithPicker(main));
        _subfolders = Add(new BoolField("SearchSubfolders", true));
        _matchCase = Add(new BoolField("SearchMatchCase"));
        _wholeWords = Add(new BoolField("SearchWholeWords"));
        _scans = Add(new BoolField("SearchScans", true));
        _setup = Add(OcrSetup.Button(main));
        // What a folder gave, and under it what the open document gave: a file of the folder, once opened, shows
        // its places without the list of files going away.
        _status = Add(new InfoField());
        _files = Add(new ListField("SearchFiles") { Chosen = item => _ = OpenFound((FileFound)item.Source), MaxHeight = 170 });
        _found = Add(new InfoField());
        _previous = new ActionField("SearchPrevious", () => Step(-1)) { Icon = Controls.Icons.Previous };
        _next = new ActionField("SearchNext", () => Step(1)) { Icon = Controls.Icons.Next };
        Add(new RowField(_previous, _next));
        _places = Add(new ListField("SearchPlaces") { Chosen = item => Go(_hits.IndexOf((TextHit)item.Source)) });
        _where.PropertyChanged += (_, _) => Update();
        _setup.IsVisible = false;
        Update();
    }

    private bool WholeFolder => _where.Key == "folder";

    public override bool NeedsDocument => !WholeFolder;
    public override string ApplyLabel => Loc.Get("SearchRun");

    private FindOptions Options => new(_matchCase.Value, _wholeWords.Value);

    private void Update()
    {
        _folder.IsVisible = _subfolders.IsVisible = _status.IsVisible = WholeFolder;
        OnPropertyChanged(nameof(NeedsDocument));
        ShowSteps();
    }

    private void ShowSteps()
    {
        _previous.IsVisible = _next.IsVisible = _hits.Count > 1;
        // A heading over nothing only raises the question what is missing.
        _places.IsVisible = _hits.Count > 0;
        _files.IsVisible = WholeFolder && _files.HasItems;
    }

    public override Task RefreshAsync(MainViewModel main)
    {
        // The box to type into has the keyboard when the tool is opened, as a search box should.
        if (!_focused)
        {
            _focused = true;
            _phrase.Focus();
        }

        if (!ReferenceEquals(main.Session.Bytes, _document))
        {
            // Another document, or this one changed: the places found may be anywhere else by now.
            Forget();
            _document = main.Session.Bytes;
        }

        ShowLack();
        Mark();
        return Task.CompletedTask;
    }

    public override void Deactivated(MainViewModel main)
    {
        _focused = false;
        main.SetPageBadges(null);
    }

    private void Forget()
    {
        _hits = [];
        _current = -1;
        _places.Fill([]);
        _main.SetPageBadges(null);
        _main.Overlays.Clear();
        _found.SetText("");
        ShowSteps();
    }

    /// <summary>Says what reading scans lacks on this computer, with the button that fetches it, while reading is asked for.</summary>
    private void ShowLack()
    {
        var lack = _scans.Value ? _main.Supplies.Lack() : null;
        _setup.IsVisible = lack is { CanFetch: true };
    }

    // ---- looking ------------------------------------------------------------------------------------------------

    public override async Task RunAsync(ToolContext context)
    {
        var phrase = _phrase.Value.Trim();
        if (phrase.Length == 0) throw new UserException(Loc.Get("SearchNothingAsked"));
        if (WholeFolder) await SearchFolder(context, phrase);
        else await SearchDocument(context, phrase);
    }

    /// <summary>
    /// How scans are to be read, or null when they are not: because the box is cleared, or because reading lacks
    /// something here that the user did not want fetched. A search is never stopped by that; it says what it left out.
    /// </summary>
    private Task<(OcrSettings? Reading, string? WhyNot)> Reading(ToolContext context) =>
        _scans.Value ? OcrSetup.ReadingFor(context) : Task.FromResult<(OcrSettings?, string?)>((null, null));

    private async Task SearchDocument(ToolContext context, string phrase)
    {
        var pdf = context.Pdf;
        var view = context.Session.View!;
        var options = Options;
        var count = context.PageCount;
        // Reading is asked about only when there is a scan to read. Finding that out is a first pass over the pages.
        var (worth, pictures) = await context.Run(() => PageWordsCache.Kinds(view, Enumerable.Range(0, count).ToList(), context, 0.25));
        var (reading, whyNot) = pictures.Count > 0 ? await Reading(context) : (null, null);

        var found = await context.Run(() =>
        {
            var hits = new List<TextHit>();
            for (var page = 0; page < count; page++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                var read = worth.Contains(page) ? reading : null;
                if (read != null && pictures.Contains(page)) PageWordsCache.Say(context, Loc.Format("SearchReading", page + 1, count));
                hits.AddRange(PdfText.Find(_words.Of(pdf, view, page, read, context.Cancel).Lines, page, phrase, options));
                context.Progress.Report(0.25 + (page + 1.0) / count * 0.75);
            }

            return hits;
        });

        _document = pdf;
        _hits = found;
        _current = -1;
        _places.Fill(found.Select(hit => new ListItem(hit)
        {
            Title = hit.InContext, Detail = Loc.Format(hit.Read ? "SearchPageRead" : "PagesChosenOne", hit.Page + 1), Warns = hit.Doubtful
        }));
        ShowSteps();
        ShowLack();

        var unread = reading == null ? pictures.Count : 0;
        var pages = found.Select(hit => hit.Page).Distinct().Count();
        var doubtful = found.Count(hit => hit.Doubtful);
        var report = new List<string> { found.Count == 0 ? Loc.Get("SearchNone") : Loc.Format("SearchFound", found.Count, pages) };
        if (doubtful > 0) report.Add(Loc.Format("SearchDoubt", doubtful));
        if (unread > 0) report.Add(whyNot == null ? Loc.Format("SearchUnreadOff", unread) : Loc.Format("SearchUnread", unread, whyNot));
        _found.SetText(string.Join("\n", report));
        _found.Warns = unread > 0 || doubtful > 0;
        context.Status(report[0]);

        Mark();
        if (found.Count > 0) Go(0);
    }

    /// <summary>Counts the places in the page list and marks those of the page on view.</summary>
    private void Mark()
    {
        if (!ReferenceEquals(_main.SelectedTool, this)) return;
        _main.SetPageBadges(_hits.GroupBy(hit => hit.Page).ToDictionary(group => group.Key,
            group => new PageBadge(group.Count().ToString(), group.Any(hit => hit.Doubtful), Loc.Format("SearchBadge", group.Count()))));
        _main.Overlays.Clear();
        Overlay? inHand = null;
        for (var i = 0; i < _hits.Count; i++)
        {
            if (_hits[i].Page != _main.CurrentPageIndex) continue;
            foreach (var area in _hits[i].Areas)
            {
                var overlay = new Overlay(area, _hits[i].Found, _hits[i], _hits[i].Read, _hits[i].Doubtful);
                _main.Overlays.Add(overlay);
                if (i == _current) inHand ??= overlay;
            }
        }

        _main.SelectedOverlay = inHand;
    }

    /// <summary>Goes to a place that was found: its page comes on view, and it is the one that stands out there.</summary>
    private void Go(int index)
    {
        if (index < 0 || index >= _hits.Count) return;
        _current = index;
        _places.Show(_places.Items[index]);
        _main.StatusText = Loc.Format("SearchPlaceOf", index + 1, _hits.Count, _hits[index].Page + 1);
        // Turning the page marks its places anew; on the page already on view that is done here.
        if (_hits[index].Page != _main.CurrentPageIndex) _main.GoToPage(_hits[index].Page);
        else Mark();
    }

    private Task Step(int direction)
    {
        if (_hits.Count > 0) Go(((Math.Max(_current, 0) + direction) % _hits.Count + _hits.Count) % _hits.Count);
        return Task.CompletedTask;
    }

    /// <summary>A place clicked on the page is the place in hand.</summary>
    public override Task OverlaySelected(MainViewModel main, Overlay? overlay)
    {
        if (overlay?.Source is TextHit hit && _hits.IndexOf(hit) is >= 0 and var index && index != _current) Go(index);
        return Task.CompletedTask;
    }

    public override bool KeyPressed(MainViewModel main, Key key, KeyModifiers modifiers)
    {
        if (key is not (Key.F3 or Key.Enter) || _hits.Count == 0) return false;
        _ = Step(modifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        return true;
    }

    // ---- a folder of documents ----------------------------------------------------------------------------------

    private async Task SearchFolder(ToolContext context, string phrase)
    {
        var folder = _folder.Path.Trim().Trim('"');
        if (folder.Length == 0 || !Directory.Exists(folder)) throw new UserException(Loc.Get("ErrChooseFolder"));
        folder = Path.GetFullPath(folder);
        var options = Options;
        var search = _subfolders.Value ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = await context.Run(() => Directory.EnumerateFiles(folder, "*.pdf", search)
            .Where(file => Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList());
        if (files.Count == 0) throw new UserException(Loc.Get("ErrCompressNoFiles"));
        var (reading, whyNot) = await Reading(context);

        var skipped = 0;
        var found = await context.Run(() =>
        {
            var list = new List<FileFound>();
            for (var i = 0; i < files.Count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                PageWordsCache.Say(context, Loc.Format("SearchLooking", Path.GetRelativePath(folder, files[i])));
                try
                {
                    var bytes = File.ReadAllBytes(files[i]);
                    if (PdfPages.NeedsPassword(bytes))
                    {
                        skipped++;
                        continue;
                    }

                    using var view = new PdfView(bytes);
                    var hits = new List<TextHit>();
                    var unread = 0;
                    for (var page = 0; page < view.PageCount; page++)
                    {
                        context.Cancel.ThrowIfCancellationRequested();
                        if (reading == null && PdfText.IsPicture(view, page)) unread++;
                        hits.AddRange(PdfText.Find(PdfText.Of(view, page, reading, context.Cancel).Lines, page, phrase, options));
                        context.Progress.Report((i + (page + 1.0) / view.PageCount) / files.Count);
                    }

                    if (hits.Count > 0 || unread > 0)
                        list.Add(new FileFound(files[i], hits.Count, hits.Select(hit => hit.Page).Distinct().ToList(),
                            hits.Count > 0 ? hits[0].InContext : "", unread, hits.Any(hit => hit.Doubtful)));
                }
                catch (Exception e) when (e is PdfException or IOException or UnauthorizedAccessException)
                {
                    // One unreadable file should not stop the rest of the folder.
                    skipped++;
                }

                context.Progress.Report((i + 1.0) / files.Count);
            }

            return list;
        });

        var having = found.Where(file => file.Places > 0).ToList();
        _files.Fill(having.Select(file => new ListItem(file)
        {
            Title = Path.GetRelativePath(folder, file.Path),
            Detail = Loc.Format("SearchFileDetail", file.Places, PageRange.Format(file.Pages)) + (file.First.Length > 0 ? "  ·  " + file.First : ""),
            Warns = file.Doubtful
        }));
        ShowSteps();

        var unreadFiles = found.Count(file => file.Unread > 0);
        var report = new List<string>
        {
            having.Count == 0 ? Loc.Format("SearchInFilesNone", files.Count) : Loc.Format("SearchInFiles", having.Count, files.Count, having.Sum(file => file.Places))
        };
        if (unreadFiles > 0) report.Add(whyNot == null ? Loc.Format("SearchFilesUnreadOff", unreadFiles) : Loc.Format("SearchFilesUnread", unreadFiles, whyNot));
        if (skipped > 0) report.Add(Loc.Format("SearchSkipped", skipped));
        _status.SetText(string.Join("\n", report));
        _status.Warns = unreadFiles > 0 || skipped > 0;
        ShowLack();
        context.Status(report[0]);
    }

    /// <summary>Opens a file of the list and shows the places in it, as if it had been searched by itself.</summary>
    private async Task OpenFound(FileFound file)
    {
        if (_main.IsBusy) return;
        if (!string.Equals(_main.Session.FilePath, file.Path, StringComparison.OrdinalIgnoreCase))
        {
            await _main.OpenFile(file.Path);
            if (!string.Equals(_main.Session.FilePath, file.Path, StringComparison.OrdinalIgnoreCase)) return;
        }

        var phrase = _phrase.Value.Trim();
        if (phrase.Length > 0) await _main.RunBusy(context => SearchDocument(context, phrase));
    }
}

/// <summary>
/// Takes text or a picture off a page: of the whole page, or of the part of it a frame is drawn around. The text
/// that would be taken stands in a box, where it can be put right before it is copied, and the lines it comes from
/// are marked on the page; on a scan it is read off the picture, and what was hard to read is marked red. The text
/// of whole pages can also be written into a file.
/// </summary>
public sealed class CopyTool : Tool
{
    private readonly MainViewModel _main;
    private readonly TextField _text;
    private readonly BoolField _scans;
    private readonly InfoField _note;
    private readonly ActionField _setup, _whole;
    private readonly SliderField _dpi;
    private readonly PagesField _pages;
    private readonly PageWordsCache _words = new();

    // The frame on the page on view; it belongs to that page of that state of the document.
    private RectD? _area;
    private (byte[]? Document, int Page) _shownFor;
    private int _version;

    public CopyTool(MainViewModel main) : base("Copy", "GroupFind")
    {
        _main = main;
        Add(new InfoField()).SetKey("CopyHint");
        _whole = Add(new ActionField("CopyWholePage", () => Frame(null)));
        _scans = Add(new BoolField("CopyScans", true));
        _note = Add(new InfoField());
        _setup = Add(OcrSetup.Button(main));
        _text = Add(new TextField("CopyText", "", true));
        Add(new RowField(new ActionField("CopyDoText", CopyText), new ActionField("CopyDoPicture", () => Picture(false)) { Icon = Controls.Icons.Picture }));
        _dpi = Add(new SliderField("ImageDpi", 300, 72, 600, 6).WithStops("", 150, 300, 600));
        Add(new ActionField("CopySavePicture", () => Picture(true)) { Icon = Controls.Icons.SaveAs });
        _pages = Add(new PagesField("CopyPagesText"));
        _scans.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BoolField.Value)) _ = Show(main);
        };
        _whole.IsVisible = _setup.IsVisible = false;
    }

    /// <summary>A frame is drawn on the page, and stays there to be moved and pulled into shape.</summary>
    public override CanvasMode CanvasMode => CanvasMode.Area;

    public override string ApplyLabel => Loc.Get("CopySaveText");

    public override Task RefreshAsync(MainViewModel main)
    {
        if (_shownFor != (main.Session.Bytes, main.CurrentPageIndex))
        {
            _area = null;
            _shownFor = (main.Session.Bytes, main.CurrentPageIndex);
        }

        main.Region = _area;
        return Show(main);
    }

    public override Task RegionSelected(MainViewModel main, RectD region) => Frame(region.Width >= 3 && region.Height >= 3 ? region : null);

    private Task Frame(RectD? area)
    {
        _area = area;
        _main.Region = area;
        return Show(_main);
    }

    public override bool KeyPressed(MainViewModel main, Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.Escape when _area != null:
                _ = Frame(null);
                return true;
            case Key.C when modifiers.HasFlag(KeyModifiers.Control):
                _ = CopyText();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Puts into the box what the page says, inside the frame or all over, and marks the lines it is from.</summary>
    private async Task Show(MainViewModel main)
    {
        if (!ReferenceEquals(main.SelectedTool, this)) return;
        var version = ++_version;
        _whole.IsVisible = _area != null;
        if (main.Session.Bytes is not { } document || main.Session.View is not { } view)
        {
            _text.Value = "";
            _note.SetText("");
            _setup.IsVisible = false;
            return;
        }

        var page = main.CurrentPageIndex;
        var area = _area;
        bool Current() => version == _version && ReferenceEquals(main.Session.Bytes, document) && main.CurrentPageIndex == page &&
                          ReferenceEquals(main.SelectedTool, this);
        try
        {
            var (worth, picture) = await Task.Run(() => PdfText.Kind(view, page));
            if (!Current()) return;
            OcrSettings? reading = null;
            string? whyNot = null;
            if (worth && _scans.Value)
            {
                // Opening this tool on a scan is asking for its text: what reading lacks is offered right away.
                // A page that merely has hardly a letter on it is read when that can be done, and asks for nothing.
                if (main.Supplies.Lack() is not { } lack) reading = OcrSetup.From(main.Settings);
                else if (picture) whyNot = lack.Text;
                if (whyNot != null) main.Supplies.Offer();
            }

            _setup.IsVisible = whyNot != null && main.Supplies.Lack() is { CanFetch: true };
            if (reading != null && picture) main.StatusText = Loc.Get("CopyReading");
            var words = await Task.Run(() => _words.Of(document, view, page, reading, CancellationToken.None));
            if (!Current()) return;

            // Of every line, the words the frame takes in: they are what is marked, not the line they stand on.
            var taken = words.Lines
                .Select(line => area is { } within ? line.Words.Where(word => within.Contains(word.Bounds.CenterX, word.Bounds.CenterY)).ToList() : line.Words)
                .Where(inside => inside.Count > 0)
                .Select(inside => new OcrLine(string.Join(' ', inside.Select(word => word.Text)), inside.Select(word => word.Bounds).Aggregate((a, b) => a.Union(b)), inside))
                .ToList();
            _text.Value = PdfText.In(words.Lines, area);
            main.Overlays.Clear();
            foreach (var line in taken)
                main.Overlays.Add(line.Words.Any(word => word.Confidence < 100) ? OcrSetup.Highlight(line) : new Overlay(line.Bounds, line.Text, line, false));
            var doubtful = main.Overlays.Count(overlay => overlay.Doubtful);
            if (reading != null) main.StatusText = Loc.Format("StatusTextFound", taken.Count);

            _note.Warns = doubtful > 0 || (picture && reading == null);
            _note.SetText(doubtful > 0 ? Loc.Format("CopyDoubt", doubtful)
                : picture && reading == null ? (whyNot == null ? Loc.Get("CopyScanOff") : Loc.Format("CopyScanUnread", whyNot))
                : taken.Count == 0 ? Loc.Get("CopyNoText")
                : "");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The document was closed under the reading, or the page cannot be read at all.
            if (!Current()) return;
            _note.Warns = true;
            _note.SetText(MainViewModel.Describe(e));
        }
    }

    private async Task CopyText()
    {
        if (_text.Value.Length == 0)
        {
            _main.StatusText = Loc.Get("CopyNoText");
            return;
        }

        await _main.CopyText(_text.Value);
        _main.StatusText = Loc.Format("CopyTextCopied", _text.Value.Length);
    }

    /// <summary>The frame, or the whole page without one, as a picture: onto the clipboard or into a file.</summary>
    private async Task Picture(bool save)
    {
        if (_main.Session.View is not { } view || _main.IsBusy) return;
        var page = _main.CurrentPageIndex;
        var area = _area ?? new RectD(0, 0, view.Pages[page].Width, view.Pages[page].Height);
        // As sharp as asked for, short of a picture no program could take.
        var scale = Math.Min(_dpi.Number / 72, Math.Sqrt(80_000_000 / Math.Max(1, area.Width * area.Height)));
        string? path = null;
        if (save)
        {
            path = await _main.Dialogs.SaveFile(Loc.Get("CopySavePicture"), $"{Units.DocumentName(_main.Session)}_{page + 1}.png", Dialogs.ImageFiles);
            if (path == null) return;
        }

        await _main.RunBusy(async context =>
        {
            using var bitmap = await context.Run(() => view.RenderArea(page, area, scale));
            if (path != null)
            {
                await context.Run(() =>
                {
                    File.WriteAllBytes(path, PdfImages.Encode(bitmap, ImageFormat.Png));
                    return true;
                });
                context.Status(Loc.Format("StatusSaved", Path.GetFileName(path)));
            }
            else
            {
                await context.Main.CopyPicture(bitmap);
                context.Status(Loc.Format("CopyPictureCopied", bitmap.Width, bitmap.Height));
            }
        });
    }

    public override string? Summary(MainViewModel main)
    {
        var pages = _pages.Resolve(main.SelectedPageIndexes, main.CurrentPageIndex, main.Pages.Count) ?? Enumerable.Range(0, main.Pages.Count).ToList();
        return Loc.Format("SummaryCopyText", pages.Count, PageRange.Format(pages));
    }

    /// <summary>The text of the chosen pages, page after page with an empty line between, and how many scans among them were not read.</summary>
    internal async Task<(string Text, int Unread, string? WhyNot)> TextOfPages(ToolContext context)
    {
        var pdf = context.Pdf;
        var view = context.Session.View!;
        var pages = context.Resolve(_pages) ?? Enumerable.Range(0, context.PageCount).ToList();
        var (worth, pictures) = await context.Run(() => PageWordsCache.Kinds(view, pages, context, 0.1));
        var (reading, whyNot) = pictures.Count > 0 && _scans.Value ? await OcrSetup.ReadingFor(context) : (null, null);
        var text = await context.Run(() =>
        {
            var all = new System.Text.StringBuilder();
            for (var i = 0; i < pages.Count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                var read = worth.Contains(pages[i]) ? reading : null;
                if (read != null && pictures.Contains(pages[i])) PageWordsCache.Say(context, Loc.Format("SearchReading", pages[i] + 1, context.PageCount));
                var said = PdfText.In(_words.Of(pdf, view, pages[i], read, context.Cancel).Lines);
                if (said.Length > 0)
                {
                    if (all.Length > 0) all.Append("\n\n");
                    all.Append(said);
                }

                context.Progress.Report(0.1 + (i + 1.0) / pages.Count * 0.9);
            }

            return all.ToString();
        });
        return (text, reading == null ? pictures.Count : 0, whyNot);
    }

    public override async Task RunAsync(ToolContext context)
    {
        var (text, unread, whyNot) = await TextOfPages(context);
        var note = unread == 0 ? "" : " " + (whyNot == null ? Loc.Format("SearchUnreadOff", unread) : Loc.Format("SearchUnread", unread, whyNot));
        if (text.Length == 0)
        {
            context.Status(Loc.Get("CopyNoText") + note);
            return;
        }

        var path = await context.Dialogs.SaveFile(Loc.Get("CopySaveText"), Units.DocumentName(context.Session) + ".txt", Dialogs.TextFiles);
        if (path == null) return;
        await context.Run(() =>
        {
            // With the mark at its start that tells every editor the letters are Unicode.
            File.WriteAllText(path, text.ReplaceLineEndings(), new System.Text.UTF8Encoding(true));
            return true;
        });
        context.Status(Loc.Format("StatusSaved", Path.GetFileName(path)) + note);
    }
}

/// <summary>
/// Saves the pictures a document is made with, at the resolution they are stored in: of a scanned page the scan
/// itself, of a brochure its photographs. The pictures of the page on view are marked on it and shown small, so it
/// is plain what will be saved; one of them can be picked and saved or copied by itself.
/// </summary>
public sealed class PicturesTool : Tool
{
    private const int Shown = 24;

    private readonly MainViewModel _main;
    private readonly PagesField _pages;
    private readonly SliderField _smallest, _quality;
    private readonly ModeField _format;
    private readonly BoolField _once;
    private readonly InfoField _found;
    private readonly PieceListField _onPage;
    private readonly ActionField _saveOne, _copyOne;

    // How many pictures the chosen pages hold, for the settings it was counted with.
    private (byte[] Document, string Asked, int Pictures, int Pages)? _counted;
    private CancellationTokenSource? _counting;
    private PagePicture? _picked;
    private int _here, _version;

    public PicturesTool(MainViewModel main) : base("Pictures", "GroupFind")
    {
        _main = main;
        _pages = Add(new PagesField());
        _smallest = Add(new SliderField("PicturesSmallest", 50, 0, 1000, 10).WithStops(" px", 0, 50, 100, 300));
        _format = Add(new ModeField("PicturesFormat", new Choice("stored", "PicturesAsStored"), new Choice("png", "PNG", true), new Choice("jpg", "JPEG", true)));
        _quality = Add(new SliderField("ImageQuality", 90, 10, 100, 5));
        _once = Add(new BoolField("PicturesOnce", true));
        _found = Add(new InfoField());
        _onPage = Add(new PieceListField("PicturesOnPage") { Chosen = item => Pick((PagePicture)item.Source) });
        _saveOne = new ActionField("PicturesSaveOne", () => One(true)) { Icon = Controls.Icons.SaveAs };
        _copyOne = new ActionField("PicturesCopyOne", () => One(false)) { Icon = Controls.Icons.Picture };
        Add(new RowField(_saveOne, _copyOne));
        _format.PropertyChanged += (_, _) => _quality.IsVisible = _format.Key == "jpg";
        // What is marked and counted follows the settings as they are changed.
        SettingsChanged += () => _ = Show(main);
        _quality.IsVisible = _saveOne.IsVisible = _copyOne.IsVisible = _onPage.IsVisible = false;
    }

    private PictureOptions Options => new((int)_smallest.Number,
        _format.Key switch { "png" => PictureFormat.Png, "jpg" => PictureFormat.Jpeg, _ => PictureFormat.AsStored }, (int)_quality.Number, _once.Value);

    public override Task RefreshAsync(MainViewModel main) => Show(main);

    public override void Deactivated(MainViewModel main)
    {
        _counting?.Cancel();
        Clear();
    }

    private void Clear()
    {
        foreach (var item in _onPage.Items) (item.Picture as IDisposable)?.Dispose();
        _onPage.Items.Clear();
        _onPage.IsVisible = false;
    }

    /// <summary>Marks the pictures of the page on view, shows them small, and counts those of the chosen pages.</summary>
    private async Task Show(MainViewModel main)
    {
        if (!ReferenceEquals(main.SelectedTool, this)) return;
        var version = ++_version;
        Pick(null);
        if (main.Session.Bytes is not { } document || main.Session.View is not { } view)
        {
            Clear();
            _found.SetText("");
            return;
        }

        var page = main.CurrentPageIndex;
        var smallest = (int)_smallest.Number;
        bool Current() => version == _version && ReferenceEquals(main.Session.Bytes, document) && main.CurrentPageIndex == page &&
                          ReferenceEquals(main.SelectedTool, this);
        try
        {
            var pictures = await Task.Run(() => view.GetPictures(page, smallest));
            if (!Current()) return;
            _here = pictures.Count;
            main.Overlays.Clear();
            foreach (var picture in pictures) main.Overlays.Add(new Overlay(picture.Bounds, $"{picture.Width} × {picture.Height}", picture, true));

            // Small pictures of them, as they show on the page.
            var small = await Task.Run(() => pictures.Take(Shown).Select(picture =>
            {
                var scale = Math.Min(2, 168 / Math.Max(1, Math.Max(picture.Bounds.Width, picture.Bounds.Height)));
                using var rendered = view.RenderArea(page, picture.Bounds, scale);
                return (picture, Bitmap: Services.BitmapConverter.ToAvalonia(rendered));
            }).ToList());
            if (!Current())
            {
                foreach (var (_, bitmap) in small) bitmap.Dispose();
                return;
            }

            Clear();
            foreach (var (picture, bitmap) in small)
                _onPage.Items.Add(new PieceItem(picture)
                {
                    Picture = bitmap, Label = (_onPage.Items.Count + 1).ToString(), Aspect = picture.Bounds.Width / Math.Max(1, picture.Bounds.Height)
                });
            _onPage.IsVisible = _onPage.Items.Count > 0;
            Report();
            Count(main, document);
        }
        catch (Exception e) when (e is ObjectDisposedException or PdfException)
        {
            // The document was replaced while its pictures were being looked at; the next look takes over.
        }
    }

    /// <summary>What was asked the last time the pictures of the chosen pages were counted: the pages, and what counts as a picture.</summary>
    private string Asked(MainViewModel main)
    {
        var pages = _pages.Resolve(main.SelectedPageIndexes, main.CurrentPageIndex, main.Pages.Count);
        return $"{(pages == null ? "all" : PageRange.Format(pages))}|{(int)_smallest.Number}|{_once.Value}";
    }

    private void Count(MainViewModel main, byte[] document)
    {
        string asked;
        List<int>? pages;
        try
        {
            asked = Asked(main);
            pages = _pages.Resolve(main.SelectedPageIndexes, main.CurrentPageIndex, main.Pages.Count);
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
        {
            return;
        }

        if (_counted is { } known && ReferenceEquals(known.Document, document) && known.Asked == asked) return;
        _counting?.Cancel();
        var counting = _counting = new CancellationTokenSource();
        var (smallest, once) = ((int)_smallest.Number, _once.Value);
        Task.Run(() =>
        {
            var pictures = PdfPictures.In(document, pages, smallest, null, counting.Token);
            var counted = once ? pictures.DistinctBy(picture => picture.Mark).ToList() : pictures.ToList();
            return (counted.Count, counted.Select(picture => picture.Page).Distinct().Count());
        }, counting.Token).ContinueWith(task => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (counting.IsCancellationRequested || !ReferenceEquals(main.Session.Bytes, document)) return;
            _counted = (document, asked, task.Result.Item1, task.Result.Item2);
            Report();
            // The line above Apply says how many will be saved, now that it is known.
            if (ReferenceEquals(main.SelectedTool, this)) main.InvalidatePreview();
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void Report()
    {
        var known = _counted is { } counted && ReferenceEquals(counted.Document, _main.Session.Bytes) && SameAsked(counted.Asked);
        _found.SetText(known ? Loc.Format("PicturesFound", _here, _counted!.Value.Pictures) : Loc.Format("PicturesFoundCounting", _here));
    }

    private bool SameAsked(string asked)
    {
        try
        {
            return asked == Asked(_main);
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public override void PagesSelected(MainViewModel main)
    {
        if (main.Session.Bytes is not { } document) return;
        Report();
        Count(main, document);
    }

    public override string? Summary(MainViewModel main) =>
        _counted is { } counted && ReferenceEquals(counted.Document, main.Session.Bytes) && SameAsked(counted.Asked)
            ? counted.Pictures == 0 ? Loc.Get("PicturesNone") : Loc.Format("SummaryPictures", counted.Pictures, counted.Pages)
            : null;

    // ---- one picture --------------------------------------------------------------------------------------------

    public override Task OverlaySelected(MainViewModel main, Overlay? overlay)
    {
        Pick(overlay?.Source as PagePicture, mark: false);
        return Task.CompletedTask;
    }

    private void Pick(PagePicture? picture, bool mark = true)
    {
        _picked = picture;
        _saveOne.IsVisible = _copyOne.IsVisible = picture != null;
        if (mark) _main.SelectedOverlay = picture == null ? null : _main.Overlays.FirstOrDefault(overlay => ReferenceEquals(overlay.Source, picture));
        if (picture != null) _main.StatusText = Loc.Format("PicturesPicked", picture.Width, picture.Height);
    }

    private async Task One(bool save)
    {
        if (_picked is not { } picture || _main.Session.Bytes is not { } document || _main.IsBusy) return;
        var options = Options;
        if (!save)
        {
            await _main.RunBusy(async context =>
            {
                using var pixels = await context.Run(() => PdfPictures.Pixels(document, picture)) ?? throw new UserException(Loc.Get("ErrImage"));
                await context.Main.CopyPicture(pixels);
                context.Status(Loc.Format("CopyPictureCopied", pixels.Width, pixels.Height));
            });
            return;
        }

        var file = await Task.Run(() => PdfPictures.AsFile(document, picture, options));
        if (file is not { } found)
        {
            _main.StatusText = Loc.Get("ErrImage");
            return;
        }

        var path = await _main.Dialogs.SaveFile(Loc.Get("PicturesSaveOne"), Name(Units.DocumentName(_main.Session), picture.Page, picture.Index + 1, found.Ending), Dialogs.ImageFiles);
        if (path == null) return;
        await Task.Run(() => File.WriteAllBytes(path, found.Data));
        _main.StatusText = Loc.Format("StatusSaved", Path.GetFileName(path));
    }

    private static string Name(string document, int page, int number, string ending) => $"{document}_p{page + 1:D3}_{number}{ending}";

    public override async Task RunAsync(ToolContext context)
    {
        var pdf = context.Pdf;
        var pages = context.Resolve(_pages);
        var options = Options;
        var folder = await context.Dialogs.PickFolder(Loc.Get("PickOutputFolder"));
        if (folder == null) return;
        var written = await SaveTo(context, pdf, pages, options, folder);
        context.Status(written == 0 ? Loc.Get("PicturesNone") : Loc.Format("StatusFilesWritten", written, folder));
    }

    /// <summary>Writes the pictures of the chosen pages into a folder, each named after its page and its place on it.</summary>
    internal static Task<int> SaveTo(ToolContext context, byte[] pdf, List<int>? pages, PictureOptions options, string folder)
    {
        var name = Units.DocumentName(context.Session);
        return context.Run(() =>
        {
            var onPage = new Dictionary<int, int>();
            return PdfPictures.Extract(pdf, pages, options, (picture, data, ending) =>
            {
                var number = onPage[picture.Page] = onPage.GetValueOrDefault(picture.Page) + 1;
                File.WriteAllBytes(Path.Combine(folder, Name(name, picture.Page, number, ending)), data);
            }, context.Progress, context.Cancel);
        });
    }
}
