using Avalonia.Controls;
using Avalonia.Input;

namespace NoIntrovert.Views;

public partial class JobListPanel : UserControl
{
    public JobListPanel()
    {
        InitializeComponent();
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && DataContext is ViewModels.BatchTabViewModel vm)
                vm.RemoveSelectedCommand.Execute(null);
        };
    }
}
