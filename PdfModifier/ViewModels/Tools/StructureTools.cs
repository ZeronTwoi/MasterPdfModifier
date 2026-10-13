using PdfModifier.Core.Operations;
using PdfModifier.Localization;

namespace PdfModifier.ViewModels.Tools;

/// <summary>
/// The bookmarks of a document: the list of contents a reader shows beside the pages. Each can be named, led to
/// another page, moved up and down and in under another; every button acts at once. The app can also propose them
/// from the headings of the document, with a slider for how readily a line counts as one and the close calls
/// marked red. What is changed here is written into the document when it is saved or another tool takes over, and
/// goes with one Undo.
/// </summary>
public sealed class BookmarksTool : Tool
{
    /// <summary>A bookmark as the tool holds it: what it is, and whether it is a proposal that wants a look.</summary>
    private sealed class Entry(Bookmark bookmark, bool doubtful = false)
    {
        public Bookmark Bookmark = bookmark;
        public bool Doubtful = doubtful;
    }

    private readonly MainViewModel _main;
    private readonly InfoField _note;
    private readonly ListField _list;
    private readonly TextField _title;
    private readonly ActionField _rename, _point, _confirm, _up, _down, _out, _in, _delete, _clear;
    private readonly SliderField _readiness;

    // The bookmarks of one state of the document, and whether they are still as that document has them.
    private List<Entry> _entries = [];
    private byte[]? _document;
    private bool _changed, _proposed, _syncing;
    private int _unread;

    public BookmarksTool(MainViewModel main) : base("Bookmarks", "GroupEdit")
    {
        _main = main;
        _note = Add(new InfoField());
        _list = Add(new ListField("BookmarksList") { Chosen = _ => Chosen(), MaxHeight = 290 });
        _title = Add(new TextField("BookmarkTitle"));
        _title.Submit = () => _ = Selected >= 0 ? Rename() : AddHere();
        _rename = new ActionField("BookmarkRename", Rename);
        Add(new RowField(new ActionField("BookmarkAdd", AddHere) { Icon = Controls.Icons.Plus }, _rename));
        _confirm = Add(new ActionField("BookmarkConfirm", () => Change(entry => entry.Doubtful = false)));
        _point = Add(new ActionField("BookmarkPoint", () => Change(entry =>
        {
            entry.Bookmark = entry.Bookmark with { Page = main.CurrentPageIndex, Top = null };
            entry.Doubtful = false;
        })));
        _up = new ActionField("BookmarkUp", () => Move(-1)) { Icon = Controls.Icons.Up, Large = true };
        _down = new ActionField("BookmarkDown", () => Move(1)) { Icon = Controls.Icons.Down, Large = true };
        _out = new ActionField("BookmarkOut", () => Shift(-1)) { Icon = Controls.Icons.LevelOut, Large = true };
        _in = new ActionField("BookmarkIn", () => Shift(1)) { Icon = Controls.Icons.LevelIn, Large = true };
        Add(new RowField(_up, _down, _out, _in));
        _delete = new ActionField("BookmarkDelete", Delete) { Icon = Controls.Icons.Trash };
        _clear = new ActionField("BookmarkClear", Clear);
        Add(new RowField(_delete, _clear));
        Add(new InfoField()).SetKey("BookmarkSuggestHint");
        _readiness = Add(new SliderField("BookmarkReadiness", 50, 0, 100, 5));
        Add(new ActionField("BookmarkSuggest", () => main.RunBusy(Suggest)) { Icon = Controls.Icons.Search });
        // Moved while proposals are on view, the slider proposes anew: the list answers it at once.
        _readiness.Edited += () =>
        {
            if (_proposed && !main.IsBusy) _ = main.RunBusy(Suggest);
        };
        Show(-1);
    }

    public override bool HasApply => false;
    public override bool HasPendingChanges => _changed;

    private int Selected => _list.Selected == null ? -1 : _list.Items.IndexOf(_list.Selected);

