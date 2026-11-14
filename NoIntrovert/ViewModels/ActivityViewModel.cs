using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The Activity drawer: one resizable panel above the status bar with two tabs,
/// Queue (every job from every page) and Log. The status bar's Activity button
/// and Ctrl+J open and close it. Whether it's open, its tab and its height are
/// remembered in <see cref="GeneralSettings"/>; when Settings › Log's "Open the
/// log when a run has errors" opens it, that isn't saved as the user's choice.
/// </summary>
public partial class ActivityViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly LogViewModel _log;
    private bool _opening;

    public ActivityViewModel(AppSettings settings, LogViewModel log)
    {
        _settings = settings;
        _log = log;
        Tab = settings.General.ActivityTab;
        IsOpen = settings.General.ActivityOpen;
        Height = settings.General.ActivityHeight is > 0 and var h ? h : GeneralSettings.DefaultActivityHeight;
        log.ErrorsLogged += OnErrorsLogged;
    }

    /// <summary>Whether the drawer is open.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ToolTipText))] public partial bool IsOpen { get; set; }

    /// <summary>The tab showing.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsQueueTab), nameof(IsLogTab))] public partial ActivityTab Tab { get; set; }

    /// <summary>The drawer's height as the splitter left it (the window may show it shorter while it's small).</summary>
    [ObservableProperty] public partial double Height { get; set; }

    /// <summary>The Queue tab is showing; a tab's radio button sets it (unchecking one is ignored: the other sets the tab).</summary>
    public bool IsQueueTab
    {
        get => Tab == ActivityTab.Queue;
        set { if (value) Tab = ActivityTab.Queue; }
    }

    /// <summary>The Log tab is showing.</summary>
    public bool IsLogTab
    {
        get => Tab == ActivityTab.Log;
        set { if (value) Tab = ActivityTab.Log; }
    }

    /// <summary>The Activity button's tooltip, with its shortcut.</summary>
    public string ToolTipText => $"{(IsOpen ? "Hide" : "Show")} the queue and the log ({ShortcutLabel})";

    public static string ShortcutLabel => OperatingSystem.IsMacOS() ? "⌘J" : "Ctrl+J";

    [RelayCommand]
    public void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Opens the drawer on <paramref name="tab"/>.</summary>
    public void Open(ActivityTab tab)
    {
        Tab = tab;
        IsOpen = true;
    }

    /// <summary>
    /// The status bar's error and warning chip: the Log tab, with Show only errors on
    /// when there's an error (with only warnings it would show nothing, so it stays as it is).
    /// </summary>
    [RelayCommand]
    private void ShowProblems()
    {
        if (_log.HasErrors) _log.ShowOnlyErrors = true;
        Open(ActivityTab.Log);
    }

    /// <summary>Settings › Log › Open the log when a run has errors: a closed drawer opens on the Log tab.</summary>
    private void OnErrorsLogged()
    {
        if (IsOpen || !_settings.General.OpenLogOnErrors) return;
        // Opened for the user, not chosen by them: the saved choice stays as it was.
        _opening = true;
        try { Open(ActivityTab.Log); }
        finally { _opening = false; }
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (_opening || _settings.General.ActivityOpen == value) return;
        _settings.General.ActivityOpen = value;
        _settings.Save();
    }

    partial void OnTabChanged(ActivityTab value)
    {
        if (_opening || _settings.General.ActivityTab == value) return;
        _settings.General.ActivityTab = value;
        _settings.Save();
    }

    partial void OnHeightChanged(double value)
    {
        if (value <= 0 || Math.Abs(_settings.General.ActivityHeight - value) < 0.5) return;
        _settings.General.ActivityHeight = Math.Round(value);
        _settings.Save();
    }
}
