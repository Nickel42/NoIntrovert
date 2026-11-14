using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NoIntrovert.ViewModels;

/// <summary>Where a page's button sits in the navigation rail.</summary>
public enum RailSection
{
    /// <summary>The rail's list of pages, in labelled groups (<see cref="PageViewModel.Group"/>).</summary>
    Main,
    /// <summary>At the bottom of the rail, below a divider.</summary>
    Bottom,
}

/// <summary>
/// One page of the window, registered once: the rail button, the command
/// line (--page ID), and drag and drop all come from this. Order is the
/// order of registration.
/// </summary>
public sealed partial class PageViewModel(string id, string title, string icon, ViewModelBase content,
    Func<IReadOnlyList<string>, Task>? accept = null, RailSection section = RailSection.Main, string subtitle = "",
    string group = "") : ViewModelBase
{
    /// <summary>Lower-case name used on the command line (--page convert).</summary>
    public string Id { get; } = id;
    public string Title { get; } = title;

    /// <summary>Small text under the rail button's title; empty for none.</summary>
    public string Subtitle { get; } = subtitle;
    public bool HasSubtitle => Subtitle.Length > 0;

    /// <summary>SVG path data for the rail icon (24×24, drawn as a stroke).</summary>
    public string Icon { get; } = icon;

    public RailSection Section { get; } = section;

    /// <summary>The rail group the page is listed under ("Library", "Files", "Tools"); empty for none.</summary>
    public string Group { get; } = group;

    public ViewModelBase Content { get; } = content;

    /// <summary>What dropping files on this page does; null means it doesn't take drops (they go to Convert).</summary>
    public Func<IReadOnlyList<string>, Task>? Accept { get; } = accept;

    /// <summary>The page's keyboard shortcut as shown ("Ctrl+2"), or "" for none.</summary>
    public string Shortcut { get; } = PageShortcuts.LabelFor(id);

    /// <summary>The rail button's tooltip: "Convert (Ctrl+2)".</summary>
    public string ToolTipText => Shortcut.Length > 0 ? $"{Title} ({Shortcut})" : Title;

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; } = true;
}

/// <summary>
/// A labelled group of pages in the rail: a small uppercase header ("FILES") over its pages in the wide
/// rail, a thin divider in the icon-only rail. Hidden while none of its pages is (Tools outside Power-user mode).
/// </summary>
public sealed partial class RailGroupViewModel : ViewModelBase
{
    public RailGroupViewModel(string title, IReadOnlyList<PageViewModel> pages, bool isFirst)
    {
        Title = title;
        Pages = pages;
        IsFirst = isFirst;
        foreach (var page in pages)
            page.PropertyChanged += OnPageChanged;
        IsVisible = pages.Any(p => p.IsVisible);
    }

    /// <summary>The group's name ("Files"); the rail shows it in capitals.</summary>
    public string Title { get; }

    /// <summary>The header as the rail shows it: "FILES".</summary>
    public string Header => Title.ToUpperInvariant();

    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>The top group: no divider above it in the icon-only rail.</summary>
    public bool IsFirst { get; }

    [ObservableProperty] public partial bool IsVisible { get; private set; }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModel.IsVisible)) IsVisible = Pages.Any(p => p.IsVisible);
    }

    /// <summary>The rail's groups, in the order their first page was registered.</summary>
    internal static IReadOnlyList<RailGroupViewModel> For(IEnumerable<PageViewModel> pages) =>
        pages.GroupBy(p => p.Group).Select((g, i) => new RailGroupViewModel(g.Key, g.ToList(), i == 0)).ToList();
}

/// <summary>A page the app can open on (Settings › General › Start on). An empty id means automatic.</summary>
public sealed record StartPageOption(string Id, string Title)
{
    public override string ToString() => Title;
}
