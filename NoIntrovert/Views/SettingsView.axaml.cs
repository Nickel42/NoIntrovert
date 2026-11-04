using Avalonia;
using Avalonia.Controls;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

public partial class SettingsView : UserControl
{
    /// <summary>Below this width the cards stack in one column.</summary>
    private const double TwoColumnWidth = 780;

    private bool? _twoColumns;

    public SettingsView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Arrange(e.NewSize.Width >= TwoColumnWidth);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Choosing Replace the originals as the default asks once, like the run bar.
        if (DataContext is SettingsViewModel vm)
            vm.Output.ConfirmReplace = () => ConfirmDialog.ConfirmReplaceOriginalsAsync(this, vm.Output.UseRecycleBin);
    }

    /// <summary>Puts each two-column grid's second column beside the first, or under it.</summary>
    private void Arrange(bool twoColumns)
    {
        if (_twoColumns == twoColumns) return;
        _twoColumns = twoColumns;
        foreach (var grid in new[] { MainColumns, AdvancedColumns })
        {
            grid.ColumnDefinitions[1].Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            grid.ColumnSpacing = twoColumns ? 12 : 0;
            var second = grid.Children[1];
            Grid.SetColumn(second, twoColumns ? 1 : 0);
            Grid.SetRow(second, twoColumns ? 0 : 1);
        }
    }
}
