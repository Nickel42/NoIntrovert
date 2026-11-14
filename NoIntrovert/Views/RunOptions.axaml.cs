using Avalonia.Controls;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

/// <summary>The run bar's Save to row; it also asks, as each run with Replace the originals starts, how many originals go.</summary>
public partial class RunOptions : UserControl
{
    public RunOptions() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is BatchTabViewModel vm)
            vm.ConfirmReplace = count => ConfirmDialog.ConfirmReplaceRunAsync(this, count, vm.Settings.Output.UseRecycleBin);
    }
}
