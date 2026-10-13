using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfModifier.Controls;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using PdfModifier.Views;
using SkiaSharp;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>
/// Nothing in the window is given less room than it draws in. What sticks out of the place it was given is cut off
/// there or drawn over by what comes after it: a slider with half a handle, a label that runs out of its button, a
/// name that ends in three dots. Looked at for every tool, in every language, and at the scalings screens are set to.
/// </summary>
public class LayoutUiTests
{
    private static void Laid()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static string Name(Visual visual)
    {
        var parts = new List<string>();
        for (Visual? at = visual; at != null && parts.Count < 6; at = at.GetVisualParent())
            parts.Add(at.GetType().Name + (at is Control { Name: { Length: > 0 } name } ? "#" + name : ""));
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    /// <summary>What a visual belongs to, for saying where something is wrong.</summary>
    private static string Of(Visual visual)
    {
        for (Visual? at = visual; at != null; at = at.GetVisualParent())
            switch ((at as StyledElement)?.DataContext)
            {
                case Field field: return field.GetType().Name + " \"" + field.Label + "\"";
                case Choice choice: return "choice \"" + choice.Label + "\"";
                case Tool tool: return "tool \"" + tool.Title + "\"";
            }

        return "window";
    }

    /// <summary>Placed outside its parent on purpose: a mark that sits on the corner of what it belongs to.</summary>
    private static bool Hangs(Visual visual) =>
        visual is Layoutable { Margin: var margin } && (margin.Left < 0 || margin.Top < 0 || margin.Right < 0 || margin.Bottom < 0);

    /// <summary>
    /// The inside of a stock scroll bar, of a box with arrows for a number, of a list that opens over the window:
    /// laid out by rules of their own, which are not what is looked at here.
    /// </summary>
    private static bool Stock(Visual visual) =>
        visual.GetSelfAndVisualAncestors().Any(at => at is ScrollBar or ButtonSpinner or Popup or AdornerLayer or PathIcon);

    private static IEnumerable<Visual> Seen(Visual root) =>
        root.GetVisualDescendants().Where(visual => visual.IsEffectivelyVisible && visual.Bounds is { Width: > 0, Height: > 0 } && !Stock(visual));

    /// <summary>Everything that reaches beyond the bounds of what holds it, by more than the pixel rounding costs.</summary>
    internal static IEnumerable<string> StickingOut(Visual root, double scaling)
    {
        foreach (var visual in Seen(root))
        {
            if (visual.GetVisualParent() is not { } parent) continue;
            // What scrolls is larger than its window onto it, what is scaled to fit is larger than it is shown, and
            // what lies on a canvas lies where it is put.
            if (parent is ScrollContentPresenter or Canvas or Viewbox || Hangs(visual)) continue;
            // The panels of this app put their cells on whole pixels and are held to it; elsewhere a pixel of the
            // screen is what rounding may cost.
            var allowed = parent is EvenGrid or RowPanel ? 0.01 : 1 / scaling + 0.05;
            var bounds = visual.Bounds;
            var over = Math.Max(Math.Max(-bounds.Left, -bounds.Top),
                Math.Max(bounds.Right - parent.Bounds.Width, bounds.Bottom - parent.Bounds.Height));
            if (over > allowed) yield return $"{Of(visual)}: {Name(visual)} is {over:0.##} outside ({bounds} in {parent.Bounds.Size})";
        }
    }

    /// <summary>Text that does not fit the box it is written in and is cut off at its edge without a word.</summary>
    internal static IEnumerable<string> TextCutOff(Visual root) =>
        from text in Seen(root).OfType<TextBlock>()
        let layout = text.TextLayout
        let room = text.Bounds.Size.Deflate(text.Padding)
        where layout.Width > room.Width + 1 || layout.Height > room.Height + 1
        select $"{Of(text)}: \"{text.Text}\" needs {layout.Width:0.#} x {layout.Height:0.#}, has {room} in {Name(text)}";

    /// <summary>Controls that are worked with a click and lie on top of one another.</summary>
    internal static IEnumerable<string> Overlapping(Visual root)
    {
        var parts = Seen(root)
            .Where(visual => visual is Button or TextBox or CheckBox or Thumb or ComboBox or ListBoxItem or Slider)
            .Select(visual => (Visual: visual, Rect: visual.GetTransformedBounds()?.Clip ?? default))
            .Where(part => part.Rect is { Width: > 0, Height: > 0 })
            .ToList();
        for (var a = 0; a < parts.Count; a++)
        for (var b = a + 1; b < parts.Count; b++)
        {
            var (first, second) = (parts[a], parts[b]);
            if (first.Visual.IsVisualAncestorOf(second.Visual) || second.Visual.IsVisualAncestorOf(first.Visual)) continue;
            var shared = first.Rect.Intersect(second.Rect);
            if (shared is { Width: > 1, Height: > 1 })
                yield return $"{Of(first.Visual)}: {Name(first.Visual)} and {Name(second.Visual)} share {shared}";
        }
    }

    /// <summary>
    /// A shadow, and the ring that marks what is selected, lie around their owner. One that clips to its own bounds
    /// never shows them, and one that stands whole inside something that clips shows them cut to a hard edge.
    /// </summary>
    internal static IEnumerable<string> ShadowsCutOff(Visual root)
    {
        foreach (var border in Seen(root).OfType<Border>())
        {
            double left = 0, top = 0, right = 0, bottom = 0;
            for (var i = 0; i < border.BoxShadow.Count; i++)
            {
                var shadow = border.BoxShadow[i];
                if (shadow.IsInset || shadow.Color.A == 0) continue;
                var reach = shadow.Blur + shadow.Spread;
                (left, right) = (Math.Max(left, reach - shadow.OffsetX), Math.Max(right, reach + shadow.OffsetX));
                (top, bottom) = (Math.Max(top, reach - shadow.OffsetY), Math.Max(bottom, reach + shadow.OffsetY));
            }

            if (left + top + right + bottom == 0) continue;
            if (border.ClipToBounds) yield return $"{Of(border)}: {Name(border)} clips the shadow it casts";
            var own = new Rect(border.Bounds.Size);
            for (var at = border.GetVisualParent(); at != null; at = at.GetVisualParent())
            {
                if (!at.ClipToBounds || border.TransformToVisual(at) is not { } matrix) continue;
                var room = new Rect(at.Bounds.Size).Inflate(0.5);
                // Scrolled half out of view, it is its owner that is cut, and the shadow with it.
                if (!room.Contains(own.TransformToAABB(matrix))) break;
                if (room.Contains(own.Inflate(new Thickness(left, top, right, bottom)).TransformToAABB(matrix))) continue;
                yield return $"{Of(border)}: the shadow of {Name(border)} is cut off by {Name(at)}";
                break;
            }
        }
    }

    /// <summary>A slider is as high as the box beside it and its handle lies within it.</summary>
    internal static IEnumerable<string> SlidersCutOff(Visual root, double scaling)
    {
        foreach (var slider in Seen(root).OfType<Slider>())
        {
            var handle = slider.GetVisualDescendants().OfType<Thumb>().First();
            var top = handle.TranslatePoint(new Point(), slider)!.Value.Y;
            if (top < -0.01 || top + handle.Bounds.Height > slider.Bounds.Height + 0.01 || slider.Bounds.Height > 32 + 1 / scaling + 0.05)
                yield return $"{Of(slider)}: slider {slider.Bounds.Size}, handle of {handle.Bounds.Size} at {top:0.##} down";
        }
    }

    /// <summary>The words of the app itself (names of tools, labels, choices, buttons) that are shown with their end left out.</summary>
    internal static IEnumerable<string> LabelsCutShort(Visual root) =>
        from text in Seen(root).OfType<TextBlock>()
        where text.DataContext is ViewModels.Tool or Choice or ViewModels.Field && text.TextTrimming != TextTrimming.None
              && text.TextLayout.TextLines.Any(line => line.HasCollapsed)
        select $"{Of(text)}: \"{text.Text}\" ends in dots in {Name(text)}";

    private static List<string> Faults(MainWindow window, double scaling) =>
        StickingOut(window, scaling).Concat(TextCutOff(window)).Concat(Overlapping(window)).Concat(ShadowsCutOff(window))
            .Concat(SlidersCutOff(window, scaling)).Concat(LabelsCutShort(window)).ToList();

    /// <summary>Tells the window that its screen is set to another scaling, as the 125 % and 150 % of most laptops.</summary>
    private static void Scale(MainWindow window, double scaling)
    {
        if (scaling == 1) return;
        var impl = window.PlatformImpl!;
        var field = impl.GetType().GetField("<RenderScaling>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("The headless window keeps its scaling somewhere else now.");
        field.SetValue(impl, scaling);
        ((Action<double>?)impl.GetType().GetProperty("ScalingChanged")!.GetValue(impl))?.Invoke(scaling);
    }

    private static void Report(string name, IEnumerable<string> found)
    {
        if (ScreenshotFolder is not { Length: > 0 }) return;
        Directory.CreateDirectory(ScreenshotFolder);
        File.WriteAllLines(Path.Combine(ScreenshotFolder, name + ".txt"), found);
    }

    [AvaloniaTheory]
    [InlineData("en", 1.0)]
    [InlineData("en", 1.25)]
    [InlineData("en", 1.5)]
    [InlineData("de", 1.25)]
    [InlineData("es", 1.0)]
    [InlineData("fr", 1.75)]
    [InlineData("ja", 1.0)]
    [InlineData("pt", 1.25)]
    [InlineData("ru", 1.0)]
    [InlineData("tr", 1.25)]
    public async Task NothingInAToolPanelIsCutOffOrCovered(string language, double scaling)
    {
        var (window, vm) = Start(language);
        Scale(window, scaling);
        vm.Session.Open(TestPdf.WithText("Invoice 2026-104", "Second page"), null);
        await Settle(() => vm.PageBitmap != null);

        var found = new List<string>();
        var options = window.FindControl<ScrollViewer>("OptionsViewer")!;
        foreach (var tool in vm.Tools)
        {
            vm.SelectedTool = tool;
            Laid();
            found.AddRange(Faults(window, scaling).Select(line => $"[{tool.Id}] {line}"));
            // A tool opens at its head: not pushed up to show a choice further down, with its heading cut off.
            if (options.Offset.Y != 0) found.Add($"[{tool.Id}] the options open scrolled down by {options.Offset.Y:0.#}");
            // And with everything shown that the tool has: what it only shows in some cases is laid out as well.
            foreach (var field in AllFields(tool).Concat(tool.Fields)) field.IsVisible = true;
            Laid();
            found.AddRange(Faults(window, scaling).Select(line => $"[{tool.Id}, all shown] {line}"));
            Capture(window, $"layout-{language}-{scaling}-{tool.Id}");
        }

        found = found.Distinct().ToList();
        Report($"layout-{language}-{scaling}", found);
        window.Close();
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found.Take(40)));
    }

