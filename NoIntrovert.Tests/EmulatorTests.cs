using System.Net;
using System.Text.Json.Nodes;
using NoIntrovert.Core.Emulators;
using NoIntrovert.Core.Formats;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>"Works with": the bundled emulator data and the ✓/✕/? rules (Core/Emulators).</summary>
internal static class EmulatorTests
{
    private static readonly string[] DefaultCdCodecs = ["cdlz", "cdzl", "cdfl"];

    public static void Run(Action<string, Func<string?>> test)
    {
        test("Works with: PlayStation CHD with the default codecs", () =>
        {
            var entries = EmulatorSupport.For("playstation", ".chd", DefaultCdCodecs);
            if (entries.Count == 0) return "no emulators for PlayStation";
            var firstStandalone = entries.ToList().FindIndex(e => e.Group == EmulatorGroup.Standalone);
            if (firstStandalone <= 0 || entries.Skip(firstStandalone).Any(e => e.Group != EmulatorGroup.Standalone))
                return "RetroArch cores don't all come before the standalone emulators";
            return Expect(entries, ("DuckStation", SupportState.Works), ("Mednafen", SupportState.DoesNot),
                ("PCSX-Redux", SupportState.DoesNot), ("ePSXe", SupportState.Works), ("Beetle PSX HW", SupportState.Works),
                ("SwanStation", SupportState.Works))
                ?? (Find(entries, "Mednafen")!.Detail.Contains("BIN/CUE") ? null : "Mednafen's tooltip doesn't suggest BIN/CUE");
        });

        test("Works with: Zstandard CHDs leave unconfirmed readers at ?", () =>
        {
            var entries = EmulatorSupport.For("playstation", "chd", ["cdzs", "cdzl", "cdfl"]);
            return Expect(entries, ("ePSXe", SupportState.Unknown), ("DuckStation", SupportState.Works), ("Beetle PSX", SupportState.Works),
                       ("Mednafen", SupportState.DoesNot))
                   ?? (Find(entries, "DuckStation")!.Detail.Contains("2024") ? null : "DuckStation's tooltip doesn't name the 2024 builds");
        });

        test("Works with: PlayStation ISO, BIN/CUE and an ES-DE system id", () =>
        {
            var iso = EmulatorSupport.For("playstation", ".iso", []);
            var cue = EmulatorSupport.For("psx", ".cue", []);
            return Expect(iso, ("Beetle PSX", SupportState.DoesNot), ("Beetle PSX HW", SupportState.DoesNot), ("SwanStation", SupportState.Works),
                       ("Mednafen", SupportState.DoesNot))
                   ?? Expect(cue, ("Mednafen", SupportState.Works), ("PCSX-Redux", SupportState.Works), ("Beetle PSX", SupportState.Works));
        });

        test("Works with: GameCube RVZ, and CHD that Dolphin can't read", () =>
        {
            var rvz = EmulatorSupport.For("gamecube", ".rvz", []);
            var chd = EmulatorSupport.For("gamecube", ".chd", []);
            return Expect(rvz, ("Dolphin", SupportState.Works)) ?? (chd.Any(e => e.Name == "Dolphin" && e.State != SupportState.DoesNot) ? "Dolphin opens CHD" : null);
        });

        test("Works with: an unknown system or no output gives an empty list", () =>
        {
            if (EmulatorSupport.For("no-such-system", ".chd", []).Count != 0) return "an unknown system listed emulators";
            if (EmulatorSupport.For(EmulatorCatalog.Current, null, null, null).Count != 0) return "no system listed emulators";
            if (EmulatorSupport.For("playstation", "", []).Count != 0) return "no extension listed emulators";
            // Every registry system, with its own target: no crash, and no more standalone emulators than the cap.
            foreach (var system in TargetFormats.All)
            {
                var entries = EmulatorSupport.For(system.Id, system.Target.Format, []);
                if (entries.Count(e => e.Group == EmulatorGroup.Standalone) > EmulatorSupport.MaxStandalone) return $"{system.Id} lists too many standalone emulators";
            }
            return null;
        });

        test("Works with: the bundled data is consistent", () =>
        {
            var catalog = EmulatorCatalog.Load();
            var known = catalog.Snapshot.Cores.Select(c => ("core", c.Id))
                .Concat(catalog.Snapshot.Standalone.Select(e => ("standalone", e.Id)))
                .Concat(catalog.Facts.ExtraStandalone.Select(e => ("standalone", e.Id)))
                .ToHashSet();
            var unknown = catalog.Facts.Facts.Where(f => !known.Contains((f.Kind, f.Emulator))).Select(f => $"{f.Kind} {f.Emulator}").Distinct().ToList();
            if (unknown.Count > 0) return "facts about unknown emulators: " + string.Join(", ", unknown);
            var badSystems = catalog.Snapshot.Cores.SelectMany(c => c.Systems).Concat(catalog.Snapshot.Systems.Keys)
                .Concat(catalog.Facts.ExtraStandalone.SelectMany(e => e.Systems))
                .Where(id => TargetFormats.ById(id) is null).Distinct().ToList();
            if (badSystems.Count > 0) return "system ids not in the registry: " + string.Join(", ", badSystems);
            if (catalog.Facts.Facts.Any(f => (f.Extension is null) == (f.Codec is null) || f.Source.Length == 0)) return "a fact needs one extension or codec, and a source";
            foreach (var id in new[] { "playstation", "playstation-2", "playstation-portable", "dreamcast", "saturn", "gamecube", "wii" })
                if (!catalog.Snapshot.Cores.Any(c => c.Systems.Contains(id))) return $"no RetroArch cores for {id}";
            return catalog.SourceText.StartsWith("From libretro core info") ? Note(catalog.SourceText) : $"source text \"{catalog.SourceText}\"";
        });

        test("Works with: the view model recomputes on every change", () =>
        {
            var vm = new WorksWithViewModel();
            if (vm.HasAny) return "an empty view model lists emulators";
            vm.Set("playstation", ".chd", DefaultCdCodecs);
            if (!vm.HasCores || !vm.HasStandalone) return "PlayStation lists no cores or no standalone emulators";
            if (vm.Standalone.First(e => e.Name == "ePSXe").State != SupportState.Works) return "ePSXe isn't ✓ for a default CHD";
            vm.Codecs = ["cdzs", "cdzl", "cdfl"];
            if (vm.Standalone.First(e => e.Name == "ePSXe").State != SupportState.Unknown) return "changing the codecs didn't recompute";
            vm.OutputExtension = ".iso";
            if (vm.Cores.First(e => e.Name == "Beetle PSX").State != SupportState.DoesNot) return "changing the extension didn't recompute";
            vm.SystemId = "nowhere";
            if (vm.HasAny) return "an unknown system still lists emulators";
            return vm.Source.Length > 0 ? null : "no source text";
        });

        test("Works with: a newer download wins over the bundled data", () =>
        {
            var folder = EmulatorCatalog.Folder;
            var bundled = EmulatorCatalog.Load();
            var newer = Retimed(bundled, 1, out var newerSnapshot, out var newerFacts);
            Directory.CreateDirectory(folder);
            try
            {
                // An older copy in the settings folder is ignored.
                Retimed(bundled, -1, out var olderSnapshot, out _);
                File.WriteAllText(Path.Combine(folder, EmulatorCatalog.SnapshotFile), olderSnapshot);
                if (EmulatorCatalog.Load().Snapshot.Generated != bundled.Snapshot.Generated) return "an older download replaced the bundled data";

                var result = EmulatorCatalog.CheckNowAsync(handler: new FakeRaw(newerSnapshot, newerFacts)).GetAwaiter().GetResult();
                if (result.Problem is not null) return result.Problem;
                if (!result.Updated) return "a newer download wasn't kept";
                if (EmulatorCatalog.Current.Snapshot.Generated != newer) return "Current wasn't reloaded";
                if (!result.Source.Contains(EmulatorCatalog.FormatDate(newer))) return $"source text \"{result.Source}\"";
                var again = EmulatorCatalog.CheckNowAsync(handler: new FakeRaw(newerSnapshot, newerFacts)).GetAwaiter().GetResult();
                return again.Updated ? "the same download was kept twice" : null;
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
                EmulatorCatalog.Reload();
            }
        });
    }

