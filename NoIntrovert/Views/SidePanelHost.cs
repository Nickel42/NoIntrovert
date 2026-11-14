using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NoIntrovert.Views;

/// <summary>
/// A page's layout: the job list (<see cref="Main"/>) and the side panel (<see cref="Panel"/>, the page's
/// <c>ScrollViewer.sidepanel</c>). While the page is at least <see cref="Breakpoint"/> px wide the panel is
/// a column at the right, as it always was. Narrower, the panel leaves the layout so the list takes the
/// width, and a thin <b>Details</b> tab at the right edge (or Ctrl+I) opens the same panel as an overlay
/// from the right, about 340 px wide, with ✕; Escape or a click outside closes it. The panel is the same
/// controls in both modes: it is moved and restyled, never built twice.
/// </summary>
/// <remarks>
/// The breakpoint follows the page's own width (<see cref="Responsive"/>), not the window's: the rail
/// shrinks to icons below a 1100 px window, which gives the page about 126 px back. With the full rail a
/// 1100 px window leaves the page 886 px, so a breakpoint at or under that never flips back and forth as
/// the rail changes: 880 px collapses the panel below a window of about 968 px (with the icon rail),
/// and the list never gets less than about 570 px while the panel is a column.
/// </remarks>
public sealed class SidePanelHost : Decorator
{
    /// <summary>The page width under which the panel becomes an overlay (see the remarks).</summary>
    public const double Breakpoint = 880;

    /// <summary>The overlay's width.</summary>
    public const double FlyoutWidth = 340;

    public static readonly StyledProperty<Control?> MainProperty =
        AvaloniaProperty.Register<SidePanelHost, Control?>(nameof(Main));

