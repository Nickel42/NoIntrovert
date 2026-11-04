using NoIntrovert.Core;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Presets;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>
/// Fixes from the redesign's code review: an unconfirmed Replace asks before
/// a run, parallel jobs never pick the same output name, Settings decides
/// whether presets' files are checked, and migrations are saved once.
/// </summary>
internal static class ReviewFixTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir, ILog log)
    {
        test("Save to: a run whose Replace the originals wasn't confirmed asks first, and doesn't start when refused", () =>
            WithSettingsFolder(dir("review-replace-run"), () =>
            {
                var settings = new AppSettings { Output = { SaveTo = SaveTo.ReplaceOriginals, ReplaceConfirmed = false } };
                var vm = new SettingsViewModel(settings, new NoDialogs());
                var convert = new ConvertViewModel(vm, new LogViewModel(new NoDialogs(), settings), new NoDialogs(), new JobQueue(() => 1));
                var file = Path.Combine(dir("review-replace-run-files"), "Game.cue");
                File.WriteAllText(file, "");
                var row = new JobItemViewModel(file);
                convert.Items.Add(row);

                var asked = 0;
                convert.ConfirmReplace = () => { asked++; return Task.FromResult(false); };
                convert.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                if (asked != 1) return $"asked {asked} times before the run";
                if (convert.IsRunning || row.Status != JobStatus.Queued) return "the run started although Replace was refused";
                if (vm.Output.ReplaceConfirmed) return "a refusal was saved as confirmed";

                // Confirmed: it's remembered and not asked again (the row is Leave as is, so nothing runs).
                convert.ConfirmReplace = () => { asked++; return Task.FromResult(true); };
                convert.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                if (asked != 2 || !vm.Output.ReplaceConfirmed) return "confirming wasn't remembered";
                convert.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                return asked == 2 ? null : "asked again after confirming";
            }));

        test("Save to: two jobs running at once never pick the same output name", () =>
        {
            var folder = dir("review-claims");
            var target = Path.Combine(folder, "Game.chd");
            var both = new Barrier(2);
            var picked = new System.Collections.Concurrent.ConcurrentBag<string>();
            BatchJob Job(string name) => new(name, new NullReport(), _ =>
            {
                both.SignalAndWait(TimeSpan.FromSeconds(10)); // both jobs are running before either picks
                picked.Add(OutputPaths.Available(target, overwrite: false));
                both.SignalAndWait(TimeSpan.FromSeconds(10)); // and both have picked before either writes
                return new JobOutcome(JobStatus.Succeeded, "ok");
            }, _ => { }, _ => { });
            var group = new JobQueue(() => 2).Submit("Convert", [Job("USA"), Job("EUR")], new AppSettings(), log);
            if (!group.Completion.Wait(TimeSpan.FromSeconds(30))) return "the jobs never finished";
            var names = picked.Select(Path.GetFileName).OrderBy(n => n).ToList();
            if (string.Join(", ", names) != "Game (2).chd, Game.chd") return $"picked {string.Join(", ", names)}";
            // Even with Overwrite on, a name a running job holds isn't handed out again.
            picked.Clear();
            BatchJob Overwriting(string name) => new(name, new NullReport(), _ =>
            {
                both.SignalAndWait(TimeSpan.FromSeconds(10));
                picked.Add(OutputPaths.Available(target, overwrite: true));
                both.SignalAndWait(TimeSpan.FromSeconds(10));
                return new JobOutcome(JobStatus.Succeeded, "ok");
            }, _ => { }, _ => { });
            group = new JobQueue(() => 2).Submit("Convert", [Overwriting("a"), Overwriting("b")], new AppSettings(), log);
            if (!group.Completion.Wait(TimeSpan.FromSeconds(30))) return "the overwriting jobs never finished";
            if (picked.Distinct().Count() != 2) return "two jobs overwrite the same file";
            // Finished jobs give their names back: nothing was written, so the name is free again.
            return OutputPaths.Available(target, overwrite: false) == target ? null : "a finished job kept its name";
        });

        test("Save to: USA/Game and EUR/Game packed into one folder in parallel become Game.7z and Game (2).7z", () =>
        {
            var src = dir("review-claims-7z");
            var outDir = dir("review-claims-7z-out");
            var inputs = new[] { "USA", "EUR" }.Select(region =>
            {
                Directory.CreateDirectory(Path.Combine(src, region));
                var path = Path.Combine(src, region, "Game.txt");
                File.WriteAllText(path, $"{region} " + new string('x', 200_000));
                return path;
            }).ToList();
            var settings = new AppSettings { Performance = { ParallelJobs = 2 } };
            var jobs = inputs.Select(p => new BatchJob(p, new NullReport(), c => ArchiveJobs.CompressOne(c, [p], "Game", outDir), _ => { }, _ => { })).ToList();
            var group = new JobQueue(() => 2).Submit("Convert", jobs, settings, log);
            if (!group.Completion.Wait(TimeSpan.FromSeconds(60))) return "the jobs never finished";
            if (group.Jobs.FirstOrDefault(j => j.Outcome?.Status != JobStatus.Succeeded) is { } bad) return $"{bad.Outcome?.Status}: {bad.Outcome?.Summary}";
            var made = Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(n => n).ToList();
            return string.Join(", ", made) == "Game (2).7z, Game.7z" ? null : $"made {string.Join(", ", made)}";
        });

        test("Settings › \"Check each file after converting\" decides for presets; Custom and saved presets keep their own", () =>
        {
            var s = new AppSettings { Chd = { VerifyAfterCreate = false }, Archives = { TestAfter = false } };
            foreach (var kind in new[] { TargetKind.Chd, TargetKind.Rvz })
                if (ConvertPlanner.SettingsFor(s, kind, preset: FormatPresets.StandardFor(kind).Values).Chd.VerifyAfterCreate)
                    return $"the {kind} preset turned checking back on";
            if (ConvertPlanner.SettingsFor(s, TargetKind.Chd).Chd.VerifyAfterCreate) return "the CHD action preset turned checking back on";
            if (ConvertPlanner.SettingsFor(s, TargetKind.SevenZip, preset: FormatPresets.StandardFor(TargetKind.SevenZip).Values).Archives.TestAfter)
                return "the 7z preset turned testing back on";
            s.Chd.VerifyAfterCreate = true;
            if (!ConvertPlanner.SettingsFor(s, TargetKind.Chd, preset: FormatPresets.StandardFor(TargetKind.Chd).Values with { Verify = false }).Chd.VerifyAfterCreate)
                return "a preset turned checking off";
            var own = new PresetValues { Target = TargetKind.Chd, Verify = false };
            return ConvertPlanner.SettingsFor(s, TargetKind.Chd, preset: own, ownChecks: true).Chd.VerifyAfterCreate ? "Custom's own Verify was ignored" : null;
        });

        test("Settings: a migrated schema-2 file is saved, and loading again adds no second \"My CHD settings\"", () =>
            WithSettingsFolder(dir("review-migrate-once"), () =>
            {
                var file = Path.Combine(AppSettings.SettingsDirectory, "settings.json");
                File.WriteAllText(file, """
                    { "SchemaVersion": 2, "Output": { "SaveTo": "NextToOriginals" }, "Chd": { "Compression": "cdzs,cdfl" },
                      "Presets": { "Chosen": { "Chd": "custom" } } }
                    """);
                int Mine(AppSettings x) => x.Presets.Saved.Count(p => p.Name == "My CHD settings");
                var first = AppSettings.Load();
                if (Mine(first) != 1) return $"{Mine(first)} saved presets after the first load";
                if (!File.ReadAllText(file).Contains("DefaultBySystem")) return "the migration wasn't saved";
                var stamp = File.GetLastWriteTimeUtc(file).AddMinutes(-5);
                File.SetLastWriteTimeUtc(file, stamp);
                var second = AppSettings.Load();
                if (Mine(second) != 1) return $"{Mine(second)} saved presets after loading again";
                if (File.GetLastWriteTimeUtc(file) != stamp) return "loading again rewrote the file";
                // Run twice on the same settings, the migration reuses its saved preset.
                second.Presets.DefaultBySystem.Clear();
                FormatPresets.Migrate(second);
                return Mine(second) == 1 ? null : "migrating again added a twin";
            }));

        test("Settings › Library: updated emulator data makes open rows count again", () =>
        {
            var presets = new DefaultPresetsViewModel(new AppSettings { IsScratch = true });
            var changed = 0;
            presets.Changed += () => changed++;
            presets.EmulatorDataChanged();
            return changed == 1 ? null : "the Convert rows weren't told to rebuild their lists";
        });
    }

    private static string? WithSettingsFolder(string folder, Func<string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }
}
