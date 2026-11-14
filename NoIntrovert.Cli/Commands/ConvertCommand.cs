using System.CommandLine;
using System.Text.Json.Nodes;
using NoIntrovert.Cli.Cli;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Pipelines;
using NoIntrovert.Core.Presets;

namespace NoIntrovert.Cli.Commands;

/// <summary>noi convert: the Convert page (docs/requirements/cli.md §5.2).</summary>
public static class ConvertCommand
{
    private static readonly Argument<string[]> PathsArg = PathOptions.Paths();

    public static Command Command()
    {
        var c = new Command("convert", "Convert to each system's best format (CHD, RVZ, 7z, NSZ…)");
        c.Arguments.Add(PathsArg);
        foreach (var o in FormatOptions.All.Concat(SaveOptions.All).Concat(PathOptions.All).Concat(TuningOptions.All).Concat(KeyOptions.All)) c.Options.Add(o);
        return c;
    }

    public static HelpDoc Doc => new(
        "Convert files to each system's best format.",
        "Each file becomes its system's recommended format, unless you choose another preset: disc images → CHD, GameCube/Wii → RVZ, " +
        "Wii U → .wua, Switch → .nsz, cartridge ROMs → .7z. New files are verified. Originals are kept unless you use --replace.\n\n" +
        "A folder brings in what isn't in its system's format yet; a CHD, RVZ or .wua named on its own is converted back (to cue/bin, ISO…).",
        "noi convert <files or folders…> [options]",
        [
            ("noi convert D:\\Roms", "Everything to its recommended format, next to the originals."),
            ("noi convert D:\\Roms -o E:\\Library --layout esde", "Into E:\\Library\\roms\\<system>\\, as ES-DE expects."),
            ("noi convert D:\\GC D:\\Wii -p smallest", "GameCube and Wii discs as the smallest RVZ."),
            ("noi convert D:\\PS2 -p ps2=zso-opl", "PS2 discs as ZSO for OPL on a real PS2; other systems as usual."),
            ("noi convert Game.chd --to cue", "A CHD back to cue/bin."),
            ("noi convert D:\\Roms --replace -y -q", "Replace each original once its new file verifies; no questions."),
        ],
        [
            new("WHAT TO MAKE", FormatOptions.All),
            new("WHERE TO SAVE", SaveOptions.All, "(default: next to the originals)"),
            new("WHICH FILES", PathOptions.All),
            new("OUTPUT", [GlobalOptions.DryRun, GlobalOptions.Yes, GlobalOptions.Quiet, GlobalOptions.Silent, GlobalOptions.Verbose, GlobalOptions.Json, GlobalOptions.Ndjson, GlobalOptions.Jobs]),
            new("KEYS", KeyOptions.All, "(found where the app finds them; noi keys)"),
            new("CUSTOM COMPRESSION SETTINGS", TuningOptions.All, Advanced: true),
        ]);

    public static async Task<int> Run(ParseResult r, Session s)
    {
        var started = DateTime.UtcNow;
        var request = Request(r, s.Settings.Presets);
        var paths = Common.Paths(r, PathsArg, "noi convert");
        var filter = Common.Filter(r);
        var inputs = Common.Gather(s, paths, Inputs.Kind.Convert, filter);
        Common.ApplySaveTo(r, s, inputs.Folders);
        Common.ApplyKeys(r, s);

        var rows = ConvertPipeline.Choose(inputs.Items, request, s.Settings);
        var (jobs, plan, converting) = ConvertPipeline.Plan(rows, s.Settings, s.Log);
        var left = rows.Where(x => x.Skip is not null || x.Target == TargetKind.Leave).ToList();
        if (plan.Error is { } error)
        {
            s.Ui.Error(error);
            return Exit.Missing;
        }
        foreach (var w in plan.Warnings) s.Ui.Warn(w);

        if (s.DryRun || jobs.Count == 0)
        {
            ShowPlan(s, rows);
            Report(s, started, rows, [], jobs.Count == 0 && !s.DryRun ? "Nothing to convert: every file is already in its format or left as it is." : null);
            return Exit.Ok;
        }
        if (Common.ConfirmReplace(s, converting.Count(x => x.Target is not (TargetKind.Iso or TargetKind.DiscImage))) is { } stop) return stop;

        s.Ui.Status("Converting", $"{Words.Count(converting.Count, "file")} ({Common.Size(converting.Sum(x => x.Item.Size))}) with {Words.Count(s.Settings.Performance.ParallelJobs, "job")} at a time, {DescribeSaveTo(s)}", s.Ui.Paint.BoldCyan);
        foreach (var row in left.Where(x => s.Ui.IsVerbose)) s.Ui.Status("Skipped", $"{row.Item.Name} — {row.Skip ?? "left as it is"}", s.Ui.Paint.Dim);
        await Common.RunAsync(s, "convert", "Converting", jobs, (job, prefix) => Finished(s, job, prefix));
        return Report(s, started, rows, jobs, null);
    }

