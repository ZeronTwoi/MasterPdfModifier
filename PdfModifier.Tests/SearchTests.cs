using Avalonia.Headless.XUnit;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Localization;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>
/// Finding a word or a phrase: in the text a page has, in what is read off a scan, across the end of a line, and
/// through the tool in the window. Tests that read scans need the recognition data and pass vacuously without it
/// (see <see cref="OcrTests"/>).
/// </summary>
public class SearchTests
{
    private static bool CanRead => PdfOcr.EngineStatus().Available && TessData.Missing(["en"]).Count == 0;

    /// <summary>A line of words as a reading of a scan gives them, each 40 points wide, one after the other.</summary>
    private static OcrLine Line(double top, double confidence, params string[] words)
    {
        var list = words.Select((word, i) => new OcrWord(word, new RectD(50 + i * 50, top, 90 + i * 50, top + 12), confidence)).ToList();
        return new OcrLine(string.Join(' ', words), new RectD(50, top, 40 + words.Length * 50, top + 12), list);
    }

    [Fact]
    public void ThePageGivesItsTextWordByWordWithWhereEachStands()
    {
        using var view = new PdfView(TestPdf.WithText("Invoice 2026-104 for Mr Smith"));

        var line = Assert.Single(view.GetLines(0));

        Assert.Equal(["Invoice", "2026-104", "for", "Mr", "Smith"], line.Words.Select(word => word.Text));
        Assert.Equal("Invoice 2026-104 for Mr Smith", line.Text);
        // The text was drawn with its baseline 100 points down and 72 in: that is where the words are, left to right.
        Assert.InRange(line.Bounds.Left, 70, 76);
        Assert.InRange(line.Bounds.Bottom, 98, 108);
        Assert.All(line.Words.Zip(line.Words.Skip(1)), pair => Assert.True(pair.First.Bounds.Right <= pair.Second.Bounds.Left + 0.5));
        Assert.All(line.Words, word => Assert.Equal(100, word.Confidence));
    }

    [Fact]
    public void APhraseIsFoundWhateverItsCapitalsUnlessTheyAreToCount()
    {
        using var view = new PdfView(TestPdf.WithText("Invoice 2026-104 for Mr Smith"));
        var lines = view.GetLines(0);

        var hit = Assert.Single(PdfText.Find(lines, 0, "  mr   SMITH "));

        Assert.Equal("Mr Smith", hit.Found);
        Assert.Equal("Invoice 2026-104 for ", hit.Before);
        Assert.Equal("", hit.After);
        Assert.False(hit.Read);
        Assert.False(hit.Doubtful);
        // One area, from where "Mr" begins to where "Smith" ends.
        var area = Assert.Single(hit.Areas);
        Assert.Equal(lines[0].Words[3].Bounds.Left, area.Left, 0.01);
        Assert.Equal(lines[0].Words[4].Bounds.Right, area.Right, 0.01);

        Assert.Empty(PdfText.Find(lines, 0, "mr smith", new FindOptions(MatchCase: true)));
        Assert.Single(PdfText.Find(lines, 0, "Mr Smith", new FindOptions(MatchCase: true)));
        Assert.Empty(PdfText.Find(lines, 0, "Mrs Smith"));
        Assert.Empty(PdfText.Find(lines, 0, "   "));
    }

    [Fact]
    public void PartOfAWordIsFoundAndMarkedAsThatPartUnlessWholeWordsAreAskedFor()
    {
        using var view = new PdfView(TestPdf.WithText("Invoice 2026-104 for Mr Smith"));
        var lines = view.GetLines(0);
        var word = lines[0].Words[0].Bounds;

        var area = Assert.Single(Assert.Single(PdfText.Find(lines, 0, "voi")).Areas);

        Assert.True(area.Left > word.Left + 1 && area.Right < word.Right - 1, "only the letters meant are marked");
        Assert.Equal(word.Width * 3 / 7, area.Width, 0.01);
        Assert.Empty(PdfText.Find(lines, 0, "voi", new FindOptions(WholeWords: true)));
        Assert.Single(PdfText.Find(lines, 0, "invoice", new FindOptions(WholeWords: true)));
        // A number with its hyphen is found as it is written.
        Assert.Equal("2026-104", Assert.Single(PdfText.Find(lines, 0, "2026-104")).Found);
    }

