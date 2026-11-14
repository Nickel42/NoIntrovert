using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NoIntrovert.Core.Presets;
using NoIntrovert.Services;
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
        // Right-click, Shift+F10 or the Menu key on a row: the row menu.
        List.ContextRequested += OnRowContextRequested;
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
        // The window takes the drop (MainWindow.axaml.cs); the empty list's drop zone only lights up meanwhile.
        _top?.AddHandler(DragDrop.DragEnterEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        _top?.AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        _top?.AddHandler(DragDrop.DragLeaveEvent, OnDragEnded, RoutingStrategies.Bubble, handledEventsToo: true);
        _top?.AddHandler(DragDrop.DropEvent, OnDragEnded, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnShortcut);
        _top?.RemoveHandler(DragDrop.DragEnterEvent, OnDragOver);
        _top?.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        _top?.RemoveHandler(DragDrop.DragLeaveEvent, OnDragEnded);
        _top?.RemoveHandler(DragDrop.DropEvent, OnDragEnded);
        if (DataContext is BatchTabViewModel vm) vm.IsDragOver = false;
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    // ---- Drag and drop: files dragged over the window light up the empty list's drop zone ----

    private void OnDragOver(object? sender, DragEventArgs e) =>
        SetDragOver(IsEffectivelyVisible && e.DataTransfer.Contains(DataFormat.File));

    /// <summary>
    /// The drag left the window or dropped. Moving between controls raises a leave and then an enter, so the
    /// zone stays lit; only leaving the window (or dropping) leaves it off.
    /// </summary>
    private void OnDragEnded(object? sender, DragEventArgs e) => SetDragOver(false);

    /// <summary>Lights the drop zone up, or not (tests call it as a drag would).</summary>
    public void SetDragOver(bool over)
    {
        if (DataContext is BatchTabViewModel vm && vm.IsDragOver != over) vm.IsDragOver = over;
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

    // ---- Row menu ----

    /// <summary>
    /// The row menu for <paramref name="container"/>'s row. As in Explorer, a row that isn't selected
    /// becomes the only selected row first; on a selected row the selection stays, and the menu acts on all of it.
    /// </summary>
    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not BatchTabViewModel vm || e.Source is not Visual source) return;
        var container = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (container?.DataContext is not JobItemViewModel row) return;
        e.Handled = true;
        ShowRowMenu(vm, container, row);
    }

    /// <summary>Selects and focuses the row as a right-click does, then opens its menu.</summary>
    private void ShowRowMenu(BatchTabViewModel vm, ListBoxItem container, JobItemViewModel row)
    {
        SelectForMenu(vm, container, row);
        new ContextMenu { ItemsSource = RowMenuItems(vm, row) }.Open(container);
    }

    /// <summary>What a right-click does to the selection: an unselected row becomes the only selected one; a selected row keeps the selection. Either way it's focused.</summary>
    public void SelectForMenu(BatchTabViewModel vm, ListBoxItem container, JobItemViewModel row)
    {
        if (!container.IsSelected)
        {
            List.SelectedItems!.Clear();
            List.SelectedItems.Add(row);
        }
        vm.FocusedItem = row;
        container.Focus(NavigationMethod.Pointer);
    }

    /// <summary>The row menu's items for <paramref name="row"/> on <paramref name="vm"/>'s page, acting on its selected rows.</summary>
    public static List<Control> RowMenuItems(BatchTabViewModel vm, JobItemViewModel row)
    {
        var count = vm.SelectedCount;
        var items = new List<Control>
        {
            Item(FileReveal.MenuLabel, vm.CanRevealSelection, () => _ = vm.RevealSelectedAsync(), "reveal"),
            Item("Copy path", vm.CanRevealSelection, () => _ = vm.CopySelectedPathsAsync(), "copypath"),
        };
        if (vm.OffersVerify || vm.OffersPresets(row)) items.Add(new Separator());
        if (vm.OffersVerify)
            items.Add(Item(count == 1 ? "Verify this file" : $"Verify {count:N0} files", vm.CanVerifySelection, () => _ = vm.VerifySelectedAsync(), "verify"));
        if (vm.OffersPresets(row))
        {
            var presets = new MenuItem { Header = "Preset", IsEnabled = vm.CanChoosePreset, Name = "presets" };
            foreach (var option in row.PresetOptions)
            {
                if (option.IsDivider) { presets.Items.Add(new Separator()); continue; }
                if (option.IsGroup) { presets.Items.Add(new MenuItem { Header = option.Label, IsEnabled = false }); continue; }
                var choice = Item(option.Label, option.IsEnabled, () => vm.ApplyPresetToSelected(option.Preset!.Id));
                choice.ToggleType = MenuItemToggleType.Radio;
                choice.IsChecked = option == row.PresetChoice;
                if (option.Hint.Length > 0) ToolTip.SetTip(choice, option.Hint);
                presets.Items.Add(choice);
            }
            items.Add(presets);
        }
        items.Add(new Separator());
        var remove = Item("Remove from list", vm.CanRemoveSelection, () => vm.RemoveSelectedCommand.Execute(null), "remove");
        remove.InputGesture = new KeyGesture(Key.Delete);
        items.Add(remove);
        return items;

        static MenuItem Item(string header, bool enabled, Action act, string? name = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, Name = name };
            item.Click += (_, _) => act();
            return item;
        }
    }
}
