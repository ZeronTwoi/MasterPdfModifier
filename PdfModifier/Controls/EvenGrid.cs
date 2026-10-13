using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PdfModifier.Controls;

/// <summary>
/// Cells of one size, row after row, with their edges on whole pixels. The stock uniform grid gives every cell the
/// same fraction of the width and each cell then rounds that up for itself: three cells in 317 pixels come to 318,
/// and the last one ends outside the grid, where its rounded corners are shaved off. Here a cell ends where the
/// next begins and the last one where the grid does, at every scaling of the screen.
/// </summary>
public class EvenGrid : Panel
{
    public static readonly StyledProperty<int> RowsProperty = AvaloniaProperty.Register<EvenGrid, int>(nameof(Rows));
    public static readonly StyledProperty<int> ColumnsProperty = AvaloniaProperty.Register<EvenGrid, int>(nameof(Columns));

    static EvenGrid() => AffectsMeasure<EvenGrid>(RowsProperty, ColumnsProperty);

    /// <summary>How many rows there are; 0 leaves it to the number of cells and of columns.</summary>
    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>How many cells stand in a row; 0 leaves it to the number of cells and of rows.</summary>
    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private (int Rows, int Columns, List<Control> Cells) Cells()
    {
        var cells = Children.Where(child => child.IsVisible).ToList();
        int rows = Rows, columns = Columns;
        if (rows <= 0 && columns <= 0) columns = (int)Math.Ceiling(Math.Sqrt(cells.Count));
        if (columns <= 0) columns = (int)Math.Ceiling(cells.Count / (double)rows);
        columns = Math.Max(1, columns);
        if (rows <= 0) rows = (int)Math.Ceiling(cells.Count / (double)columns);
        return (Math.Max(1, rows), columns, cells);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (rows, columns, cells) = Cells();
        var room = new Size(availableSize.Width / columns, availableSize.Height / rows);
        double width = 0, height = 0;
        foreach (var cell in cells)
        {
            cell.Measure(room);
            width = Math.Max(width, cell.DesiredSize.Width);
            height = Math.Max(height, cell.DesiredSize.Height);
        }

        return new Size(width * columns, height * rows);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (rows, columns, cells) = Cells();
        var scale = LayoutHelper.GetLayoutScale(this);
        double Edge(int index, int of, double length) => LayoutHelper.RoundLayoutValue(length * index / of, scale);
        for (var i = 0; i < cells.Count; i++)
        {
            int column = i % columns, row = i / columns;
            double left = Edge(column, columns, finalSize.Width), top = Edge(row, rows, finalSize.Height);
            cells[i].Arrange(new Rect(left, top, Edge(column + 1, columns, finalSize.Width) - left, Edge(row + 1, rows, finalSize.Height) - top));
        }

        return finalSize;
    }
}
