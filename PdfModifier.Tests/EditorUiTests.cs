using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PdfModifier.Controls;
using PdfModifier.Core;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using PdfModifier.Localization;
using PdfModifier.Services;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using PdfModifier.Views;
using SkiaSharp;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>
/// The page view and the editor, used the way a person uses them: with the mouse on the page and the keyboard.
/// </summary>
public class EditorUiTests
{
    private static PageCanvas Canvas(MainWindow window) => window.FindControl<PageCanvas>("PageView")!;

    private static ScrollViewer Viewer(MainWindow window) => window.FindControl<ScrollViewer>("Viewer")!;

    /// <summary>Where a point of the page (in points from its top-left corner) is in the window right now.</summary>
    private static Point At(MainWindow window, MainViewModel vm, double x, double y) =>
        Canvas(window).TranslatePoint(new Point(x * vm.PointScale, y * vm.PointScale), window)!.Value;

    private static void Drag(MainWindow window, MainViewModel vm, double x0, double y0, double x1, double y1)
    {
        window.MouseDown(At(window, vm, x0, y0), MouseButton.Left);
        // The page may move under the mouse while it is dragged, so every step is worked out from where the page was at the start.
        var from = At(window, vm, x0, y0);
        var by = new Point((x1 - x0) * vm.PointScale, (y1 - y0) * vm.PointScale);
        for (var step = 1; step <= 4; step++) window.MouseMove(new Point(from.X + by.X * step / 4, from.Y + by.Y * step / 4));
        window.MouseUp(new Point(from.X + by.X, from.Y + by.Y), MouseButton.Left);
    }

    private static void Click(MainWindow window, MainViewModel vm, double x, double y)
    {
        window.MouseDown(At(window, vm, x, y), MouseButton.Left);
        window.MouseUp(At(window, vm, x, y), MouseButton.Left);
    }

    private static void Key(MainWindow window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }

    private static ModeField Mode(EditTool edit) => edit.Fields.OfType<ModeField>().First();

    private static async Task<(MainWindow, MainViewModel, EditTool)> Editing(byte[] pdf, double zoom = 0.75)
    {
        var (window, vm) = Start();
        vm.Session.Open(pdf, null);
        var edit = Tool<EditTool>(vm);
        vm.Zoom = zoom;
        await Settle(() => vm.PageBitmap != null && !string.IsNullOrEmpty(edit.Fields.OfType<InfoField>().ElementAt(1).Text));
        return (window, vm, edit);
    }

