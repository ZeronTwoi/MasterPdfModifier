using Avalonia.Headless.XUnit;
using PdfModifier.Core;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using PdfModifier.Localization;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>The bookmarks of a document: written, read back, and proposed from its headings.</summary>
public class BookmarkTests
{
    /// <summary>A page of running text with headings set larger, as a report or a manual has them.</summary>
    private static byte[] Chapter(string heading, string? section = null, double headingSize = 22)
    {
        return TestPdf.Compose((page, fonts) =>
        {
            PageDraw.DrawText(page, fonts, heading, new TextStyle(headingSize), 72, 90);
            for (var i = 0; i < 12; i++) PageDraw.DrawText(page, fonts, "Running text that says what the chapter is about, line after line.", new TextStyle(11), 72, 130 + i * 16);
            if (section == null) return;
            PageDraw.DrawText(page, fonts, section, new TextStyle(15), 72, 360);
            for (var i = 0; i < 12; i++) PageDraw.DrawText(page, fonts, "More running text, this time under a smaller heading of its own.", new TextStyle(11), 72, 390 + i * 16);
        });
    }

    [Fact]
    public void BookmarksComeBackAsTheyWereWritten()
    {
        var pdf = TestPdf.Numbered(6);
        Assert.Empty(PdfBookmarks.Read(pdf));
        Bookmark[] written =
        [
            new("Introduction", 0),
            new("Chapter one", 1),
            new("A section of it", 2, 1, 300),
            new("And a part of that", 2, 2, 520.5),
            new("Chapter two: İstanbul’da çay, 日本語", 4, 0, null, false),
            new("Closed away under it", 5, 1)
        ];

        var read = PdfBookmarks.Read(PdfBookmarks.Write(pdf, written));

        Assert.Equal(written.Select(b => (b.Title, b.Page, b.Level, b.Open)), read.Select(b => (b.Title, b.Page, b.Level, b.Open)));
        Assert.Null(read[0].Top);
        Assert.Equal(300, read[2].Top!.Value, 0.5);
        Assert.Equal(520.5, read[3].Top!.Value, 0.5);
        // The pages and what is on them are untouched.
        Assert.Equal(6, TestPdf.PageCount(PdfBookmarks.Write(pdf, written)));
        Assert.Equal("Page 3", TestPdf.Text(PdfBookmarks.Write(pdf, written), 2));
        // What a reader goes by for the first level, the app itself included.
        Assert.Equal([0, 1, 4], PdfPages.GetBookmarkTargets(PdfBookmarks.Write(pdf, written)));
    }

    [Fact]
    public void WritingReplacesWhatWasThereAndNoneTakesThemAllAway()
    {
        var pdf = PdfBookmarks.Write(TestPdf.Numbered(3), [new Bookmark("Old one", 0), new Bookmark("Old two", 1)]);

        var replaced = PdfBookmarks.Write(pdf, [new Bookmark("New", 2)]);
        var none = PdfBookmarks.Write(replaced, []);

        Assert.Equal(["New"], PdfBookmarks.Read(replaced).Select(b => b.Title));
        Assert.Empty(PdfBookmarks.Read(none));
        Assert.Equal(3, TestPdf.PageCount(none));
    }