    [AvaloniaTheory]
    [InlineData("de")]
    [InlineData("ru")]
    public async Task TheSmallestWindowShowsItsControlsWhole(string language)
    {
        var (window, vm) = Start(language);
        (window.Width, window.Height) = (window.MinWidth, window.MinHeight);
        vm.Session.Open(TestPdf.WithText("Invoice 2026-104", "Second page"), "A document with a rather long name, 2026.pdf");
        await Settle(() => vm.PageBitmap != null);

        var found = new List<string>();
        foreach (var tool in vm.Tools)
        {
            vm.SelectedTool = tool;
            Laid();
            found.AddRange(Faults(window, 1).Select(line => $"[{tool.Id}] {line}"));
        }

        found = found.Distinct().ToList();
        Report($"layout-small-{language}", found);
        Capture(window, $"layout-small-{language}");
        window.Close();
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found.Take(40)));
    }

    private static SKBitmap Picture(MainWindow window)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var stream = new MemoryStream();
        frame.Save(stream);
        return SKBitmap.Decode(stream.ToArray());
    }

    private static bool Close(SKColor a, Color b) => Math.Abs(a.Red - b.R) + Math.Abs(a.Green - b.G) + Math.Abs(a.Blue - b.B) < 24;

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public async Task TheSelectedPageWearsItsRingAndASliderItsWholeHandle(double scaling)
    {
        var (window, vm) = Start();
        Scale(window, scaling);
        vm.Session.Open(TestPdf.WithText("Invoice 2026-104", "Second page"), null);
        var tool = Tool<CompressTool>(vm);
        await Settle(() => vm.PageBitmap != null && vm.Pages[0].Thumbnail != null);
        using var picture = Picture(window);
        Capture(window, $"layout-ring-{scaling}");
        SKColor At(Visual visual, double x, double y)
        {
            var point = visual.TranslatePoint(new Point(x, y), window)!.Value;
            return picture.GetPixel((int)(point.X * scaling), (int)(point.Y * scaling));
        }

        // The ring lies around the thumbnail of the page that is selected, and around no other.
        Assert.True(window.TryFindResource("SelectedRing", window.ActualThemeVariant, out var ring));
        var colour = ((BoxShadows)ring!)[0].Color;
        var thumbs = window.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("thumb")).ToList();
        Assert.Equal(2, thumbs.Count);
        Assert.True(Close(At(thumbs[0], -1, thumbs[0].Bounds.Height / 2), colour), "no ring left of the selected page");
        Assert.True(Close(At(thumbs[0], thumbs[0].Bounds.Width / 2, thumbs[0].Bounds.Height + 1), colour), "no ring under the selected page");
        Assert.False(Close(At(thumbs[1], -1, thumbs[1].Bounds.Height / 2), colour));

        // The handle of a slider is a full circle: as much of it below its middle as above.
        var slider = window.GetVisualDescendants().OfType<Slider>().First(s => s.IsEffectivelyVisible);
        var handle = slider.GetVisualDescendants().OfType<Thumb>().First();
        Assert.Equal(Field<SliderField>(tool, "CompressDpi").Position, slider.Value);
        var fill = At(handle, handle.Bounds.Width / 2, handle.Bounds.Height / 2);
        int Reach(int direction)
        {
            var reach = 0;
            while (reach < 20 && At(handle, handle.Bounds.Width / 2, handle.Bounds.Height / 2 + direction * (reach + 1) / scaling) == fill) reach++;
            return reach;
        }

        Assert.InRange(Reach(1), 3, 19);
        Assert.InRange(Reach(1) - Reach(-1), -1, 1);
        window.Close();
    }
}