    [Fact]
    public void APhraseMayRunOverTheEndOfALineAlsoWhereAWordWasDividedThere()
    {
        OcrLine[] lines = [Line(100, 100, "This", "is", "an", "exam-"), Line(120, 100, "ple", "of", "a", "well-"), Line(140, 100, "Known", "case")];

        var hit = Assert.Single(PdfText.Find(lines, 4, "an example of"));

        Assert.Equal(4, hit.Page);
        Assert.Equal("an example of", hit.Found);
        // One area on each of the two lines it runs over.
        Assert.Equal(2, hit.Areas.Count);
        Assert.Equal(100, hit.Areas[0].Top);
        Assert.Equal(120, hit.Areas[1].Top);
        // A hyphen in front of a capital is a hyphen: the two words are not run together.
        Assert.Empty(PdfText.Find(lines, 4, "wellKnown"));
        Assert.Single(PdfText.Find(lines, 4, "well- known"));
        // Each place is counted once, and places that follow each other are told apart.
        Assert.Equal(2, PdfText.Find([Line(100, 100, "tick", "tick", "tock")], 0, "tick").Count);
        Assert.Single(PdfText.Find([Line(100, 100, "aaaa")], 0, "aaa"));
    }

    [Fact]
    public void LettersAreFoundHoweverTheyAreWritten()
    {
        // The dotted and the dotless i of Turkish, a sign that stands for two letters, typographic quotes.
        OcrLine[] lines = [Line(100, 100, "İSTANBUL", "ısı", "ﬁnal", "don’t")];

        Assert.Single(PdfText.Find(lines, 0, "istanbul"));
        Assert.Single(PdfText.Find(lines, 0, "ISI"));
        Assert.Equal("final", Assert.Single(PdfText.Find(lines, 0, "final")).Found);
        Assert.Single(PdfText.Find(lines, 0, "don't"));
        Assert.Empty(PdfText.Find(lines, 0, "istanbul", new FindOptions(MatchCase: true)));
    }

    [Fact]
    public void WhatWasReadOffAScanWithLittleCertaintyIsSaidToBeSo()
    {
        OcrLine[] lines = [Line(100, 96, "Payment", "is", "due"), Line(120, 41, "within", "thirty", "days")];

        var sure = Assert.Single(PdfText.Find(lines, 0, "payment"));
        var guessed = Assert.Single(PdfText.Find(lines, 0, "thirty days"));
        var mixed = Assert.Single(PdfText.Find(lines, 0, "due within"));

        Assert.True(sure.Read);
        Assert.False(sure.Doubtful);
        Assert.True(guessed.Doubtful);
        Assert.True(mixed.Doubtful);
    }

    [Fact]
    public void TextIsTakenOutOfAPageOrOfAPartOfItLineByLine()
    {
        OcrLine[] lines = [Line(100, 100, "Dear", "Sir"), Line(114, 100, "thank", "you"), Line(160, 100, "Yours", "truly")];

        // Lines that follow closely are one paragraph; a wider gap is kept as an empty line.
        Assert.Equal("Dear Sir\nthank you\n\nYours truly", PdfText.In(lines));
        // Of an area, the words whose middle lies in it.
        Assert.Equal("Sir\nyou", PdfText.In(lines, new RectD(95, 90, 200, 130)));
        Assert.Equal("", PdfText.In(lines, new RectD(300, 300, 400, 400)));
    }

    [Fact]
    public void AScanGivesItsTextOnlyWhenItIsRead()
    {
        using var scan = TestPdf.FakeScan(1240, 1754);
        var pdf = TestPdf.FromImage(scan, 150);
        using var view = new PdfView(pdf);

        Assert.True(PdfText.IsPicture(view, 0));
        Assert.Empty(PdfText.Of(view, 0).Lines);
        using var typed = new PdfView(TestPdf.WithText("Typed text, nothing to read"));
        Assert.False(PdfText.IsPicture(typed, 0));
        Assert.False(PdfText.Of(typed, 0, OcrSettings.DefaultFor("en")).Read);
        // A page with hardly a letter on it is tried when reading can be done, but it is no scan: nothing is said
        // to have been passed over when it was not read.
        using var brief = new PdfView(TestPdf.WithText("Page 2"));
        Assert.Equal((true, false), PdfText.Kind(brief, 0));
        Assert.Equal((true, true), PdfText.Kind(view, 0));

        if (!CanRead) return;
        var words = PdfText.Of(view, 0, OcrSettings.DefaultFor("en"));
        Assert.True(words.Read);
        var hits = PdfText.Find(words.Lines, 0, "lazy dog");
        Assert.True(hits.Count >= 15, $"found {hits.Count} times");
        Assert.All(hits, hit => Assert.True(hit.Read));
        // The first line of the scan has its baseline 220 pixels down at 150 dpi: about 105 points.
        Assert.InRange(hits[0].Areas[0].Bottom, 96, 114);
    }

