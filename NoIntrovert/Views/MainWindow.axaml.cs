using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using NoIntrovert.Core;
using NoIntrovert.Services;
using NoIntrovert.ViewModels;
using SukiUI.Controls;

namespace NoIntrovert.Views;

public partial class MainWindow : SukiWindow, IDialogs
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        ShowThemeMode(AppTheme.System);
        SetUpConventions();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        BodyGrid.SizeChanged += (_, _) => FitDrawers();
    }

    /// <summary>
    /// On a short window the log and the queue drawer give up height first, so
    /// the page above them keeps room for its list and run bar: each takes at
    /// most about a quarter of the body (never under a few rows).
    /// </summary>
    private void FitDrawers()
    {
        var height = BodyGrid.Bounds.Height;
        if (height <= 0) return;
        QueueList.MaxHeight = Math.Clamp(height * 0.2, 48, 220);
        var row = BodyGrid.RowDefinitions[2];
        if (Vm?.Log.IsExpanded == true && row.Height.IsAbsolute)
        {
            // A height the splitter set is the one wanted; it comes back when the window grows again.
            if (Math.Abs(row.Height.Value - _fittedLogHeight) > 0.5) _expandedLogRow = row.Height;
            _fittedLogHeight = Math.Min(_expandedLogRow.Value, Math.Max(110, height * 0.3));
            row.Height = new GridLength(_fittedLogHeight);
        }
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnThemeToggleClick(object? sender, RoutedEventArgs e) => ToggleTheme();

    /// <summary>
    /// The title bar's theme button: cycles Settings › General › Theme System → Dark → Light → System, so one
    /// click never leaves System without saying so; the setting, the saved file and the colours stay in step.
    /// </summary>
    public void ToggleTheme()
    {
        if (Vm is not { } vm) return;
        vm.Settings.General.SelectedTheme = GeneralSettingsViewModel.Themes.First(t => t.Value == NextTheme(vm.Settings.General.Theme));
    }

    public static AppTheme NextTheme(AppTheme theme) => theme switch
    {
        AppTheme.System => AppTheme.Dark,
        AppTheme.Dark => AppTheme.Light,
        _ => AppTheme.System,
    };

    private const string MonitorIcon = "M4 4h16a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM8 20h8M12 16v4";
    private const string MoonIcon = "M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9z";
    private const string SunIcon = "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8zM12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M6.3 17.7l-1.4 1.4M19.1 4.9l-1.4 1.4";

    private static string ModeName(AppTheme theme) => theme switch
    {
        AppTheme.System => OperatingSystem.IsWindows() ? "System (follows Windows)" : "System (follows your system's setting)",
        AppTheme.Dark => "Dark",
        _ => "Light",
    };

    /// <summary>The words the theme button's tooltip and accessible name say: the mode now, and the one a click gives.</summary>
    public static string ThemeToggleTip(AppTheme theme) => $"Theme: {ModeName(theme)}. Click for {ModeName(NextTheme(theme)).Split(' ')[0]}.";

    /// <summary>The theme button's icon (monitor for System, moon, sun), tooltip and accessible name, for the current setting.</summary>
    private void ShowThemeMode(AppTheme theme)
    {
        ThemeToggleIcon.Data = Avalonia.Media.StreamGeometry.Parse(theme switch
        {
            AppTheme.System => MonitorIcon,
            AppTheme.Dark => MoonIcon,
            _ => SunIcon,
        });
        var tip = ThemeToggleTip(theme);
        ToolTip.SetTip(ThemeToggle, tip);
        Avalonia.Automation.AutomationProperties.SetName(ThemeToggle, tip);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
        Width = vm.Settings.General.WindowWidth;
        Height = vm.Settings.General.WindowHeight;
        ShowThemeMode(vm.Settings.General.Theme);
        vm.Settings.General.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GeneralSettingsViewModel.Theme)) ShowThemeMode(vm.Settings.General.Theme);
        };
        vm.Log.EntriesAdded += () => ScrollLogToEnd(vm);
        vm.Log.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LogViewModel.IsExpanded)) ApplyLogLayout(vm);
        };
        ApplyLogLayout(vm);
    }

    private const double ExpandedLogHeight = 230;
    private GridLength _expandedLogRow = new(ExpandedLogHeight);
    private double _fittedLogHeight = ExpandedLogHeight;

    /// <summary>
    /// Collapsed, the log's row fits just its bar and the page takes the rest;
    /// expanded, it gets back the height it had (the splitter can change it).
    /// </summary>
    private void ApplyLogLayout(MainViewModel vm)
    {
        var row = BodyGrid.RowDefinitions[2];
        if (vm.Log.IsExpanded)
        {
            row.Height = _expandedLogRow;
            _fittedLogHeight = _expandedLogRow.Value;
            FitDrawers();
            ScrollLogToEnd(vm);
        }
        else
        {
            if (row.Height.IsAbsolute && row.Height.Value > 60 && Math.Abs(row.Height.Value - _fittedLogHeight) > 0.5) _expandedLogRow = row.Height;
            row.Height = GridLength.Auto;
        }
    }

    private void ScrollLogToEnd(MainViewModel vm)
    {
        if (vm.Log.IsExpanded && vm.Log.Entries.Count > 0)
            LogList.ScrollIntoView(vm.Log.Entries[^1]);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Vm is { } vm && OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle)
            vm.Queue.Indicator = TaskbarProgress.For(handle.Handle);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (Vm is not { } vm) return;
        vm.Settings.General.WindowWidth = Bounds.Width;
        vm.Settings.General.WindowHeight = Bounds.Height;
        vm.Settings.Save();

        if (!vm.IsAnyRunning || _closeConfirmed) return;
        e.Cancel = true;
        if (await ConfirmAsync(CloseTitle, CloseMessage, "Close anyway", "Keep running", destructive: true))
        {
            vm.Queue.Queue.CancelAll();
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>What closing during a run asks: what happens to the user's files, whatever the jobs are.</summary>
    public const string CloseTitle = "Jobs are still running";
    public const string CloseMessage =
        "Closing now stops them. Files that are finished are kept. A file still being written is deleted, and its original stays as it was.";

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        var items = e.DataTransfer.TryGetFiles();
        if (items is null) return;
        var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await vm.DropAsync(paths);
    }

    // IDialogs

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, IReadOnlyList<string> patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = [pattern] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task OpenFolderAsync(string folder)
    {
        if (Directory.Exists(folder)) await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
    }

    public Task<bool> ConfirmAsync(string title, string message, string yes, string no, bool destructive = false) =>
        new ConfirmDialog(title, message, yes, no, destructive).ShowDialog<bool>(this);

    public async Task SetClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
