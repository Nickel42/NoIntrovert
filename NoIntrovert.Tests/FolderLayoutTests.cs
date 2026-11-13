using System.Text.Json.Nodes;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>
/// Save to › Folder's layouts: ES-DE (roms/&lt;system&gt;, the default), RomM (roms/&lt;platform&gt;), the same
/// folders as the originals, and no subfolders; the setting saved, the run's added folders not; and a real
/// conversion landing where each layout says.
/// </summary>
internal static class FolderLayoutTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir,
        Func<JobWork, Action<AppSettings>?, JobOutcome> runJob, string synthCue, string synthBin)
    {
        test("Folder layout: ES-DE is the default, puts a game in roms/<ES-DE name>, and an unknown one in the folder itself", () =>
        {
            var root = dir("layout-esde");
            var gba = Write(Path.Combine(root, "Stuff", "Game.gba"));
            var unknown = Write(Path.Combine(root, "Stuff", "Mystery.bin"));
            var target = Path.Combine(root, "Out");
            var s = new AppSettings { Output = { SaveTo = SaveTo.Folder, Folder = target } };
            if (s.Output.FolderLayout != FolderLayout.EsDe) return "the default isn't ES-DE";
            if (OutputPaths.FolderFor(s, Write(Path.Combine(root, "Stuff", "Game.md"))) is var md && !Same(md, Path.Combine(target, "roms", "megadrive")))
                return "the Mega Drive game went to " + md;
            if (OutputPaths.FolderFor(s, gba) is var g && !Same(g, Path.Combine(target, "roms", "gba"))) return "the GBA game went to " + g;
            if (OutputPaths.FolderFor(s, unknown) is var u && !Same(u, target)) return "the unknown file went to " + u;
            // A Save to folder that is itself a roms folder isn't given another one.
            s.Output.Folder = Path.Combine(root, "roms");
            return OutputPaths.FolderFor(s, gba) is var r && Same(r, Path.Combine(root, "roms", "gba")) ? null : "in a roms folder it went to " + r;
        });

        test("Folder layout: RomM puts a game in roms/<RomM platform> (genesis, not ES-DE's megadrive), and an unknown one in the folder itself", () =>
        {
            var root = dir("layout-romm");
            var md = Write(Path.Combine(root, "Stuff", "Game.md"));
            var gba = Write(Path.Combine(root, "Stuff", "Game.gba"));
            var unknown = Write(Path.Combine(root, "Stuff", "Mystery.bin"));
            var target = Path.Combine(root, "Out");
            var s = new AppSettings { Output = { SaveTo = SaveTo.Folder, Folder = target, FolderLayout = FolderLayout.RomM } };
            if (OutputPaths.FolderFor(s, md) is var m && !Same(m, Path.Combine(target, "roms", "genesis"))) return "the Mega Drive game went to " + m;
            if (OutputPaths.FolderFor(s, gba) is var g && !Same(g, Path.Combine(target, "roms", "gba"))) return "the GBA game went to " + g;
            return OutputPaths.FolderFor(s, unknown) is var u && Same(u, target) ? null : "the unknown file went to " + u;
        });

        test("Folder layout: every system has RomM's platform name, as RomM spells its folders (lower case, no spaces)", () =>
        {
            var missing = Core.Formats.TargetFormats.All.Where(t => t.Romm is null && t.Id != Core.Formats.SystemDetector.GenericDisc).Select(t => t.Id).ToList();
            if (missing.Count > 0) return "no RomM folder for " + string.Join(", ", missing);
            var odd = Core.Formats.TargetFormats.All.Where(t => t.Romm is { } r && !System.Text.RegularExpressions.Regex.IsMatch(r, "^[a-z0-9-]+$")).Select(t => $"{t.Id}: {t.Romm}").ToList();
            if (odd.Count > 0) return "not a RomM folder name: " + string.Join(", ", odd);
            // A few RomM names that differ from ES-DE's, from docs.romm.app's Supported platforms.
            var expected = new Dictionary<string, string> { ["gamecube"] = "ngc", ["dreamcast"] = "dc", ["mega-drive-genesis"] = "genesis", ["nintendo-3ds"] = "3ds", ["pc-engine-turbografx-16"] = "tg16" };
            var wrong = expected.Where(e => Core.Formats.TargetFormats.ById(e.Key)?.Romm != e.Value).Select(e => e.Key).ToList();
            return wrong.Count == 0 ? null : "wrong RomM folder for " + string.Join(", ", wrong);
        });

        test("Folder layout: the same folders keeps the subfolders below the added folder (the innermost), or a Library folder", () =>
        {
            var root = dir("layout-same");
            var added = Path.Combine(root, "Games");
            var deep = Write(Path.Combine(added, "Nintendo", "GBA", "Game.gba"));
            var top = Write(Path.Combine(added, "Top.gba"));
            var library = Path.Combine(root, "Library");
            var inLibrary = Write(Path.Combine(library, "NES", "Game.nes"));
            var alone = Write(Path.Combine(root, "Elsewhere", "Alone.gba"));
            var target = Path.Combine(root, "Out");
            var s = new AppSettings { Output = { SaveTo = SaveTo.Folder, Folder = target, FolderLayout = FolderLayout.SameAsOriginals, SourceRoots = [root, added] } };
            s.Library.Folders.Add(library);
            if (OutputPaths.FolderFor(s, deep) is var d && !Same(d, Path.Combine(target, "Nintendo", "GBA"))) return "below the added folder: " + d;
            if (OutputPaths.FolderFor(s, top) is var t && !Same(t, target)) return "at the added folder's top: " + t;
            if (OutputPaths.FolderFor(s, alone) is var a && !Same(a, Path.Combine(target, "Elsewhere"))) return "below the outer added folder: " + a;
            s.Output.SourceRoots = null;
            if (OutputPaths.FolderFor(s, inLibrary) is var l && !Same(l, Path.Combine(target, "NES"))) return "below the Library folder: " + l;
            return OutputPaths.FolderFor(s, alone) is var o && Same(o, target) ? null : "a file added on its own went to " + o;
        });

        test("Folder layout: no subfolders puts everything in the folder; next to the originals ignores the layout", () =>
        {
            var root = dir("layout-flat");
            var gba = Write(Path.Combine(root, "GBA", "Game.gba"));
            var target = Path.Combine(root, "Out");
            var s = new AppSettings { Output = { SaveTo = SaveTo.Folder, Folder = target, FolderLayout = FolderLayout.Flat } };
            if (OutputPaths.FolderFor(s, gba) is var f && !Same(f, target)) return "no subfolders: " + f;
            s.Output.SaveTo = SaveTo.NextToOriginals;
            s.Output.FolderLayout = FolderLayout.EsDe;
            return OutputPaths.FolderFor(s, gba) is var n && Same(n, Path.GetDirectoryName(gba)!) ? null : "next to the originals: " + n;
        });

        test("Folder layout: saved with the settings; a run's added folders survive a copy but are never saved", () =>
        {
            var s = new AppSettings { Output = { FolderLayout = FolderLayout.SameAsOriginals, SourceRoots = [@"D:\Games"] } };
            var json = JsonNode.Parse(s.ToJson())!["Output"]!;
            if ((string?)json["FolderLayout"] != "SameAsOriginals") return "the layout isn't saved by name: " + json["FolderLayout"];
            if (s.Clone().Output.SourceRoots is not [@"D:\Games"]) return "a copy lost the added folders";
            s.Output.SourceRoots = null;
            return JsonNode.Parse(s.ToJson())!["Output"]!.AsObject().ContainsKey("SourceRoots") ? "an empty SourceRoots was written" : null;
        });

        test("Folder layout: the run log says which layout a folder run uses", () =>
        {
            string Text(FolderLayout layout) => ViewModels.BatchTabViewModel.DescribeSaveTo(
                new OutputSettings { SaveTo = SaveTo.Folder, Folder = @"D:\Out", FolderLayout = layout });
            if (!Text(FolderLayout.EsDe).Contains("ES-DE folders (roms/<system>)")) return Text(FolderLayout.EsDe);
            if (!Text(FolderLayout.RomM).Contains("RomM folders (roms/<platform>)")) return Text(FolderLayout.RomM);
            if (!Text(FolderLayout.SameAsOriginals).Contains("same folders")) return Text(FolderLayout.SameAsOriginals);
            return Text(FolderLayout.Flat).Contains("no subfolders") ? null : Text(FolderLayout.Flat);
        });

        test("Folder layout: a PS1 cue converts into roms/psx (ES-DE and RomM), or into its own subfolder with the same folders", () =>
        {
            var root = dir("layout-convert");
            var ps1 = Path.Combine(root, "Source", "PS1");
            Directory.CreateDirectory(ps1);
            var cue = Path.Combine(ps1, Path.GetFileName(synthCue));
            File.Copy(synthCue, cue);
            File.Copy(synthBin, Path.Combine(ps1, Path.GetFileName(synthBin)));
            var name = Path.GetFileNameWithoutExtension(synthCue) + ".chd";
            foreach (var (layout, target, below) in new[]
                     {
                         (FolderLayout.EsDe, Path.Combine(root, "EsDe"), Path.Combine("roms", "psx")),
                         (FolderLayout.RomM, Path.Combine(root, "RomM"), Path.Combine("roms", "psx")),
                         (FolderLayout.SameAsOriginals, Path.Combine(root, "Same"), "PS1"),
                     })
            {
                var expected = Path.Combine(target, below, name);
                var o = runJob(c => ChdJobs.Convert(c, cue), s =>
                {
                    s.Output.Folder = target;
                    s.Output.FolderLayout = layout;
                    s.Output.SourceRoots = [Path.Combine(root, "Source")];
                });
                if (o.Status is not (JobStatus.Succeeded or JobStatus.Warning)) return $"{layout}: {o.Status} {o.Summary}";
                if (!File.Exists(expected)) return $"{layout}: no {expected}; made " + string.Join(", ", Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories));
            }
            return null;
        });
    }

    /// <summary>In the real window (headless): the run bar's layout dropdown beside Folder, and the Settings row.</summary>
    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        test("Save to: the folder layout dropdown shows beside Folder only when Folder is chosen, fits at every size, and is Settings' choice too", () => dispatch(() =>
        {
            var convert = f.Vm.Convert;
            var output = f.Vm.Settings.Output;
            var folder = output.Folder;
            f.Show("convert");
            try
            {
                Avalonia.Controls.ComboBox? Picker() => f.Find<Avalonia.Controls.ComboBox>(c => c.Name == "FolderLayoutPicker" && c.IsEffectivelyVisible);
                convert.ChooseSaveToAsync(SaveTo.NextToOriginals).GetAwaiter().GetResult();
                f.Layout();
                if (Picker() is not null) return "the dropdown shows with Next to the originals";
                output.Folder = Path.Combine(Path.GetTempPath(), "NoIntrovert layout test", "Out");
                convert.ChooseSaveToAsync(SaveTo.Folder).GetAwaiter().GetResult();
                foreach (var (width, height) in LayoutTests.Sizes)
                {
                    f.Resize(width, height);
                    f.Layout();
                    if (Picker() is not { } picker) return $"{width}×{height}: no folder layout dropdown beside Folder";
                    if (picker.SelectedItem is not Choice<FolderLayout> { Value: FolderLayout.EsDe }) return "it doesn't show ES-DE";
                    if (width == 900 && screenshots is not null) f.Save(Path.Combine(screenshots, "folder-layout-run-bar-900.png"));
                    var problems = LayoutTests.Check(f.Window);
                    if (problems.Count > 0) return $"{width}×{height}: " + string.Join("\n        ", problems.Take(6));
                }
                // One setting: the run bar's choice is Settings' choice.
                output.SelectedShortFolderLayout = OutputSettingsViewModel.ShortFolderLayouts.First(c => c.Value == FolderLayout.Flat);
                if (output.SelectedFolderLayout.Value != FolderLayout.Flat) return "Settings didn't follow the run bar";
                f.Show("settings");
                f.Resize(1366, 768);
                f.Layout();
                var row = f.Find<Avalonia.Controls.ComboBox>(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Folder layout" && c.Name != "FolderLayoutPicker");
                if (row is null) return "no Folder layout row in Settings";
                if (row.SelectedItem is not Choice<FolderLayout> { Value: FolderLayout.Flat }) return "the Settings row doesn't show the run bar's choice";
                return null;
            }
            finally
            {
                output.SelectedFolderLayout = OutputSettingsViewModel.FolderLayouts[0];
                convert.ChooseSaveToAsync(SaveTo.NextToOriginals).GetAwaiter().GetResult();
                output.Folder = folder;
            }
        }));
    }

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4096]);
        return path;
    }

    private static bool Same(string a, string b) => PathNames.Same(a, b);
}
