using System.Text.Json.Nodes;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Presets;

namespace NoIntrovert.Tests;

/// <summary>
/// Save to (the run bar's Next to the originals / Folder / Replace the
/// originals): settings from before it, the " (2)" naming when Overwrite is
/// off, and Replace keeping an original whose new file doesn't verify.
/// </summary>
internal static class SaveToTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir,
        Func<JobWork, Action<AppSettings>?, JobOutcome> runJob, string synthCue, string synthBin)
    {
        test("Save to: settings from before it keep their behaviour", () =>
        {
            // Each case: the Output section (and presets) of an old file, and the Save to it loads as.
            // Replace is never confirmed by a migration: the first run asks.
            var cases = new (string What, string Output, string? Presets, SaveTo Expect, bool Confirmed)[]
            {
                ("nothing removed", """{ "Folder": "", "RemoveOriginals": false }""", null, SaveTo.NextToOriginals, false),
                ("an output folder", """{ "Folder": "D:\\Out" }""", null, SaveTo.Folder, false),
                // Before presets, the switches always counted.
                ("disc originals removed", """{ "Folder": "D:\\Out", "RemoveOriginals": true }""", null, SaveTo.ReplaceOriginals, false),
                ("archive originals removed", """{ "RemoveArchiveOriginals": true }""", null, SaveTo.ReplaceOriginals, false),
                ("Wii U originals removed", """{ "RemoveWiiUOriginals": true }""", null, SaveTo.ReplaceOriginals, false),
                ("a Replace preset", """{ }""", """{ "Chosen": { "Chd": "replace", "Rvz": "smallest" } }""", SaveTo.ReplaceOriginals, false),
                ("Unpack and remove", """{ }""", """{ "Chosen": { "Unpack": "remove" } }""", SaveTo.ReplaceOriginals, false),
                // With presets, a switch counted only under its action's Custom: a stale one left from Custom did nothing.
                ("a stale switch under Standard", """{ "RemoveOriginals": true, "RemoveArchiveOriginals": true }""", """{ "Chosen": { "Rvz": "smallest" } }""", SaveTo.NextToOriginals, false),
                ("a stale switch, another action on Custom", """{ "Folder": "D:\\Out", "RemoveOriginals": true }""", """{ "Chosen": { "Archive": "custom" } }""", SaveTo.Folder, false),
                ("Custom with its switch on", """{ "RemoveArchiveOriginals": true }""", """{ "Chosen": { "Archive": "custom" } }""", SaveTo.ReplaceOriginals, false),
                // A file that already has Save to is left as it is, whatever its folder.
                ("already has Save to", """{ "SaveTo": "NextToOriginals", "Folder": "D:\\Out" }""", null, SaveTo.NextToOriginals, false),
            };
            var previous = AppSettings.SettingsDirectoryOverride;
            try
            {
                for (var i = 0; i < cases.Length; i++)
                {
                    var (what, output, presets, expect, confirmed) = cases[i];
                    var folder = dir($"saveto-migrate-{i}");
                    AppSettings.SettingsDirectoryOverride = folder;
                    var json = $$"""{ "SchemaVersion": 2, "Output": {{output}}{{(presets is null ? "" : $", \"Presets\": {presets}")}} }""";
                    File.WriteAllText(Path.Combine(folder, "settings.json"), json);
                    var s = AppSettings.Load();
                    if (s.Output.SaveTo != expect || s.Output.ReplaceConfirmed != confirmed)
                        return $"{what}: loaded as {s.Output.SaveTo}, confirmed {s.Output.ReplaceConfirmed}";
                    if (presets is not null && presets.Contains("replace") || presets?.Contains("remove") == true)
                    {
                        if (!ActionPresets.Selected(s, PresetAction.Chd).IsPreferred || !ActionPresets.Selected(s, PresetAction.Unpack).IsPreferred)
                            return $"{what}: the removed preset wasn't replaced by the preferred one";
                        if (s.Presets.Chosen.ContainsKey("Chd") || s.Presets.Chosen.ContainsKey("Unpack")) return $"{what}: the removed preset is still written down";
                        if (presets.Contains("smallest") && ActionPresets.Selected(s, PresetAction.Rvz).Id != "smallest") return $"{what}: another action's preset was lost";
                    }
                    // Saved again, the old switches are gone and the choice stays.
                    s.Save();
                    var saved = File.ReadAllText(Path.Combine(folder, "settings.json"));
                    if (saved.Contains("RemoveOriginals") || saved.Contains("RemoveArchiveOriginals")) return $"{what}: the old switches were saved again";
                    if (AppSettings.Load().Output.SaveTo != expect) return $"{what}: didn't survive saving and loading";
                }
            }
            finally { AppSettings.SettingsDirectoryOverride = previous; }
            return null;
        });

        test("Save to: a taken name gets \" (2)\", \" (3)\"… unless Overwrite is on", () =>
        {
            var folder = dir("saveto-names");
            var game = Path.Combine(folder, "Game.chd");
            if (OutputPaths.Available(game, overwrite: false) != game) return "a free name was changed";
            File.WriteAllText(game, "");
            if (OutputPaths.Available(game, overwrite: true) != game) return "Overwrite didn't keep the name";
            if (Path.GetFileName(OutputPaths.Available(game, overwrite: false)) != "Game (2).chd") return "no (2)";
            File.WriteAllText(Path.Combine(folder, "Game (2).chd"), "");
            if (Path.GetFileName(OutputPaths.Available(game, overwrite: false)) != "Game (3).chd") return "no (3) after (2)";
            var dotted = Path.Combine(folder, "Vol. 1");
            Directory.CreateDirectory(dotted);
            if (Path.GetFileName(OutputPaths.Available(dotted, overwrite: false, isFolder: true)) != "Vol. 1 (2)") return "a folder's dots were read as an extension";
            File.WriteAllText(Path.Combine(folder, "Disc (Track 1).bin"), "");
            return OutputPaths.AvailableName(folder, "Disc", [".cue"], overwrite: false) == "Disc (2)"
                ? null : "a cue/bin set whose bins are taken kept its name";
        });

        test("Save to: Folder writes there, and Replace keeps an original whose CHD doesn't verify", () =>
        {
            var folder = dir("saveto-folder");
            var outDir = dir("saveto-folder-out");
            var cue = Copy(folder, synthCue, synthBin);
            var o = runJob(c => ChdJobs.Convert(c, cue), s => { s.Output.Folder = outDir; s.Output.SaveTo = SaveTo.Folder; });
            if (o.Status != JobStatus.Succeeded || !File.Exists(Path.Combine(outDir, "synth.chd"))) return $"Folder: {o.Status} {o.Summary}";
            // The last folder used is remembered, but Next to the originals doesn't use it.
            o = runJob(c => ChdJobs.Convert(c, cue), s => { s.Output.Folder = outDir; s.Output.SaveTo = SaveTo.ReplaceOriginals; s.Output.UseRecycleBin = false; });
            if (!File.Exists(Path.Combine(folder, "synth.chd"))) return "Replace didn't save next to the original";
            if (File.Exists(cue)) return "Replace kept a verified original";

            // Verification fails (the CHD is damaged before it's checked), with "Check each file" off: the original stays.
            var bad = dir("saveto-verify-fails");
            cue = Copy(bad, synthCue, synthBin);
            ChdJobs.BeforeVerifyForTests = Damage;
            try
            {
                o = runJob(c => ChdJobs.Convert(c, cue), s =>
                {
                    s.Output.SaveTo = SaveTo.ReplaceOriginals;
                    s.Output.UseRecycleBin = false;
                    s.Chd.VerifyAfterCreate = false; // Replace verifies anyway
                });
            }
            finally { ChdJobs.BeforeVerifyForTests = null; }
            if (o.Status != JobStatus.Failed) return $"a damaged CHD gave {o.Status}: {o.Summary}";
            return File.Exists(cue) && File.Exists(Path.Combine(bad, "synth.bin")) ? null : "the original was removed although its CHD didn't verify";
        });

        test("Save to: the run log says where this run saves", () =>
        {
            var replace = new OutputSettings { SaveTo = SaveTo.ReplaceOriginals, UseRecycleBin = false };
            var text = ViewModels.BatchTabViewModel.DescribeSaveTo(replace);
            if (!text.Contains("replacing") || !text.Contains("deleted permanently")) return text;
            if (!ViewModels.BatchTabViewModel.DescribeSaveTo(new OutputSettings { SaveTo = SaveTo.Folder, Folder = @"D:\Out" }).Contains(@"D:\Out"))
                return "Folder doesn't name the folder";
            // Folder with no folder chosen is next to the originals, as the jobs treat it.
            return ViewModels.BatchTabViewModel.DescribeSaveTo(new OutputSettings { SaveTo = SaveTo.Folder }) == "saving next to the originals"
                ? null : "an empty Folder isn't described as next to the originals";
        });
    }

    private static string Copy(string folder, string cue, string bin)
    {
        File.Copy(bin, Path.Combine(folder, Path.GetFileName(bin)), overwrite: true);
        var copy = Path.Combine(folder, Path.GetFileName(cue));
        File.Copy(cue, copy, overwrite: true);
        return copy;
    }

    /// <summary>Flips bytes through the middle of a file.</summary>
    private static void Damage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = bytes.Length / 3; i < bytes.Length * 2 / 3; i += 7) bytes[i] ^= 0x5A;
        File.WriteAllBytes(path, bytes);
    }
}