    public override async Task RefreshAsync(MainViewModel main)
    {
        if (!ReferenceEquals(main.Session.Bytes, _document))
        {
            // Another document, or this one changed by something else (an undo, another tool): what it has now counts.
            var document = _document = main.Session.Bytes;
            (_changed, _proposed, _unread) = (false, false, 0);
            var read = document == null ? [] : await Task.Run(() => PdfBookmarks.Read(document));
            if (!ReferenceEquals(document, _document)) return;
            _entries = read.Select(bookmark => new Entry(bookmark)).ToList();
            Show(-1);
        }

        ShowButtons();
    }

    /// <summary>Writes the bookmarks into the document, as one step that Undo takes back.</summary>
    public override async Task FlushAsync(MainViewModel main)
    {
        if (!_changed) return;
        _changed = false;
        if (main.Session.Bytes is not { } pdf || !ReferenceEquals(pdf, _document)) return;
        var bookmarks = _entries.Select(entry => entry.Bookmark).ToList();
        var written = await Task.Run(() => PdfBookmarks.Write(pdf, bookmarks));
        // Known as this tool's own state before the document says it has changed, so that it is not read back.
        _document = written;
        main.Commit(written);
        main.NotifyUndoState();
    }

    // ---- showing ------------------------------------------------------------------------------------------------

    /// <summary>Lists the bookmarks, with the one at <paramref name="select"/> in hand.</summary>
    private void Show(int select)
    {
        var levels = PdfBookmarks.Levels(_entries.Select(entry => entry.Bookmark).ToList());
        for (var i = 0; i < _entries.Count; i++) _entries[i].Bookmark = _entries[i].Bookmark with { Level = levels[i] };
        _list.Fill(_entries.Select(entry => new ListItem(entry)
        {
            Title = entry.Bookmark.Title,
            Detail = entry.Bookmark.Page < 0 ? Loc.Get("BookmarkNoPage") : Loc.Format("PagesChosenOne", entry.Bookmark.Page + 1),
            Level = entry.Bookmark.Level,
            // Red: a proposal that is a close call, or an entry whose page is gone.
            Warns = entry.Doubtful || entry.Bookmark.Page < 0
        }));
        _list.IsVisible = _entries.Count > 0;
        if (select >= 0 && select < _list.Items.Count) _list.Show(_list.Items[select]);

        var doubtful = _entries.Count(entry => entry.Doubtful);
        var lines = new List<string>
        {
            _entries.Count == 0 ? Loc.Get("BookmarksNone")
            : doubtful > 0 ? Loc.Format("BookmarksDoubt", _entries.Count, doubtful)
            : Loc.Format("BookmarksCount", _entries.Count)
        };
        if (_unread > 0) lines.Add(Loc.Format("BookmarkUnread", _unread));
        _note.SetText(string.Join("\n", lines));
        _note.Warns = doubtful > 0 || _unread > 0;
        ShowTitle();
        ShowButtons();
    }

    private void ShowTitle()
    {
        _syncing = true;
        _title.Value = Selected >= 0 ? _entries[Selected].Bookmark.Title : "";
        _syncing = false;
    }

    private void ShowButtons()
    {
        var index = Selected;
        var has = index >= 0;
        _rename.IsVisible = _delete.IsVisible = _up.IsVisible = _down.IsVisible = _out.IsVisible = _in.IsVisible = has;
        _confirm.IsVisible = has && _entries[index].Doubtful;
        _point.IsVisible = has && _main.HasDocument && _entries[index].Bookmark.Page != _main.CurrentPageIndex;
        _clear.IsVisible = _entries.Count > 0;
    }

    /// <summary>A bookmark clicked in the list: its page comes on view, its name into the box.</summary>
    private void Chosen()
    {
        var index = Selected;
        if (index < 0) return;
        ShowTitle();
        var page = _entries[index].Bookmark.Page;
        if (page >= 0 && page != _main.CurrentPageIndex) _main.GoToPage(page);
        ShowButtons();
    }

