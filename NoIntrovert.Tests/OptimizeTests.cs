using Avalonia.Controls;
using NoIntrovert.Core;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Library;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>
/// Optimize as Verify, Convert and Organize in one click, and the file list shared by Optimize, Convert,
/// Verify and Organize: the shared list's adds, removes and moves, the plan converting files the
/// databases don't know (Optimize only), what each row's CHANGES says, and rows following their files.
/// </summary>
internal static class OptimizeTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir)
    {
        test("Shared files: adds are kept once, a removed file stays removed until added again, moves and Clear reach every page", () =>
        {
            var w = new FileWorkspace();
            var added = 0;
            IReadOnlyCollection<string>? removed = null;
            IReadOnlyDictionary<string, string>? moved = null;
            var cleared = false;
            w.Added += () => added++;
            w.Removed += r => removed = r;
            w.Moved += m => moved = m;
            w.Cleared += () => cleared = true;
            var folder = dir("shared-files");
            var a = Path.Combine(folder, "A.nes");
            w.Add([folder, a, a + Path.DirectorySeparatorChar]);
            if (w.Paths.Count != 2 || added != 1) return $"{w.Paths.Count} paths, {added} Added events";
            if (w.Folders.Count != 1) return "the folder isn't among Folders";
            w.Add([a]);
            if (added != 1) return "adding a path again raised Added";
            w.Remove([a]);
            if (removed?.Single() != a || !w.IsRemoved(a) || w.Paths.Contains(a)) return "Remove didn't take the file off";
            w.Add([a]);
            if (w.IsRemoved(a)) return "adding a removed file again left it removed";
            var b = Path.Combine(folder, "B.chd");
            w.Move(new Dictionary<string, string> { [a] = b });
            if (moved is null || !w.Paths.Contains(b) || w.Paths.Contains(a)) return "Move didn't follow the file";
            w.Clear();
            return cleared && w.Paths.Count == 0 ? null : "Clear didn't empty the list";
        });

        test("Optimize plan: a file the databases don't know is converted where it is, keeping its name; Organize and a damaged file leave it", () =>
            WithSettingsFolder(dir("optimize-plan-settings"), () =>
            {
                var folder = dir("optimize-plan");
                var cue = WriteDisc(folder, "Unknown Game (USA)");
                var settings = new AppSettings();
                var index = LibraryScanner.Describe([cue], [folder], settings);
                if (index.Items.Single() is not { System: "playstation", FormatStatus: Core.Formats.TargetStatus.Convert } item)
                    return $"the disc reads as {index.Items.FirstOrDefault()?.System} {index.Items.FirstOrDefault()?.FormatStatus}";
                if (!PathNames.Same(item.Root, folder)) return $"its library folder is {item.Root}";
                var optimize = CleanupPlanner.Plan(index.Games(), settings, MatchStore.Default, convertUnmatched: true);
                if (optimize.Steps.SingleOrDefault() is not { Kind: CleanupKind.Convert } convert) return "Optimize didn't plan a conversion";
                if (!PathNames.Same(convert.Target!, Path.Combine(folder, "Unknown Game (USA).chd"))) return $"it converts to {convert.Target}";
                var organize = CleanupPlanner.Plan(index.Games(), settings, MatchStore.Default);
                if (organize.Steps.Single().Kind != CleanupKind.Untouched) return "Organize changed a file Verify hasn't checked";
                MatchStore.Default.Set(cue, MatchResult.Of(MatchStatus.Damaged, "Damaged: a track doesn't match"));
                var damaged = CleanupPlanner.Plan(index.Games(), settings, MatchStore.Default, convertUnmatched: true);
                return damaged.Steps.Single().Kind == CleanupKind.Untouched ? null : "Optimize would convert a damaged file";
            }));

        test("Layouts: a matched game goes to roms/<ES-DE system>, roms/<RomM platform>, stays in its folder, or to the top with no subfolders", () =>
            WithSettingsFolder(dir("layouts-settings"), () =>
            {
                var root = dir("layouts");
                var sub = Path.Combine(root, "Sega", "Master System");
                Directory.CreateDirectory(sub);
                var rom = Path.Combine(sub, "alex kidd.sms");
                File.WriteAllBytes(rom, new byte[32 * 1024]);
                const string game = "Alex Kidd in Miracle World (USA, Europe)";
                MatchStore.Default.Set(rom, new MatchResult(MatchStatus.Verified, "Sega - Master System", game, game + ".sms", "Verified"));
                var settings = new AppSettings();
                var index = LibraryScanner.Describe([rom], [root], settings);
                if (index.Items.SingleOrDefault()?.System != "master-system-mark-iii") return $"the ROM reads as {index.Items.FirstOrDefault()?.System}";
                var problems = new List<string>();
                // A Master System ROM's best form is a .7z: it's converted (named as in the database) into its folder.
                foreach (var (layout, folder) in new (LibraryLayout, string)[]
                {
                    (LibraryLayout.EsDe, Path.Combine(root, "roms", "mastersystem")),
                    (LibraryLayout.RomM, Path.Combine(root, "roms", "sms")),
                    (LibraryLayout.InPlace, sub),
                    (LibraryLayout.Flat, root),
                })
                {
                    settings.Library.Layout = layout;
                    var step = CleanupPlanner.Plan(index.Games(), settings, MatchStore.Default).Steps.SingleOrDefault();
                    if (step is not { Kind: CleanupKind.Convert, Target: { } target } || !PathNames.Same(target, Path.Combine(folder, game + ".7z")))
                        problems.Add($"{layout}: {step?.Kind} → {step?.Target}");
                }
                return problems.Count == 0 ? null : string.Join("; ", problems);
            }));

        test("Layouts: in RomM a multi-disc game is a folder of its own, with its .m3u and its discs inside", () =>
            WithSettingsFolder(dir("layouts-romm-settings"), () =>
            {
                var root = dir("layouts-romm");
                const string game = "Final Fantasy VII (USA)";
                var discs = new[] { WriteDisc(root, "ff7 (Disc 1)"), WriteDisc(root, "ff7 (Disc 2)") };
                for (var i = 0; i < discs.Length; i++)
                    MatchStore.Default.Set(discs[i], new MatchResult(MatchStatus.Verified, "Sony - PlayStation", $"{game} (Disc {i + 1})", $"{game} (Disc {i + 1}).cue", "Verified"));
                var settings = new AppSettings { Library = { Layout = LibraryLayout.RomM } };
                var index = LibraryScanner.Describe(discs, [root], settings);
                var step = CleanupPlanner.Plan(index.Games(), settings, MatchStore.Default).Steps.SingleOrDefault(s => s.Kind == CleanupKind.Convert);
                if (step is null) return "no multi-disc conversion";
                var folder = Path.Combine(root, "roms", "psx", index.Games().Single().Title);
                if (!PathNames.Same(Path.GetDirectoryName(step.Target!)!, folder) || !step.Target!.EndsWith(".m3u")) return $"the playlist goes to {step.Target}";
                return step.SkipFile is null ? null : "RomM got ES-DE's noload.txt";
            }));

        test("Optimize: a file not yet checked says what's likely, Verify first; a checked one says its exact steps", () =>
            WithSettingsFolder(dir("optimize-text-settings"), () =>
            {
                var folder = dir("optimize-text");
                var cue = WriteDisc(folder, "Some Game (USA)");
                var settings = new AppSettings();
                var item = LibraryScanner.Describe([cue], [folder], settings).Items.Single();
                var text = OptimizeViewModel.Predicted(item, settings, null);
                if (text != "Verify → Convert → .chd → Name from the database → Move → roms/psx/") return $"\"{text}\"";
                settings.Library.Layout = LibraryLayout.InPlace;
                text = OptimizeViewModel.Predicted(item, settings, null);
                if (text != "Verify → Convert → .chd → Name from the database") return $"keeping the layout: \"{text}\"";
                settings.Library.Layout = LibraryLayout.Flat;
                text = OptimizeViewModel.Predicted(item, settings, null);
                if (text != "Verify → Convert → .chd → Name from the database") return $"no subfolders, for a file already at the top: \"{text}\"";
                var step = new CleanupStep
                {
                    Kind = CleanupKind.Convert, GameKey = "g", Game = "Some Game", Root = folder, Sources = [cue],
                    Target = Path.Combine(folder, "roms", "psx", "Some Game (USA).chd"), Reason = "",
                };
                var exact = PlanTabViewModel.StepsText(item, [step]);
                if (exact != "Convert → roms/psx/Some Game (USA).chd") return $"\"{exact}\"";
                step.Enabled = false;
                return PlanTabViewModel.StepsText(item, [step]).StartsWith("Off") ? null : "a step whose box is off reads as if it runs";
            }));

        test("Optimize and Organize: rows follow their files, moved or converted, and a removed duplicate leaves the other pages", () =>
        {
            var run = new CleanupRun
            {
                Entries =
                [
                    new(JournalAction.Moved, "C:/lib/roms/nes/Zelda (USA).nes", "C:/lib/zelda.nes", 1, 1, 1),
                    new(JournalAction.Created, "C:/lib/roms/psx/Crash (USA).chd", null, 1, 1, 2),
                    new(JournalAction.Trashed, "C:/lib/.noi-trash/r/crash.cue", "C:/lib/crash.cue", 1, 1, 2),
                    new(JournalAction.Trashed, "C:/lib/.noi-trash/r/crash.bin", "C:/lib/crash.bin", 1, 1, 2),
                    new(JournalAction.Trashed, "C:/lib/.noi-trash/r/copy.chd", "C:/lib/copy.chd", 1, 1, 3),
                ],
            };
            var (moves, trashed) = PlanTabViewModel.MovesOf(run, ["C:/lib/zelda.nes", "C:/lib/crash.cue", "C:/lib/copy.chd", "C:/lib/other.nes"]);
            if (moves.GetValueOrDefault("C:/lib/zelda.nes") != "C:/lib/roms/nes/Zelda (USA).nes") return "the moved file's row doesn't follow it";
            if (moves.GetValueOrDefault("C:/lib/crash.cue") != "C:/lib/roms/psx/Crash (USA).chd") return "the converted disc's row doesn't follow it to the CHD";
            if (moves.Count != 2) return $"{moves.Count} moves";
            if (trashed.SingleOrDefault() != "C:/lib/copy.chd") return "the removed duplicate isn't taken off";
            run.NewLibrary = "D:/new";
            var (none, gone) = PlanTabViewModel.MovesOf(run, ["C:/lib/zelda.nes"]);
            return none.Count == 0 && gone.Count == 0 ? null : "a new library moved rows, though the files stay where they are";
        });
    }

    /// <summary>In the real window (headless): one click verifies, then converts, and the row follows its file on every page.</summary>
    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        test("Save to: Optimize and Organize have it in the run bar (where they are, a new library, the layout), shared and fitting", () => dispatch(() =>
        {
            var options = f.Vm.Optimize.Options;
            var before = options.Layout;
            try
            {
                foreach (var page in new[] { "optimize", "cleanup" })
                {
                    f.Show(page);
                    foreach (var (width, height) in LayoutTests.Sizes)
                    {
                        f.Resize(width, height);
                        f.Layout();
                        if (f.Find<RadioButton>(r => r.Content as string == "Where they are" && r.IsEffectivelyVisible) is not { IsChecked: true })
                            return $"{page} {width}×{height}: no Where they are, chosen";
                        if (f.Find<Avalonia.Controls.TextBlock>(t => t.Text == "New library:" && t.IsEffectivelyVisible) is null) return $"{page}: no New library choice";
                        if (f.Find<Avalonia.Controls.TextBlock>(t => t.Text == "Folder:" && t.IsEffectivelyVisible) is not null) return $"{page}: Convert's Save to shows too";
                        var picker = f.Find<ComboBox>(c => c.Name == "OrganizeLayoutPicker" && c.IsEffectivelyVisible);
                        if (picker is null) return $"{page}: no Layout list";
                        var labels = string.Join(", ", picker.Items.OfType<Choice<LibraryLayout>>().Select(c => c.Label));
                        if (labels != "ES-DE, RomM, Keep as is, No subfolders") return $"the layouts: {labels}";
                        var problems = LayoutTests.Check(f.Window);
                        if (problems.Count > 0) return $"{page} {width}×{height}: " + string.Join("\n        ", problems.Take(6));
                        if (width == 1366 && screenshots is not null) f.Save(Path.Combine(screenshots, $"saveto-{page}.png"));
                    }
                }
                // Picked on Organize, it's the choice on Optimize and in Settings too.
                var organizePicker = f.Find<ComboBox>(c => c.Name == "OrganizeLayoutPicker" && c.IsEffectivelyVisible)!;
                organizePicker.SelectedItem = OrganizeOptions.Layouts.First(l => l.Value == LibraryLayout.RomM);
                f.Layout();
                if (f.Vm.Settings.Library.SelectedLayout.Value != LibraryLayout.RomM) return "RomM picked in the run bar didn't reach Settings";
                f.Show("optimize");
                f.Layout();
                var optimizePicker = f.Find<ComboBox>(c => c.Name == "OrganizeLayoutPicker" && c.IsEffectivelyVisible)!;
                return (optimizePicker.SelectedItem as Choice<LibraryLayout>)?.Value == LibraryLayout.RomM ? null : "Optimize's Save to doesn't show RomM";
            }
            finally
            {
                options.Layout = before;
                f.Layout();
            }
        }));

        test("Optimize: one click verifies each file, then converts it; the row and the other pages follow it to the CHD", () => dispatch(() =>
        {
            var folder = Path.GetFullPath(Path.Combine(f.LibraryFolder, "..", "optimize-run"));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            var cue = WriteDisc(folder, "Optimize Run Game (USA)");
            var chd = Path.Combine(folder, "Optimize Run Game (USA).chd");
            var tab = f.Vm.Optimize;
            var keep = tab.Options.Layout;
            tab.Options.Layout = LibraryLayout.InPlace; // in place: the test's folder stays as it is but for the conversion
            try
            {
                f.Show("optimize");
                tab.AddPaths([cue]);
                if (!f.Pump(() => tab.Items.Any(i => PathNames.Same(i.Path, cue)) && !tab.IsScanning)) return "Optimize didn't take the disc";
                f.Show("verify"); // Verify takes it too
                if (!f.Pump(() => f.Vm.Verify.Items.Any(i => PathNames.Same(i.Path, cue)))) return "Verify didn't show the disc added on Optimize";
                f.Show("optimize");
                var row = tab.Items.First(i => PathNames.Same(i.Path, cue));
                if (!f.Pump(() => tab.PlanMade.IsCompleted && !tab.IsPlanning && row.Changes.StartsWith("Verify → Convert → .chd"))) return $"its changes: \"{row.Changes}\"";
                // Only this row runs: the others are marked done so Start skips them.
                var others = tab.Items.Where(i => i != row && i.Status != JobStatus.Succeeded).ToList();
                foreach (var other in others) other.Status = JobStatus.Succeeded;
                try
                {
                    tab.StartCommand.Execute(null);
                    if (!f.Pump(() => tab.IsRunning)) return "the run didn't start";
                    if (!f.Pump(() => !tab.IsRunning)) return "the run didn't finish";
                }
                finally { foreach (var other in others) other.Status = JobStatus.Queued; }
                if (MatchStore.Default.Get(chd) is null && !File.Exists(chd)) return "nothing was converted";
                if (!File.Exists(chd)) return "no CHD where the disc was";
                if (File.Exists(cue)) return "the cue is still there (it should be in the trash)";
                var moved = tab.Items.FirstOrDefault(i => PathNames.Same(i.Path, chd));
                if (moved is null) return "Optimize's row didn't follow the disc to its CHD";
                if (moved.Status is not (JobStatus.Succeeded or JobStatus.Warning)) return $"the row is {moved.Status}: {moved.Result}";
                if (!moved.Result.Contains("Converted → Optimize Run Game (USA).chd")) return $"the row says \"{moved.Result}\"";
                // Verify ran first: what it said leads the row's result.
                if (moved.Result.StartsWith("Converted")) return $"the row doesn't say what Verify found: \"{moved.Result}\"";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "optimize-run.png"));
                if (f.Vm.Verify.Items.Any(i => PathNames.Same(i.Path, cue))) return "Verify still lists the cue that went to the trash";
                return f.Vm.Verify.Items.Any(i => PathNames.Same(i.Path, chd)) ? null : "Verify's row didn't follow the disc to its CHD";
            }
            finally
            {
                tab.Options.Layout = keep;
                foreach (var t in f.Vm.SharedTabs) t.Items.RemoveWhere(i => i.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
                f.Show("convert");
                f.Layout();
            }
        }));
    }

    /// <summary>A raw Mode 2 cue/bin whose volume descriptor says PLAYSTATION, so it's read as a PS1 disc. Returns the cue.</summary>
    internal static string WriteDisc(string folder, string name)
    {
        const int sectors = 40, size = 2352;
        var data = new byte[sectors * size];
        for (var i = 0; i < sectors; i++)
        {
            data.AsSpan(i * size + 1, 10).Fill(0xFF);
            data[i * size + 15] = 2;
        }
        var pvd = 16 * size + 24;
        data[pvd] = 1;
        "CD001"u8.CopyTo(data.AsSpan(pvd + 1));
        "PLAYSTATION"u8.CopyTo(data.AsSpan(pvd + 8));
        File.WriteAllBytes(Path.Combine(folder, name + ".bin"), data);
        var cue = Path.Combine(folder, name + ".cue");
        File.WriteAllText(cue, string.Join(Environment.NewLine, $"FILE \"{name}.bin\" BINARY", "  TRACK 01 MODE2/2352", "    INDEX 01 00:00:00", ""));
        return cue;
    }

    private static string? WithSettingsFolder(string folder, Func<string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }
}
