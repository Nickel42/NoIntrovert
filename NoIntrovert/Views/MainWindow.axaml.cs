using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NoIntrovert.Services;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

public partial class MainWindow : Window, IDialogs
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
        Width = vm.Settings.General.WindowWidth;
        Height = vm.Settings.General.WindowHeight;
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
        var dialog = new ConfirmDialog("A batch is still running",
            "Closing now cancels it; lib-chd-kit deletes the CHD it was writing. Close anyway?", "Close", "Keep running");
        if (await dialog.ShowDialog<bool>(this))
        {
            vm.Queue.Queue.CancelAll();
            _closeConfirmed = true;
            Close();
        }
    }

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

    public async Task SetClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