    // ---- changing -----------------------------------------------------------------------------------------------

    /// <summary>Where the bookmarks under the one at <paramref name="index"/> end: the next one that is not deeper.</summary>
    private int EndOf(int index)
    {
        var end = index + 1;
        while (end < _entries.Count && _entries[end].Bookmark.Level > _entries[index].Bookmark.Level) end++;
        return end;
    }

    private void Changed(int select)
    {
        _changed = true;
        _proposed = false;
        Show(select);
        _main.NotifyUndoState();
    }

    private Task Change(Action<Entry> change)
    {
        var index = Selected;
        if (index < 0) return Task.CompletedTask;
        change(_entries[index]);
        Changed(index);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A bookmark for the page on view: under the one in hand, or where its page puts it among the others. It is
    /// named by what stands in the box, or else after the heading of the page, and can be renamed at once.
    /// </summary>
    private Task AddHere()
    {
        if (_main.Session.View is not { } view) return Task.CompletedTask;
        var page = _main.CurrentPageIndex;
        var index = Selected;
        var typed = _title.Value.Trim();
        var title = typed.Length > 0 && (index < 0 || typed != _entries[index].Bookmark.Title) ? typed
            : PdfBookmarks.TitleFor(view, page) ?? Loc.Format("PagesChosenOne", page + 1);
        int at, level;
        if (index >= 0) (at, level) = (EndOf(index), _entries[index].Bookmark.Level);
        else (at, level) = (_entries.FindIndex(entry => entry.Bookmark.Page > page) is >= 0 and var before ? before : _entries.Count, 0);
        _entries.Insert(at, new Entry(new Bookmark(title, page, level)));
        Changed(at);
        _title.Focus();
        return Task.CompletedTask;
    }

    private Task Rename()
    {
        var title = _title.Value.Trim();
        if (_syncing || title.Length == 0) return Task.CompletedTask;
        return Change(entry =>
        {
            entry.Bookmark = entry.Bookmark with { Title = title };
            entry.Doubtful = false;
        });
    }

    /// <summary>Moves the bookmark in hand, with all that stands under it, past its neighbour of the same level.</summary>
    private Task Move(int direction)
    {
        var index = Selected;
        if (index < 0) return Task.CompletedTask;
        var level = _entries[index].Bookmark.Level;
        var end = EndOf(index);
        var moved = _entries.GetRange(index, end - index);
        int to;
        if (direction < 0)
        {
            // The neighbour before it: the nearest earlier entry of the same level, unless one less deep comes first.
            var before = index - 1;
            while (before >= 0 && _entries[before].Bookmark.Level > level) before--;
            if (before < 0 || _entries[before].Bookmark.Level < level) return Task.CompletedTask;
            to = before;
        }
        else
        {
            if (end >= _entries.Count || _entries[end].Bookmark.Level < level) return Task.CompletedTask;
            to = EndOf(end) - moved.Count;
        }

        _entries.RemoveRange(index, moved.Count);
        _entries.InsertRange(to, moved);
        Changed(to);
        return Task.CompletedTask;
    }

    /// <summary>Moves the bookmark in hand, with all under it, one level in under the entry before it, or one out.</summary>
    private Task Shift(int by)
    {
        var index = Selected;
        if (index < 0) return Task.CompletedTask;
        var level = _entries[index].Bookmark.Level;
        if (by > 0 ? index == 0 || level > _entries[index - 1].Bookmark.Level : level == 0) return Task.CompletedTask;
        for (int i = index, end = EndOf(index); i < end; i++)
            _entries[i].Bookmark = _entries[i].Bookmark with { Level = _entries[i].Bookmark.Level + by };
        Changed(index);
        return Task.CompletedTask;
    }

    /// <summary>Takes the bookmark in hand away; what stood under it moves out one level.</summary>
    private Task Delete()
    {
        var index = Selected;
        if (index < 0) return Task.CompletedTask;
        for (int i = index + 1, end = EndOf(index); i < end; i++)
            _entries[i].Bookmark = _entries[i].Bookmark with { Level = _entries[i].Bookmark.Level - 1 };
        _entries.RemoveAt(index);
        Changed(Math.Min(index, _entries.Count - 1));
        return Task.CompletedTask;
    }

    private Task Clear()
    {
        _entries.Clear();
        _unread = 0;
        Changed(-1);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Proposes bookmarks from the headings and puts them in the place of the ones there are. Nothing is written
    /// yet: they can be gone through, the red ones first, and Undo brings back what the document had.
    /// </summary>
    private async Task Suggest(ToolContext context)
    {
        var view = context.Session.View!;
        var readiness = _readiness.Number / 100;
        var (found, unread) = await context.Run(() =>
        {
            var proposals = PdfBookmarks.Suggest(view, readiness, out var pictures, context.Progress, context.Cancel);
            return (proposals, pictures);
        });
        _unread = unread;
        if (found.Count == 0)
        {
            // What is there stays; the note says why nothing came of it.
            Show(Selected);
            _note.SetText(Loc.Get("BookmarkSuggestNone") + (unread > 0 ? "\n" + Loc.Format("BookmarkUnread", unread) : ""));
            _note.Warns = true;
            context.Status(Loc.Get("BookmarkSuggestNone"));
            return;
        }

        _entries = found.Select(proposal => new Entry(proposal.Bookmark, proposal.Doubtful)).ToList();
        Changed(-1);
        _proposed = true;
        context.Status(Loc.Format("BookmarkSuggested", found.Count));
    }
}

/// <summary>
/// Looks whether the document keeps to PDF/A, the standard for files that have to stay readable for decades, and
/// lists what stands in the way: by name, with the pages it is on, one click from the page. The look is taken when
/// the tool is opened and again whenever the document changes. What it cannot tell for certain is marked red, and
/// the tool says plainly what it looks at and that this is not every rule there is.
/// </summary>
public sealed class ArchiveTool : Tool
{
    private readonly MainViewModel _main;
    private readonly ModeField _against;
    private readonly InfoField _verdict;
    private readonly ListField _findings;

    // What was found, for one state of the document and one part of the standard.
    private (byte[] Document, string Against)? _looked;
    private ArchiveReport? _report;
    private int _version;

    public ArchiveTool(MainViewModel main) : base("Archive", "GroupReview")
    {
        _main = main;
        _against = Add(new ModeField("ArchiveAgainst",
            new Choice("declared", "ArchiveDeclared"), new Choice("1", "PDF/A-1", true), new Choice("2", "PDF/A-2", true), new Choice("3", "PDF/A-3", true))
        {
            Columns = 2
        });
        _verdict = Add(new InfoField());
        _findings = Add(new ListField("ArchiveFindings") { Chosen = Go, MaxHeight = 320 });
        Add(new InfoField()).SetKey("ArchiveScope");
        _against.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChoiceField.Selected)) _ = Look(main);
        };
        _findings.IsVisible = false;
    }

    public override bool HasApply => false;

    public override Task RefreshAsync(MainViewModel main) => Look(main);

    public override void Deactivated(MainViewModel main) => main.SetPageBadges(null);

    private async Task Look(MainViewModel main)
    {
        if (!ReferenceEquals(main.SelectedTool, this)) return;
        var against = _against.Key;
        if (main.Session.Bytes is not { } document)
        {
            (_looked, _report) = (null, null);
            _verdict.SetText("");
            _findings.Fill([]);
            _findings.IsVisible = false;
            return;
        }

        if (_looked is { } done && ReferenceEquals(done.Document, document) && done.Against == against)
        {
            // Turning a page asks again; what was found still holds, and the marks in the page list go back on.
            Mark();
            return;
        }

        var version = ++_version;
        _verdict.Warns = false;
        _verdict.SetText(Loc.Get("ArchiveLooking"));
        _findings.Fill([]);
        _findings.IsVisible = false;
        var level = against switch { "1" => ArchiveLevel.A1, "2" => ArchiveLevel.A2, "3" => ArchiveLevel.A3, _ => ArchiveLevel.Declared };
        ArchiveReport report;
        try
        {
            report = await Task.Run(() => PdfArchive.Check(document, level));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (version != _version) return;
            _verdict.Warns = true;
            _verdict.SetText(Loc.Format("ArchiveUnreadable", e.Message));
            return;
        }

        if (version != _version || !ReferenceEquals(main.Session.Bytes, document)) return;
        (_looked, _report) = ((document, against), report);
        Show(report);
        Mark();
    }

    private void Show(ArchiveReport report)
    {
        var target = "PDF/A-" + report.AgainstPart;
        var says = report.Says is { } mark ? Loc.Format("ArchiveSays", mark) : Loc.Get("ArchiveSaysNothing");
        _verdict.Warns = report.Findings.Count > 0;
        _verdict.SetText(says + "\n" + (report.Findings.Count == 0 ? Loc.Format("ArchiveClean", target) : Loc.Format("ArchiveProblems", report.Findings.Count, target)));
        _findings.Fill(report.Findings.Select(finding => new ListItem(finding)
        {
            Title = finding.Certain ? Describe(finding) : Loc.Format("ArchiveUncertain", Describe(finding)),
            Detail = finding.Pages.Count switch
            {
                0 => Loc.Get("ArchiveWholeFile"),
                1 => Loc.Format("PagesChosenOne", finding.Pages[0] + 1),
                _ => Loc.Format("ArchivePages", Core.PageRange.Format(finding.Pages))
            },
            // Red where the app is not sure, as everywhere.
            Warns = !finding.Certain
        }));
        _findings.IsVisible = report.Findings.Count > 0;
        _main.StatusText = report.Findings.Count == 0 ? Loc.Format("ArchiveClean", target) : Loc.Format("ArchiveProblems", report.Findings.Count, target);
    }

    private static string Describe(ArchiveFinding finding) => Loc.Format(finding.Rule switch
    {
        ArchiveRule.Declaration => "ArchiveRuleDeclaration",
        ArchiveRule.Encryption => "ArchiveRuleEncryption",
        ArchiveRule.Font => "ArchiveRuleFont",
        ArchiveRule.OutputIntent => "ArchiveRuleOutputIntent",
        ArchiveRule.Transparency => "ArchiveRuleTransparency",
        ArchiveRule.Script => "ArchiveRuleScript",
        ArchiveRule.Attachment => "ArchiveRuleAttachment",
        ArchiveRule.Compression => "ArchiveRuleCompression",
        ArchiveRule.Form => "ArchiveRuleForm",
        ArchiveRule.Annotation => "ArchiveRuleAnnotation",
        ArchiveRule.Structure => "ArchiveRuleStructure",
        _ => "ArchiveRuleIdentifier"
    }, finding.Detail);

    /// <summary>Marks in the page list the pages something was found on.</summary>
    private void Mark()
    {
        if (!ReferenceEquals(_main.SelectedTool, this) || _report is not { } report) return;
        _main.SetPageBadges(report.Findings.SelectMany(finding => finding.Pages.Select(page => (page, finding))).GroupBy(pair => pair.page)
            .ToDictionary(group => group.Key, group => new PageBadge("!", true, string.Join("\n", group.Select(pair => Describe(pair.finding)).Distinct()))));
    }

    /// <summary>A finding clicked in the list: the first page it is on comes on view.</summary>
    private void Go(ListItem item)
    {
        if (item.Source is ArchiveFinding { Pages.Count: > 0 } finding) _main.GoToPage(finding.Pages[0]);
    }
}