    [Fact]
    public void AnEntryNeverStandsDeeperThanDirectlyUnderItsPredecessor()
    {
        Bookmark[] loose = [new("Starts too deep", 0, 2), new("Jumps two steps", 1, 3), new("Back out", 2, 0), new("Under it", 2, 1)];

        Assert.Equal([0, 1, 0, 1], PdfBookmarks.Levels(loose));
        Assert.Equal([0, 1, 0, 1], PdfBookmarks.Read(PdfBookmarks.Write(TestPdf.Numbered(3), loose)).Select(b => b.Level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void HowFarDownThePageAnEntryLeadsHoldsOnATurnedPageToo(int turn)
    {
        var pdf = PdfPages.Rotate(TestPdf.Numbered(2), null, turn);

        var read = PdfBookmarks.Read(PdfBookmarks.Write(pdf, [new Bookmark("Part way down", 1, 0, 250)]));

        Assert.Equal(250, Assert.Single(read).Top!.Value, 0.5);
    }

    [Fact]
    public void AnEntryForAPageThatIsGoneLeadsNowhereAndIsKept()
    {
        var pdf = PdfBookmarks.Write(TestPdf.Numbered(3), [new Bookmark("First", 0), new Bookmark("Last", 2), new Bookmark("Beyond the end", 9)]);

        var after = PdfBookmarks.Read(PdfPages.Delete(pdf, [2]));

        Assert.Equal([("First", 0), ("Last", -1), ("Beyond the end", -1)], after.Select(b => (b.Title, b.Page)));
    }

    [Fact]
    public void HeadingsAreProposedAsBookmarksByTheSizeOfTheirPrint()
    {
        var pdf = PdfPages.Merge([Chapter("Getting started", "Unpacking the box"), Chapter("Daily use"), Chapter("When something goes wrong", "Getting help")]);
        using var view = new PdfView(pdf);

        var found = PdfBookmarks.Suggest(view, PdfBookmarks.UsualReadiness, out var unread);

        Assert.Equal(0, unread);
        Assert.Equal(
            [("Getting started", 0, 0), ("Unpacking the box", 0, 1), ("Daily use", 1, 0), ("When something goes wrong", 2, 0), ("Getting help", 2, 1)],
            found.Select(s => (s.Bookmark.Title, s.Bookmark.Page, s.Bookmark.Level)));
        // Each leads to its heading, not to the top of its page: a little above the line.
        Assert.InRange(found[0].Bookmark.Top!.Value, 45, 80);
        Assert.InRange(found[1].Bookmark.Top!.Value, 325, 352);
        Assert.All(found, suggestion => Assert.False(suggestion.Doubtful));

        // Asked to be choosy, only the large print counts.
        Assert.Equal(["Getting started", "Daily use", "When something goes wrong"],
            PdfBookmarks.Suggest(view, 0, out _).Select(s => s.Bookmark.Title));
        // What it proposes can be written as it is.
        Assert.Equal(5, PdfBookmarks.Read(PdfBookmarks.Write(pdf, found.Select(s => s.Bookmark).ToList())).Count);
    }

    [Fact]
    public void AHeadingOnlyALittleLargerIsACloseCallAndSaidToBeOne()
    {
        // 11 point text with a heading of 12: larger, but not by much.
        var pdf = Chapter("Hardly a heading", null, 12);
        using var view = new PdfView(pdf);

        Assert.Empty(PdfBookmarks.Suggest(view, PdfBookmarks.UsualReadiness, out _));
        var eager = Assert.Single(PdfBookmarks.Suggest(view, 1, out _));
        Assert.Equal("Hardly a heading", eager.Bookmark.Title);
        Assert.True(eager.Doubtful);
    }

    [Fact]
    public void WhatStandsOnEveryPageIsAPageHeaderAndAScanHasNoPrintToMeasure()
    {
        byte[] Sheet(string heading) => TestPdf.Compose((page, fonts) =>
        {
            PageDraw.DrawText(page, fonts, "Annual report 2026", new TextStyle(20), 72, 50);
            PageDraw.DrawText(page, fonts, heading, new TextStyle(20), 72, 110);
            for (var i = 0; i < 10; i++) PageDraw.DrawText(page, fonts, "Running text of the report, line after line after line.", new TextStyle(11), 72, 150 + i * 16);
        });
        using var scan = TestPdf.FakeScan(620, 877);
        var pdf = PdfPages.Merge([Sheet("Sales"), Sheet("Staff"), Sheet("Outlook"), Sheet("Thanks"), TestPdf.FromImage(scan, 75)]);
        using var view = new PdfView(pdf);

        var found = PdfBookmarks.Suggest(view, PdfBookmarks.UsualReadiness, out var unread);

        Assert.Equal(["Sales", "Staff", "Outlook", "Thanks"], found.Select(s => s.Bookmark.Title));
        Assert.Equal(1, unread);
        // A name for a bookmark nobody typed one for: the largest line near the top of its page, where there is one.
        Assert.Equal("Annual report 2026", PdfBookmarks.TitleFor(view, 0));
        Assert.Null(PdfBookmarks.TitleFor(view, 4));
    }

    [AvaloniaFact]
    public async Task BookmarksAreAddedNamedNestedMovedAndWrittenWithTheDocument()
    {
        var (window, vm) = Start();
        vm.Session.Open(PdfPages.Merge([Chapter("Getting started", "Unpacking the box"), Chapter("Daily use"), Chapter("When something goes wrong")]),
            Path.Combine(Path.GetTempPath(), "manual.pdf"));
        var tool = Tool<BookmarksTool>(vm);
        await Settle();
        var list = Field<ListField>(tool, "BookmarksList");
        var name = Field<TextField>(tool, "BookmarkTitle");
        var note = tool.Fields.OfType<InfoField>().First();
        Assert.False(list.HasItems);
        Assert.Equal(Strings.English["BookmarksNone"], note.Text);
        Assert.False(Field<ActionField>(tool, "BookmarkRename").IsVisible);
        Assert.False(vm.CanUndo);

        // A bookmark for the page on view is named after the heading of that page, and is in hand at once.
        Press(tool, "BookmarkAdd");
        Assert.Equal("Getting started", Assert.Single(list.Items).Title);
        Assert.Equal(string.Format(Strings.English["PagesChosenOne"], 1), list.Items[0].Detail);
        Assert.Same(list.Items[0], list.Selected);
        Assert.Equal("Getting started", name.Value);
        Assert.True(vm.CanUndo);
        Assert.True(Field<ActionField>(tool, "BookmarkRename").IsVisible);
        // It leads to this very page, so there is nowhere else to lead it.
        Assert.False(Field<ActionField>(tool, "BookmarkPoint").IsVisible);

        vm.GoToPage(1);
        await Settle(() => vm.CurrentPageIndex == 1);
        Assert.True(Field<ActionField>(tool, "BookmarkPoint").IsVisible);
        Press(tool, "BookmarkAdd");
        vm.GoToPage(2);
        await Settle(() => vm.CurrentPageIndex == 2);
        name.Value = "Trouble";
        Press(tool, "BookmarkAdd");
        Assert.Equal(["Getting started", "Daily use", "Trouble"], list.Items.Select(item => item.Title));

        // In under the one before it, and that one moved up with what stands under it.
        Press(tool, "BookmarkIn");
        Assert.Equal([0, 0, 1], list.Items.Select(item => item.Level));
        Press(tool, "BookmarkIn");
        Assert.Equal([0, 0, 1], list.Items.Select(item => item.Level));
        Press(tool, "BookmarkUp");
        Assert.Equal(["Getting started", "Daily use", "Trouble"], list.Items.Select(item => item.Title));
        list.Selected = list.Items[1];
        await Settle(() => vm.CurrentPageIndex == 1);
        Assert.Equal(1, vm.CurrentPageIndex);
        Assert.Equal("Daily use", name.Value);
        Press(tool, "BookmarkUp");
        Assert.Equal([("Daily use", 0), ("Trouble", 1), ("Getting started", 0)], list.Items.Select(item => (item.Title, item.Level)));
        Assert.Same(list.Items[0], list.Selected);

        // Renamed by what is typed into the box.
        name.Value = "  Everyday use ";
        Press(tool, "BookmarkRename");
        Assert.Equal("Everyday use", list.Items[0].Title);
        await Settle();
        Capture(window, "67-bookmarks");

        // Written into the document when something else is about to work with it, as one step that Undo takes back.
        Assert.Empty(PdfBookmarks.Read(vm.Session.Bytes!));
        await vm.FlushTool();
        await Settle();
        Assert.Equal([("Everyday use", 1, 0), ("Trouble", 2, 1), ("Getting started", 0, 0)],
            PdfBookmarks.Read(vm.Session.Bytes!).Select(b => (b.Title, b.Page, b.Level)));
        Assert.True(vm.Session.IsDirty);
        Assert.Equal(3, list.Items.Count);
        await vm.UndoCommand.ExecuteAsync(null);
        await Settle(() => !list.HasItems);
        Assert.Empty(PdfBookmarks.Read(vm.Session.Bytes!));
        Assert.False(list.HasItems);

        // Proposed from the headings: all four, the smaller one a level in. Asked to be choosy, only the large print.
        Press(tool, "BookmarkSuggest");
        await Settle(() => !vm.IsBusy && list.Items.Count == 4);
        Assert.Equal([("Getting started", 0), ("Unpacking the box", 1), ("Daily use", 0), ("When something goes wrong", 0)],
            list.Items.Select(item => (item.Title, item.Level)));
        Assert.All(list.Items, item => Assert.False(item.Warns));
        Assert.Equal(string.Format(Strings.English["BookmarkSuggested"], 4), vm.StatusText);
        Field<SliderField>(tool, "BookmarkReadiness").Set(0);
        await Settle(() => !vm.IsBusy && list.Items.Count == 3);
        Assert.DoesNotContain("Unpacking the box", list.Items.Select(item => item.Title));

        // One deleted, then all of them.
        list.Selected = list.Items[1];
        Press(tool, "BookmarkDelete");
        Assert.Equal(["Getting started", "When something goes wrong"], list.Items.Select(item => item.Title));
        Press(tool, "BookmarkClear");
        Assert.False(list.HasItems);
        await vm.FlushTool();
        Assert.Empty(PdfBookmarks.Read(vm.Session.Bytes!));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ProposalsThatAreCloseCallsAreMarkedRedUntilTheyAreConfirmed()
    {
        var (window, vm) = Start();
        vm.Session.Open(Chapter("Hardly a heading", null, 12), null);
        var tool = Tool<BookmarksTool>(vm);
        await Settle();
        var list = Field<ListField>(tool, "BookmarksList");
        var note = tool.Fields.OfType<InfoField>().First();

        // As the tool starts out the line is not taken for a heading, and the tool says what would change that.
        Press(tool, "BookmarkSuggest");
        await Settle(() => !vm.IsBusy && note.Text.StartsWith(Strings.English["BookmarkSuggestNone"]));
        Assert.False(list.HasItems);
        Assert.True(note.Warns);

        Field<SliderField>(tool, "BookmarkReadiness").Set(100);
        Press(tool, "BookmarkSuggest");
        await Settle(() => !vm.IsBusy && list.Items.Count == 1);
        Assert.True(list.Items[0].Warns);
        Assert.Equal(string.Format(Strings.English["BookmarksDoubt"], 1, 1), note.Text);
        Assert.True(note.Warns);

        list.Selected = list.Items[0];
        Assert.True(Field<ActionField>(tool, "BookmarkConfirm").IsVisible);
        Press(tool, "BookmarkConfirm");
        Assert.False(list.Items[0].Warns);
        Assert.False(Field<ActionField>(tool, "BookmarkConfirm").IsVisible);
        Assert.False(note.Warns);
        window.Close();
    }

    [Fact]
    public void AShortLineInBoldIsAHeadingTooButACloseCall()
    {
        // Smaller headings are often set at the size of the text, only bold.
        var pdf = TestPdf.Compose((page, fonts) =>
        {
            PageDraw.DrawText(page, fonts, "Terms of delivery", new TextStyle(11, Bold: true), 72, 90);
            for (var i = 0; i < 12; i++) PageDraw.DrawText(page, fonts, "Running text that says what is agreed, line after line after line.", new TextStyle(11), 72, 120 + i * 16);
        });
        using var view = new PdfView(pdf);
        var printed = view.GetPrintedLines(0);
        Assert.Equal(11, printed[1].Size);
        Assert.False(printed[1].Bold);
        // Whether the bold line is known as bold depends on a bold face being installed for the test to draw with.
        if (!printed[0].Bold) return;

        var found = Assert.Single(PdfBookmarks.Suggest(view, PdfBookmarks.UsualReadiness, out _));

        Assert.Equal("Terms of delivery", found.Bookmark.Title);
        Assert.True(found.Doubtful);
        Assert.Empty(PdfBookmarks.Suggest(view, 0.1, out _));
    }
}