    [AvaloniaFact]
    public async Task ThePlacesFoundAreListedCountedOnThePagesAndMarkedOnThePage()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.WithText("Invoice 2026-104 for Mr Smith", "Nothing of the kind here", "Mr Smith again, and Mr Smith once more"), null);
        var tool = Tool<SearchTool>(vm);
        await Settle();
        var places = Field<ListField>(tool, "SearchPlaces");
        Assert.False(places.HasItems);
        Assert.False(Field<ActionField>(tool, "SearchNext").IsVisible);

        Field<TextField>(tool, "SearchPhrase").Value = "mr smith";
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && places.Items.Count == 3 && vm.Overlays.Count == 1);

        // Three places on two pages: listed with the words around them, counted in the page list, the first in hand.
        Assert.Equal(3, places.Items.Count);
        Assert.Equal("Invoice 2026-104 for Mr Smith", places.Items[0].Title);
        Assert.Equal(string.Format(Strings.English["PagesChosenOne"], 3), places.Items[2].Detail);
        Assert.Same(places.Items[0], places.Selected);
        Assert.Equal("1", vm.Pages[0].Badge?.Text);
        Assert.Null(vm.Pages[1].Badge);
        Assert.Equal("2", vm.Pages[2].Badge?.Text);
        Assert.Equal(0, vm.CurrentPageIndex);
        Assert.Same(Assert.Single(vm.Overlays), vm.SelectedOverlay);
        Assert.Equal("Mr Smith", vm.Overlays[0].Text);
        Assert.Equal(string.Format(Strings.English["SearchPlaceOf"], 1, 3, 1), vm.StatusText);
        var found = tool.Fields.OfType<InfoField>().Last();
        Assert.Equal(string.Format(Strings.English["SearchFound"], 3, 2), found.Text);
        Assert.False(found.Warns);

        // Next turns to the page of the next place, where both places of that page are marked and the first stands out.
        Press(tool, "SearchNext");
        await Settle(() => vm.CurrentPageIndex == 2 && vm.Overlays.Count == 2 && vm.SelectedOverlay != null);
        Assert.Equal(2, vm.CurrentPageIndex);
        Assert.Same(vm.Overlays[0], vm.SelectedOverlay);
        Assert.Same(places.Items[1], places.Selected);
        Capture(window, "61-search");

        // A row clicked in the list, a place clicked on the page, and Previous round the beginning all go where they say.
        places.Selected = places.Items[2];
        await Settle(() => ReferenceEquals(vm.SelectedOverlay, vm.Overlays.ElementAtOrDefault(1)));
        Assert.Same(vm.Overlays[1], vm.SelectedOverlay);
        await vm.OverlayClicked(vm.Overlays[0]);
        Assert.Same(places.Items[1], places.Selected);
        Press(tool, "SearchPrevious");
        Press(tool, "SearchPrevious");
        await Settle(() => vm.CurrentPageIndex == 2 && ReferenceEquals(places.Selected, places.Items[2]));
        Assert.Same(places.Items[2], places.Selected);

        // The document changes: what was found may be anywhere else by now, so it is not kept.
        vm.Commit(PdfPages.Delete(vm.Session.Bytes!, [0]));
        await Settle(() => !places.HasItems);
        Assert.False(places.HasItems);
        Assert.All(vm.Pages, page => Assert.Null(page.Badge));
        Assert.Empty(vm.Overlays);

        // Nothing found is said as plainly.
        Field<TextField>(tool, "SearchPhrase").Value = "no such words";
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && found.HasText);
        Assert.Equal(Strings.English["SearchNone"], found.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AScannedPageIsReadForTheSearchOrSaidNotToHaveBeenRead()
    {
        var (window, vm) = Start();
        using var scan = TestPdf.FakeScan(1240, 1754);
        vm.Session.Open(TestPdf.FromImage(scan, 150), null);
        var tool = Tool<SearchTool>(vm);
        await Settle();
        var places = Field<ListField>(tool, "SearchPlaces");
        var found = tool.Fields.OfType<InfoField>().Last();

        // With reading switched off the scan is passed over, and the tool says that it was.
        Field<BoolField>(tool, "SearchScans").Value = false;
        Field<TextField>(tool, "SearchPhrase").Value = "lazy dog";
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && found.HasText);
        Assert.False(places.HasItems);
        Assert.Contains(string.Format(Strings.English["SearchUnreadOff"], 1), found.Text);
        Assert.True(found.Warns);

        Field<BoolField>(tool, "SearchScans").Value = true;
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && (places.HasItems || found.Text.Contains(':')), 60);
        if (!CanRead)
        {
            // Nothing to read with here: the search still ends, and says what it left out and why.
            Assert.False(places.HasItems);
            Assert.StartsWith(Strings.English["SearchNone"], found.Text);
            Assert.True(found.Warns);
            window.Close();
            return;
        }

        Assert.True(places.Items.Count >= 15, $"found {places.Items.Count} times");
        Assert.Equal(string.Format(Strings.English["SearchPageRead"], 1), places.Items[0].Detail);
        Assert.Contains("lazy dog", places.Items[0].Title);
        Assert.Equal(places.Items.Count.ToString(), vm.Pages[0].Badge?.Text);
        Assert.Equal(places.Items.Count, vm.Overlays.Count);
        Assert.All(vm.Overlays, overlay => Assert.True(overlay.IsScan));
        Capture(window, "62-search-scan");
        window.Close();
    }

    [AvaloniaFact]
    public async Task AFolderIsSearchedAndAFileOfItOpensWithItsPlacesShown()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PdfModifier.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "older"));
        File.WriteAllBytes(Path.Combine(folder, "letter.pdf"), TestPdf.WithText("Dear Mr Smith", "No name here", "Yours, Mr Smith"));
        File.WriteAllBytes(Path.Combine(folder, "notes.pdf"), TestPdf.WithText("Nothing of the kind"));
        File.WriteAllBytes(Path.Combine(folder, "older", "contract.pdf"), TestPdf.WithText("Between Mr Smith and the company"));
        File.WriteAllBytes(Path.Combine(folder, "locked.pdf"), PdfSecurity.Protect(TestPdf.WithText("Mr Smith, in secret"), "secret"));
        File.WriteAllText(Path.Combine(folder, "broken.pdf"), "this is not a PDF");
        try
        {
            var (window, vm) = Start();
            var tool = Tool<SearchTool>(vm);
            await Settle();
            Field<ModeField>(tool, "SearchWhere").Select("folder");
            Field<FolderField>(tool, "SearchFolder").Path = folder;
            Field<TextField>(tool, "SearchPhrase").Value = "mr smith";
            var files = Field<ListField>(tool, "SearchFiles");
            var places = Field<ListField>(tool, "SearchPlaces");
            Assert.False(files.IsVisible);

            // No document is needed for this.
            await vm.ApplyCommand.ExecuteAsync(null);
            await Settle(() => !vm.IsBusy && files.Items.Count == 2);

            Assert.Equal(["letter.pdf", Path.Combine("older", "contract.pdf")], files.Items.Select(item => item.Title));
            Assert.StartsWith(string.Format(Strings.English["SearchFileDetail"], 2, "1, 3"), files.Items[0].Detail);
            Assert.Contains("Dear Mr Smith", files.Items[0].Detail);
            var report = tool.Fields.OfType<InfoField>().First();
            Assert.Contains(string.Format(Strings.English["SearchInFiles"], 2, 5, 3), report.Text);
            Assert.Contains(string.Format(Strings.English["SearchSkipped"], 2), report.Text);
            Capture(window, "63-search-folder");

            // A file clicked in the list opens, with the places in it listed and the first of them in hand.
            files.Selected = files.Items[0];
            await Settle(() => !vm.IsBusy && places.Items.Count == 2 && vm.Overlays.Count == 1);
            Assert.Equal(Path.Combine(folder, "letter.pdf"), vm.Session.FilePath);
            Assert.Equal(2, places.Items.Count);
            Assert.Single(vm.Overlays);
            // The list of files is still there, to go on to the next one.
            Assert.Equal(2, files.Items.Count);
            Assert.Contains(string.Format(Strings.English["SearchInFiles"], 2, 5, 3), report.Text);

            // Without the folders inside it, only what lies in the folder itself is looked at.
            Field<BoolField>(tool, "SearchSubfolders").Value = false;
            await vm.ApplyCommand.ExecuteAsync(null);
            await Settle(() => !vm.IsBusy && files.Items.Count == 1);
            Assert.Equal("letter.pdf", Assert.Single(files.Items).Title);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
