using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;

namespace NoIntrovert.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        GameList.LayoutUpdated += (_, _) => AlignHeadings();
    }

    /// <summary>
    /// Gives the column headings the rows' left and right edges. The rows sit inside the list's border, padding and,
    /// once it scrolls, beside its scrollbar, none of which the headings have; matching the first row's actual
    /// edges keeps every heading over its column whatever the theme or the scrollbar does.
    /// </summary>
    private void AlignHeadings()
    {
        if (Headings.GetVisualParent() is not Visual parent) return;
        // The first row's column grid (the one holding its Status cell), or with no rows the area they'd fill, less a row's padding.
        // (The list keeps spare, empty row containers for recycling, so the first container isn't always a row.)
        Visual? content = GameList.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Classes.Contains("col-status") && c.IsEffectivelyVisible)?.GetVisualParent();
        double inset = 0;
        if (content is null)
        {
            content = GameList.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
            inset = 12; // a row's own padding (ListBoxItem, in LibraryView.axaml)
        }
        if (content is null || content.TranslatePoint(new Point(0, 0), parent) is not { } topLeft) return;
        var left = topLeft.X + inset;
        var right = parent.Bounds.Width - (topLeft.X + content.Bounds.Width - inset);
        var margin = Headings.Margin;
        if (Math.Abs(margin.Left - left) > 0.5 || Math.Abs(margin.Right - right) > 0.5)
            Headings.Margin = new Thickness(left, margin.Top, right, margin.Bottom);
    }
}
