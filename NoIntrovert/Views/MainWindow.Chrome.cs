using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

/// <summary>
/// The window's platform conventions: Windows 11 caption buttons, the page
/// shortcuts (Ctrl+1…4, Ctrl+,) and the Keyboard shortcuts sheet (F1, Ctrl+/).
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Windows draws its own kind of caption buttons: 46×32 rectangles flush with the corner
    /// (MainWindow.axaml's .wincaption styles). macOS and Linux keep the round ones.
    /// </summary>
    public static bool UsesWindowsCaption => OperatingSystem.IsWindows();

    private void SetUpConventions()
    {
        AddHandler(KeyDownEvent, OnWindowShortcut, RoutingStrategies.Bubble);
        if (!UsesWindowsCaption) return;
        Classes.Add("wincaption");
        TitleBarControlSize = 10;
    }

    /// <summary>
    /// SukiWindow's title bar sets its buttons 7 px apart inside a 12×9 px margin, and sits the
    /// minimize glyph near the bottom of a small round button. On Windows the buttons touch and
    /// reach the window's top and right edges, and every glyph is centred. (Snap Layouts, the
    /// maximize button's hover flyout, is SukiWindow's own: it answers WM_NCHITTEST with
    /// HTMAXBUTTON over that button, whatever its size.)
    /// </summary>
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (!UsesWindowsCaption) return;
        if (e.NameScope.Find<Button>("PART_CloseButton")?.GetVisualParent() is StackPanel buttons)
        {
            buttons.Spacing = 0;
            buttons.VerticalAlignment = VerticalAlignment.Top;
            if (buttons.GetVisualParent() is DockPanel bar) bar.Margin = new Thickness(12, 0, 0, 0);
        }
        if (e.NameScope.Find<Button>("PART_MinimizeButton") is { } minimize)
        {
            minimize.VerticalContentAlignment = VerticalAlignment.Center;
            if (minimize.Content is Control glyph)
            {
                glyph.Margin = default;
                glyph.VerticalAlignment = VerticalAlignment.Center;
            }
        }
    }

    private void OnWindowShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Handled || Vm is not { } vm) return;
        e.Handled = WindowShortcut(vm, e.Key, e.KeyModifiers);
    }

    /// <summary>Runs the window's shortcut for <paramref name="key"/>: a page, or the shortcuts sheet. False when it isn't one.</summary>
    public bool WindowShortcut(MainViewModel vm, Key key, KeyModifiers modifiers)
    {
        if (PageShortcuts.PageFor(key, modifiers) is { } page) return vm.Show(page);
        if (!PageShortcuts.OpensSheet(key, modifiers)) return false;
        _ = ShowShortcutsAsync();
        return true;
    }

    private ShortcutsDialog? _shortcuts;

    /// <summary>The Keyboard shortcuts sheet; a second F1 while it's open does nothing.</summary>
    public async Task ShowShortcutsAsync()
    {
        if (_shortcuts is not null) return;
        _shortcuts = new ShortcutsDialog();
        try { await _shortcuts.ShowDialog(this); }
        finally { _shortcuts = null; }
    }

    /// <summary>The sheet while it's open (for the tests).</summary>
    public ShortcutsDialog? OpenShortcuts => _shortcuts;
}
