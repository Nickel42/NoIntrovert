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
        test("Save to: with Replace the originals the run button says how many originals go, and a refused run doesn't start", () =>
            WithSettingsFolder(dir("review-replace-run"), () =>
            {
                var settings = new AppSettings { Output = { SaveTo = SaveTo.ReplaceOriginals, ReplaceConfirmed = false, UseRecycleBin = false } };
                var vm = new SettingsViewModel(settings, new NoDialogs());
                var convert = new ConvertViewModel(vm, new LogViewModel(new NoDialogs(), settings), new NoDialogs(), new JobQueue(() => 1));
                var files = dir("review-replace-run-files");
                JobItemViewModel Row(string name, TargetChoice? target)
                {
                    var file = Path.Combine(files, name);
                    File.WriteAllText(file, "");
                    var row = new JobItemViewModel(file) { Target = target };
                    convert.Items.Add(row);
                    return row;
                }
                var first = Row("A.cue", TargetChoice.Chd);
                if (!convert.IsReplacing) return "Replace the originals as the default isn't shown as replacing";
                if (convert.RunLabel != "Convert and replace 1 original") return $"one row: the button says '{convert.RunLabel}'";
                Row("B.cue", TargetChoice.Leave); // left as is: its original stays
                Row("C.cue", TargetChoice.Chd);
                if (convert.RunLabel != "Convert and replace 2 originals") return $"two rows to convert and one left: the button says '{convert.RunLabel}'";
                if (convert.ReplaceNote != "Originals are deleted after each new file verifies.") return $"with the Recycle Bin off the line says '{convert.ReplaceNote}'";
                vm.Output.UseRecycleBin = true;
                if (FileRemover.CanUseBin && convert.ReplaceNote != $"Originals go to the {FileRemover.BinName} after each new file verifies.")
                    return $"with the Recycle Bin on the line says '{convert.ReplaceNote}'";

                var asked = new List<int>();
                convert.ConfirmReplace = n => { asked.Add(n); return Task.FromResult(new ReplaceAnswer(false)); };
                convert.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                if (asked.Count != 1 || asked[0] != 2) return $"asked {asked.Count} times ({string.Join(",", asked)}) before the run";
                if (convert.IsRunning || first.Status != JobStatus.Queued || first.StatusText != "Ready") return "the run started although Replace was refused";
                if (vm.Output.ReplaceConfirmed || vm.Output.SkipReplaceQuestion) return "a refusal was remembered";

                // Choosing Next to the originals in the run bar: the button is Convert again, and Settings' default is unchanged.
                convert.ChooseSaveToAsync(SaveTo.NextToOriginals).GetAwaiter().GetResult();
                if (convert.IsReplacing || convert.RunLabel != "Convert") return $"after Next to the originals the button says '{convert.RunLabel}'";
                return settings.Output.SaveTo == SaveTo.ReplaceOriginals ? null : "the run bar's choice changed Settings' default";
            }));

        test("Save to: Replace the originals asks at every run start until \"Don't ask again\", only for this session, and the run bar's choice isn't saved", () =>
            WithSettingsFolder(dir("review-replace-every-run"), () =>
            {
                var settings = new AppSettings();
                var vm = new SettingsViewModel(settings, new NoDialogs());
                var page = new GatedPage(vm, new LogViewModel(new NoDialogs(), settings));
                page.Items.Add(new JobItemViewModel(Path.Combine(dir("review-replace-every-run-files"), "A.iso")));
                var asked = 0;
                var answer = new ReplaceAnswer(true);
                page.ConfirmReplace = _ => { asked++; return Task.FromResult(answer); };

                // Picking Replace in the run bar asks nothing yet; the run start does.
                page.ChooseSaveToAsync(SaveTo.ReplaceOriginals).GetAwaiter().GetResult();
                if (asked != 0) return "picking Replace in the run bar asked";
                if (settings.Output.SaveTo != SaveTo.NextToOriginals) return "the run bar's Replace became Settings' default";
                if (page.RunLabel != "Gate and replace 1 original") return $"the button says '{page.RunLabel}'";
                page.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                page.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                if (asked != 2 || page.Reached != 2) return $"two runs asked {asked} times and started {page.Reached} times";
                answer = new ReplaceAnswer(true, DontAskAgain: true);
                page.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                page.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                if (asked != 3 || page.Reached != 4) return $"after Don't ask again: asked {asked} times, started {page.Reached} times";
                if (settings.Output.ReplaceConfirmed) return "Don't ask again was saved in the settings";

                // A new session (new view models over the same saved settings) starts from Settings' default and asks again.
                var next = new GatedPage(new SettingsViewModel(settings, new NoDialogs()), new LogViewModel(new NoDialogs(), settings));
                if (next.IsReplacing) return "a new session started on the last session's run bar choice";
                next.Items.Add(new JobItemViewModel(Path.Combine(dir("review-replace-every-run-files"), "A.iso")));
                next.ConfirmReplace = _ => { asked++; return Task.FromResult(new ReplaceAnswer(false)); };
                next.ChooseSaveToAsync(SaveTo.ReplaceOriginals).GetAwaiter().GetResult();
                next.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                return asked == 4 && next.Reached == 0 ? null : "a new session didn't ask, or started after a no";
            }));

        test("Rows: an added row reads Ready with no progress bar until a run puts it in the queue", () =>
        {
            var row = new JobItemViewModel(Path.Combine(dir("review-ready"), "A.iso"));
            if (row.StatusText != "Ready" || row.IsQueued || row.ShowsProgress) return $"a new row reads '{row.StatusText}'";
            row.Reset(); // what a run does to each row it hands to the queue
            if (row.StatusText != "Queued" || !row.IsQueued || row.ShowsProgress) return $"a submitted row reads '{row.StatusText}'";
            row.Status = JobStatus.Running;
            if (!row.ShowsProgress) return "a running row hides its progress bar";
            var moved = row.MovedTo(new JobItemViewModel(Path.Combine(dir("review-ready"), "B.iso")));
            return moved.IsSubmitted ? null : "a moved row forgot it was submitted";
        });

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

    /// <summary>A page with Save to whose runs stop just after the Replace question, counting how often they got that far.</summary>
    private sealed class GatedPage(SettingsViewModel settings, LogViewModel log)
        : BatchTabViewModel(settings, log, new NoDialogs(), new JobQueue(() => 1))
    {
        public int Reached { get; private set; }
        public override string Title => "Gate";
        public override string StartLabel => "Gate";
        public override string EmptyHint => "";
        public override bool ShowsSaveTo => true;
        protected override string FilterName => "Any";
        protected override IReadOnlyList<FormatInfo>? InputFormats => null;
        protected override JobOutcome Work(JobContext context, JobItemViewModel item) => new(JobStatus.Succeeded, "ok");

        protected override bool BeforeStart(IReadOnlyList<JobItemViewModel> pending)
        {
            Reached++;
            return false;
        }
    }

    private static string? WithSettingsFolder(string folder, Func<string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }
}
