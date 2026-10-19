using CommunityToolkit.Mvvm.ComponentModel;

namespace NoIntrovert.ViewModels;

/// <summary>
/// One page of the window, registered once: the rail button, the command
/// line (--page ID), and drag and drop all come from this. Order is the
/// order of registration.
/// </summary>
public sealed partial class PageViewModel(string id, string title, string icon, ViewModelBase content,
    Func<IReadOnlyList<string>, Task>? accept = null) : ViewModelBase
{
    /// <summary>Lower-case name used on the command line (--page convert).</summary>
    public string Id { get; } = id;
    public string Title { get; } = title;

    /// <summary>SVG path data for the rail icon (24×24, drawn as a stroke).</summary>
    public string Icon { get; } = icon;

    public ViewModelBase Content { get; } = content;

    /// <summary>What dropping files on this page does; null means it doesn't take drops (they go to Convert).</summary>
    public Func<IReadOnlyList<string>, Task>? Accept { get; } = accept;

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; } = true;
}