    private static string DescribeSaveTo(Session s)
    {
        var o = s.Settings.Output;
        return o.SaveTo switch
        {
            SaveTo.Folder => $"saving to {o.Folder} ({o.FolderLayout switch { FolderLayout.RomM => "RomM layout", FolderLayout.SameAsOriginals => "the same subfolders", FolderLayout.Flat => "no subfolders", _ => "ES-DE layout" }})",
            SaveTo.ReplaceOriginals => "replacing the originals",
            _ => "next to the originals",
        };
    }

    private static void Finished(Session s, RunJob job, string prefix)
    {
        var o = job.Outcome!;
        var (word, color) = Common.Look(s.Ui, o.Status, "Converted");
        var text = $"{prefix}{job.Name}{(o.Summary.Length > 0 ? $" {s.Ui.Sym.Dot} {o.Summary}" : "")}";
        if (s.Ui.Term.LiveBars || !s.Ui.ShowsResults) s.Ui.Status(word, text, color);
        else s.Ui.Line($"{prefix}{word.ToLowerInvariant(),-10} {job.Name}{(o.Summary.Length > 0 ? $": {o.Summary}" : "")}");
        if (o.Status == JobStatus.Failed) s.Ui.QuietFailure($"{job.Name}: {o.Summary}");
        s.Ui.Event("file-done", new JsonObject
        {
            ["name"] = job.Name, ["paths"] = Json.Array(job.Paths), ["result"] = o.Status.ToString().ToLowerInvariant(), ["summary"] = o.Summary,
            ["elapsedMs"] = (long)job.Elapsed.TotalMilliseconds,
        });
    }

    private static void ShowPlan(Session s, IReadOnlyList<ConvertRow> rows)
    {
        if (rows.Count == 0) return;
        s.Ui.Table(["File", "System", "Preset", "Becomes"], rows.Select(r => (IReadOnlyList<string>)
        [
            r.Item.Name,
            TargetFormats.NameOf(r.Item.System),
            r.Preset is { } p ? $"{p.Id}" : s.Ui.Paint.Dim("—"),
            r.Skip is { } why ? s.Ui.Paint.Dim(why) : $"{Becomes(r)}",
        ]));
    }

    private static string Becomes(ConvertRow r) =>
        r.Values?.OutputExtension(r.Item.System) is { } ext ? ext : r.Preset?.Format ?? r.Target.ToString();

