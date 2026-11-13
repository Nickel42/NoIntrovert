using Avalonia.Controls;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

public partial class CleanupView : UserControl
{
    public CleanupView()
    {
        InitializeComponent();
        // Tick all: a click ticks every step that can run, or, when all are ticked, none.
        TickAll.Click += (_, _) =>
        {
            if (DataContext is CleanupViewModel vm) vm.AllEnabled = vm.AllEnabled != true;
        };
    }
}
