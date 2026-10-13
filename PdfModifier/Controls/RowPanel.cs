using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using PdfModifier.ViewModels;

namespace PdfModifier.Controls;

/// <summary>
/// Lays out the fields of a <see cref="RowField"/> on one line. Small switches (bold, italic) take just the room
/// they need; the other fields share what is left equally, so a number box next to two switches stays readable and
/// buttons in a row come out the same width.
/// </summary>
public class RowPanel : Panel
{
    private const double Gap = 6, SwitchWidth = 44;

    private static bool IsSwitch(Control child) => child is ContentPresenter { Content: ToggleField };

    private static bool IsButton(Control child) => child is ContentPresenter { Content: ActionField };

    /// <summary>A field that is hidden takes no room: its neighbours share the line among themselves.</summary>
    private List<Control> Shown() =>
        Children.Where(c => c.IsVisible && c is not ContentPresenter { Content: Field { IsVisible: false } }).ToList();

    private static double ShareOf(IReadOnlyList<Control> shown, double width)
    {
        var switches = shown.Count(IsSwitch);
        var others = shown.Count - switches;
        var free = width - switches * SwitchWidth - Math.Max(0, shown.Count - 1) * Gap;
        return others == 0 ? 0 : Math.Max(0, free / others);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Shown();
        var share = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : ShareOf(shown, availableSize.Width);
        double width = 0, height = 0;
        // The hidden ones are measured too, though they take no room, and with room to grow into: a child that
        // was never measured, or was told there is no room at all, does not say so when it is shown later, and
        // the row would stay as it was.
        foreach (var hidden in Children.Except(shown)) hidden.Measure(availableSize);
        foreach (var child in shown)
        {
            child.Measure(new Size(IsSwitch(child) ? SwitchWidth : share, availableSize.Height));
            width += IsSwitch(child) ? SwitchWidth : child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        width += Math.Max(0, shown.Count - 1) * Gap;
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = Shown();
        var share = ShareOf(shown, finalSize.Width);
        foreach (var hidden in Children.Except(shown)) hidden.Arrange(default);
        // Buttons side by side are as high as the highest of them: one whose label takes two lines does not
        // stand taller than its neighbour.
        var buttons = shown.Where(IsButton).Select(child => child.DesiredSize.Height).DefaultIfEmpty().Max();
        var scale = LayoutHelper.GetLayoutScale(this);
        double x = 0;
        foreach (var child in shown)
        {
            var width = IsSwitch(child) ? SwitchWidth : share;
            // On whole pixels: a share of the row is seldom one, each field would round it up for itself, and the
            // last of them would end a pixel outside the row.
            double left = LayoutHelper.RoundLayoutValue(x, scale), right = LayoutHelper.RoundLayoutValue(x + width, scale);
            var height = IsButton(child) ? buttons : child.DesiredSize.Height;
            // Lined up along the bottom: a labelled box is taller than a bare button and their controls should meet.
            child.Arrange(new Rect(left, finalSize.Height - height, right - left, height));
            x += width + Gap;
        }

        return finalSize;
    }
}
