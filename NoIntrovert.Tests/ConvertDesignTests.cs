using NoIntrovert.Core;
using NoIntrovert.Core.Emulators;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Presets;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>
/// The Convert page's text from the Penpot design (docs/requirements/ui-redesign.md):
/// the line under each row's preset, the Result column, the panel's preset count,
/// Works with's count and footer, and Custom's note and codec list.
/// </summary>
internal static class ConvertDesignTests
{
    public static void Run(Action<string, Func<string?>> test)
    {
        test("Convert design: under a row's preset, \"Convert → .chd\", or \"Optimal\" when left as is", () =>
        {
            var chd = FormatPresets.StandardFor(TargetKind.Chd);
            var results = new (string Got, string Want)[]
            {
                (JobItemViewModel.NoteFor(chd, null, "playstation", TargetKind.Chd, "Convert → .chd"), "Convert → .chd"),
                (JobItemViewModel.NoteFor(FormatPresets.Leave, null, "playstation", TargetKind.Leave, "Optimal"), "Optimal"),
                // Left as is although it could be converted: not "Optimal".
                (JobItemViewModel.NoteFor(FormatPresets.Leave, null, "playstation", TargetKind.Chd, "Convert → .chd"), "Left as is"),
                (JobItemViewModel.NoteFor(FormatPresets.Custom, new PresetValues { Target = TargetKind.Rvz }, "gamecube", TargetKind.Rvz, "Convert → .rvz"), "Convert → .rvz"),
                // Off the Convert page there's no preset: the format status.
                (JobItemViewModel.NoteFor(null, null, null, TargetKind.Leave, "Optimal"), "Optimal"),
            };
            var wrong = results.Where(r => r.Got != r.Want).Select(r => $"'{r.Got}' (want '{r.Want}')").ToList();
            return wrong.Count == 0 ? null : string.Join("; ", wrong);
        });

        test("Convert design: a row's note follows its preset", () =>
        {
            var row = new JobItemViewModel(Path.Combine(Path.GetTempPath(), "no-such-dir", "Game.cue"))
            {
                SystemId = "playstation", PlannedTarget = TargetKind.Chd, FormatStatus = "Convert → .chd",
                Targets = [new TargetChoice(TargetKind.Chd, "CHD"), TargetChoice.Leave],
            };
            var options = FormatPresets.ListFor("playstation", row.Targets.Select(t => t.Kind).ToList(), new PresetSettings());
            row.PresetOptions = options;
            var changes = new List<string>();
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(JobItemViewModel.PresetNote)) changes.Add(row.PresetNote); };
            row.PresetChoice = options.First(o => o.IsPreset && o.Preset!.Target == TargetKind.Chd && o.IsEnabled);
            row.PresetChoice = options.First(o => o.IsPreset && o.Preset!.IsLeave);
            return changes is [.., "Convert → .chd", "Left as is"] ? null : "notes: " + string.Join(" | ", changes);
        });

        test("Convert design: Result shows the new file's size next to the original", () =>
        {
            var stats = new OperationStats(TimeSpan.FromSeconds(10), 1000, 410);
            var pct = (0.41).ToString("P0");
            string?[] problems =
            [
                JobItemViewModel.ResultText(new JobOutcome(JobStatus.Succeeded, "Game.rvz", stats)) == $"Game.rvz · {pct}" ? null : "RVZ name has no percentage",
                JobItemViewModel.ResultText(new JobOutcome(JobStatus.Succeeded, $"Game.chd · {pct}", stats)) == $"Game.chd · {pct}" ? null : "CHD percentage doubled",
                JobItemViewModel.ResultText(new JobOutcome(JobStatus.Succeeded, "OK", stats)) == "OK" ? null : "a verify result got a percentage",
                JobItemViewModel.ResultText(JobOutcome.Failed("track03.bin is missing")) == "track03.bin is missing" ? null : "a failure's reason changed",
            ];
            var failed = new JobItemViewModel("x.cue");
            failed.Finished(JobOutcome.Failed("track03.bin is missing"));
            return problems.FirstOrDefault(p => p is not null) ?? (failed.IsFailed ? null : "a failed row isn't marked failed (its reason isn't red)");
        });

        test("Convert design: the panel counts presets for \"PlayStation discs\"", () =>
        {
            var results = new (string Got, string Want)[]
            {
                (SidePanelViewModel.PresetCountText(6, "playstation"), "6 presets for PlayStation discs"),
                (SidePanelViewModel.PresetCountText(1, null), "1 preset for this file"),
            };
            var cart = SidePanelViewModel.PresetCountText(3, "game-boy");
            var wrong = results.Where(r => r.Got != r.Want).Select(r => $"'{r.Got}' (want '{r.Want}')").ToList();
            if (!cart.EndsWith(" cartridges")) wrong.Add($"'{cart}' (want … cartridges)");
            return wrong.Count == 0 ? null : string.Join("; ", wrong);
        });

        test("Convert design: Works with counts \"7 of 9 play it · 1 not confirmed\"", () =>
        {
            SupportState[] states = [.. Enumerable.Repeat(SupportState.Works, 7), SupportState.DoesNot, SupportState.Unknown];
            var all = WorksWithViewModel.CountText(states);
            var none = WorksWithViewModel.CountText([SupportState.Works, SupportState.DoesNot]);
            if (all != "7 of 9 play it · 1 not confirmed") return $"reads '{all}'";
            if (none != "1 of 2 play it") return $"reads '{none}'";
            return WorksWithViewModel.CountText([]) == "" ? null : "an empty list has a count";
        });

        test("Convert design: Works with's footer names each group's source and the date", () =>
        {
            var footer = WorksWithViewModel.FooterFor(EmulatorCatalog.Current);
            var date = EmulatorCatalog.FormatDate(EmulatorCatalog.Current.Snapshot.Generated);
            var vm = new WorksWithViewModel();
            vm.Set("playstation", ".chd", []);
            if (footer != $"Cores: libretro · Standalone: ES-DE · {date}") return $"footer reads '{footer}'";
            return vm.Footer == footer && vm.PlayCount.EndsWith("play it") ? null : $"view model reads '{vm.Footer}' / '{vm.PlayCount}'";
        });

        test("Convert design: Custom's note and codec list", () =>
        {
            var note = PresetSettingsViewModel.NoteFor("Apply to 3 selected");
            if (note != "Changes apply to this file. Use “Apply to 3 selected” to share them.") return $"note reads '{note}'";
            if (PresetSettingsViewModel.NoteFor(null).Contains("Apply to")) return "the note names a button that isn't shown";
            var zstd = PresetSettingsViewModel.CodecsFor(new PresetValues { Target = TargetKind.Chd, Compression = "cdzs,cdzl,cdfl" });
            var standard = PresetSettingsViewModel.CodecsFor(new PresetValues { Target = TargetKind.Chd });
            return zstd == "cdzs, cdzl, cdfl" && standard == "Default codecs" ? null : $"codecs read '{zstd}' / '{standard}'";
        });
    }
}
