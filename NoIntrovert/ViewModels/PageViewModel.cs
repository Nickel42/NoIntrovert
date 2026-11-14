using CommunityToolkit.Mvvm.ComponentModel;

namespace NoIntrovert.ViewModels;

/// <summary>Where a page's button sits in the navigation rail.</summary>
public enum RailSection
{
    /// <summary>The rail's list of pages, under the logo.</summary>
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
    Func<IReadOnlyList<string>, Task>? accept = null, RailSection section = RailSection.Main, string subtitle = "") : ViewModelBase
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

    public ViewModelBase Content { get; } = content;

    /// <summary>What dropping files on this page does; null means it doesn't take drops (they go to Convert).</summary>
    public Func<IReadOnlyList<string>, Task>? Accept { get; } = accept;

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; } = true;
}

/// <summary>A page the app can open on (Settings › General › Start on). An empty id means automatic.</summary>
public sealed record StartPageOption(string Id, string Title)
{
    public override string ToString() => Title;
}