    /// <summary>The bundled files with their dates moved by <paramref name="days"/>; returns the snapshot's new date.</summary>
    private static DateOnly Retimed(EmulatorCatalog catalog, int days, out string snapshot, out string facts)
    {
        var generated = catalog.Snapshot.Generated.AddDays(days);
        var s = JsonNode.Parse(Resource(EmulatorCatalog.SnapshotFile))!;
        s["generated"] = generated.ToString("yyyy-MM-dd");
        snapshot = s.ToJsonString();
        var f = JsonNode.Parse(Resource(EmulatorCatalog.FactsFile))!;
        f["updated"] = catalog.Facts.Updated.AddDays(days).ToString("yyyy-MM-dd");
        facts = f.ToJsonString();
        return generated;
    }

    private static string Resource(string file)
    {
        using var stream = typeof(EmulatorCatalog).Assembly.GetManifestResourceStream("NoIntrovert.Emulators." + file)!;
        return new StreamReader(stream).ReadToEnd();
    }

    private static EmulatorSupportEntry? Find(IReadOnlyList<EmulatorSupportEntry> entries, string name) => entries.FirstOrDefault(e => e.Name == name);

    private static string? Expect(IReadOnlyList<EmulatorSupportEntry> entries, params (string Name, SupportState State)[] expected)
    {
        foreach (var (name, state) in expected)
        {
            var entry = Find(entries, name);
            if (entry is null) return $"{name} isn't listed (has {string.Join(", ", entries.Select(e => e.Name))})";
            if (entry.State != state) return $"{name} is {entry.State}, not {state} ({entry.Detail})";
        }
        return null;
    }

    private static string Note(string text) => "\u0001" + text;

    /// <summary>Answers the raw GitHub URLs with the given files.</summary>
    private sealed class FakeRaw(string snapshot, string facts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var body = url.EndsWith(EmulatorCatalog.SnapshotFile) ? snapshot : url.EndsWith(EmulatorCatalog.FactsFile) ? facts : null;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
