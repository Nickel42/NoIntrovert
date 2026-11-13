using NoIntrovert.Core;
using NoIntrovert.Services;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>
/// Fixes from the 2026-11-12 review of Settings, the log and errors: files that
/// can't be read or written don't throw, keys are described plainly, the
/// working folder and preset names are checked, and counts use real plurals.
/// </summary>
static class SettingsReviewTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir)
    {
        test("Settings review: a keys.txt another program has locked is reported, not thrown", () => WithSettings(dir("review-keys-locked"), (vm, folder) =>
        {
            var path = Path.Combine(folder, "keys.txt");
            File.WriteAllText(path, new string('a', 32) + "\n");
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var keys = Core.WiiU.WuaJobs.LoadKeys(path, out var problem);
            if (!keys.IsEmpty || problem is null) return "a locked keys.txt gave keys, or no problem";
            vm.Keys.WiiUKeysPath = path;
            string summary;
            try { summary = vm.Keys.WiiUKeysSummary; }
            catch (Exception ex) { return $"the summary threw {ex.GetType().Name}"; }
            return summary.StartsWith("Couldn't read keys.txt") ? null : $"the summary reads \"{summary}\"";
        }));

        test("Settings review: the Wii U keys summary counts in words and shows lines that aren't keys", () => WithSettings(dir("review-keys-summary"), (vm, folder) =>
        {
            var path = Path.Combine(folder, "keys.txt");
            File.WriteAllText(path, new string('a', 32) + "\nnot a key\n");
            vm.Keys.WiiUKeysPath = path;
            var one = vm.Keys.WiiUKeysSummary;
            if (!one.StartsWith($"1 key in {path}.") || !one.Contains("line 2 isn't a key")) return $"one key reads \"{one}\"";
            File.WriteAllText(path, "not a key\n");
            vm.Keys.RefreshWiiUKeysCommand.Execute(null);
            var none = vm.Keys.WiiUKeysSummary;
            return none.StartsWith($"{path} has no keys in it.") ? null : $"no keys reads \"{none}\"";
        }));

        test("Settings review: every missing-keys message sends people to Settings › Keys", () =>
        {
            foreach (var (name, text) in new[] { ("Wii U", Core.WiiU.WuaJobs.NoKeysMessage), ("3DS", Core.Ctr.CtrJobs.NoKeysHelp), ("Switch", Core.Switch.SwitchJobs.NoKeysHelp) })
                if (!text.Contains("Settings › Keys")) return $"the {name} message reads \"{text}\"";
            return null;
        });

        test("Settings review: the working folder refuses a partial path with a hint, and expands %VARIABLES%", () => WithSettings(dir("review-work-folder"), (vm, folder) =>
        {
            var p = vm.Performance;
            var work = Path.Combine(folder, "work");
            p.TempFolder = work;
            if (p.TempFolder != work || p.HasTempFolderProblem || !p.TempFolderNote.EndsWith(" free.")) return $"a usable folder notes \"{p.TempFolderNote}\"";
            p.TempFolder = "work";
            if (p.TempFolder != work) return $"a partial path was saved: \"{p.TempFolder}\"";
            if (!p.HasTempFolderProblem || !p.TempFolderNote.Contains("isn't a full path")) return $"a partial path notes \"{p.TempFolderNote}\"";
            Environment.SetEnvironmentVariable("NOINTROVERT_TEST_WORK", folder);
            try { p.TempFolder = @"%NOINTROVERT_TEST_WORK%" + Path.DirectorySeparatorChar + "w"; }
            finally { Environment.SetEnvironmentVariable("NOINTROVERT_TEST_WORK", null); }
            if (p.TempFolder != Path.Combine(folder, "w") || p.HasTempFolderProblem) return $"%VARIABLES% became \"{p.TempFolder}\"";
            if (!OperatingSystem.IsWindows()) return null;
            var missing = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").FirstOrDefault(d => !Directory.Exists(d));
            if (missing is null) return null; // every drive letter is in use
            var problem = PerformanceSettingsViewModel.TempFolderProblem(Path.Combine(missing, "Work"));
            return problem?.Contains("isn't there") == true ? null : $"a missing drive gives \"{problem}\"";
        }));

        test("Settings review: renaming a saved preset refuses an empty or taken name, with a hint", () => WithSettings(dir("review-rename"), (vm, folder) =>
        {
            var presets = vm.DefaultPresets;
            presets.Save("Mine", "playstation", new Core.Presets.PresetValues());
            presets.Save("Other", "playstation", new Core.Presets.PresetValues());
            var mine = presets.Saved.First(p => p.Name == "Mine");
            mine.Name = "   ";
            if (mine.Name != "Mine" || !mine.HasRenameProblem || !mine.RenameProblem.Contains("needs a name")) return $"an empty name gave \"{mine.Name}\" / \"{mine.RenameProblem}\"";
            mine.Name = "other";
            if (mine.Name != "Mine" || !mine.RenameProblem.Contains("already")) return $"a taken name gave \"{mine.Name}\" / \"{mine.RenameProblem}\"";
            mine.Name = " Mine 2 ";
            if (mine.Name != "Mine 2" || mine.HasRenameProblem) return $"a good name gave \"{mine.Name}\" / \"{mine.RenameProblem}\"";
            return File.ReadAllText(Path.Combine(folder, "settings.json")).Contains("\"Mine 2\"") ? null : "the new name wasn't saved";
        }));

        test("Settings review: the log bar's counts use real plurals", () =>
        {
            var texts = new[] { LogViewModel.DescribeCounts(0, 0), LogViewModel.DescribeCounts(1, 1), LogViewModel.DescribeCounts(2, 3) };
            return string.Join(" | ", texts) == "0 errors · 0 warnings | 1 error · 1 warning | 2 errors · 3 warnings" ? null : string.Join(" | ", texts);
        });

        test("Settings review: Copy and Save write the same lines, and a log that can't be saved is reported", () => WithSettings(dir("review-log-save"), (_, folder) =>
        {
            var target = Path.Combine(folder, "saved.log");
            var dialogs = new FileDialogs(target);
            var log = new LogViewModel(dialogs, new AppSettings());
            log.Info("one");
            log.Debug("hidden");
            log.Warn("two");
            Drain(log);
            log.CopyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            log.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var saved = string.Join(Environment.NewLine, File.ReadAllLines(target));
            if (dialogs.Clipboard != saved) return "Copy and Save wrote different lines";
            if (saved.Contains("hidden")) return "a debug line was saved with Show debug off";
            using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { log.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult(); }
                catch (Exception ex) { return $"saving to a locked file threw {ex.GetType().Name}"; }
            }
            Drain(log);
            return log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.StartsWith("Couldn't save the log")) ? null : "the failed save wasn't logged";
        }));

        test("Settings review: exporting target overrides to a locked file says so instead of throwing", () => WithSettings(dir("review-export"), (_, folder) =>
        {
            var target = Path.Combine(folder, "overrides.json");
            File.WriteAllText(target, "");
            var formats = new TargetFormatSettingsViewModel(new AppSettings(), new FileDialogs(target));
            using var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try { formats.ExportCommand.ExecuteAsync(null).GetAwaiter().GetResult(); }
            catch (Exception ex) { return $"Export threw {ex.GetType().Name}"; }
            return formats.TransferNote.StartsWith("Couldn't export") ? null : $"the note reads \"{formats.TransferNote}\"";
        }));

        test("Settings review: an error nothing caught is logged in plain words, with the details as a debug line", () =>
        {
            var log = new ListLog();
            App.ReportUnhandled(log, new IOException("The disk is full."), fatal: false);
            if (log.Lines.Count != 2) return $"{log.Lines.Count} lines";
            var (level, text) = log.Lines[0];
            if (level != LogLevel.Error || text != "Something went wrong (The disk is full). NoIntrovert kept running; if anything looks wrong, restart it.") return text;
            return log.Lines[1].Level == LogLevel.Debug && log.Lines[1].Text.Contains("IOException") ? null : "the details aren't a debug line";
        });
    }

    /// <summary>Runs the log's batching once, as its timer does.</summary>
    private static void Drain(LogViewModel log) =>
        typeof(LogViewModel).GetMethod("Drain", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(log, null);

    /// <summary>Runs <paramref name="body"/> on a settings view model whose file lives in <paramref name="folder"/>.</summary>
    private static string? WithSettings(string folder, Func<SettingsViewModel, string, string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(new SettingsViewModel(new AppSettings(), new NoDialogs()), folder); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }

    /// <summary>Save as… always answers <paramref name="path"/>; the clipboard is kept.</summary>
    private sealed class FileDialogs(string path) : IDialogs
    {
        public string? Clipboard { get; private set; }
        public Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
        public Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns) => Task.FromResult<string?>(path);
        public Task<string?> PickFolderAsync(string title) => throw new NotSupportedException();
        public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern) => Task.FromResult<string?>(path);
        public Task SetClipboardAsync(string text) { Clipboard = text; return Task.CompletedTask; }
        public Task OpenFolderAsync(string folder) => Task.CompletedTask;
    }

    private sealed class ListLog : ILog
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public void Write(LogLevel level, string message) => Lines.Add((level, message));
    }
}
