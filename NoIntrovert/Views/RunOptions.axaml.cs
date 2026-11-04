using Avalonia.Controls;
using NoIntrovert.Core.Jobs;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

/// <summary>The run bar's Save to row; it also asks, once, before Replace the originals is first chosen.</summary>
public partial class RunOptions : UserControl
{
    public RunOptions() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is BatchTabViewModel vm) vm.ConfirmReplace = () => ConfirmReplaceAsync(vm);
    }

    private async Task<bool> ConfirmReplaceAsync(BatchTabViewModel vm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        var fate = vm.Settings.Output.UseRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently";
        var dialog = new ConfirmDialog("Replace the originals?",
            $"Each original is {fate} once its new file verifies. If verification fails, the original stays and the row shows why. " +
            "You're asked only this once.",
            "Replace the originals", "Cancel");
        return await dialog.ShowDialog<bool>(owner);
    }
}
