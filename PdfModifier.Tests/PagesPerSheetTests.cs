using Avalonia.Headless.XUnit;
using PdfModifier.Core;
using PdfModifier.Core.Operations;
using PdfModifier.Localization;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using SkiaSharp;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>Several pages on each sheet: how a sheet is laid out, and that the pages come out where they belong.</summary>
public class PagesPerSheetTests
{
    private const double Short = 595.28, Long = 841.89;

    /// <summary>How much ink the part of a page inside <paramref name="area"/> (shares of the page, 0 to 1) carries.</summary>
    private static double Ink(byte[] pdf, int page, double left, double top, double right, double bottom)
    {
        using var whole = TestPdf.Render(pdf, page);
        var area = new SKRectI((int)(left * whole.Width), (int)(top * whole.Height), (int)(right * whole.Width), (int)(bottom * whole.Height));
        using var part = new SKBitmap(area.Width, area.Height);
        whole.ExtractSubset(part, area);
        using var copy = part.Copy();
        return TestPdf.InkRatio(copy);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(6, 2, 3)]
    [InlineData(8, 2, 4)]
    [InlineData(9, 3, 3)]
    [InlineData(16, 4, 4)]
    public void PagesStandAsSquareAsTheirNumberAllows(int perSheet, int fewer, int more) =>
        Assert.Equal((fewer, more), PdfPagesPerSheet.Grid(perSheet));

    [Fact]
    public void TheSheetStandsTheWayThatLeavesThePagesLarger()
    {
        // Two upright pages lie side by side on a sheet on its side; four keep the sheet upright.
        var two = PdfPagesPerSheet.Plan(2, Short, Long, SheetTurn.Automatic, Short, Long);
        Assert.Equal((Long, Short, 2, 1), (two.Width, two.Height, two.Columns, two.Rows));
        var four = PdfPagesPerSheet.Plan(4, Short, Long, SheetTurn.Automatic, Short, Long);
        Assert.Equal((Short, Long, 2, 2), (four.Width, four.Height, four.Columns, four.Rows));
        // Pages that lie on their side are stacked on an upright sheet.
        var wide = PdfPagesPerSheet.Plan(2, Short, Long, SheetTurn.Automatic, Long, Short);
        Assert.Equal((Short, Long, 1, 2), (wide.Width, wide.Height, wide.Columns, wide.Rows));
        // Said outright, it stands as said, whatever that does to the pages.
        var forced = PdfPagesPerSheet.Plan(2, Short, Long, SheetTurn.Upright, Short, Long);
        Assert.Equal((Short, Long, 1, 2), (forced.Width, forced.Height, forced.Columns, forced.Rows));
        Assert.True(two.ScaleOf(Short, Long) > forced.ScaleOf(Short, Long));
        // Paper given the other way round is the same paper.
        Assert.Equal(two with { }, PdfPagesPerSheet.Plan(2, Long, Short, SheetTurn.Automatic, Short, Long));
    }

    [Fact]
    public void ThePagesAreLaidInTheOrderAskedFor()
    {
        var plan = new SheetPlan(600, 400, 3, 2);
        Assert.Equal([(0, 0), (1, 0), (2, 0), (0, 1), (1, 1), (2, 1)], Enumerable.Range(0, 6).Select(plan.PlaceOf));
        Assert.Equal([(2, 0), (1, 0), (0, 0), (2, 1), (1, 1), (0, 1)],
            Enumerable.Range(0, 6).Select((plan with { Order = SheetOrder.RowsRightToLeft }).PlaceOf));
        Assert.Equal([(0, 0), (0, 1), (1, 0), (1, 1), (2, 0), (2, 1)], Enumerable.Range(0, 6).Select((plan with { Order = SheetOrder.Columns }).PlaceOf));

        // The rooms lie inside the margin, a gap apart, and fill the sheet between them.
        var roomy = plan with { Margin = 30, Gap = 12 };
        Assert.Equal(new RectD(30, 30, 202, 194), roomy.RoomOf(0));
        Assert.Equal(new RectD(398, 206, 570, 370), roomy.RoomOf(5));
    }

    [Fact]
    public void FivePagesTwoToASheetComeToThreeSheetsThatStillHoldTheirText()
    {
        var pdf = TestPdf.Numbered(5);
        var plan = PdfPagesPerSheet.Plan(2, Short, Long, SheetTurn.Automatic, TestPdf.A4Width, TestPdf.A4Height);

        var sheets = PdfPagesPerSheet.Arrange(pdf, null, plan);

        var infos = PdfPages.GetPageInfos(sheets);
        Assert.Equal(3, infos.Count);
        Assert.All(infos, info =>
        {
            Assert.Equal(Long, info.Width, 0.1);
            Assert.Equal(Short, info.Height, 0.1);
        });
        // The text is still text, in the order of the pages.
        Assert.Matches(@"Page 1\s+Page 2", TestPdf.Text(sheets, 0));
        Assert.Matches(@"Page 3\s+Page 4", TestPdf.Text(sheets, 1));
        Assert.Equal("Page 5", TestPdf.Text(sheets, 2));
        // Page 5 stands on the left half of the last sheet; its right half is empty.
        Assert.True(Ink(sheets, 2, 0, 0, 0.5, 1) > 0.0005);
        Assert.Equal(0, Ink(sheets, 2, 0.5, 0, 1, 1));
    }