    private static string TempImage(SKColor color, int width = 120, int height = 80, string name = "picture")
    {
        var folder = Path.Combine(Path.GetTempPath(), "PdfModifier.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bitmap.Erase(color);
        var path = Path.Combine(folder, name + ".png");
        File.WriteAllBytes(path, PdfImages.Encode(bitmap, ImageFormat.Png));
        return path;
    }

    [AvaloniaFact]
    public async Task ATextBoxIsDrawnOnThePageTypedStyledMovedAndUndone()
    {
        var (window, vm, edit) = await Editing(TestPdf.WithText("Existing"));

        // Text mode: the place for the text is marked by dragging, not by a single click.
        Mode(edit).Select("text");
        Assert.Equal(CanvasMode.Region, vm.CanvasMode);
        Drag(window, vm, 100, 300, 350, 340);
        await Settle();

        var obj = Assert.Single(vm.Objects);
        Assert.Same(obj, vm.SelectedObject);
        Assert.Equal("select", Mode(edit).Key);
        var box = Assert.IsType<PageText>(obj.Model);
        Assert.Equal(100, box.Bounds.Left, 1.5);
        Assert.Equal(300, box.Bounds.Top, 1.5);
        Assert.Equal(250, box.Bounds.Width, 3.0);

        // The keyboard goes straight to the new box: no click anywhere else is needed before typing.
        var text = Field<TextField>(edit, "EditSelectedText", 1);
        Assert.True(text.IsVisible);
        await Settle();
        window.KeyTextInput("Typed");
        Assert.Equal("Typed", text.Value);
        Assert.Equal("Typed", ((PageText)obj.Model).Text);

        // Typing shows on the page at once; so does a change of font, size, weight and colour.
        text.Value = "Typed into the box";
        Field<NumberField>(edit, "FontSize").Value = 20;
        Field<ToggleField>(edit, "Bold").Value = true;
        Field<ToggleField>(edit, "Italic").Value = true;
        Field<ColorField>(edit, "TextColor").Value = "#C00000";
        Field<ModeField>(edit, "TextAlign").Select("right");
        var font = Field<ChoiceField>(edit, "FontFamily");
        await Settle(() => font.Options.Count > 1);
        if (font.Options.Any(o => o.Key == "Times New Roman")) font.Select("Times New Roman");

        var styled = Assert.IsType<PageText>(obj.Model);
        Assert.Equal(("Typed into the box", 20.0, true, true, 0xFFC00000u, TextAlign.Right),
            (styled.Text, styled.FontSize, styled.Bold, styled.Italic, styled.Color, styled.Align));
        Assert.NotNull(obj.Picture);
        Assert.True(vm.CanUndo, "an edit that is only on screen so far can be undone too");
        Capture(window, "10-text-box");

        await vm.FlushTool();
        var stored = Assert.IsType<PageText>(Assert.Single(PdfPageObjects.Read(vm.Session.Bytes!, 0)));
        Assert.Equal(styled.Text, stored.Text);
        Assert.Equal(20, stored.FontSize, 0.01);
        Assert.Equal(styled.FontFamily, stored.FontFamily);
        Assert.Contains("Typed into the box", TestPdf.Text(vm.Session.Bytes!, 0));
        Assert.Same(obj, vm.SelectedObject);

        // Moved with the mouse, then nudged with the arrow keys.
        var middle = styled.Bounds;
        Drag(window, vm, middle.CenterX, middle.CenterY, middle.CenterX + 60, middle.CenterY + 40);
        await Settle();
        Assert.Equal(middle.Left + 60, obj.Model.Bounds.Left, 1.5);
        Assert.Equal(middle.Top + 40, obj.Model.Bounds.Top, 1.5);
        Key(window, PhysicalKey.ArrowRight);
        Key(window, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
        Assert.Equal(middle.Left + 61, obj.Model.Bounds.Left, 1.5);
        Assert.Equal(middle.Top + 50, obj.Model.Bounds.Top, 1.5);

        await vm.FlushTool();
        var moved = Assert.Single(PdfPageObjects.Read(vm.Session.Bytes!, 0));
        Assert.Equal(obj.Model.Bounds.Left, moved.Bounds.Left, 0.1);

        // Delete takes it away; undo brings it back, as an object that can be selected again.
        Key(window, PhysicalKey.Delete);
        Assert.Empty(vm.Objects);
        await vm.FlushTool();
        Assert.Empty(PdfPageObjects.Read(vm.Session.Bytes!, 0));

        await vm.UndoCommand.ExecuteAsync(null);
        await Settle(() => vm.Objects.Count == 1);
        Assert.Equal("Typed into the box", Assert.IsType<PageText>(Assert.Single(vm.Objects).Model).Text);
        Assert.Equal("Existing", Assert.Single(vm.Overlays).Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EditsAreNotLostWhenThePageOrTheToolIsChangedRightAfterThem()
    {
        var (window, vm, edit) = await Editing(TestPdf.Numbered(3));

        // Typed on page 1, and on to page 2 at once, before the edit had time to be written.
        Mode(edit).Select("text");
        Drag(window, vm, 100, 300, 350, 340);
        Field<TextField>(edit, "EditSelectedText", 1).Value = "Note on the first page";
        Assert.True(edit.HasPendingChanges);
        vm.GoToPage(1);
        await Settle(() => !edit.HasPendingChanges && vm.Overlays.Count == 1 && vm.Overlays[0].Text == "Page 2");
        Assert.Empty(vm.Objects);
        Assert.Contains("Note on the first page", TestPdf.Text(vm.Session.Bytes!, 0));

        // Typed on page 2, and straight to another tool.
        Mode(edit).Select("text");
        Drag(window, vm, 100, 300, 350, 340);
        Field<TextField>(edit, "EditSelectedText", 1).Value = "Note on the second page";
        vm.SelectedTool = vm.Tools.OfType<DeleteTool>().Single();
        await Settle(() => !edit.HasPendingChanges);
        await Settle();
        Assert.Contains("Note on the second page", TestPdf.Text(vm.Session.Bytes!, 1));
        Assert.Empty(vm.Objects);

        // Back in the editor both notes are objects again, each on its page.
        vm.SelectedTool = edit;
        await Settle(() => vm.Objects.Count == 1);
        Assert.Equal("Note on the second page", ((PageText)vm.Objects[0].Model).Text);
        vm.GoToPage(0);
        await Settle(() => vm.Objects.Count == 1 && ((PageText)vm.Objects[0].Model).Text == "Note on the first page");
        Assert.Equal("Page 3", TestPdf.Text(vm.Session.Bytes!, 2));
        window.Close();
    }

    [AvaloniaFact]
    public async Task UndoTakesBackAnEditThatIsOnlyOnScreenSoFar()
    {
        var (window, vm, edit) = await Editing(TestPdf.WithText("Existing"));
        var original = vm.Session.Bytes;

        Mode(edit).Select("text");
        Drag(window, vm, 100, 300, 350, 340);
        Field<TextField>(edit, "EditSelectedText", 1).Value = "About to be undone";
        Assert.Same(original, vm.Session.Bytes); // not written yet
        Assert.True(vm.CanUndo);

        await vm.UndoCommand.ExecuteAsync(null);
        await Settle(() => vm.Objects.Count == 0 && vm.Overlays.Count == 1);
        Assert.DoesNotContain("About to be undone", TestPdf.Text(vm.Session.Bytes!, 0));
        Assert.True(vm.CanRedo);

        await vm.RedoCommand.ExecuteAsync(null);
        await Settle(() => vm.Objects.Count == 1);
        Assert.Equal("About to be undone", ((PageText)vm.Objects[0].Model).Text);

        // A box that was drawn but never typed into leaves nothing behind, not even something to undo.
        vm.SelectedObject = null;
        Mode(edit).Select("text");
        Drag(window, vm, 100, 500, 350, 540);
        Assert.Equal(2, vm.Objects.Count);
        var before = vm.Session.Bytes;
        Click(window, vm, 450, 700);
        Assert.Single(vm.Objects);
        await vm.FlushTool();
        Assert.Same(before, vm.Session.Bytes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ATextBoxIsResizedAndTurnedByItsHandles()
    {
        var (window, vm, edit) = await Editing(TestPdf.WithText(""));
        Mode(edit).Select("text");
        Drag(window, vm, 100, 200, 300, 240);
        Field<TextField>(edit, "EditSelectedText", 1).Value = "Some words that will wrap when the box gets narrow";
        await Settle();
        var obj = Assert.Single(vm.Objects);
        var before = (PageText)obj.Model;
        var lineHeight = before.FontSize * before.LineSpacing;
        Assert.Equal(2 * lineHeight, before.Bounds.Height, 0.5);

        // The handle on the right edge changes the width: the text wraps into more lines and the box grows downwards.
        Drag(window, vm, before.Bounds.Right, before.Bounds.CenterY, before.Bounds.Right - 110, before.Bounds.CenterY);
        var narrow = (PageText)obj.Model;
        Assert.Equal(before.Bounds.Width - 110, narrow.Bounds.Width, 2.0);
        Assert.Equal(before.Bounds.Left, narrow.Bounds.Left, 0.5);
        Assert.Equal(before.Bounds.Top, narrow.Bounds.Top, 0.5);
        Assert.True(narrow.Bounds.Height > before.Bounds.Height + lineHeight * 0.9, "more lines, taller box");
        Assert.Equal(before.FontSize, narrow.FontSize);

        // A corner handle makes the whole box bigger, letters included.
        Drag(window, vm, narrow.Bounds.Right, narrow.Bounds.Bottom, narrow.Bounds.Right + narrow.Bounds.Width / 2, narrow.Bounds.Bottom + narrow.Bounds.Height / 2);
        var bigger = (PageText)obj.Model;
        Assert.Equal(narrow.FontSize * 1.5, bigger.FontSize, 0.4);
        Assert.Equal(narrow.Bounds.Width * 1.5, bigger.Bounds.Width, 3.0);
        Assert.Equal(narrow.Bounds.Left, bigger.Bounds.Left, 0.5);

        // The rotation can be set exactly, and what is turned still writes and reads back the same.
        Field<SliderField>(edit, "Rotation").Value = 30;
        Assert.Equal(30, obj.Model.Rotation);
        await vm.FlushTool();
        var stored = Assert.IsType<PageText>(Assert.Single(PdfPageObjects.Read(vm.Session.Bytes!, 0)));
        Assert.Equal(30, stored.Rotation, 0.05);
        Assert.Equal(bigger.FontSize, stored.FontSize, 0.05);
        Capture(window, "11-text-box-turned");
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnImageUsedOnceIsOfferedAgainAndCanBePlacedResizedAndPartlyErased()
    {
        var path = TempImage(SKColors.Red);
        var (window, vm, edit) = await Editing(TestPdf.WithText("Existing"));
        var file = Field<FileField>(edit, "EditImageFile");
        Assert.False(file.HasRecent);

        // Chosen once (as Browse does it), the image stays on offer under the field, marked as the one in use.
        vm.RememberImage(path);
        file.Path = path;
        Assert.Equal(path, Assert.Single(file.Recent).Path);
        Assert.True(file.Recent[0].IsCurrent);
        Assert.Equal([path], Settings.Load().RecentImages);
        await Settle(() => file.Recent[0].Thumbnail != null);

        Mode(edit).Select("image");
        Click(window, vm, 300, 400);
        await Settle(() => vm.Objects.Count == 1);
        var obj = Assert.Single(vm.Objects);
        var image = Assert.IsType<PageImage>(obj.Model);
        // 120 x 80 pixels at 96 to the inch are 90 x 60 points, centred where the click was.
        Assert.Equal((90.0, 60.0), (Math.Round(image.Bounds.Width, 1), Math.Round(image.Bounds.Height, 1)));
        Assert.Equal(300, image.Bounds.CenterX, 1.5);
        Assert.Same(obj, vm.SelectedObject);

        // The same image again, from the list, without browsing for it: this time into an area drawn for it.
        Mode(edit).Select("image");
        file.UseRecentCommand.Execute(file.Recent[0]);
        Drag(window, vm, 100, 500, 400, 600);
        await Settle(() => vm.Objects.Count == 2);
        var second = Assert.IsType<PageImage>(vm.Objects[1].Model);
        Assert.Equal(150, second.Bounds.Width, 2.0); // fitted into 300 x 100 keeping 3:2
        Assert.Equal(100, second.Bounds.Height, 2.0);
        Assert.Equal(250, second.Bounds.CenterX, 2.0);

        // Resized by a corner it keeps its proportions; its opacity is a setting of its own.
        Drag(window, vm, second.Bounds.Right, second.Bounds.Bottom, second.Bounds.Right + 75, second.Bounds.Bottom + 20);
        var resized = (PageImage)vm.Objects[1].Model;
        Assert.Equal(1.5, resized.Bounds.Width / resized.Bounds.Height, 0.02);
        Assert.Equal(225, resized.Bounds.Width, 3.0);
        Field<SliderField>(edit, "Opacity").Value = 50;
        Assert.Equal(0.5, ((PageImage)vm.Objects[1].Model).Opacity);

        // The eraser, allowed to touch added images only: the right part of the first image goes, the page stays.
        Mode(edit).Select("erase");
        var what = edit.Fields.OfType<CheckListField>().Single();
        foreach (var item in what.Items) item.IsChecked = item.Key == "images";
        Drag(window, vm, 300, 350, 380, 450);
        await Settle();
        Assert.Equal(2, vm.Objects.Count);
        Assert.True(((PageImage)vm.Objects[0].Model).Pixels.HasAlpha);
        Capture(window, "12-images");

        await vm.FlushTool();
        using var rendered = TestPdf.Render(vm.Session.Bytes!, 0);
        Assert.True(rendered.GetPixel(280, 400).Red > 240 && rendered.GetPixel(280, 400).Green < 20, "left part of the image is still there");
        Assert.Equal(SKColors.White, rendered.GetPixel(325, 400));
        Assert.Contains("Existing", TestPdf.Text(vm.Session.Bytes!, 0));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheEraserPaintsThePageOverInAChosenColourAndCanRemoveAddedTextInstead()
    {
        var (window, vm, edit) = await Editing(TestPdf.WithText("Erase me"));
        await Settle(() => vm.Overlays.Count == 1);
        var line = vm.Overlays[0].Bounds;

        // A text box right under the line, which the first sweep of the eraser is told to leave alone.
        Mode(edit).Select("text");
        Drag(window, vm, 72, 140, 300, 170);
        Field<TextField>(edit, "EditSelectedText", 1).Value = "Added afterwards";
        await Settle();

        Mode(edit).Select("erase");
        var what = edit.Fields.OfType<CheckListField>().Single();
        Assert.Equal(["page", "text", "images"], what.CheckedKeys);
        foreach (var item in what.Items) item.IsChecked = item.Key == "page";
        var color = Field<ColorField>(edit, "EditEraseColor");
        Assert.True(color.Automatic);
        color.Value = "#FFE0A0";
        Assert.False(color.Automatic);
        Drag(window, vm, line.Left - 6, line.Top - 6, line.Right + 6, 200);
        await Settle(() => vm.Objects.Count == 2);

        var patch = Assert.IsType<PagePatch>(vm.Objects[0].Model);
        Assert.Equal(0xFFFFE0A0, patch.Color);
        Assert.IsType<PageText>(vm.Objects[1].Model); // still there, and on top of the patch
        await vm.FlushTool();
        var text = TestPdf.Text(vm.Session.Bytes!, 0);
        Assert.DoesNotContain("Erase me", text); // gone for good, not just hidden
        Assert.Contains("Added afterwards", text);
        using (var rendered = TestPdf.Render(vm.Session.Bytes!, 0))
            Assert.Equal(new SKColor(0xFF, 0xE0, 0xA0), rendered.GetPixel((int)line.CenterX, (int)line.CenterY));

        // The second sweep may touch added text only: the box goes, the patch and the page stay as they are.
        foreach (var item in what.Items) item.IsChecked = item.Key == "text";
        Drag(window, vm, 60, 130, 320, 190);
        await Settle();
        Assert.IsType<PagePatch>(Assert.Single(vm.Objects).Model);

        // The patch is an object too: recoloured from its own field.
        Mode(edit).Select("select");
        Click(window, vm, line.CenterX, line.CenterY);
        Assert.Same(vm.Objects[0], vm.SelectedObject);
        Field<ColorField>(edit, "EditPatchColor").Value = "#FFFFFF";
        Assert.Equal(0xFFFFFFFF, ((PagePatch)vm.Objects[0].Model).Color);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ALineOfRealTextCanBeTurnedIntoABoxThatMoves()
    {
        var (window, vm, edit) = await Editing(TestPdf.WithText("Move me elsewhere"));
        await Settle(() => vm.Overlays.Count == 1);
        var run = (TextRun)vm.Overlays[0].Source;

        await vm.OverlayClicked(vm.Overlays[0]);
        var lift = Field<ActionField>(edit, "EditMakeMovable");
        Assert.True(lift.IsVisible);
        lift.Command.Execute(null);
        await Settle(() => !vm.IsBusy && vm.Objects.Count == 1);

        // Same words in the same place and size, but now an object; the line it came from is no longer page content.
        var box = Assert.IsType<PageText>(Assert.Single(vm.Objects).Model);
        Assert.Equal("Move me elsewhere", box.Text);
        Assert.Equal(run.FontSize, box.FontSize, 0.01);
        Assert.Equal(run.Bounds.Left, box.Bounds.Left, 2.0);
        Assert.InRange(run.Baseline, box.Bounds.Top, box.Bounds.Bottom);
        Assert.Empty(vm.Overlays);
        Assert.Same(vm.Objects[0], vm.SelectedObject);
        Assert.Equal("Move me elsewhere", TestPdf.Text(vm.Session.Bytes!, 0));
        using var before = TestPdf.Render(TestPdf.WithText("Move me elsewhere"), 0, 2);
        using var after = TestPdf.Render(vm.Session.Bytes!, 0, 2);
        Assert.True(PdfModifier.Core.Imaging.ImageAnalysis.DifferenceRatio(before, after, 96) < 0.002, "the page should look the same");
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheTextLayerOfASearchableScanAndTextInNestedGraphicsAreFoundWithoutRecognition()
    {
        using var scan = TestPdf.FakeScan(620, 877);
        var searchable = TestPdf.ScanWith(scan, over: (page, fonts) =>
            PageDraw.DrawText(page, fonts, "Layer line one", new TextStyle(12, Invisible: true), 40, 60));
        var (window, vm, edit) = await Editing(searchable);
        await Settle(() => vm.Overlays.Count > 0);
        var layer = Assert.Single(vm.Overlays);
        Assert.Equal("Layer line one", layer.Text);
        Assert.True(layer.IsScan);

        // Retyping it paints over the old line and removes its hidden text, so a search finds the new words only.
        await vm.OverlayClicked(layer);
        Field<TextField>(edit, "EditSelectedText").Value = "Replaced";
        Press(edit, "EditApplyChange");
        await Settle(() => vm.Objects.Count == 2);
        await vm.FlushTool();
        Assert.Equal("Replaced", TestPdf.Text(vm.Session.Bytes!, 0));

        // A page whose content is wrapped in a nested graphic: its text is on offer just the same.
        vm.Session.Open(TestPdf.Nested(TestPdf.WithText("Wrapped line")), null);
        await Settle(() => vm.Overlays.Count > 0);
        Assert.Equal("Wrapped line", Assert.Single(vm.Overlays).Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OnAScanWithSomeTypedTextBothKindsOfTextAreOffered()
    {
        if (!PdfOcr.EngineStatus().Available || TessData.Missing(["en"]).Count > 0) return;
        using var scan = TestPdf.FakeScan(1240, 1754);
        var mixed = TestPdf.ScanWith(scan, over: (page, fonts) =>
            PageDraw.DrawText(page, fonts, "Reference 2026-0417", new TextStyle(12), 60, 50));
        var (window, vm, _) = await Editing(mixed, 0.5);
        await Settle(() => vm.Overlays.Count > 10, 90);

        // The typed line as text of the document, the lines of the scan as recognised text: neither hides the other.
        Assert.Equal("Reference 2026-0417", Assert.Single(vm.Overlays, o => !o.IsScan).Text);
        Assert.True(vm.Overlays.Count(o => o.IsScan) >= 15, $"{vm.Overlays.Count(o => o.IsScan)} scanned lines offered");
        Capture(window, "13-mixed-page");
        window.Close();
    }

    [AvaloniaFact]
    public async Task PagesAreTurnedWithTheArrowKeysAndByScrollingPastTheEdge()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(3), null);
        Tool<DeleteTool>(vm);
        vm.Zoom = 1; // taller than the window: there is something to scroll
        await Settle(() => vm.PageBitmap != null);
        var viewer = Viewer(window);
        var over = At(window, vm, 300, 300);
        Assert.True(viewer.Extent.Height > viewer.Viewport.Height + 100);
        Click(window, vm, 300, 300);

        Key(window, PhysicalKey.ArrowRight);
        Assert.Equal(1, vm.CurrentPageIndex);
        Key(window, PhysicalKey.End);
        Assert.Equal(2, vm.CurrentPageIndex);
        Key(window, PhysicalKey.ArrowRight); // nothing after the last page
        Assert.Equal(2, vm.CurrentPageIndex);
        Key(window, PhysicalKey.Home);
        Assert.Equal(0, vm.CurrentPageIndex);
        await Settle();

        // The wheel scrolls within the page first...
        window.MouseWheel(over, new Vector(0, -1));
        await Settle();
        Assert.True(viewer.Offset.Y > 10);
        Assert.Equal(0, vm.CurrentPageIndex);

        // ...and only from its bottom edge, after coming to rest there, goes on to the next page, shown from its top.
        viewer.Offset = new Vector(0, viewer.Extent.Height - viewer.Viewport.Height);
        await Task.Delay(350);
        window.MouseWheel(over, new Vector(0, -1));
        await Settle();
        Assert.Equal(1, vm.CurrentPageIndex);
        Assert.Equal(0, viewer.Offset.Y, 1.0);

        // Scrolling up from the top goes back, to the bottom of the page before.
        await Task.Delay(350);
        window.MouseWheel(over, new Vector(0, 1));
        await Settle();
        Assert.Equal(0, vm.CurrentPageIndex);
        Assert.Equal(viewer.Extent.Height - viewer.Viewport.Height, viewer.Offset.Y, 1.0);

        // Down arrow: scrolls the page, and turns it at the edge.
        viewer.Offset = new Vector(0, viewer.Extent.Height - viewer.Viewport.Height);
        Key(window, PhysicalKey.ArrowDown);
        Assert.Equal(1, vm.CurrentPageIndex);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ZoomingKeepsThePlaceUnderThePointerAndDraggingMovesThePage()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(1), null);
        Tool<DeleteTool>(vm);
        vm.Zoom = 1.5;
        await Settle(() => vm.PageBitmap != null);
        var viewer = Viewer(window);
        Assert.True(viewer.Extent.Width > viewer.Viewport.Width, "zoomed in, the page is wider than the view");

        // Zoom in on a spot well away from the top-left corner: that spot stays under the pointer.
        var spot = At(window, vm, 250, 200);
        window.MouseWheel(spot, new Vector(0, 1), RawInputModifiers.Control);
        await Settle();
        Assert.True(vm.Zoom > 1.8);
        var after = At(window, vm, 250, 200);
        Assert.Equal(spot.X, after.X, 2.0);
        Assert.Equal(spot.Y, after.Y, 2.0);
        Assert.True(viewer.Offset.X > 50 && viewer.Offset.Y > 50, $"the view moved to {viewer.Offset} instead of staying in the corner");

        // The zoom buttons have no pointer to go by: they keep the middle of the view where it is.
        var centre = new Point(viewer.Viewport.Width / 2, viewer.Viewport.Height / 2);
        var shown = viewer.TranslatePoint(centre, Canvas(window))!.Value;
        var scale = vm.PointScale;
        vm.ZoomOutCommand.Execute(null);
        await Settle();
        Assert.True(vm.PointScale < scale);
        var still = Canvas(window).TranslatePoint(new Point(shown.X * vm.PointScale / scale, shown.Y * vm.PointScale / scale), viewer)!.Value;
        Assert.Equal(centre.X, still.X, 2.0);
        Assert.Equal(centre.Y, still.Y, 2.0);

        // Dragging the page moves it under the mouse, in both directions.
        var offset = viewer.Offset;
        var grab = At(window, vm, 250, 200);
        window.MouseDown(grab, MouseButton.Left);
        window.MouseMove(new Point(grab.X - 40, grab.Y - 30));
        window.MouseMove(new Point(grab.X - 120, grab.Y - 90));
        window.MouseUp(new Point(grab.X - 120, grab.Y - 90), MouseButton.Left);
        Assert.Equal(offset.X + 120, viewer.Offset.X, 2.0);
        Assert.Equal(offset.Y + 90, viewer.Offset.Y, 2.0);

        // So does the middle button, and the left one while the space bar is held, whatever the tool is doing.
        Tool<CropTool>(vm).Fields.OfType<ChoiceField>().First().Select("hand");
        Assert.Equal(CanvasMode.Area, vm.CanvasMode);
        await Settle(() => vm.Region != null);
        var frame = vm.Region;
        offset = viewer.Offset;
        grab = At(window, vm, 300, 300);
        window.MouseDown(grab, MouseButton.Middle);
        window.MouseMove(new Point(grab.X + 50, grab.Y + 40));
        window.MouseUp(new Point(grab.X + 50, grab.Y + 40), MouseButton.Middle);
        Assert.Equal(offset.X - 50, viewer.Offset.X, 2.0);
        Assert.Equal(frame, vm.Region);

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        grab = At(window, vm, 300, 300);
        window.MouseDown(grab, MouseButton.Left);
        window.MouseMove(new Point(grab.X + 30, grab.Y + 30));
        window.MouseUp(new Point(grab.X + 30, grab.Y + 30), MouseButton.Left);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.Equal(offset.X - 80, viewer.Offset.X, 2.0);
        Assert.Equal(frame, vm.Region);

        vm.ZoomToWidth(viewer.Bounds.Width);
        await Settle();
        Assert.True(viewer.Extent.Width <= viewer.Viewport.Width + 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AToolShowsOnThePageWhatItWillDoBeforeItIsApplied()
    {
        var (window, vm) = Start();
        var original = TestPdf.Numbered(2);
        vm.Session.Open(original, null);
        vm.Zoom = 0.75;
        var watermark = Tool<WatermarkTool>(vm);
        await Settle(() => vm.IsPreview && vm.PageBitmap != null);

        uint Centre() => BitmapConverter.PixelAt(vm.PageBitmap, 0.5, 0.5)!.Value;

        // Black and solid, so the middle of the page tells whether the watermark is shown.
        Field<SliderField>(watermark, "Opacity").Value = 100;
        Field<SliderField>(watermark, "Rotation").Value = 0;
        Field<ColorField>(watermark, "TextColor").Value = "#000000";
        Field<TextField>(watermark, "WatermarkTextValue").Value = "████";
        await Settle(() => Centre() == 0xFF000000);
        Assert.True(vm.IsPreview);
        Assert.Same(original, vm.Session.Bytes); // shown, not done
        Assert.False(vm.CanUndo);
        Capture(window, "14-preview");

        // Without the preview the page is as it is; with it again, as it would be.
        vm.ShowPreview = false;
        await Settle(() => !vm.IsPreview && Centre() == 0xFFFFFFFF);
        vm.ShowPreview = true;
        await Settle(() => vm.IsPreview && Centre() == 0xFF000000);

        // A spot chosen on the small page puts the mark there: at the top right, a fifth of the way in.
        var place = watermark.Fields.OfType<PlaceField>().Single();
        Assert.Equal("Center", place.Key);
        place.ChooseCommand.Execute(place.Spots.Single(spot => spot.Key == "TopRight"));
        await Settle(() => vm.IsPreview && Centre() == 0xFFFFFFFF);
        Assert.Equal(0xFF000000, BitmapConverter.PixelAt(vm.PageBitmap, 0.8, 0.14));
        Assert.True(place.Spots.Single(spot => spot.Key == "TopRight").IsChosen);

        // Placed by a click on the page, the mark goes where the click was, and none of the spots is the chosen one.
        Assert.Equal(CanvasMode.Click, vm.CanvasMode);
        Click(window, vm, 300, 150);
        await Settle(() => vm.IsPreview && BitmapConverter.PixelAt(vm.PageBitmap, 0.8, 0.14) == 0xFFFFFFFF);
        Assert.Equal(0xFF000000, BitmapConverter.PixelAt(vm.PageBitmap, 300 / 595.0, 150 / 842.0));
        Assert.Null(place.Key);
        Assert.DoesNotContain(place.Spots, spot => spot.IsChosen);

        // Where a click means something, dragging still moves the page under the mouse.
        var viewer = window.FindControl<ScrollViewer>("Viewer")!;
        vm.Zoom = 2;
        await Settle(() => viewer.Extent.Height > viewer.Viewport.Height + 200);
        var before = viewer.Offset;
        var grab = viewer.TranslatePoint(new Point(viewer.Viewport.Width / 2, viewer.Viewport.Height / 2), window)!.Value;
        window.MouseDown(grab, MouseButton.Left);
        window.MouseMove(new Point(grab.X - 30, grab.Y - 40));
        window.MouseMove(new Point(grab.X - 60, grab.Y - 80));
        window.MouseUp(new Point(grab.X - 60, grab.Y - 80), MouseButton.Left);
        Assert.Equal(before.Y + 80, viewer.Offset.Y, 3.0);
        Assert.Null(place.Key);
        vm.Zoom = 0.75;
        await Settle(() => vm.IsPreview);
        Assert.Equal(0xFF000000, BitmapConverter.PixelAt(vm.PageBitmap, 300 / 595.0, 150 / 842.0));

        // On a page that is not among those chosen there is nothing to preview, and the panel says why.
        var pages = watermark.Fields.OfType<PagesField>().Single();
        pages.Scope = pages.Scopes[2];
        pages.Range = "2";
        await Settle(() => !vm.IsPreview && vm.PreviewNote.Length > 0);
        Assert.Equal(Strings.English["PreviewOutside"], vm.PreviewNote);
        pages.Scope = pages.Scopes[0];
        await Settle(() => vm.IsPreview);

        // Applied, the page shows the result itself. It is not previewed on top of that again until a setting changes.
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy && vm.PageBitmap != null);
        await Settle();
        Assert.False(vm.IsPreview);
        Assert.NotSame(original, vm.Session.Bytes);
        Assert.Equal(0xFF000000, BitmapConverter.PixelAt(vm.PageBitmap, 300 / 595.0, 150 / 842.0));
        Field<SliderField>(watermark, "Opacity").Value = 60;
        await Settle(() => vm.IsPreview);

        // A preview may also change the size of the page.
        var resize = Tool<ResizeTool>(vm);
        Field<ChoiceField>(resize, "ResizeTarget").Select("a3");
        await Settle(() => vm.IsPreview && vm.PageWidth > 800);
        Assert.Equal((842.0, 1191.0), (Math.Round(vm.PageWidth), Math.Round(vm.PageHeight)));
        Assert.Equal(595, PdfPages.GetPageInfos(vm.Session.Bytes!)[0].Width, 1.0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AMarkThatIsPartOfAScanIsTakenOutThroughTheWatermarkTool()
    {
        using var scan = TestPdf.FakeScan(620, 877, paper: 240);
        using (var canvas = new SKCanvas(scan))
        using (var paint = new SKPaint { Color = new SKColor(196, 196, 196), TextSize = 110, IsAntialias = true, BlendMode = SKBlendMode.Multiply })
            canvas.DrawText("SAMPLE", 110, 480, paint);
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.FromImage(scan), null);
        vm.Zoom = 0.75;
        var tool = Tool<RemoveWatermarkTool>(vm);
        var method = Field<ChoiceField>(tool, "WatermarkMethod");

        // The page is a picture and has nothing laid over it: the tool starts out with the method for that.
        await Settle(() => method.Key == "light" && vm.IsPreview, 10);
        Assert.Equal(Strings.English["WatermarkPageScan"], tool.Fields.OfType<InfoField>().First().Text);
        Capture(window, "15-scan-mark-preview");

        // The rows between two lines of text that the mark runs through: only the mark has its gray there.
        static bool MarkAt(byte[] pdf)
        {
            using var page = TestPdf.Render(pdf, 0, 150 / 72.0);
            return Enumerable.Range(444, 24).Count(y => Enumerable.Range(120, 380).Count(x => page.GetPixel(x, y) is { Red: > 174 and < 194 }) > 40) > 12;
        }

        Assert.True(MarkAt(vm.Session.Bytes!), "the fixture should show the mark");
        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy);
        Assert.False(MarkAt(vm.Session.Bytes!));
        Assert.True(vm.CanUndo);

        // Limited to an area, the tool works on a rectangle that is drawn on the page and stays there.
        Field<BoolField>(tool, "WatermarkLimitArea").Value = true;
        Assert.Equal(CanvasMode.Area, vm.CanvasMode);
        Drag(window, vm, 50, 100, 250, 300);
        Assert.NotNull(vm.Region);
        Assert.Equal(200, vm.Region!.Value.Width, 3.0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheAreaACropWillKeepIsShownAndCanBePulledIntoPlace()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.WithText("Tight crop"), null);
        vm.Zoom = 0.75;
        var crop = Tool<CropTool>(vm);
        var run = PdfTextEdit.GetTextRuns(vm.Session.Bytes!, 0)[0];

        // To the content: what was found, plus the space asked for, with the rest of the page darkened.
        await Settle(() => vm.Region != null);
        Assert.True(vm.DimOutsideRegion);
        Assert.Equal(CanvasMode.Select, vm.CanvasMode);
        Assert.Equal(run.Bounds.Left - 3 * 72 / 25.4, vm.Region!.Value.Left, 2.0);
        Field<SliderField>(crop, "CropPadding").Set(10);
        await Settle(() => vm.Region is { } r && Math.Abs(r.Left - (run.Bounds.Left - 10 * 72 / 25.4)) < 2);
        Assert.Equal(run.Bounds.Left - 10 * 72 / 25.4, vm.Region!.Value.Left, 2.0);

        // By hand: the frame starts a centimetre in from every edge, and typing a margin moves it.
        var mode = Field<ModeField>(crop, "CropMode");
        mode.Select("hand");
        Assert.Equal(CanvasMode.Area, vm.CanvasMode);
        var left = Field<NumberField>(crop, "MarginLeft");
        Assert.True(left.IsVisible);
        left.Value = 25.4m;
        await Settle(() => vm.Region is { } r && Math.Abs(r.Left - 72) < 0.5);
        Assert.Equal(595 - 10 * 72 / 25.4, vm.Region!.Value.Right, 0.5);

        // Pulled on the page by its right edge, and moved as a whole from its inside; the margins follow.
        var region = vm.Region!.Value;
        Drag(window, vm, region.Right, region.CenterY, 380, region.CenterY);
        await Settle(() => vm.Region is { } r && Math.Abs(r.Right - 380) < 1);
        Assert.Equal(380, vm.Region!.Value.Right, 1.0);
        Assert.Equal((595 - 380) / (72 / 25.4), Field<NumberField>(crop, "MarginRight").Number, 0.5);
        Assert.Equal(25.4, left.Number, 0.01);
        Drag(window, vm, 200, 400, 180, 430);
        await Settle(() => vm.Region is { } r && Math.Abs(r.Left - 52) < 1);
        region = vm.Region!.Value;
        Assert.Equal((52.0, 360.0), (Math.Round(region.Left), Math.Round(region.Right)));
        Assert.Equal(52 / (72 / 25.4), left.Number, 0.5);
        Assert.Contains($"{region.Width / (72 / 25.4):0} × {region.Height / (72 / 25.4):0} mm", crop.Fields.OfType<InfoField>().Single().Text);
        Capture(window, "16-crop-area");

        await vm.ApplyCommand.ExecuteAsync(null);
        await Settle(() => !vm.IsBusy);
        var info = PdfPages.GetPageInfos(vm.Session.Bytes!)[0];
        Assert.Equal(region.Width, info.Width, 1.0);
        Assert.Equal(region.Height, info.Height, 1.0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ToolsThatShowNothingOnThePageSayWhatApplyWillDo()
    {
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.Numbered(8), null);
        await Settle(() => vm.PageBitmap != null);

        var delete = Tool<DeleteTool>(vm);
        vm.SetSelection([1, 2, 3, 6], 1);
        Assert.Equal("Apply deletes 4 of 8 pages: 2-4, 7", vm.ApplySummary);
        var pages = delete.Fields.OfType<PagesField>().Single();
        pages.Scope = pages.Scopes[0];
        Assert.Equal(Strings.English["ErrDeleteAll"], vm.ApplySummary); // said before Apply, not after
        pages.Scope = pages.Scopes[2];
        pages.Range = "5-";
        Assert.Equal("Apply deletes 4 of 8 pages: 5-8", vm.ApplySummary);
        pages.Range = "nonsense";
        Assert.Equal(Strings.English["ErrPageRange"], vm.ApplySummary);

        var split = Tool<SplitTool>(vm);
        Assert.Equal("Apply writes 8 files, with the pages: 1; 2; 3; 4; 5; 6; …", vm.ApplySummary);
        Field<NumberField>(split, "SplitPagesPerFile").Value = 3;
        Assert.Equal("Apply writes 3 files, with the pages: 1-3; 4-6; 7-8", vm.ApplySummary);

        Tool<ExtractTool>(vm);
        Assert.Equal("Apply saves 4 pages: 2-4, 7", vm.ApplySummary);
        Tool<RotateTool>(vm);
        Assert.Equal("", vm.ApplySummary); // shown on the page instead
        window.Close();
    }

    [AvaloniaFact]
    public async Task AColourCanBeTakenFromThePage()
    {
        using var red = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Opaque));
        red.Erase(new SKColor(200, 30, 40));
        var pdf = TestPdf.Compose((page, _) => PageDraw.DrawImage(page, red, new RectD(100, 100, 300, 300), jpegQuality: 100));
        var (window, vm) = Start();
        vm.Session.Open(pdf, null);
        vm.Zoom = 0.75;
        var numbers = Tool<PageNumbersTool>(vm);
        await Settle(() => vm.PageBitmap != null);
        var color = Field<ColorField>(numbers, "TextColor");

        color.PickCommand.Execute(null);
        Assert.True(color.IsPicking);
        Assert.Equal(CanvasMode.Pick, vm.CanvasMode);
        Click(window, vm, 200, 200);

        Assert.False(color.IsPicking);
        Assert.Equal(CanvasMode.Select, vm.CanvasMode);
        var picked = color.Argb!.Value;
        Assert.InRange((int)(picked >> 16 & 0xFF), 190, 210);
        Assert.InRange((int)(picked >> 8 & 0xFF), 20, 40);

        // Typed colours are checked, and a swatch is one click.
        color.Value = "not a colour";
        Assert.False(color.IsValid);
        color.UseCommand.Execute(color.Swatches[0]);
        Assert.Equal(0xFF000000, color.Argb);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CompressionAndCleanUpAreShownOnThePageWithWhatTheyGain()
    {
        using var photo = ContentOperationTests.Photo(1654, 2339);
        var (window, vm) = Start();
        vm.Session.Open(TestPdf.FromImage(photo, 300), null);
        vm.Zoom = 0.5;
        Tool<CompressTool>(vm);
        await Settle(() => vm.IsPreview, 15);
        Assert.Matches(@"\d+ KB|\d+\.\d MB", vm.PreviewNote);
        Assert.Contains("→", vm.PreviewNote);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WhatWouldBeRecognisedOnAPageCanBeLookedAtFirst()
    {
        if (!PdfOcr.EngineStatus().Available || TessData.Missing(["en"]).Count > 0) return;
        using var scan = TestPdf.FakeScan(1240, 1754);
        var (window, vm) = Start();
        var pdf = TestPdf.FromImage(scan, 150);
        vm.Session.Open(pdf, null);
        vm.Zoom = 0.5;
        var ocr = Tool<OcrTool>(vm);
        await Settle(() => vm.PageBitmap != null);

        Press(ocr, "OcrShow");
        await Settle(() => !vm.IsBusy && vm.Overlays.Count > 0, 60);
        Assert.True(vm.Overlays.Count >= 15);
        Assert.Same(pdf, vm.Session.Bytes);
        window.Close();
    }
}
