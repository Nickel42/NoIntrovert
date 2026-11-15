using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using NoIntrovert.Cli;
using NoIntrovert.Cli.Cli;
using CliApp = NoIntrovert.Cli.Cli.App;
using NoIntrovert.Core;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Pipelines;

namespace NoIntrovert.Tests;

/// <summary>
/// noi, the command line (docs/requirements/cli.md): its help, its parsing (Windows-style options too), exit codes and
/// output modes, and real runs of convert, verify, organize and undo, through noi itself in a process of its own.
/// </summary>
internal static class CliTests
{
    public static void Run(Action<string, Func<string?>> test, string scratch)
    {
        var settingsDir = Path.Combine(scratch, "cli-settings");
        Directory.CreateDirectory(settingsDir);
        var noi = Path.Combine(AppContext.BaseDirectory, "noi.dll");

        (int Code, string Out, string Err) Noi(params string[] args)
        {
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
            info.ArgumentList.Add(noi);
            foreach (var a in args) info.ArgumentList.Add(a);
            info.Environment["NOINTROVERT_SETTINGS_DIR"] = settingsDir;
            info.Environment["NO_COLOR"] = "1";
            info.Environment.Remove("CI");
            using var p = Process.Start(info)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000)) { p.Kill(true); return (-1, stdout.Result, "timed out"); }
            return (p.ExitCode, stdout.Result, stderr.Result);
        }

        // A NES ROM the databases don't know (a valid iNES header, then a pattern), in a game folder of its own.
        string MakeRom(string folder, string name, byte seed)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            var data = new byte[16 + 32768];
            "NES\u001a"u8.CopyTo(data);
            data[4] = 2;
            for (var i = 16; i < data.Length; i++) data[i] = (byte)(i * 31 + seed);
            File.WriteAllBytes(path, data);
            return path;
        }

        // ---- Help ----

        test("noi: /?, -?, -h, --help and help all give the short help, with exit code 0", () =>
        {
            foreach (var args in new[] { new[] { "/?" }, ["-?"], ["-h"], ["--help"], ["help"], [] })
            {
                var (code, output, _) = Noi(args);
                if (code != 0) return $"noi {string.Join(' ', args)} exited {code}";
                foreach (var part in new[] { "USAGE", "MAIN COMMANDS", "optimize", "convert", "verify", "organize", "EXAMPLES", "COMMON OPTIONS", "Exit codes" })
                    if (!output.Contains(part)) return $"noi {string.Join(' ', args)}: no \"{part}\" in the help";
            }
            return null;
        });

        test("noi: every command's help has usage and examples, and lists every option it takes", () =>
        {
            var app = new CliApp();
            var problems = new List<string>();
            foreach (var (command, doc) in app.Docs)
            {
                var text = HelpWriter.Render(command, doc, new Cli.Output.Paint(false), all: true, width: 100);
                if (!text.Contains("USAGE")) problems.Add($"{command.Name}: no usage");
                if (doc.Examples.Count == 0 && command.Name is not ("list" or "status" or "path")) problems.Add($"{command.Name}: no examples");
                foreach (var option in command.Options.Where(o => !o.Hidden && !GlobalOptions.All.Contains(o)))
                    if (!doc.Groups.Any(g => g.Options.Contains(option)) && !text.Contains(option.Name))
                        problems.Add($"{command.Name}: {option.Name} isn't in its help");
            }
            return problems.Count == 0 ? null : string.Join("; ", problems.Take(8));
        });

        test("noi: every example in the help parses", () =>
        {
            var app = new CliApp();
            var problems = new List<string>();
            var examples = app.Docs.Values.SelectMany(d => d.Examples).Select(e => e.Command).Distinct()
                .Where(e => e.StartsWith("noi ") && !e.Contains("||") && !e.Contains(">>") && !e.Contains(" > "));
            foreach (var example in examples)
            {
                var words = Words(example[4..]);
                var parse = app.Root.Parse(WindowsArgs.Normalize(words, app.OptionNames()));
                if (parse.Errors.Count > 0) problems.Add($"{example}: {parse.Errors[0].Message}");
                else if (!app.Handlers.ContainsKey(parse.CommandResult.Command) && parse.CommandResult.Command.Name != "help")
                    problems.Add($"{example}: no command runs");
            }
            return problems.Count == 0 ? null : string.Join("; ", problems.Take(6));
        });

        test("noi: a command's --help and /? show its own page, examples first", () =>
        {
            foreach (var args in new[] { new[] { "convert", "--help" }, ["convert", "/?"], ["help", "convert"], ["convert", "--to", "/?"] })
            {
                var (code, output, _) = Noi(args);
                if (code != 0) return $"{string.Join(' ', args)} exited {code}";
                if (!output.Contains("WHERE TO SAVE") || !output.Contains("noi convert D:\\Roms")) return $"{string.Join(' ', args)}: not convert's help";
                if (output.IndexOf("EXAMPLES", StringComparison.Ordinal) > output.IndexOf("WHAT TO MAKE", StringComparison.Ordinal)) return "examples should come before the options";
                if (output.Contains("--chd-codecs")) return "Custom compression options belong in --help-all";
            }
            var (_, all, _) = Noi("convert", "--help-all");
            return all.Contains("--chd-codecs") && all.Contains("--rvz-level") ? null : "--help-all should list the Custom values";
        });

        // ---- Parsing ----

        test("noi: Windows-style options become their - and -- forms; paths that exist stay paths", () =>
        {
            var names = new CliApp().OptionNames();
            string Show(params string[] args) => string.Join(" ", WindowsArgs.Normalize(args, names));
            var cases = new (string[] In, string Out)[]
            {
                (["/?"], "--help"), (["/q"], "-q"), (["/Q"], "-q"), (["/quiet"], "--quiet"), (["/o:C:\\Out"], "-o C:\\Out"),
                (["/output=D:\\x"], "--output D:\\x"), (["/jobs:4"], "--jobs 4"), (["/y", "/n"], "-y -n"), (["/layout:esde"], "--layout esde"),
                (["/nonsense"], "/nonsense"), (["-q", "D:\\Roms"], "-q D:\\Roms"),
            };
            var wrong = cases.Where(c => Show(c.In) != c.Out).Select(c => $"{string.Join(' ', c.In)} → {Show(c.In)}").ToList();
            var existing = Directory.CreateDirectory(Path.Combine(scratch, "q")).FullName;
            if (!OperatingSystem.IsWindows() && Show(existing) != existing) wrong.Add("an existing path was taken for an option");
            return wrong.Count == 0 ? null : string.Join("; ", wrong);
        });

        test("noi: exit codes combine in the documented order (2, 130, 5, 1, 4, 3, 0)", () =>
        {
            var cases = new (int[] In, int Out)[]
            {
                ([0, 0], 0), ([3, 1], 1), ([4, 3], 4), ([1, 4], 1), ([5, 1], 5), ([130, 5], 130), ([2, 130], 2), ([3], 3),
            };
            var wrong = cases.Where(c => Exit.Combine(c.In) != c.Out).Select(c => $"{string.Join(",", c.In)} → {Exit.Combine(c.In)}").ToList();
            return wrong.Count == 0 ? null : string.Join("; ", wrong);
        });

        test("noi: wrong commands, options and paths exit 2 with a suggestion; --silent says nothing", () =>
        {
            var (c1, _, e1) = Noi("verfy", "x");
            if (c1 != 2 || !e1.Contains("Did you mean \"verify\"")) return $"a typo: {c1} {e1}";
            var (c2, _, e2) = Noi("verify", "--onl", "damaged", scratch);
            if (c2 != 2 || !e2.Contains("--only")) return $"an unknown option: {c2} {e2}";
            var (c3, _, _) = Noi("verify", Path.Combine(scratch, "no-such-folder"));
            if (c3 != 2) return $"a missing path should exit 2, not {c3}";
            var (c4, o4, e4) = Noi("verify", Path.Combine(scratch, "no-such-folder"), "--silent");
            if (c4 != 2 || o4.Length + e4.Length > 0) return $"--silent printed \"{o4}{e4}\"";
            var (c5, _, e5) = Noi("convert", scratch, "-p", "smalest");
            return c5 == 2 && e5.Contains("Did you mean \"smallest\"") ? null : $"an unknown preset: {c5} {e5}";
        });

        test("noi: inputs follow --include, --exclude and --system; globs match names and paths", () =>
        {
            if (!Glob.Matches("*.cue", "D:/Roms/a/Game.cue") || Glob.Matches("*.cue", "D:/Roms/Game.bin")) return "a name pattern";
            if (!Glob.Matches("**/hidden/**", "D:\\Roms\\psx\\hidden\\Disc 1.chd") || Glob.Matches("**/hidden/**", "D:/Roms/psx/Game.chd")) return "a path pattern";
            if (!Glob.Matches("Mega Man ?.nes", "x/Mega Man 2.nes")) return "?";
            var dir = Path.Combine(scratch, "cli-inputs");
            MakeRom(Path.Combine(dir, "A"), "Alpha (USA).nes", 1);
            MakeRom(Path.Combine(dir, "B"), "Beta (USA).nes", 2);
            var settings = new AppSettings();
            var all = Inputs.Gather([dir], Inputs.Kind.Games, new InputFilter(), settings);
            var some = Inputs.Gather([dir], Inputs.Kind.Games, new InputFilter { Exclude = ["Beta*"] }, settings);
            var none = Inputs.Gather([dir], Inputs.Kind.Games, new InputFilter { Systems = ["playstation"] }, settings);
            return all.Items.Count == 2 && some.Items.Count == 1 && none.Items.Count == 0 && all.Folders.Count == 1
                ? null : $"found {all.Items.Count} / {some.Items.Count} / {none.Items.Count}";
        });

        test("noi: presets by id, by the end of the id and by name; formats by word", () =>
        {
            var gc = Core.Presets.FormatPresets.ForSystem("gamecube");
            if (ConvertPipeline.Find("smallest", gc)?.Id != "rvz-smallest") return "smallest";
            if (ConvertPipeline.Find("RVZ-FASTEST", gc)?.Id != "rvz-fastest") return "case";
            if (ConvertPipeline.Find("leave", gc)?.IsLeave != true) return "leave";
            if (ConvertPipeline.FormatNamed(".7z")?.Kind != Core.Formats.TargetKind.SevenZip) return "7z";
            if (ConvertPipeline.FormatNamed("cue")?.Extract != ExtractFormat.CueBin) return "cue";
            return ConvertPipeline.IsKnownPreset("zso-opl", new()) && !ConvertPipeline.IsKnownPreset("smalest", new()) ? null : "known presets";
        });

        // ---- Runs ----

        test("noi convert: a NES ROM becomes a .7z in the --output folder's ES-DE layout, reported as JSON", () =>
        {
            var dir = Path.Combine(scratch, "cli-convert");
            var rom = MakeRom(Path.Combine(dir, "in"), "Gamma (USA).nes", 3);
            var outDir = Path.Combine(dir, "out");
            var (code, output, err) = Noi("convert", rom, "-o", outDir, "--json");
            if (code != 0) return $"exit {code}: {err}";
            var doc = JsonNode.Parse(output)!;
            if ((int?)doc["summary"]?["converted"] != 1) return $"summary: {doc["summary"]?.ToJsonString()}";
            var made = Path.Combine(outDir, "roms", "nes", "Gamma (USA).7z");
            if (!File.Exists(made)) return $"no {made}";
            return File.Exists(rom) ? null : "the original should stay";
        });

        test("noi convert: --dry-run plans and writes nothing; --replace without --yes is refused where nothing can be asked", () =>
        {
            var dir = Path.Combine(scratch, "cli-dry");
            var rom = MakeRom(dir, "Delta (USA).nes", 4);
            var (code, output, _) = Noi("convert", dir, "-n");
            if (code != 0 || !output.Contains("7z-standard")) return $"dry run: {code} {output}";
            if (Directory.GetFiles(dir).Length != 1) return "a dry run wrote something";
            var (c2, _, e2) = Noi("convert", dir, "--replace", "--no-input");
            if (c2 != 2 || !e2.Contains("--yes")) return $"--replace --no-input: {c2} {e2}";
            var (c3, _, e3) = Noi("convert", dir, "--replace", "--yes", "-q");
            if (c3 != 0) return $"--replace --yes: {c3} {e3}";
            return !File.Exists(rom) && File.Exists(Path.Combine(dir, "Delta (USA).7z")) ? null : "the original should have been replaced by its .7z";
        });

        test("noi verify: a damaged zip exits 3; --only, --report and --ndjson", () =>
        {
            var dir = Path.Combine(scratch, "cli-verify");
            Directory.CreateDirectory(dir);
            var good = MakeRom(Path.Combine(dir, "good"), "Epsilon (USA).nes", 5);
            var zip = Path.Combine(dir, "Zeta (USA).zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("Zeta (USA).nes", CompressionLevel.NoCompression);
                using var s = entry.Open();
                s.Write(File.ReadAllBytes(good));
            }
            var bytes = File.ReadAllBytes(zip);
            bytes[100] ^= 0xFF; // inside the stored data: its CRC no longer matches
            File.WriteAllBytes(zip, bytes);
            var report = Path.Combine(dir, "report.csv");
            var (code, output, err) = Noi("verify", dir, "--offline", "--only", "damaged", "--report", report);
            if (code != 3) return $"expected exit 3, got {code}: {err}";
            if (!output.Contains("Zeta (USA).zip")) return "the damaged file isn't listed";
            if (!File.Exists(report) || !File.ReadAllText(report).Contains("damaged")) return "no report";
            var (_, events, _) = Noi("verify", good, "--offline", "--ndjson");
            var lines = events.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
            var kinds = lines.Select(l => (string?)l["event"]).ToList();
            return kinds.FirstOrDefault() == "start" && kinds.Contains("file-done") && kinds.LastOrDefault() == "summary" ? null : $"events: {string.Join(",", kinds)}";
        });

        test("noi organize: no terminal and no --yes shows the plan only; --plan-out, --plan, a changed file exits 5; undo", () =>
        {
            var dir = Path.Combine(scratch, "cli-organize");
            var rom = MakeRom(Path.Combine(dir, "Some Folder"), "Eta (USA).nes", 6);
            // A DAT of our own that knows it, so Verify matches it and the plan may move it.
            var dats = Directory.CreateDirectory(Path.Combine(scratch, "cli-dats")).FullName;
            var hashed = Hasher.HashFile(rom).First(h => !h.Headerless);
            File.WriteAllLines(Path.Combine(dats, "nes.dat"),
            [
                "clrmamepro (",
                "  name \"Nintendo - Nintendo Entertainment System\"",
                ")",
                "game (",
                "  name \"Eta (USA)\"",
                $"  rom ( name \"Eta (USA).nes\" size {hashed.Size} crc {hashed.Crc} sha1 {hashed.Sha1} )",
                ")",
            ]);
            var (c0, _, e0) = Noi("verify", dir, "--offline", "-q", "--dat-folder", dats);
            if (c0 != 0) return $"verify: {c0} {e0}";
            var plan = Path.Combine(scratch, "cli-plan.json");
            var (c1, o1, e1) = Noi("organize", dir);
            if (c1 != 0 || !o1.Contains("--yes") || !File.Exists(rom)) return $"plan only: {c1} {o1} {e1}";
            var (c2, _, e2) = Noi("organize", dir, "-n", "--plan-out", plan, "-q");
            if (c2 != 0 || !File.Exists(plan)) return $"--plan-out: {c2} {e2}";
            var (c3, o3, e3) = Noi("organize", "--plan", plan, "-y", "--json");
            if (c3 != 0) return $"--plan: {c3} {e3}";
            var runId = (string?)JsonNode.Parse(o3)!["runId"];
            var moved = Path.Combine(dir, "roms", "nes", "Eta (USA).7z");
            if (!File.Exists(moved)) return $"no {moved}";
            var (c4, _, _) = Noi("organize", "--plan", plan, "-y", "-q");
            if (c4 != 5) return $"running a plan whose files changed should exit 5, not {c4}";
            var (c5, _, e5) = Noi("runs", "undo", runId ?? "last", "--yes", "-q");
            if (c5 != 0) return $"undo: {c5} {e5}";
            return File.Exists(rom) && !File.Exists(moved) ? null : "undo should bring the original back and remove the .7z";
        });

        test("noi: @list.txt takes one path per line (spaces kept, # comments skipped)", () =>
        {
            var dir = Path.Combine(scratch, "cli-lists");
            var a = MakeRom(Path.Combine(dir, "With Spaces One"), "Iota One (USA).nes", 8);
            var b = MakeRom(Path.Combine(dir, "With Spaces Two"), "Kappa Two (USA).nes", 9);
            var list = Path.Combine(dir, "list.txt");
            File.WriteAllLines(list, ["# my games", a, "", $"\"{b}\""]);
            var (code, output, err) = Noi("verify", "@" + list, "--offline", "--json");
            if (code != 0) return $"@list: {code} {err}";
            return (int?)JsonNode.Parse(output)!["summary"]?["files"] == 2 ? null : $"@list found {JsonNode.Parse(output)!["summary"]?["files"]} files";
        });

        test("noi: info, presets, keys, db, settings and runs answer, also as JSON", () =>
        {
            var rom = MakeRom(Path.Combine(scratch, "cli-info"), "Theta (USA).nes", 7);
            foreach (var args in new[] { new[] { "info", rom }, ["presets", "ps2"], ["keys"], ["db"], ["settings", "path"], ["runs"] })
            {
                var (code, output, err) = Noi(args);
                if (code != 0 || output.Length == 0) return $"{string.Join(' ', args)}: {code} {err}";
                var (jc, json, je) = Noi([.. args, "--json"]);
                if (jc != 0) return $"{string.Join(' ', args)} --json: {jc} {je}";
                try { JsonNode.Parse(json); }
                catch (System.Text.Json.JsonException) { return $"{string.Join(' ', args)} --json isn't JSON: {json[..Math.Min(80, json.Length)]}"; }
            }
            var (sc, _, se) = Noi("settings", "set", "performance.parallelJobs", "3");
            var (_, value, _) = Noi("settings", "get", "performance.parallelJobs");
            return sc == 0 && value.Trim() == "3" ? null : $"settings set/get: {sc} {se} {value}";
        });

        test("Shared caches: a save keeps what another process saved since", () =>
        {
            var path = Path.Combine(scratch, "merge.json");
            var mine = new Dictionary<string, int> { ["a"] = 1 };
            File.WriteAllText(path, "{\"a\":1}");
            var file = new ThrottledJsonFile(path, () => System.Text.Json.JsonSerializer.Serialize(mine), json =>
            {
                foreach (var (k, v) in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json)!) mine.TryAdd(k, v);
            });
            // Another process writes its own entry.
            Thread.Sleep(20);
            File.WriteAllText(path, "{\"a\":1,\"theirs\":2}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            mine["b"] = 3;
            file.Changed();
            file.Flush();
            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))!;
            return saved.ContainsKey("theirs") && saved.ContainsKey("b") ? null : $"saved {string.Join(",", saved.Keys)}";
        });
    }

    /// <summary>Splits an example command line into words, keeping quoted ones together.</summary>
    private static string[] Words(string line)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ' ' && !quoted)
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) words.Add(current.ToString());
        return [.. words];
    }
}