    public static readonly StyledProperty<Control?> PanelProperty =
        AvaloniaProperty.Register<SidePanelHost, Control?>(nameof(Panel));

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<SidePanelHost, bool>(nameof(IsOpen));

    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
    private readonly Border _scrim = new() { Classes = { "sidepanelscrim" }, IsVisible = false, ZIndex = 1 };
    private readonly Border _flyout = new() { Classes = { "sidepanelflyout" } };
    private readonly DockPanel _flyoutBody = new();
    private readonly Grid _flyoutHeader = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 0, 0, 8), IsVisible = false };
    private TopLevel? _topLevel;

    public SidePanelHost()
    {
        var mac = OperatingSystem.IsMacOS();
        DetailsButton = new Button
        {
            Classes = { "tool", "detailstab" },
            Content = new VerticalLabel { Text = "Details" },
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(7, 12),
            IsVisible = false,
        };
        ToolTip.SetTip(DetailsButton, $"Show details for the selected file ({(mac ? "⌘I" : "Ctrl+I")})");
        Avalonia.Automation.AutomationProperties.SetName(DetailsButton, "Details");
        DetailsButton.Click += (_, _) => IsOpen = !IsOpen;
        Grid.SetColumn(DetailsButton, 1);

        CloseButton = new Button { Classes = { "tool" }, Content = "✕", Padding = new Thickness(10, 6), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(CloseButton, "Close (Esc)");
        Avalonia.Automation.AutomationProperties.SetName(CloseButton, "Close details");
        CloseButton.Click += (_, _) => IsOpen = false;
        Grid.SetColumn(CloseButton, 1);
        _flyoutHeader.Children.Add(new TextBlock { Text = "DETAILS", Classes = { "section" }, Margin = default, VerticalAlignment = VerticalAlignment.Center });
        _flyoutHeader.Children.Add(CloseButton);

        DockPanel.SetDock(_flyoutHeader, Dock.Top);
        _flyoutBody.Children.Add(_flyoutHeader);
        _flyout.Child = _flyoutBody;

        Grid.SetColumnSpan(_scrim, 2);
        _scrim.PointerPressed += (_, e) =>
        {
            IsOpen = false;
            e.Handled = true;
        };

        _grid.Classes.Add("sidepanelhost");
        _grid.Children.Add(_scrim);
        _grid.Children.Add(DetailsButton);
        _grid.Children.Add(_flyout);
        Child = _grid;

        Classes.CollectionChanged += (_, _) => Arrange();
        Responsive.SetBelow(this, $"collapsed:{Breakpoint.ToString(CultureInfo.InvariantCulture)}");
        Arrange();
    }

    /// <summary>The list side of the page: the job list and whatever sits above it.</summary>
    public Control? Main
    {
        get => GetValue(MainProperty);
        set => SetValue(MainProperty, value);
    }

    /// <summary>The page's side panel (a <c>ScrollViewer.sidepanel</c>).</summary>
    public Control? Panel
    {
        get => GetValue(PanelProperty);
        set => SetValue(PanelProperty, value);
    }

    /// <summary>Whether the overlay is open. Only while <see cref="IsCollapsed"/>; growing past the breakpoint closes it.</summary>
    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>Whether the page is under the breakpoint, so the panel is an overlay rather than a column.</summary>
    public bool IsCollapsed => Classes.Contains("collapsed");

    /// <summary>The thin tab at the right edge that opens the overlay (for the tests).</summary>
    public Button DetailsButton { get; }

    /// <summary>What covers the list while the overlay is open; a click on it closes the overlay (for the tests).</summary>
    public Border Scrim => _scrim;

    /// <summary>The overlay's ✕ (for the tests).</summary>
    public Button CloseButton { get; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MainProperty)
        {
            if (change.OldValue is Control old) _grid.Children.Remove(old);
            if (change.NewValue is Control main) _grid.Children.Insert(0, main);
        }
        else if (change.Property == PanelProperty)
        {
            if (change.OldValue is Control old) _flyoutBody.Children.Remove(old);
            if (change.NewValue is Control panel) _flyoutBody.Children.Add(panel);
        }
        else if (change.Property == IsOpenProperty)
        {
            if (IsOpen && !IsCollapsed)
            {
                SetCurrentValue(IsOpenProperty, false);
                return;
            }
            // Closing hides the overlay, which drops its focus: note first whether focus was in it
            var focusWasIn = !IsOpen && IsKeyboardFocusWithinFlyout();
            Arrange();
            if (IsOpen) Dispatcher.UIThread.Post(FocusPanel, DispatcherPriority.Loaded);
            else if (focusWasIn && IsCollapsed) DetailsButton.Focus(NavigationMethod.Tab);
        }
    }

    /// <summary>Lays the panel out for the current mode: a column, or an overlay that is open or closed.</summary>
    private void Arrange()
    {
        var collapsed = IsCollapsed;
        if (!collapsed && IsOpen)
        {
            SetCurrentValue(IsOpenProperty, false); // grew past the breakpoint: back to the column, overlay closed
            return;
        }
        var open = collapsed && IsOpen;
        _grid.Classes.Set("collapsed", collapsed);
        _grid.ColumnSpacing = collapsed ? 6 : 12;
        DetailsButton.IsVisible = collapsed;
        _flyoutHeader.IsVisible = collapsed;
        _scrim.IsVisible = open;
        _flyout.Classes.Set("overlay", collapsed);
        _flyout.IsVisible = !collapsed || open;
        Grid.SetColumn(_flyout, collapsed ? 0 : 1);
        Grid.SetColumnSpan(_flyout, collapsed ? 2 : 1);
        _flyout.ZIndex = collapsed ? 2 : 0;
        _flyout.HorizontalAlignment = collapsed ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        if (collapsed) _flyout.Width = FlyoutWidth;
        else _flyout.ClearValue(WidthProperty);
        KeyboardNavigation.SetTabNavigation(_flyout, open ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Continue);
    }

    private bool IsKeyboardFocusWithinFlyout() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused && (focused == _flyout || _flyout.IsVisualAncestorOf(focused));

    /// <summary>Moves focus into the overlay: the panel's first control (not selectable text), else ✕.</summary>
    private void FocusPanel()
    {
        if (!IsOpen) return;
        var target = Panel?.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(e => e.Focusable && e.IsTabStop && e.IsEffectivelyVisible && e.IsEffectivelyEnabled && e is not (ScrollViewer or SelectableTextBlock));
        (target ?? CloseButton).Focus(NavigationMethod.Tab);
    }

    /// <summary>
    /// Escape closes the overlay wherever focus is (it tunnels from the window, so a control in the panel or
    /// the list's own Escape doesn't take it first), unless a dropdown in the panel is open: that closes first.
    /// </summary>
    private void OnWindowEscape(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || !IsOpen || !IsEffectivelyVisible) return;
        if (Panel?.GetVisualDescendants().OfType<ComboBox>().Any(c => c.IsDropDownOpen) == true) return; // Escape closes the dropdown first
        IsOpen = false;
        e.Handled = true;
    }

    /// <summary>Ctrl+I (⌘I) anywhere in the window toggles the overlay while this page shows and its panel is collapsed.</summary>
    public bool ToggleShortcut(Key key, KeyModifiers modifiers)
    {
        if (key != Key.I || modifiers is not (KeyModifiers.Control or KeyModifiers.Meta)) return false;
        if (!IsCollapsed || !IsEffectivelyVisible) return false;
        IsOpen = !IsOpen;
        return true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Handled) e.Handled = ToggleShortcut(e.Key, e.KeyModifiers);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble);
        _topLevel?.AddHandler(KeyDownEvent, OnWindowEscape, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _topLevel?.RemoveHandler(KeyDownEvent, OnWindowEscape);
        _topLevel = null;
        IsOpen = false;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>A label drawn top to bottom, for the Details tab: plain text that turns, not a rotated TextBlock.</summary>
    private sealed class VerticalLabel : Control
    {
        public string Text { get; init; } = "";

        static VerticalLabel()
        {
            AffectsRender<VerticalLabel>(TextElement.ForegroundProperty);
            AffectsMeasure<VerticalLabel>(TextElement.FontSizeProperty, TextElement.FontWeightProperty, TextElement.FontFamilyProperty);
        }

        private FormattedText Format() => new(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, TextElement.GetFontWeight(this)),
            TextElement.GetFontSize(this), TextElement.GetForeground(this) ?? Brushes.Gray);

        protected override Size MeasureOverride(Size availableSize)
        {
            var text = Format();
            return new Size(text.Height, text.WidthIncludingTrailingWhitespace);
        }

        public override void Render(DrawingContext context)
        {
            var text = Format();
            using (context.PushTransform(Matrix.CreateRotation(Math.PI / 2) * Matrix.CreateTranslation(text.Height, 0)))
                context.DrawText(text, default);
        }
    }
}
