using NoIntrovert.Core;
using NoIntrovert.Core.Presets;
using NoIntrovert.Services;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>The Settings page as in the Penpot design: the segmented theme, Save to radios, the jobs stepper, Keep logs for, Default presets, Library and About.</summary>
static class SettingsDesignTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir)
    {
        test("Settings design: the theme's segments choose and save the theme", () => WithSettings(dir("settings-design-theme"), (vm, file) =>
        {
            var g = vm.General;
            if (!g.IsSystemTheme || g.IsDarkTheme || g.IsLightTheme) return "a new install isn't on System";
            var changed = new List<string?>();
            g.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            g.IsDarkTheme = true;
            if (g.Theme != AppTheme.Dark || !g.IsDarkTheme || g.IsSystemTheme) return "Dark wasn't chosen";
            if (!changed.Contains(nameof(GeneralSettingsViewModel.Theme)) || !changed.Contains(nameof(GeneralSettingsViewModel.IsSystemTheme)))
                return "the segments weren't told";
            if (!File.ReadAllText(file).Contains("\"Theme\": \"Dark\"")) return "the theme wasn't saved";
            g.IsLightTheme = false; // the radio button unchecking itself chooses nothing
            return g.Theme == AppTheme.Dark ? null : "unchecking a segment changed the theme";
        }));

        test("Settings design: Save to's radio buttons choose; Replace still asks first", () => WithSettings(dir("settings-design-saveto"), (vm, file) =>
        {
            var o = vm.Output;
            if (!o.SavesNextToOriginals) return "a new install doesn't save next to the originals";
            o.SavesToFolder = true;
            if (!o.SavesToFolder || o.SavesNextToOriginals || o.SaveTo != SaveTo.Folder) return "Folder wasn't chosen";
            o.ReplacesOriginals = true; // nobody to ask: refused
            if (o.SaveTo != SaveTo.Folder) return "Replace was chosen without asking";
            var asked = 0;
            o.ConfirmReplace = () => { asked++; return Task.FromResult(true); };
            o.ReplacesOriginals = true;
            if (asked != 1 || o.SaveTo != SaveTo.ReplaceOriginals) return "Replace wasn't asked and chosen";
            return File.ReadAllText(file).Contains("\"SaveTo\": \"ReplaceOriginals\"") ? null : "Replace wasn't saved";
        }));

        test("Settings design: the On/Off label follows the checkbox", () =>
        {
            var on = SettingsView.OnOff.Convert(true, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
            var off = SettingsView.OnOff.Convert(false, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
            return (on, off) is ("On", "Off") ? null : $"reads {on}/{off}";
        });

        test("Settings design: narrow cards put the wide controls under their text", () =>
            SettingsView.IsCompact(500) && SettingsView.IsCompact(639) && !SettingsView.IsCompact(640) && !SettingsView.IsCompact(842)
                ? null : "the compact width is off");

        test("Settings design: the Jobs at once stepper stays between 1 and the processor count", () => WithSettings(dir("settings-design-jobs"), (vm, file) =>
        {
            var p = vm.Performance;
            if (p.ParallelJobs != 2) return $"starts at {p.ParallelJobs}";
            p.DecreaseJobsCommand.Execute(null);
            if (p.ParallelJobs != 1) return "− didn't lower it";
            if (p.DecreaseJobsCommand.CanExecute(null)) return "− is enabled at 1";
            p.DecreaseJobsCommand.Execute(null);
            if (p.ParallelJobs != 1) return "− went below 1";
            p.IncreaseJobsCommand.Execute(null);
            if (p.ParallelJobs != 2 || !File.ReadAllText(file).Contains("\"ParallelJobs\": 2")) return "+ didn't raise and save it";
            for (var i = 0; i < p.MaxJobs + 3; i++) p.IncreaseJobsCommand.Execute(null);
            if (p.ParallelJobs != p.MaxJobs) return $"+ went to {p.ParallelJobs}, past {p.MaxJobs}";
            return p.IncreaseJobsCommand.CanExecute(null) ? "+ is enabled at the limit" : null;
        }));

        test("Settings design: the working folder box saves a path; empty means the default", () => WithSettings(dir("settings-design-temp"), (vm, _) =>
        {
            var p = vm.Performance;
            if (p.TempFolder != "" || p.TempFolderHint != WorkPaths.DefaultTempRoot) return "a new install doesn't use the default";
            if (!PerformanceSettingsViewModel.DefaultTempFolderText.Contains("NoIntrovert")) return "the placeholder doesn't name the folder";
            p.TempFolder = @"  D:\Work  ";
            if (p.TempFolder != @"D:\Work" || p.TempFolderHint != @"D:\Work") return $"the folder is \"{p.TempFolder}\"";
            p.TempFolder = "";
            return p.TempFolderHint == WorkPaths.DefaultTempRoot ? null : "clearing it didn't go back to the default";
        }));

        test("Settings design: Keep logs for offers a week to a year and keeps an odd saved value", () => WithSettings(dir("settings-design-logs"), (vm, file) =>
        {
            var log = vm.Log;
            var labels = log.KeepLogsChoices.Select(c => c.Label).ToList();
            if (string.Join("|", labels) != "7 days|14 days|30 days|90 days|1 year") return $"choices are {string.Join(", ", labels)}";
            if (log.SelectedKeepLogs != log.KeepLogsChoices[2]) return $"selected is {log.SelectedKeepLogs}";
            log.SelectedKeepLogs = log.KeepLogsChoices[0];
            if (log.KeepLogsDays != 7 || !File.ReadAllText(file).Contains("\"KeepLogsDays\": 7")) return "7 days wasn't saved";
            log.KeepLogsDays = 45;
            if (!log.KeepLogsChoices.Contains(log.SelectedKeepLogs) || log.SelectedKeepLogs.Label != "45 days") return "an odd value isn't in the list";
            return LogSettingsViewModel.DaysText(1) == "1 day" ? null : "1 day reads wrong";
        }));

        test("Settings design: Default presets shows the first systems, then all on request", () => WithSettings(dir("settings-design-presets"), (vm, _) =>
        {
            var d = vm.DefaultPresets;
            if (d.Systems.Count <= DefaultPresetsViewModel.FirstSystems) return $"only {d.Systems.Count} systems";
            if (d.VisibleSystems.Count != DefaultPresetsViewModel.FirstSystems || !d.HasMoreSystems) return $"shows {d.VisibleSystems.Count}";
            if (d.VisibleSystems[0].Name != "Sony PlayStation") return $"starts with {d.VisibleSystems[0].Name}";
            if (d.MoreSystemsText != $"Show {d.Systems.Count - DefaultPresetsViewModel.FirstSystems} more systems") return $"reads \"{d.MoreSystemsText}\"";
            d.ToggleAllSystemsCommand.Execute(null);
            if (d.VisibleSystems.Count != d.Systems.Count || d.MoreSystemsText != "Show fewer systems") return "Show more didn't show them all";
            return null;
        }));

        test("Settings design: Saved presets lists the saved names", () => WithSettings(dir("settings-design-saved"), (vm, _) =>
        {
            var d = vm.DefaultPresets;
            if (!d.SavedSummary.StartsWith("None yet", StringComparison.Ordinal)) return $"empty reads \"{d.SavedSummary}\"";
            var system = d.Systems[0];
            d.Save("My CHD · Zstandard", system.SystemId, new PresetValues());
            if (d.SavedSummary != "1 saved: My CHD · Zstandard") return $"reads \"{d.SavedSummary}\"";
            var changed = new List<string?>();
            d.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            d.Saved[0].Name = "Small";
            return d.SavedSummary == "1 saved: Small" && changed.Contains(nameof(DefaultPresetsViewModel.SavedSummary)) ? null : "a rename didn't update the summary";
        }));

        test("Settings design: Emulator data says what it is, where it's from and when", () =>
        {
            var line = LibrarySettingsViewModel.DescribeEmulatorSource("From libretro core info and ES-DE · 2 Nov 2026");
            if (line != "Which emulators can open each format. From libretro core info and ES-DE, updated 2 Nov 2026.") return $"reads \"{line}\"";
            var failed = LibrarySettingsViewModel.DescribeEmulatorSource("From libretro core info · 2 Nov 2026", "no network");
            if (!failed.EndsWith("updated 2 Nov 2026. Last check failed: no network", StringComparison.Ordinal)) return $"a failed check reads \"{failed}\"";
            var lib = new LibrarySettingsViewModel(new AppSettings(), new NoDialogs());
            if (!lib.EmulatorDataDescription.StartsWith("Which emulators can open each format.", StringComparison.Ordinal)) return $"the card reads \"{lib.EmulatorDataDescription}\"";
            var told = false;
            lib.PropertyChanged += (_, e) => told |= e.PropertyName == nameof(LibrarySettingsViewModel.EmulatorDataDescription);
            lib.RefreshEmulatorDataStatus("From libretro core info · 3 Nov 2026");
            return told && lib.EmulatorDataDescription.Contains("updated 3 Nov 2026") ? null : "a refresh didn't update the card";
        });

        test("Settings design: About names the app and the bundled tools' versions", () =>
        {
            var tools = AboutViewModel.DescribeTools("0.289 (mame0289) lib-chd-kit 1 · ABI 1\nlib-wii-gamecube-kit 0.1.0 (Dolphin 2412) · ABI 1");
            if (tools != "chdman from MAME 0.289 · Dolphin 2412") return $"reads \"{tools}\"";
            var missing = AboutViewModel.DescribeTools("lib-chd-kit not loaded\nlib-wii-gamecube-kit not loaded");
            if (missing != "lib-chd-kit not loaded · lib-wii-gamecube-kit not loaded") return $"missing libraries read \"{missing}\"";
            var about = new AboutViewModel(new DiagnosticsSettingsViewModel(new AppSettings(), new NoDialogs()), new NoDialogs());
            return about.Version.StartsWith("NoIntrovert ", StringComparison.Ordinal) ? null : $"version reads \"{about.Version}\"";
        });

        test("Settings design: Check for updates opens the releases page; Licenses opens the notices", () =>
        {
            var folder = dir("settings-design-licenses");
            if (AboutViewModel.FindLicenseFolder(folder) is not null) return "an empty folder has licences";
            File.WriteAllText(Path.Combine(folder, "THIRD_PARTY_NOTICES-emulator-data.txt"), "MIT");
            if (AboutViewModel.FindLicenseFolder(folder) != Path.GetFullPath(folder)) return "the notices weren't found";

            var dialogs = new FolderRecorder();
            var about = new AboutViewModel(new DiagnosticsSettingsViewModel(new AppSettings(), dialogs), dialogs);
            var opened = new List<string>();
            about.OpenUrl = url => { opened.Add(url); return Task.CompletedTask; };
            about.CheckForUpdatesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if (opened.SingleOrDefault() != AboutViewModel.ReleasesUrl) return "Check for updates didn't open the releases page";
            about.OpenLicensesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            // A build has THIRD_PARTY_NOTICES-emulator-data.txt next to it: the folder opens, not the web page.
            var local = AboutViewModel.FindLicenseFolder(AppContext.BaseDirectory);
            return local is not null
                ? dialogs.Opened == local ? null : $"Licenses opened {dialogs.Opened ?? "nothing"}"
                : opened.LastOrDefault() == AboutViewModel.LicenseUrl ? null : "Licenses opened nothing";
        });
    }

    private sealed class FolderRecorder : IDialogs
    {
        public string? Opened;
        public Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, IReadOnlyList<string> patterns) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern) => Task.FromResult<string?>(null);
        public Task SetClipboardAsync(string text) => Task.CompletedTask;
        public Task OpenFolderAsync(string folder) { Opened = folder; return Task.CompletedTask; }
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