    private static int Report(Session s, DateTime started, IReadOnlyList<ConvertRow> rows, IReadOnlyList<RunJob> jobs, string? note)
    {
        var ok = jobs.Count(j => j.Outcome?.Status is JobStatus.Succeeded or JobStatus.Warning);
        var failed = jobs.Where(j => j.Outcome?.Status == JobStatus.Failed).ToList();
        var cancelled = jobs.Count(j => j.Outcome?.Status == JobStatus.Cancelled) + (s.Token.IsCancellationRequested ? 1 : 0);
        var skipped = rows.Count(r => r.Skip is not null || r.Target == TargetKind.Leave) + jobs.Count(j => j.Outcome?.Status == JobStatus.Skipped);
        var inBytes = jobs.Where(j => j.Outcome?.Status is JobStatus.Succeeded or JobStatus.Warning).Sum(j => j.Outcome!.Stats?.InputBytes ?? 0);
        var outBytes = jobs.Where(j => j.Outcome?.Status is JobStatus.Succeeded or JobStatus.Warning).Sum(j => j.Outcome!.Stats?.OutputBytes ?? 0);
        var elapsed = DateTime.UtcNow - started;
        var code = Exit.Combine(
            s.Token.IsCancellationRequested ? Exit.Cancelled : 0,
            failed.Any(j => !Common.MissingKeys(j.Outcome)) ? Exit.Failed : 0,
            failed.Any(j => Common.MissingKeys(j.Outcome)) ? Exit.Missing : 0);

        if (note is not null) s.Ui.Line(note);
        if (jobs.Count > 0)
        {
            s.Ui.Line();
            s.Ui.Status("Finished", $"{Words.Count(jobs.Count, "job")} in {Common.Elapsed(elapsed)}", failed.Count > 0 ? s.Ui.Paint.BoldYellow : s.Ui.Paint.BoldGreen);
            var table = new List<IReadOnlyList<string>> { new[] { "Converted", ok.ToString("N0"), inBytes > 0 && outBytes > 0 ? $"{Common.Size(inBytes)} {s.Ui.Sym.Arrow} {Common.Size(outBytes)}" : "" } };
            if (skipped > 0) table.Add(["Skipped", skipped.ToString("N0"), "already in its format, or left"]);
            if (failed.Count > 0) table.Add([s.Ui.Paint.Red("Failed"), failed.Count.ToString("N0"), "see above"]);
            if (cancelled > 0) table.Add([s.Ui.Paint.Yellow("Cancelled"), cancelled.ToString("N0"), "nothing half-written was kept"]);
            s.Ui.Table(["Result", "Files", ""], table, new HashSet<int> { 1 });
            if (s.Log.Path is { } log) s.Ui.Info($"Log: {log}");
        }
        s.Ui.QuietSummary($"done: {Words.Count(rows.Count, "file")}, {ok} converted, {failed.Count} failed, {skipped} skipped" +
                          (inBytes > outBytes && outBytes > 0 ? $", {Common.Size(inBytes - outBytes)} saved" : "") + $", {Common.Elapsed(elapsed)}");

        var files = new JsonArray();
        foreach (var row in rows.Where(r => r.Skip is not null || r.Target == TargetKind.Leave))
            files.Add(new JsonObject { ["path"] = row.Item.Path, ["system"] = row.Item.System, ["result"] = "skipped", ["reason"] = row.Skip ?? "left as it is" });
        foreach (var job in jobs)
        {
            var mine = (List<ConvertRow>)job.Tag!;
            foreach (var row in mine)
                files.Add(new JsonObject
                {
                    ["path"] = row.Item.Path, ["system"] = row.Item.System, ["preset"] = row.Preset?.Id, ["target"] = row.Target.ToString(),
                    ["result"] = job.Outcome?.Status.ToString().ToLowerInvariant() ?? (s.DryRun ? "planned" : "not-run"), ["summary"] = job.Outcome?.Summary,
                    ["inBytes"] = job.Outcome?.Stats?.InputBytes, ["outBytes"] = job.Outcome?.Stats?.OutputBytes,
                });
        }
        if (s.DryRun)
            foreach (var row in rows.Where(r => r.Skip is null && r.Target != TargetKind.Leave))
                files.Add(new JsonObject { ["path"] = row.Item.Path, ["system"] = row.Item.System, ["preset"] = row.Preset?.Id, ["target"] = row.Target.ToString(), ["result"] = "planned" });
        var doc = Common.Document("convert", code, elapsed);
        doc["dryRun"] = s.DryRun;
        doc["summary"] = new JsonObject { ["files"] = rows.Count, ["converted"] = ok, ["skipped"] = skipped, ["failed"] = failed.Count, ["cancelled"] = cancelled, ["inBytes"] = inBytes, ["outBytes"] = outBytes };
        doc["files"] = files;
        s.Ui.Json(doc);
        s.Ui.Event("summary", new JsonObject { ["exitCode"] = code, ["converted"] = ok, ["failed"] = failed.Count, ["skipped"] = skipped });
        return code;
    }

    // ---- The request: presets, formats, Custom values ----