    [Fact]
    public void EachPageStandsInItsOwnRoomWhicheverWayTheyAreLaid()
    {
        // Four pages of which only the second has anything on it: where the ink is tells where it went.
        var pdf = TestPdf.WithText("", "Second", "", "");
        var plan = new SheetPlan(Short, Long, 2, 2) { Margin = 0, Gap = 0 };

        var rows = PdfPagesPerSheet.Arrange(pdf, null, plan);
        Assert.True(Ink(rows, 0, 0.5, 0, 1, 0.5) > 0.0005);
        Assert.Equal(0, Ink(rows, 0, 0, 0, 0.5, 1));
        Assert.Equal(0, Ink(rows, 0, 0.5, 0.5, 1, 1));

        var columns = PdfPagesPerSheet.Arrange(pdf, null, plan with { Order = SheetOrder.Columns });
        Assert.True(Ink(columns, 0, 0, 0.5, 0.5, 1) > 0.0005);
        Assert.Equal(0, Ink(columns, 0, 0.5, 0, 1, 1));

        var back = PdfPagesPerSheet.Arrange(pdf, null, plan with { Order = SheetOrder.RowsRightToLeft });
        Assert.True(Ink(back, 0, 0, 0, 0.5, 0.5) > 0.0005);
        Assert.Equal(0, Ink(back, 0, 0.5, 0, 1, 1));

        // A line around every page shows also where a page is empty.
        var framed = PdfPagesPerSheet.Arrange(pdf, null, plan with { Margin = 20, Gap = 10, Frame = true });
        Assert.True(Ink(framed, 0, 0, 0.5, 0.5, 1) > 0.0005);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void APageComesOutTheWayItIsShownTurnedOrCroppedOrNot(int turn)
    {
        // Text in a corner and a crop that cuts into the page: one page to a sheet of its own size has to look the same.
        var page = PdfPages.Rotate(PdfPages.CropMargins(TestPdf.WithText("Corner"), null, 40, 60, 150, 300), null, turn);
        var size = PdfPages.GetPageInfos(page)[0];
        var plan = new SheetPlan(size.Width, size.Height, 1, 1) { Margin = 0, Gap = 0 };

        var sheet = PdfPagesPerSheet.Arrange(page, null, plan);

        var result = PdfPages.GetPageInfos(sheet)[0];
        Assert.Equal(size.Width, result.Width, 0.5);
        Assert.Equal(size.Height, result.Height, 0.5);
        using (var shown = TestPdf.Render(page, 0)) Assert.True(TestPdf.InkRatio(shown) > 0.001);
        Assert.True(TestPdf.Difference(page, sheet) < 0.002, $"the sheet differs from its page by {TestPdf.Difference(page, sheet):P2}");
    }

    [Fact]
    public void OnlyTheChosenPagesGoOnSheetsAndTheSheetsStandWhereTheyStood()
    {
        var pdf = TestPdf.Numbered(6);
        var plan = new SheetPlan(Long, Short, 2, 1);

        var result = PdfPagesPerSheet.Arrange(pdf, [4, 1, 2], plan);

        // Page 1 stays, pages 2, 3 and 5 make two sheets where page 2 stood, pages 4 and 6 follow.
        Assert.Equal(5, TestPdf.PageCount(result));
        Assert.Equal("Page 1", TestPdf.Text(result, 0));
        Assert.Matches(@"Page 2\s+Page 3", TestPdf.Text(result, 1));
        Assert.Equal("Page 5", TestPdf.Text(result, 2));
        Assert.Equal("Page 4", TestPdf.Text(result, 3));
        Assert.Equal("Page 6", TestPdf.Text(result, 4));
        Assert.Equal(TestPdf.A4Height, PdfPages.GetPageInfos(result)[3].Height, 0.5);
    }

    [Fact]
    public void TheSheetAPageWillBeOnCanBeHadByItself()
    {
        var pdf = TestPdf.Numbered(5);
        var plan = new SheetPlan(Long, Short, 2, 1);

        var found = PdfPagesPerSheet.SheetWith(pdf, null, 3, plan);

        Assert.NotNull(found);
        Assert.Equal((2, 3), (found.Value.Number, found.Value.Of));
        Assert.Equal(1, TestPdf.PageCount(found.Value.Sheet));
        Assert.Matches(@"Page 3\s+Page 4", TestPdf.Text(found.Value.Sheet, 0));
        // A page that is not among the chosen ones is on no sheet.
        Assert.Null(PdfPagesPerSheet.SheetWith(pdf, [0, 1], 3, plan));
    }

    [AvaloniaFact]
    public async Task TheToolShowsTheSheetBeforeItMakesTheSheets()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(5), null);
        var tool = Tool<PerSheetTool>(vm);
        await Settle(() => vm.IsPreview);

        // Two to a sheet, as the tool starts out: the sheet with the page on view, lying on its side.
        Assert.True(vm.IsPreview);
        Assert.True(vm.PageWidth > vm.PageHeight);
        Assert.Equal(string.Format(Strings.English["PerSheetPreview"], 1, 3), vm.PreviewNote);
        Assert.Contains("5 pages on 3 sheets of 297 × 210 mm", vm.ApplySummary);
        Capture(window, "60-per-sheet");

        // Four to a sheet keeps the sheet upright; the last page is on the second of two.
        Field<ModeField>(tool, "PerSheetCount").Select("4");
        vm.GoToPage(4);
        await Settle(() => vm.IsPreview && vm.PageHeight > vm.PageWidth && vm.PreviewNote.Contains('2'));
        Assert.Equal(string.Format(Strings.English["PerSheetPreview"], 2, 2), vm.PreviewNote);

        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && vm.Pages.Count == 2);

        Assert.Equal(2, vm.Pages.Count);
        Assert.Equal(string.Format(Strings.English["StatusPerSheet"], 5, 2), vm.StatusText);
        Assert.True(vm.CanUndo);
        Assert.Matches(@"Page 1\s+Page 2\s+Page 3\s+Page 4", TestPdf.Text(vm.Session.Bytes!, 0));
        window.Close();
    }
}
