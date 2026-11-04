using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Systems;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>The File section's system facts, format paragraphs and "About file" line.</summary>
internal static class FileFactsTests
{
    public static void Run(Action<string, Func<string?>> test, string scratch)
    {
        test("System facts: every TargetFormats system has facts", () =>
        {
            var missing = TargetFormats.All.Where(s => SystemFacts.For(s.Id) is null).Select(s => s.Id).ToList();
            var extra = SystemFacts.All.Where(f => TargetFormats.ById(f.Id) is null).Select(f => f.Id).ToList();
            var blank = SystemFacts.All.Where(f => f.Name.Length == 0 || f.Short.Length == 0 || f.Manufacturer.Length == 0 || f.Media is not { Length: > 0 } || f.Icon.Length == 0)
                .Select(f => f.Id).ToList();
            var badYear = SystemFacts.All.Where(f => f.Year is < 1970 or > 2030).Select(f => f.Id).ToList();
            if (missing.Count > 0) return "no facts for " + string.Join(", ", missing);
            if (extra.Count > 0) return "facts for unknown ids " + string.Join(", ", extra);
            if (blank.Count > 0) return "blank fields in " + string.Join(", ", blank);
            if (badYear.Count > 0) return "odd year in " + string.Join(", ", badYear);
            var ps1 = SystemFacts.For("playstation")!;
            return ps1 is { Name: "Sony PlayStation", Year: 1994, Generation: "5th generation", Media: "CD-ROM" } && ps1.Subtitle == "Sony Computer Entertainment · 1994"
                ? null : $"PlayStation reads {ps1}";
        });

        test("System facts: every TargetFormats system has a formats paragraph", () =>
        {
            var paragraphs = SystemFacts.FormatParagraphs();
            var missing = TargetFormats.All.Where(s => !paragraphs.TryGetValue(s.Id, out var p) || p.Trim().Length < 20).Select(s => s.Id).ToList();
            var extra = paragraphs.Keys.Where(id => TargetFormats.ById(id) is null).ToList();
            if (missing.Count > 0) return "no paragraph for " + string.Join(", ", missing);
            if (extra.Count > 0) return "paragraphs for unknown ids " + string.Join(", ", extra);
            var noDefault = paragraphs.Where(p => !p.Value.Contains("default", StringComparison.OrdinalIgnoreCase) && p.Key != "scummvm-supported-adventure-games")
                .Select(p => p.Key).ToList();
            return noDefault.Count > 0 ? "no 'default' sentence in " + string.Join(", ", noDefault) : null;
        });

        test("System facts: unknown and missing ids", () =>
        {
            if (SystemFacts.For("no-such-system") is not null || SystemFacts.For(null) is not null) return "For returned facts for an unknown id";
            var fallback = SystemFacts.ForOrFallback("no-such-system");
            if (fallback.Name != TargetFormats.UnknownName || fallback.Subtitle != "" || fallback.Formats != "") return $"fallback reads {fallback}";
            var vm = new FileFactsViewModel();
            vm.ShowFile(Path.Combine(scratch, "mystery.bin"), "no-such-system");
            return vm.IsFile && vm.Title == TargetFormats.UnknownName && !vm.HasSubtitle && !vm.HasFormats && !vm.IsVerified && !vm.HasGeneration
                ? null : $"view model shows '{vm.Title}' / '{vm.Subtitle}'";
        });

        test("File facts: About file for a cue/bin with several tracks", () =>
        {
            var dir = Path.Combine(scratch, "file-facts");
            Directory.CreateDirectory(dir);
            const string game = "Final Fantasy VII (USA)";
            for (var disc = 1; disc <= 3; disc++)
            {
                var stem = $"{game} (Disc {disc})";
                var cue = new System.Text.StringBuilder();
                for (var t = 1; t <= 4; t++)
                {
                    var bin = $"{stem} (Track {t}).bin";
                    File.WriteAllBytes(Path.Combine(dir, bin), new byte[2352 * 10]);
                    cue.Append($"FILE \"{bin}\" BINARY\n  TRACK {t:D2} {(t == 1 ? "MODE2/2352" : "AUDIO")}\n    INDEX 01 00:00:00\n");
                }
                File.WriteAllText(Path.Combine(dir, stem + ".cue"), cue.ToString());
            }
            var path = Path.Combine(dir, $"{game} (Disc 3).cue");
            var about = FileDescription.AboutFile(path);
            const string expected = "BIN/CUE · 4 tracks · 92 KB · Disc 3 of 3";
            if (about != expected) return $"got '{about}', expected '{expected}'";

            var single = Path.Combine(dir, "Solo (USA).cue");
            File.WriteAllText(single, "FILE \"Solo (USA).bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
            File.WriteAllBytes(Path.Combine(dir, "Solo (USA).bin"), new byte[2048]);
            var soloAbout = FileDescription.AboutFile(single);
            return soloAbout == "BIN/CUE · 1 track · 2 KB" ? null : $"single-track cue reads '{soloAbout}'";
        });

        test("File facts: verification badge and several systems", () =>
        {
            if (FileDescription.VerificationOf(null) != Verification.Unknown) return "unchecked file isn't Unknown";
            if (FileDescription.VerificationOf(new MatchResult(MatchStatus.NameDiffers, "d", "g", "g.cue", "")) != Verification.Verified) return "a renamed match isn't Verified";
            if (FileDescription.VerificationOf(MatchResult.Of(MatchStatus.NotInDatabase, "")) != Verification.NotVerified) return "no match isn't NotVerified";
            if (FileDescription.DatabaseFor("playstation") != VerifyDatabase.Redump || FileDescription.DatabaseFor("game-boy") != VerifyDatabase.NoIntro)
                return "wrong database for PlayStation or Game Boy";

            var vm = new FileFactsViewModel();
            vm.ShowFile(Path.Combine(scratch, "Tetris (World).7z"), "game-boy", Verification.Verified);
            if (!vm.IsVerified || !vm.VerifiedTip.StartsWith("Checked against No-Intro") || vm.LearnMoreUri.Host != "no-intro.org") return "Game Boy badge isn't No-Intro's";
            if (vm.FormatsHeading != "Formats for Game Boy") return $"heading reads '{vm.FormatsHeading}'";
            vm.ShowFile(Path.Combine(scratch, "Crash (USA).cue"), "playstation", Verification.Verified);
            if (!vm.VerifiedTip.StartsWith("Checked against Redump, a community database of verified disc dumps.") || vm.LearnMoreUri.Host != "redump.org") return "PlayStation badge isn't Redump's";

            vm.ShowSystems(["playstation", "playstation", "saturn", null]);
            if (!vm.IsSystems || vm.SystemsHeading != "3 systems selected") return $"several systems reads '{vm.SystemsHeading}'";
            if (vm.SystemLines[0].Text != "Sony PlayStation · 2 files") return $"first line reads '{vm.SystemLines[0].Text}'";
            vm.ShowSystems(["playstation", "playstation"]);
            return vm.IsEmpty ? null : "one system should clear the several-systems view";
        });
    }
}