    public static ConvertRequest Request(ParseResult r, PresetSettings presets)
    {
        string? preset = null;
        FormatChoice? to = null;
        var presetBySystem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toBySystem = new Dictionary<string, FormatChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in r.GetValue(FormatOptions.Preset) ?? [])
        {
            if (Split(p) is { } pair)
            {
                var system = Common.ResolveSystem(pair.System);
                CheckSystemPreset(system, pair.Value, presets);
                presetBySystem[system] = pair.Value;
            }
            else
            {
                if (!ConvertPipeline.IsKnownPreset(p, presets))
                {
                    var ids = TargetFormats.All.SelectMany(x => FormatPresets.ForSystem(x.Id)).Select(x => x.Id).Distinct();
                    var guess = Suggest.Closest(p, ids);
                    throw new UsageException($"Unknown preset \"{p}\".", (guess is null ? "" : $"Did you mean \"{guess}\"?  ") + "noi presets lists them.");
                }
                preset = p;
            }
        }
        foreach (var t in r.GetValue(FormatOptions.To) ?? [])
        {
            if (Split(t) is { } pair) toBySystem[Common.ResolveSystem(pair.System)] = Format(pair.Value);
            else to = Format(t);
        }
        var keep = (r.GetValue(FormatOptions.Keep) ?? []).SelectMany(k => k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Common.ResolveSystem).ToList();
        return new ConvertRequest
        {
            Preset = preset, To = to, PresetBySystem = presetBySystem, ToBySystem = toBySystem, Keep = keep,
            Tune = Tune(r), NoVerify = r.GetValue(TuningOptions.NoVerify),
        };
    }

    private static (string System, string Value)? Split(string word)
    {
        var i = word.IndexOf('=');
        return i > 0 ? (word[..i], word[(i + 1)..]) : null;
    }

    private static FormatChoice Format(string word) =>
        ConvertPipeline.FormatNamed(word) ?? throw new UsageException($"Unknown format \"{word}\".",
            (Suggest.Closest(word, ConvertPipeline.Formats.Select(f => f.Word)) is { } g ? $"Did you mean \"{g}\"?  " : "") +
            "Formats: " + string.Join(", ", ConvertPipeline.Formats.Select(f => f.Word)));

    private static void CheckSystemPreset(string system, string word, PresetSettings presets)
    {
        var own = FormatPresets.ForSystem(system).Concat(FormatPresets.Saved(presets, system))
            .Concat(Enum.GetValues<TargetKind>().Select(FormatPresets.StandardFor)).Where(p => !p.IsCustom).ToList();
        if (ConvertPipeline.Find(word, own) is not null) return;
        var ids = FormatPresets.ForSystem(system).Select(p => p.Id).ToList();
        throw new UsageException($"Unknown preset \"{word}\" for {TargetFormats.NameOf(system)}.",
            (Suggest.Closest(word, ids) is { } g ? $"Did you mean \"{g}\"?  " : "") + $"Presets for it: noi presets {system}");
    }

    /// <summary>The Custom values given, applied over each file's preset; null when none were.</summary>
    private static Action<PresetValues>? Tune(ParseResult r)
    {
        var changes = new List<Action<PresetValues>>();
        if (r.GetValue(TuningOptions.ChdCodecs) is { } codecs) changes.Add(v => v.Compression = codecs);
        if (r.GetValue(TuningOptions.ChdHunk) is { } hunk) changes.Add(v => v.HunkSize = hunk);
        if (r.GetValue(TuningOptions.IsoMode) is { } iso) changes.Add(v => v.IsoMode = Enum.Parse<IsoMode>(iso, true));
        if (r.GetValue(TuningOptions.RvzLevel) is { } level)
        {
            if (level is < 1 or > 22) throw new UsageException("--rvz-level is 1 to 22.", "5 is Dolphin's default; 19 is the smallest the app's presets use.");
            changes.Add(v => v.RvzLevel = level);
        }
        if (r.GetValue(TuningOptions.RvzBlock) is { } block) { var bytes = BlockSize(block); changes.Add(v => v.RvzBlockSize = bytes); }
        if (r.GetValue(TuningOptions.RvzCodec) is { } codec)
            changes.Add(v => v.RvzCompression = Enum.Parse<LibWiiGameCubeKit.RvzCompression>(codec, true));
        if (r.GetValue(TuningOptions.Scrub)) changes.Add(v => v.Scrub = true);
        if (r.GetValue(TuningOptions.ArchiveLevel) is { } al) changes.Add(v => v.ArchiveLevel = Enum.Parse<ArchiveLevel>(al, true));
        if (r.GetValue(TuningOptions.NoSolid)) changes.Add(v => v.Solid = false);
        if (r.GetValue(TuningOptions.Group) is { } group)
            changes.Add(v => v.Grouping = group switch { "per-entry" => ArchiveGrouping.PerEntryInFolders, "single" => ArchiveGrouping.Single, _ => ArchiveGrouping.PerItem });
        if (r.GetValue(TuningOptions.ArchiveName) is { } name) changes.Add(v => v.SingleName = name);
        if (r.GetValue(TuningOptions.ExtractFormat) is { } ef)
            changes.Add(v => v.ExtractFormat = ef switch { "cue" => ExtractFormat.CueBin, "gdi" => ExtractFormat.Gdi, "iso" => ExtractFormat.Iso, "raw" => ExtractFormat.Raw, _ => ExtractFormat.Auto });
        if (r.GetValue(TuningOptions.SingleBin)) changes.Add(v => v.SplitBin = false);
        if (r.GetValue(TuningOptions.UnpackHere)) changes.Add(v => v.UnpackToOwnFolder = false);
        if (changes.Count == 0) return null;
        return v => { foreach (var c in changes) c(v); };
    }

    private static uint BlockSize(string text)
    {
        var t = text.Trim().ToUpperInvariant().TrimEnd('B');
        var multiplier = t.EndsWith('K') ? 1024u : t.EndsWith('M') ? 1024u * 1024 : 1u;
        if (multiplier > 1) t = t[..^1];
        if (!uint.TryParse(t, out var n) || n == 0) throw new UsageException($"--rvz-block \"{text}\" isn't a size.", "Examples: 32K, 128K, 2M.");
        var bytes = n * multiplier;
        if (bytes < 0x8000 || bytes > 0x200000 || (bytes & (bytes - 1)) != 0)
            throw new UsageException($"--rvz-block {text}: RVZ blocks are a power of two from 32K to 2M.", "128K is Dolphin's default.");
        return bytes;
    }
}
