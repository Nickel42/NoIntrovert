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
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
        Width = vm.Settings.General.WindowWidth;
        Height = vm.Settings.General.WindowHeight;
        vm.Log.EntriesAdded += () =>
        {
            if (vm.Log.Entries.Count > 0)
                LogList.ScrollIntoView(vm.Log.Entries[^1]);
        };
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
            "Closing now cancels it; libchdman deletes the CHD it was writing. Close anyway?", "Close", "Keep running");
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
