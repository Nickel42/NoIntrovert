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
        // The page reads the list's own SelectedItems. (A {Binding} of SelectedItems left the page's
        // copy null, so the summary and the panel said "0 selected" with rows ticked.)
        DataContextChanged += (_, _) => ShareSelection();
        List.SelectionChanged += (_, _) =>
        {
            ShareSelection();
            (DataContext as BatchTabViewModel)?.NotifySelectionChanged();
        };
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

    // ---- Keyboard shortcuts, while this page shows: Ctrl+O adds files, Ctrl+F finds,
    //      Ctrl+Enter starts, Escape stops a folder scan or clears the filter ----

    private TopLevel? _top;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Bubble);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnShortcut);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not BatchTabViewModel vm || !IsEffectivelyVisible) return;
        e.Handled = Shortcut(vm, e.Key, e.KeyModifiers);
    }

    /// <summary>Runs the shortcut for <paramref name="key"/>; false when it isn't one (or can't run now).</summary>
    public bool Shortcut(BatchTabViewModel vm, Key key, KeyModifiers modifiers)
    {
        var ctrl = modifiers == KeyModifiers.Control || modifiers == KeyModifiers.Meta;
        switch (key)
        {
            case Key.O when ctrl:
                if (!vm.IsIdle) return false;
                vm.AddFilesCommand.Execute(null);
                return true;
            case Key.F when ctrl:
                FilterBox.Focus();
                FilterBox.SelectAll();
                return true;
            case Key.Enter when ctrl:
                if (!vm.StartCommand.CanExecute(null)) return false;
                vm.StartCommand.Execute(null);
                return true;
            case Key.Escape when modifiers == KeyModifiers.None:
                if (vm.IsScanning) { vm.CancelScanCommand.Execute(null); return true; }
                if (FilterBox.IsFocused && !string.IsNullOrEmpty(FilterBox.Text))
                {
                    FilterBox.Text = "";
                    vm.Filter = "";
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private void ShareSelection()
    {
        if (DataContext is BatchTabViewModel vm && !ReferenceEquals(vm.SelectedItems, List.SelectedItems))
        {
            vm.SelectedItems = List.SelectedItems;
            vm.NotifySelectionChanged();
        }
    }

    private void OnRowFocused(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is not BatchTabViewModel vm || e.Source is not Visual source) return;
        var container = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (container?.DataContext is not JobItemViewModel row) return;
        vm.FocusedItem = row;
        // A click selects through the list itself (Ctrl-click toggles, so selecting here first would undo it);
        // the keyboard moves focus without selecting, so select here.
        if (e.NavigationMethod != NavigationMethod.Pointer && !container.IsSelected) container.IsSelected = true;
    }
}
