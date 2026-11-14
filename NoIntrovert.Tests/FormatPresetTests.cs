using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Presets;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>
/// Format presets (docs/requirements/ui-redesign.md, "2. Preset section"):
/// each system's list, one preset per row in the planner, Custom, saved
/// presets, each system's default and the upgrade from per-action presets.
/// </summary>
internal static class FormatPresetTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir, ILog log)
    {
        test("Format presets: PlayStation lists the spec's six, ⚠ last, PBP disabled", () =>
        {
            var list = FormatPresets.ListFor("playstation", [TargetKind.Chd, TargetKind.Leave], new PresetSettings()).ToList();
            var labels = list.Where(i => i.IsPreset && !i.Preset!.IsLeave).Select(i => i.Label).ToList();
            string[] expected = ["CHD · Standard ★", "PBP · Standard", "PBP · Multi-disc in one file", "BIN/CUE · Redump layout", "Custom", "ISO · Data track only ⚠"];
            if (!labels.SequenceEqual(expected)) return $"presets are {string.Join(" | ", labels)}";
            if (!list[^1].Preset!.Warning || !list[^2].IsGroup || list[^2].Label != "⚠ May not play") return "the ⚠ preset isn't last under its group label";
            var custom = list.FindIndex(i => i.Preset?.IsCustom == true);
            if (!list[custom - 1].IsDivider) return "no divider before Custom";
            foreach (var pbp in list.Where(i => i.Preset?.Format == "PBP"))
                if (pbp.IsEnabled || pbp.Hint != "Not available yet") return $"{pbp.Label} isn't disabled as not available yet";
            if (!list.First().IsEnabled || list.First().Preset!.Target != TargetKind.Chd) return "CHD · Standard can't be chosen for a cue";
            // A .cue can't become BIN/CUE or ISO (only a CHD can be extracted); a CHD can.
            if (list.Single(i => i.Preset?.Id == "bincue-redump").IsEnabled) return "BIN/CUE is offered for a cue";
            var fromChd = FormatPresets.ListFor("playstation", [TargetKind.DiscImage, TargetKind.Leave], new PresetSettings());
            if (!fromChd.Single(i => i.Preset?.Id == "bincue-redump").IsEnabled || !fromChd.Single(i => i.Preset?.Id == "iso-data").IsEnabled)
                return "a CHD can't become BIN/CUE or ISO";
            var plays = list.First().Plays;
            return Note(plays.Length > 0 ? $"CHD plays in {plays}" : "no emulator counts");
        });

        test("Format presets: every system has a list, CHDs keep chdman's codecs, GameCube/Wii CHD is ⚠", () =>
        {
            foreach (var system in TargetFormats.All)
            {
                var own = FormatPresets.ForSystem(system.Id);
                if (own.Count == 0) return $"{system.Id} has no presets";
                if (own.Count(p => p.Recommended) > 1 || (own.Any(p => p.Recommended) && !own[0].Recommended)) return $"{system.Id}: the recommended preset isn't first";
                if (own.Any(p => p.Values.Target == TargetKind.Chd && p.Values.Compression.Length > 0)) return $"{system.Id}: a CHD preset sets codecs";
                var list = FormatPresets.ListFor(system.Id, [TargetKind.Leave], new PresetSettings());
                if (!list.Any(i => i.Preset?.IsCustom == true)) return $"{system.Id} has no Custom";
            }
            foreach (var id in new[] { "gamecube", "wii" })
                if (FormatPresets.ForSystem(id).SingleOrDefault(p => p.Values.Target == TargetKind.Chd) is not { Warning: true }) return $"{id}'s CHD isn't ⚠";
            if (FormatPresets.ForSystem("wii").Single(p => p.Format == "WBFS").Producible) return "WBFS is producible";
            if (FormatPresets.ForSystem("playstation-2").Single(p => p.Id == "zso-opl") is not { Producible: true, Values.Target: TargetKind.Zso })
                return "the PS2 ZSO preset doesn't make a ZSO";
            // A file of an unknown system gets a standard preset for each of its targets.
            var unknown = FormatPresets.ListFor(null, [TargetKind.SevenZip, TargetKind.Zip, TargetKind.Leave], new PresetSettings());
            var names = string.Join(" | ", unknown.Where(i => i.IsPreset).Select(i => i.Label));
            return names == "7z · Standard | zip · Standard | Leave as is | Custom" ? null : $"an unknown file lists {names}";
        });

        test("Format presets: each row's job runs with its own preset's values", () =>
        {
            var s = new AppSettings();
            ActionPresets.Select(s, ActionPresets.For(PresetAction.Rvz).Single(p => p.Id == "smallest")); // the old global choice
            var fastest = FormatPresets.ForSystem("gamecube").Single(p => p.Id == "rvz-fastest").Values;
            if (ConvertPlanner.SettingsFor(s, TargetKind.Rvz, null, fastest).Nintendo.CompressionLevel != 1) return "a row on RVZ · Fastest isn't at level 1";
            if (ConvertPlanner.SettingsFor(s, TargetKind.Rvz).Nintendo.CompressionLevel != 19) return "a row without a preset lost its action's preset";
            var zstd = FormatPresets.ForSystem("playstation")[0].Values with { Compression = "cdzs,cdzl,cdfl", HunkSize = 19584 };
            var made = ConvertPlanner.SettingsFor(s, TargetKind.Chd, x => x.Chd.VerifyAfterCreate = true, zstd with { Verify = false });
            if (made.Chd.Compression != "cdzs,cdzl,cdfl" || made.Chd.HunkSize != 19584 || !made.Chd.VerifyAfterCreate) return "a Custom row's values (or the pin) didn't reach its job";

            // Packing: each row's own grouping and archive name decide the jobs, whatever the settings say.
            var folder = dir("format-presets-plan");
            string F(string name) { var p = Path.Combine(folder, name); File.WriteAllText(p, name); return p; }
            var together = new PresetValues { Target = TargetKind.SevenZip, Grouping = Core.Archives.ArchiveGrouping.Single, SingleName = "Bundle" };
            var other = together with { SingleName = "Other" };
            var rows = new List<PlanInput>
            {
                new(F("a.txt"), TargetKind.SevenZip, 1, together),
                new(F("b.txt"), TargetKind.SevenZip, 1, together),
                new(F("c.txt"), TargetKind.SevenZip, 1, other),
                new(F("d.txt"), TargetKind.SevenZip, 1, new PresetValues { Target = TargetKind.SevenZip }),
            };
            var plan = ConvertPlanner.Plan(rows, new AppSettings(), log);
            if (plan.Error is { } error) return error;
            var jobs = string.Join(" | ", plan.Jobs.Select(j => $"{j.Name}:{string.Join(",", j.Rows)}"));
            return jobs == "Bundle.7z:0,1 | Other.7z:2 | d.txt:3" ? null : $"jobs: {jobs}";
        });

        test("Format presets: Custom copies the preset's values, and Reset goes back", () =>
        {
            var file = Path.Combine(dir("format-presets-custom"), "Game.iso");
            File.WriteAllText(file, "x");
            var row = new JobItemViewModel(file)
            {
                Targets = [TargetChoice.Rvz, TargetChoice.Chd, TargetChoice.Leave], SystemId = "gamecube", PlannedTarget = TargetKind.Rvz,
            };
            var presets = new PresetSettings { DefaultBySystem = { ["gamecube"] = "rvz-smallest" } };
            row.ReplacePresetOptions(FormatPresets.ListFor("gamecube", row.Targets.Select(t => t.Kind).ToList(), presets),
                list => FormatPresets.Start(list, "gamecube", TargetKind.Rvz, presets));
            if (row.Preset?.Id != "rvz-smallest") return $"the row starts on {row.Preset?.Id}, not the system's default";
            row.PresetChoice = row.PresetOptions.Single(i => i.Preset?.IsCustom == true);
            if (!row.IsCustom || row.CustomValues?.RvzLevel != 19 || row.CustomValues.Target != TargetKind.Rvz) return "Custom didn't start from RVZ · Smallest's values";
            if (row.CustomBase?.Id != "rvz-smallest") return "Custom doesn't know what it was copied from";
            row.CustomValues = row.CustomValues with { Target = TargetKind.Chd, Compression = "cdzs,cdzl,cdfl" };
            if (row.Target?.Kind != TargetKind.Chd || row.Values?.Compression != "cdzs,cdzl,cdfl") return "the row doesn't run with its Custom values";
            var editor = new PresetSettingsViewModel(row, new DefaultPresetsViewModel(new AppSettings { IsScratch = true }));
            if (editor.SelectedFormat?.Format != "CHD" || !editor.IsChd || !editor.UsesZstandard) return "Preset settings doesn't show the CHD and its Zstandard note";
            if (editor.Formats.Any(f => f.Format == "CHD" && f.Kind != TargetKind.Chd)) return "the target formats are wrong";
            if (editor.ResetLabel != "Reset to RVZ · Smallest") return $"the reset button says {editor.ResetLabel}";
            editor.ResetCommand.Execute(null);
            if (row.Preset?.Id != "rvz-smallest" || row.Values?.RvzLevel != 19) return "Reset didn't go back to RVZ · Smallest";
            // A row left as is starts Custom from the first preset it can use.
            row.PresetChoice = row.OptionFor(FormatPreset.LeaveId);
            if (row.Values is not null || row.Target?.Kind != TargetKind.Leave) return "Leave as is still converts";
            row.PresetChoice = row.OptionFor(FormatPreset.CustomId);
            return row.CustomValues?.Target == TargetKind.Rvz && row.CustomValues.RvzLevel == 5 ? null : "Custom from Leave as is didn't start from RVZ · Balanced";
        });

        test("Format presets: saved presets round-trip and show for their system", () => WithSettings(dir("format-presets-saved"), folder =>
        {
            var settings = AppSettings.Load();
            var vm = new DefaultPresetsViewModel(settings);
            var changes = 0;
            vm.Changed += () => changes++;
            var values = new PresetValues { Target = TargetKind.Chd, Compression = "cdzs,cdzl,cdfl" };
            var saved = vm.Save("My CHD · Zstandard", "playstation", values);
            if (changes == 0) return "saving didn't announce the change";
            var loaded = AppSettings.Load();
            var back = loaded.Presets.Saved.SingleOrDefault();
            if (back is null || back.Id != saved.Id || back.Name != "My CHD · Zstandard" || back.Values != values) return "the saved preset didn't load back the same";
            var list = FormatPresets.ListFor("playstation", [TargetKind.Chd, TargetKind.Leave], loaded.Presets).ToList();
            var mine = list.FindIndex(i => i.Preset?.IsSaved == true);
            if (mine < 0 || list[mine].Label != "My CHD · Zstandard" || !list[mine].IsEnabled) return "the saved preset isn't in PlayStation's list";
            if (mine > list.FindIndex(i => i.Preset?.IsLeave == true)) return "the saved preset isn't above Leave as is and Custom";
            if (FormatPresets.ListFor("saturn", [TargetKind.Chd], loaded.Presets).Any(i => i.Preset?.IsSaved == true)) return "PlayStation's preset shows for Saturn";
            // Saving the same name again replaces it; renaming keeps its id; deleting drops it and the default that used it.
            vm.Save("my chd · zstandard", "playstation", values with { HunkSize = 19584 });
            if (settings.Presets.Saved.Count != 1 || settings.Presets.Saved[0].Values.HunkSize != 19584) return "saving the same name again didn't replace it";
            vm.Saved[0].Name = "Zstd CHDs";
            if (settings.Presets.Saved[0].Name != "Zstd CHDs" || settings.Presets.Saved[0].Id != saved.Id) return "renaming didn't keep the preset";
            vm.SetDefault("playstation", FormatPresets.Find(FormatPreset.SavedPrefix + saved.Id, "playstation", settings.Presets)!);
            if (FormatPresets.Default("playstation", settings.Presets)?.IsSaved != true) return "a saved preset can't be the default";
            vm.Saved[0].DeleteCommand.Execute(null);
            if (settings.Presets.Saved.Count != 0 || settings.Presets.DefaultBySystem.ContainsKey("playstation")) return "deleting left the preset or its default behind";
            return AppSettings.Load().Presets.Saved.Count == 0 ? null : "the deletion wasn't saved";
        }));

        test("Format presets: each system starts on its default, else the recommended one", () =>
        {
            var p = new PresetSettings();
            var vm = new DefaultPresetsViewModel(new AppSettings { IsScratch = true });
            var names = vm.Systems.Select(s => s.SystemId).ToList();
            if (names.Take(3).SequenceEqual(["playstation", "playstation-2", "playstation-portable"]) is false) return $"Settings lists {string.Join(",", names.Take(3))} first";
            if (vm.Systems.Any(s => s.Options.Any(o => !o.Producible))) return "Settings offers a preset that can't be made";
            var psx = vm.Systems[0];
            if (psx.Selected?.Id != "chd-standard") return "PlayStation's default isn't CHD · Standard";
            psx.Selected = psx.Options.Single(o => o.Id == "bincue-redump");
            if (vm.Current.DefaultBySystem.GetValueOrDefault("playstation") != "bincue-redump") return "the default wasn't stored";
            psx.Selected = psx.Options[0];
            if (vm.Current.DefaultBySystem.ContainsKey("playstation")) return "choosing the recommended preset is written down";

            p.DefaultBySystem["playstation"] = "bincue-redump";
            var cue = FormatPresets.ListFor("playstation", [TargetKind.Chd, TargetKind.Leave], p);
            if (FormatPresets.Start(cue, "playstation", TargetKind.Chd, p).Preset?.Id != "chd-standard") return "a cue started on a preset it can't use";
            var chd = FormatPresets.ListFor("playstation", [TargetKind.DiscImage, TargetKind.Leave], p);
            if (FormatPresets.Start(chd, "playstation", TargetKind.DiscImage, p).Preset?.Id != "bincue-redump") return "a CHD to extract didn't start on the default";
            if (FormatPresets.Start(chd, "playstation", TargetKind.Leave, p).Preset?.IsLeave != true) return "an optimal file didn't start on Leave as is";
            p.DefaultBySystem["playstation"] = "saved:gone";
            return FormatPresets.Default("playstation", p)?.Id == "chd-standard" ? null : "a missing default didn't fall back to the recommended preset";
        });

        test("Format presets: settings from before them keep converting the same way", () =>
        {
            var cases = new (string What, string Json, Func<AppSettings, string?> Check)[]
            {
                ("RVZ · Smallest", """{ "SchemaVersion": 2, "Presets": { "Chosen": { "Rvz": "smallest" } } }""",
                    s => s.Presets.DefaultBySystem.GetValueOrDefault("gamecube") == "rvz-smallest" && s.Presets.DefaultBySystem.GetValueOrDefault("wii") == "rvz-smallest"
                        ? null : "GameCube/Wii don't default to RVZ · Smallest"),
                ("CHD Custom", """{ "SchemaVersion": 2, "Chd": { "Compression": "cdzs,cdzl,cdfl" }, "Presets": { "Chosen": { "Chd": "custom" } } }""",
                    s =>
                    {
                        var saved = s.Presets.Saved.SingleOrDefault();
                        if (saved is null || saved.Values.Compression != "cdzs,cdzl,cdfl" || saved.System != "") return "no saved preset with the Custom values";
                        var start = FormatPresets.Default("saturn", s.Presets);
                        return start?.Id == FormatPreset.SavedPrefix + saved.Id ? null : "Saturn doesn't default to the saved Custom values";
                    }),
                ("GameCube discs to CHD", """{ "SchemaVersion": 2, "Nintendo": { "Target": "Chd" } }""",
                    s => s.Presets.DefaultBySystem.GetValueOrDefault("gamecube") == "chd-archive" ? null : "GameCube doesn't default to CHD"),
                ("already upgraded", """{ "SchemaVersion": 2, "Presets": { "Chosen": { "Rvz": "smallest" }, "DefaultBySystem": { } } }""",
                    s => s.Presets.DefaultBySystem.Count == 0 ? null : "an upgraded file was upgraded again"),
                ("nothing chosen", """{ "SchemaVersion": 2 }""",
                    s => s.Presets.DefaultBySystem.Count == 0 && s.Presets.Saved.Count == 0 ? null : "new defaults were written for no reason"),
            };
            for (var i = 0; i < cases.Length; i++)
            {
                var (what, json, check) = cases[i];
                var problem = WithSettings(dir($"format-presets-migrate-{i}"), folder =>
                {
                    File.WriteAllText(Path.Combine(folder, "settings.json"), json);
                    return check(AppSettings.Load());
                });
                if (problem is not null) return $"{what}: {problem}";
            }
            return null;
        });
    }

    private static string? WithSettings(string folder, Func<string, string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(folder); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }

    private static string Note(string text) => "\u0001" + text;
}
