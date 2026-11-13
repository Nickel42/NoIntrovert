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

    /// <summary>The Status cell last lined up with; every row's columns have the same edges, so any shown row will do.</summary>
    private Control? _statusCell;

    /// <summary>The list's scrolling area, found once.</summary>
    private ScrollContentPresenter? _presenter;

    /// <summary>
    /// Gives the column headings the rows' left and right edges. The rows sit inside the list's border, padding and,
    /// once it scrolls, beside its scrollbar, none of which the headings have; matching a row's actual
    /// edges keeps every heading over its column whatever the theme or the scrollbar does.
    /// </summary>
    /// <remarks>
    /// This runs after every layout pass of the window, so it doesn't walk the list's whole visual tree: it keeps
    /// the Status cell it found while that stays shown, and otherwise looks only inside the list's realised rows.
    /// </remarks>
    private void AlignHeadings()
    {
        if (Headings.GetVisualParent() is not Visual parent) return;
        // A row's column grid (the one holding its Status cell), or with no rows the area they'd fill, less a row's padding.
        // (The list keeps spare, empty row containers for recycling, so not every container is a row.)
        Visual? content = RowGrid();
        double inset = 0;
        if (content is null)
        {
            if (_presenter is null || _presenter.FindAncestorOfType<ListBox>() != GameList)
                _presenter = GameList.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
            content = _presenter;
            inset = 12; // a row's own padding (ListBoxItem, in LibraryView.axaml)
        }
        if (content is null || content.TranslatePoint(new Point(0, 0), parent) is not { } topLeft) return;
        var left = topLeft.X + inset;
        var right = parent.Bounds.Width - (topLeft.X + content.Bounds.Width - inset);
        var margin = Headings.Margin;
        if (Math.Abs(margin.Left - left) > 0.5 || Math.Abs(margin.Right - right) > 0.5)
            Headings.Margin = new Thickness(left, margin.Top, right, margin.Bottom);
    }

    /// <summary>A shown row's column grid (the parent of its Status cell), or null with no rows.</summary>
    private Visual? RowGrid()
    {
        if (_statusCell is not { IsEffectivelyVisible: true } || _statusCell.FindAncestorOfType<ListBox>() != GameList)
        {
            _statusCell = null;
            foreach (var container in GameList.GetRealizedContainers())
            {
                if (!container.IsEffectivelyVisible) continue;
                _statusCell = container.GetVisualDescendants().OfType<Control>()
                    .FirstOrDefault(c => c.Classes.Contains("col-status") && c.IsEffectivelyVisible);
                if (_statusCell is not null) break;
            }
        }
        return _statusCell?.GetVisualParent();
    }
}
