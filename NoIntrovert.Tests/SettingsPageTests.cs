using NoIntrovert.Core;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>The redesigned Settings page's view models: choices, live saving, the log folder and the library folders.</summary>
static class SettingsPageTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir)
    {
        test("Settings page: new settings run 2 jobs at once; a saved file keeps its value", () =>
        {
            if (new AppSettings().Performance.ParallelJobs != 2) return "the default isn't 2";
            var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "SchemaVersion": 2, "Performance": { "ParallelJobs": 1 } }""");
            return loaded?.Performance.ParallelJobs == 1 ? null : "a saved 1 didn't load as 1";
        });

        test("Settings page: theme, start page, save to and log choices save as they change", () => WithSettings(dir("settings-page"), (vm, file) =>
        {
            if (vm.DefaultPresets.Systems.Count == 0) return "Default presets has no systems";
            var ids = string.Join(",", GeneralSettingsViewModel.StartPages.Select(p => p.Value));
            if (ids != ",library,convert,verify,cleanup") return $"start pages are {ids}";

            var changed = new List<string?>();
            vm.General.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.General.SelectedTheme = GeneralSettingsViewModel.Themes.First(t => t.Value == AppTheme.Light);
            if (!changed.Contains(nameof(GeneralSettingsViewModel.Theme))) return "changing the theme didn't announce Theme (App applies it from that)";
            if (!File.Exists(file) || !File.ReadAllText(file).Contains("\"Theme\": \"Light\"")) return "the theme wasn't saved";
            if (App.VariantFor(AppTheme.System) != Avalonia.Styling.ThemeVariant.Default) return "System doesn't follow the OS";

            vm.General.SelectedStartPage = GeneralSettingsViewModel.StartPages.First(p => p.Value == "verify");
            if (!File.ReadAllText(file).Contains("\"StartPage\": \"verify\"")) return "the start page wasn't saved";

            vm.Output.SelectedSaveTo = OutputSettingsViewModel.SaveToChoices.First(c => c.Value == SaveTo.Folder);
            if (!vm.Output.SavesToFolder || vm.Output.ReplacesOriginals) return "A folder doesn't show the folder";
            if (!File.ReadAllText(file).Contains("\"SaveTo\": \"Folder\"")) return "Save to wasn't saved";

            vm.Log.KeepLogsDays = 0;
            if (vm.Log.KeepLogsDays != 1) return $"keeping logs for 0 days became {vm.Log.KeepLogsDays}";
            vm.Log.OpenLogOnErrors = false;
            return File.ReadAllText(file).Contains("\"OpenLogOnErrors\": false") ? null : "Open the log on errors wasn't saved";
        }));

        test("Settings page: Open log folder creates the log folder", () => WithSettings(dir("settings-page-log"), (vm, _) =>
        {
            if (Directory.Exists(AppSettings.LogDirectory)) return "the log folder is already there";
            vm.Log.OpenLogFolderCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            return Directory.Exists(AppSettings.LogDirectory) ? null : "the log folder wasn't created";
        }));

        test("Settings page: library folders are added and removed, through the main window when it's listening", () => WithSettings(dir("settings-page-library"), (vm, _) =>
        {
            var games = dir("settings-page-games");
            vm.Library.RemoveFolderFromListCommand.ExecuteAsync(games).GetAwaiter().GetResult();
            vm.Library.AddFolders([games]);
            if (!vm.Library.HasFolders || vm.Library.Folders.Count != 1) return "the folder wasn't added";
            string? asked = null;
            vm.Library.RemoveFolderRequested = f => { asked = f; return Task.CompletedTask; };
            vm.Library.RemoveFolderFromListCommand.ExecuteAsync(vm.Library.Folders[0]).GetAwaiter().GetResult();
            if (asked is null || !vm.Library.HasFolders) return "Remove didn't go through the main window";
            vm.Library.RemoveFolderRequested = null;
            vm.Library.RemoveFolderFromListCommand.ExecuteAsync(vm.Library.Folders[0]).GetAwaiter().GetResult();
            if (vm.Library.HasFolders) return "the folder wasn't removed";
            return vm.Library.CheckEmulatorDataCommand.CanExecute(null) ? "Check now is enabled without an emulator data source" : null;
        }));
    }

    /// <summary>Runs <paramref name="body"/> on a settings view model whose file lives in <paramref name="folder"/>.</summary>
    private static string? WithSettings(string folder, Func<SettingsViewModel, string, string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(new SettingsViewModel(new AppSettings(), new NoDialogs()), Path.Combine(folder, "settings.json")); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }
}
