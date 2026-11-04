using NoIntrovert.Core;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>The Settings page's wiring: emulator data's Check now, the start page list, and Save to's confirmation and folder.</summary>
static class WiringTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir)
    {
        test("Settings › Library: emulator data shows its source and date; Check now is off while checking", () => WithSettings(dir("wiring-emulators"), new NoDialogs(), (vm, _) =>
        {
            var lib = vm.Library;
            var source = Core.Emulators.EmulatorCatalog.Current.SourceText;
            if (lib.EmulatorDataStatus != LibrarySettingsViewModel.DescribeEmulatorData(source)) return $"status is \"{lib.EmulatorDataStatus}\"";
            if (!lib.EmulatorDataStatus.StartsWith("Emulator data: from ", StringComparison.Ordinal)) return $"status is \"{lib.EmulatorDataStatus}\"";
            if (!lib.EmulatorDataStatus.Contains(Core.Emulators.EmulatorCatalog.FormatDate(Core.Emulators.EmulatorCatalog.Current.Snapshot.Generated)))
                return "the status has no date";

            lib.CanCheckEmulatorData = true;
            if (!lib.CheckEmulatorDataCommand.CanExecute(null)) return "Check now is off with a source to check";
            lib.IsCheckingEmulatorData = true;
            if (lib.CheckEmulatorDataCommand.CanExecute(null)) return "Check now is on while checking";
            if (!lib.EmulatorDataStatus.Contains("checking")) return "the status doesn't say it's checking";
            lib.IsCheckingEmulatorData = false;
            if (lib.EmulatorDataStatus != LibrarySettingsViewModel.DescribeEmulatorData(source)) return "the status stayed on checking";

            lib.RefreshEmulatorDataStatus(source, "no network");
            return lib.EmulatorDataStatus.EndsWith("Last check failed: no network", StringComparison.Ordinal) ? null : $"a failed check reads \"{lib.EmulatorDataStatus}\"";
        }));

        test("Settings › General: Start on lists the main window's pages, Automatic first", () =>
        {
            var pages = new[] { "library", "convert", "verify", "cleanup", "tests", "settings" }
                .Select(id => new PageViewModel(id, id, "", new StubPage())).ToList();
            var options = MainViewModel.StartPagesFor(pages);
            var ids = string.Join(",", options.Select(o => o.Id));
            return ids == ",library,convert,verify,cleanup" && options[0].Title == "Automatic" ? null : $"start pages are {ids}";
        });

        test("Settings › Output: Replace the originals asks once, shared with the run bar; Choose folder picks A folder", () =>
        {
            var folder = dir("wiring-save-to-folder");
            return WithSettings(dir("wiring-save-to"), new FolderDialogs(folder), (vm, file) =>
            {
                var o = vm.Output;
                var replace = OutputSettingsViewModel.SaveToChoices.First(c => c.Value == SaveTo.ReplaceOriginals);
                o.SelectedSaveTo = replace; // nobody to ask: refused
                if (o.SaveTo == SaveTo.ReplaceOriginals) return "Replace was chosen without asking";

                var asked = 0;
                o.ConfirmReplace = () => { asked++; return Task.FromResult(false); };
                o.SelectedSaveTo = replace;
                if (o.SaveTo == SaveTo.ReplaceOriginals || o.ReplaceConfirmed) return "Replace was chosen after Cancel";

                o.ConfirmReplace = () => { asked++; return Task.FromResult(true); };
                o.SelectedSaveTo = replace;
                if (o.SaveTo != SaveTo.ReplaceOriginals || !o.ReplacesOriginals || !o.ReplaceConfirmed) return "Replace wasn't chosen after confirming";
                o.SelectedSaveTo = OutputSettingsViewModel.SaveToChoices[0];
                o.SelectedSaveTo = replace;
                if (asked != 2) return $"asked {asked} times (expected 2: Cancel, then the one confirmation)";
                if (!File.ReadAllText(file).Contains("\"ReplaceConfirmed\": true")) return "the confirmation wasn't saved";

                o.BrowseFolderCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                return o.SaveTo == SaveTo.Folder && o.SavesToFolder && o.Folder == folder ? null : $"after Choose folder Save to is {o.SaveTo}, folder {o.Folder}";
            });
        });
    }

    private static string? WithSettings(string folder, NoIntrovert.Services.IDialogs dialogs, Func<SettingsViewModel, string, string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(new SettingsViewModel(new AppSettings(), dialogs), Path.Combine(folder, "settings.json")); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }

    private sealed class StubPage : ViewModelBase;

    private sealed class FolderDialogs(string folder) : NoIntrovert.Services.IDialogs
    {
        public Task OpenFolderAsync(string f) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
        public Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(folder);
        public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern) => throw new NotSupportedException();
        public Task SetClipboardAsync(string text) => throw new NotSupportedException();
    }
}
