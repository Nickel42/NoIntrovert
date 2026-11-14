using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

/// <summary>A card of the Settings page, as the section list names it; <see cref="IsSub"/> for Advanced's own cards.</summary>
public sealed record SettingsSection(string Title, string CardName, bool IsSub = false)
{
    public override string ToString() => Title;
}

public partial class SettingsView : UserControl
{
    /// <summary>Below this card width, the wide controls (Save to, folders, preset dropdowns…) go under their text.</summary>
    internal const double CompactCardWidth = 640;

    /// <summary>Below this window width the section list is a Jump to… drop-down above the cards.</summary>
    internal const double SectionListWindowWidth = 1000;

    /// <summary>The section list's width and the space after it (SettingsView.axaml).</summary>
    private const double SectionListWidth = 180 + 16;

    /// <summary>"On" or "Off", the label at the right of a setting's checkbox.</summary>
    public static readonly IValueConverter OnOff = new FuncValueConverter<bool, string>(on => on ? "On" : "Off");

    /// <summary>Every card, in the page's order: the section list shows those that are showing (Diagnostics needs Power-user mode).</summary>
    internal static readonly IReadOnlyList<SettingsSection> AllSections =
    [
        new("General", "GeneralCard"),
        new("Appearance", "AppearanceCard"),
        new("Output", "OutputCard"),
        new("Performance", "PerformanceCard"),
        new("Log", "LogCard"),
        new("Default presets", "PresetsCard"),
        new("Library", "LibraryCard"),
        new(SettingsViewModel.KeysSection, "KeysCard"),
        new("Advanced", "AdvancedSection"),
        new("Power user", "PowerUserCard", IsSub: true),
        new("Archives", "ArchivesCard", IsSub: true),
        new("Files and folders", "FilesCard", IsSub: true),
        new("Game databases", "DatabasesCard", IsSub: true),
        new("Target formats", "TargetFormatsCard", IsSub: true),
        new("Diagnostics", "DiagnosticsCard", IsSub: true),
        new("About", "AboutCard"),
    ];

    /// <summary>The sections the list and the Jump to… drop-down offer now.</summary>
    public ObservableCollection<SettingsSection> Sections { get; } = [];

    private SettingsViewModel? _source;

    /// <summary>True while the page sets the lists' selection itself (scroll-spy), so it doesn't scroll again.</summary>
    private bool _syncing;

    /// <summary>The section just scrolled to and the offset it was scrolled to: it stays highlighted while the page stays there.</summary>
    private (SettingsSection Section, double Offset)? _jumped;

    public SettingsView()
    {
        InitializeComponent();
        RebuildSections();
        SectionList.ItemsSource = Sections;
        JumpList.ItemsSource = Sections;
        SectionList.SelectionChanged += (_, _) => OnPicked(SectionList.SelectedItem as SettingsSection);
        JumpList.SelectionChanged += (_, _) => OnPicked(JumpList.SelectedItem as SettingsSection);
        // Up and Down choose a section (and scroll to it); Enter goes into its card.
        SectionList.AddHandler(KeyDownEvent, OnSectionListKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        PageScroll.ScrollChanged += (_, _) => SpyScroll();
        AdvancedSection.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty) Dispatcher.UIThread.Post(SpyScroll, DispatcherPriority.Background);
        };

        SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        // The "commit" text boxes save when they lose focus (their bindings) or when Enter is pressed (here).
        AddHandler(KeyDownEvent, CommitOnEnter, RoutingStrategies.Tunnel);
        // Set up keys (on a row that failed for missing keys) opens this page with the Keys card in view.
        Loaded += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                _source = vm;
                vm.ShowSectionRequested += OnShowSectionRequested;
                vm.General.PropertyChanged += OnGeneralChanged;
                RebuildSections();
                if (vm.PendingSection is not null) OnShowSectionRequested(vm, EventArgs.Empty);
            }
            Dispatcher.UIThread.Post(SpyScroll, DispatcherPriority.Background);
        };
        Unloaded += (_, _) =>
        {
            if (_source is not null)
            {
                _source.ShowSectionRequested -= OnShowSectionRequested;
                _source.General.PropertyChanged -= OnGeneralChanged;
            }
            _source = null;
        };
    }

    // ---- The section list ----

    /// <summary>The card a section scrolls to.</summary>
    internal Control? CardOf(SettingsSection section) => this.FindControl<Control>(section.CardName);

    /// <summary>Power-user mode shows or hides the Diagnostics card, so its section too.</summary>
    private void OnGeneralChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GeneralSettingsViewModel.PowerUserMode))
            Dispatcher.UIThread.Post(RebuildSections, DispatcherPriority.Background);
    }

    /// <summary>The sections whose cards can show (an Advanced card counts while Advanced is collapsed).</summary>
    private void RebuildSections()
    {
        var showing = AllSections.Where(s => CardOf(s) is { IsVisible: true }).ToList();
        if (showing.SequenceEqual(Sections)) return;
        var selected = SectionList?.SelectedItem;
        _syncing = true;
        try
        {
            Sections.Clear();
            foreach (var section in showing) Sections.Add(section);
            if (selected is SettingsSection s && Sections.Contains(s)) Select(s);
        }
        finally { _syncing = false; }
    }

    /// <summary>Up and Down: the previous or next section, scrolled to; Enter: into the chosen section's card.</summary>
    private void OnSectionListKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || Sections.Count == 0) return;
        var at = SectionList.SelectedItem is SettingsSection current ? Sections.IndexOf(current) : -1;
        switch (e.Key)
        {
            case Key.Enter when at >= 0:
                e.Handled = true;
                ShowSection(Sections[at], focus: true);
                break;
            case Key.Down or Key.Up:
                e.Handled = true;
                var next = Math.Clamp(at + (e.Key == Key.Down ? 1 : -1), 0, Sections.Count - 1);
                ShowSection(Sections[next], focus: false);
                (SectionList.ContainerFromIndex(next) as Control)?.Focus(NavigationMethod.Directional);
                break;
        }
    }

    /// <summary>A section chosen in the list or the drop-down (by a person, not the scroll-spy): scroll its card into view.</summary>
    private void OnPicked(SettingsSection? section)
    {
        if (_syncing || section is null) return;
        ShowSection(section, focus: false);
    }

    /// <summary>Highlights <paramref name="section"/> in the list and the drop-down without scrolling.</summary>
    private void Select(SettingsSection? section)
    {
        var was = _syncing;
        _syncing = true;
        try
        {
            SectionList.SelectedItem = section;
            JumpList.SelectedItem = section;
        }
        finally { _syncing = was; }
    }

    /// <summary>
    /// Scrolls <paramref name="section"/>'s card to the top of the page (as far as the page goes), opening
    /// Advanced for one of its cards, and highlights it; <paramref name="focus"/> moves focus into the card.
    /// </summary>
    internal void ShowSection(SettingsSection section, bool focus)
    {
        if (CardOf(section) is not { } card) return;
        Select(section);
        if (section.IsSub && !AdvancedSection.IsExpanded)
        {
            // Open at once, not with SukiUI's growing animation, so the card is where it will stay when the page scrolls to it.
            var grows = AdvancedSection.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_ContentExpand");
            grows?.SetCurrentValue(TransitionsProperty, null);
            AdvancedSection.IsExpanded = true;
            Dispatcher.UIThread.Post(() =>
            {
                ScrollTo(section, card, focus);
                grows?.ClearValue(TransitionsProperty);
            }, DispatcherPriority.Background);
            return;
        }
        ScrollTo(section, card, focus);
    }

    private void ScrollTo(SettingsSection section, Control card, bool focus)
    {
        // Three times at most: scrolling can lay out more of the page (a card just opened), which moves the end.
        for (var pass = 0; pass < 3; pass++)
        {
            UpdateLayout();
            if (card.TranslatePoint(default, Cards) is not { } top) { card.BringIntoView(); break; }
            var max = Math.Max(0, PageScroll.Extent.Height - PageScroll.Viewport.Height);
            var offset = Math.Clamp(top.Y - 8, 0, max);
            _jumped = (section, offset);
            if (Math.Abs(PageScroll.Offset.Y - offset) < 1) break;
            PageScroll.Offset = new Vector(PageScroll.Offset.X, offset);
        }
        Select(section);
        if (focus) FirstInput(card)?.Focus(NavigationMethod.Tab);
    }

    /// <summary>The first control in <paramref name="card"/> a person can tab to (for Advanced, its header).</summary>
    private static InputElement? FirstInput(Control card) =>
        card.GetVisualDescendants().Prepend(card).OfType<TemplatedControl>()
            .FirstOrDefault(c => c is not Expander && c.Focusable && c.IsTabStop && c.IsEffectivelyEnabled && c.IsEffectivelyVisible)
        ?? card.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(b => b.IsEffectivelyVisible);

    /// <summary>The section whose card is at the top of the page now (the last one whose top has reached it).</summary>
    internal SettingsSection? SectionAtTop()
    {
        var y = PageScroll.Offset.Y + 24;
        var max = Math.Max(0, PageScroll.Extent.Height - PageScroll.Viewport.Height);
        SettingsSection? atTop = null;
        foreach (var section in Sections)
        {
            if (CardOf(section) is not { IsEffectivelyVisible: true } card || card.TranslatePoint(default, Cards) is not { } top) continue;
            if (atTop is null || top.Y <= y) atTop = section;
            else break;
        }
        // At the end of the page the last card may never reach the top: it's the one being read.
        if (max > 0 && PageScroll.Offset.Y >= max - 1)
            atTop = Sections.LastOrDefault(s => CardOf(s) is { IsEffectivelyVisible: true }) ?? atTop;
        return atTop;
    }

    /// <summary>The scroll-spy: highlights the card at the top of the page as it scrolls.</summary>
    private void SpyScroll()
    {
        if (_jumped is { } jumped)
        {
            // Still where a choice in the list scrolled to: that section stays highlighted (a short card near the end can't reach the top).
            if (Math.Abs(PageScroll.Offset.Y - jumped.Offset) < 1) { Select(jumped.Section); return; }
            _jumped = null;
        }
        Select(SectionAtTop());
    }

    // ---- Links into the page (Set up keys) ----

    private void OnShowSectionRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(BringPendingSectionIntoView, DispatcherPriority.Background);

    /// <summary>Once the page is laid out, scrolls the asked-for card to the top and focuses its first control.</summary>
    internal void BringPendingSectionIntoView()
    {
        if (DataContext is not SettingsViewModel { PendingSection: { } title } vm) return;
        var section = AllSections.FirstOrDefault(s => s.Title == title);
        if (section is null || CardOf(section) is not { IsVisible: true } card) { vm.PendingSection = null; return; }
        // Not on screen yet: Loaded asks again.
        if (!section.IsSub && !card.IsEffectivelyVisible) return;
        vm.PendingSection = null;
        ShowSection(section, focus: true);
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

    /// <summary>Whether a window <paramref name="windowWidth"/> wide shows the section list (otherwise the Jump to… drop-down).</summary>
    internal static bool ShowsSectionList(double windowWidth) => windowWidth >= SectionListWindowWidth;

    /// <summary>The section list or the drop-down for the window's width, and compact rows when the cards are narrow.</summary>
    private void Arrange(double width)
    {
        var windowWidth = TopLevel.GetTopLevel(this)?.Bounds.Width is double w and > 0 ? w : width;
        var list = ShowsSectionList(windowWidth);
        SectionList.IsVisible = list;
        JumpList.IsVisible = !list;
        // The page's right margin (12) and the cards' widest.
        var cardWidth = Math.Min(Cards.MaxWidth, width - (list ? SectionListWidth : 0) - 12);
        Classes.Set("compact", IsCompact(cardWidth));
    }
}
