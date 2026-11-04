using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

/// <summary>
/// The job list. Selection (the checkbox column, Ctrl/Shift-click, Ctrl+A) and
/// focus are separate: the focused row is the last one clicked or moved to with
/// the keyboard, and it's always selected too.
/// </summary>
public partial class JobListPanel : UserControl
{
    public JobListPanel()
    {
        InitializeComponent();
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && DataContext is BatchTabViewModel vm)
                vm.RemoveSelectedCommand.Execute(null);
        };
        List.SelectionChanged += (_, _) => (DataContext as BatchTabViewModel)?.NotifySelectionChanged();
        // Any focus inside a row (the row itself, its Preset dropdown) makes it the focused row.
        List.AddHandler(GotFocusEvent, OnRowFocused, RoutingStrategies.Bubble, handledEventsToo: true);
        SelectAll.Click += (_, _) =>
        {
            if (DataContext is not BatchTabViewModel vm) return;
            if (vm.AllSelected == true) List.UnselectAll();
            else List.SelectAll();
            vm.NotifySelectionChanged();
        };
    }

    private void OnRowFocused(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is not BatchTabViewModel vm || e.Source is not Visual source) return;
        var container = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (container?.DataContext is not JobItemViewModel row) return;
        vm.FocusedItem = row;
        if (!container.IsSelected) container.IsSelected = true;
    }
}
