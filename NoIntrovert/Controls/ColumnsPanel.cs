using Avalonia;
using Avalonia.Controls;

namespace NoIntrovert.Controls;

/// <summary>
/// Lays its children out left to right in <see cref="Columns"/> equal columns,
/// like a UniformGrid, but each row is only as tall as its own tallest child:
/// one entry with a second line doesn't make every row taller.
/// </summary>
public sealed class ColumnsPanel : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<ColumnsPanel, int>(nameof(Columns), 2, validate: v => v > 0);

    static ColumnsPanel() => AffectsMeasure<ColumnsPanel>(ColumnsProperty);

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private double ColumnWidth(double width) => double.IsInfinity(width) ? double.PositiveInfinity : Math.Max(0, width / Columns);

    protected override Size MeasureOverride(Size availableSize)
    {
        var column = ColumnWidth(availableSize.Width);
        double height = 0, widest = 0, row = 0;
        var visible = Children.Where(c => c.IsVisible).ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(column, double.PositiveInfinity));
            widest = Math.Max(widest, visible[i].DesiredSize.Width);
            row = Math.Max(row, visible[i].DesiredSize.Height);
            if (i % Columns == Columns - 1 || i == visible.Count - 1)
            {
                height += row;
                row = 0;
            }
        }
        var width = double.IsInfinity(column) ? widest * Math.Min(Columns, visible.Count) : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var column = finalSize.Width / Columns;
        var visible = Children.Where(c => c.IsVisible).ToList();
        double y = 0;
        for (var start = 0; start < visible.Count; start += Columns)
        {
            var cells = visible.Skip(start).Take(Columns).ToList();
            var row = cells.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < cells.Count; i++) cells[i].Arrange(new Rect(i * column, y, column, row));
            y += row;
        }
        return finalSize;
    }
}
