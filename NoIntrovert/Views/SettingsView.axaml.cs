using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

public partial class SettingsView : UserControl
{
    /// <summary>Below this width the cards stack in one column.</summary>
    private const double TwoColumnWidth = 780;

    /// <summary>Below this card width, the wide controls (Save to, folders, preset dropdowns…) go under their text.</summary>
    internal const double CompactCardWidth = 640;

    /// <summary>"On" or "Off", the label at the right of a setting's checkbox.</summary>
    public static readonly IValueConverter OnOff = new FuncValueConverter<bool, string>(on => on ? "On" : "Off");

    private bool? _twoColumns;

    public SettingsView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        // The "commit" text boxes save when they lose focus (their bindings) or when Enter is pressed (here).
        AddHandler(KeyDownEvent, CommitOnEnter, RoutingStrategies.Tunnel);
        // Set up keys (on a row that failed for missing keys) opens this page with the Keys card in view.
        Loaded += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            _keysSource = vm;
            vm.ShowKeysRequested += OnShowKeysRequested;
            if (vm.ShowKeysPending) OnShowKeysRequested(vm, EventArgs.Empty);
        };
        Unloaded += (_, _) =>
        {
            if (_keysSource is not null) _keysSource.ShowKeysRequested -= OnShowKeysRequested;
            _keysSource = null;
        };
    }

    private SettingsViewModel? _keysSource;

    /// <summary>Once the page is laid out (two columns or one), scrolls the Keys card to the top and focuses its first button.</summary>
    private void OnShowKeysRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(BringKeysIntoView, DispatcherPriority.Background);

    internal void BringKeysIntoView()
    {
        if (DataContext is not SettingsViewModel { ShowKeysPending: true } vm || !KeysCard.IsEffectivelyVisible) return;
        vm.ShowKeysPending = false;
        if (KeysCard.FindAncestorOfType<ScrollViewer>() is { Content: Visual content } scroll
            && KeysCard.TranslatePoint(default, content) is { } top)
        {
            var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(top.Y - 8, 0, max));
        }
        else KeysCard.BringIntoView();
        KeysCard.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyEnabled)?.Focus(NavigationMethod.Tab);
    }

    /// <summary>Enter in a "commit" text box saves what's typed, as leaving the box does.</summary>
    internal static void CommitOnEnter(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Source is not Visual source) return;
        if (source.FindAncestorOfType<TextBox>(includeSelf: true) is not { } box || !box.Classes.Contains("commit")) return;
        BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not SettingsViewModel vm) return;
        // Choosing Replace the originals as the default asks once, like the run bar.
        vm.Output.ConfirmReplace = () => ConfirmDialog.ConfirmReplaceOriginalsAsync(this, vm.Output.UseRecycleBin);
        // Check for updates and Licenses (without local files) open the browser.
        vm.About.OpenUrl = async url =>
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url));
        };
    }

    /// <summary>Whether a card <paramref name="cardWidth"/> wide puts its wide controls under their text.</summary>
    internal static bool IsCompact(double cardWidth) => cardWidth < CompactCardWidth;

    /// <summary>One or two columns for this width, and compact rows when the cards are narrow.</summary>
    private void Arrange(double width)
    {
        var twoColumns = width >= TwoColumnWidth;
        // The page's right margin (12), and the space between the columns (14).
        var cardWidth = (twoColumns ? (width - 12 - 14) / 2 : width - 12);
        Classes.Set("compact", IsCompact(cardWidth));

        if (_twoColumns == twoColumns) return;
        _twoColumns = twoColumns;
        foreach (var grid in new[] { MainColumns, AdvancedColumns })
        {
            grid.ColumnDefinitions[1].Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            grid.ColumnSpacing = twoColumns ? 14 : 0;
            var second = grid.Children[1];
            Grid.SetColumn(second, twoColumns ? 1 : 0);
            Grid.SetRow(second, twoColumns ? 0 : 1);
        }
    }
}
