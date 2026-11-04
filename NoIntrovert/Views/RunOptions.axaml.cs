using Avalonia.Controls;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

/// <summary>The run bar's Save to row; it also asks, once, before Replace the originals is first chosen.</summary>
public partial class RunOptions : UserControl
{
    public RunOptions() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is BatchTabViewModel vm)
            vm.ConfirmReplace = () => ConfirmDialog.ConfirmReplaceOriginalsAsync(this, vm.Settings.Output.UseRecycleBin);
    }
}
