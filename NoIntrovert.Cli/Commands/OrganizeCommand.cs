using System.CommandLine;
using System.Text.Json.Nodes;
using NoIntrovert.Cli.Cli;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Pipelines;
using NoIntrovert.Core.Presets;

namespace NoIntrovert.Cli.Commands;

/// <summary>
/// noi organize (§5.4) and the plan-and-run half of noi optimize (§5.5): Terraform's plan → apply. The plan is shown,
/// then run when confirmed (or with --yes); without a terminal and without --yes it's shown and nothing runs.
/// </summary>
public static class OrganizeCommand
{
    private static readonly Argument<string[]> PathsArg = PathOptions.Paths();
    public static readonly Option<string?> PlanOut = new("--plan-out") { Description = "Save the plan as JSON (to review, or edit by removing steps)", HelpName = "file" };
    public static readonly Option<string?> PlanIn = new("--plan") { Description = "Run exactly this saved plan; refused if a file in it changed", HelpName = "file" };
    public static readonly Option<string?> NewLibrary = new("--new-library") { Description = "Build a new library in this folder; the files where they are don't change", HelpName = "folder" };
    public static readonly Option<bool> NoCopyUnmatched = new("--no-copy-unmatched") { Description = "With --new-library: leave out games that aren't matched" };
    public static readonly Option<bool> NoCopySaves = new("--no-copy-saves") { Description = "With --new-library: don't copy each game's saves and states along with it" };
    public static readonly Option<bool> CopyOtherFiles = new("--copy-other-files") { Description = "With --new-library: also copy every other file in the folders" };
    public static readonly Option<bool> NoConvert = new("--no-convert") { Description = "Don't convert" };
    public static readonly Option<bool> NoRename = new("--no-rename") { Description = "Don't rename" };
    public static readonly Option<bool> NoMove = new("--no-move") { Description = "Don't move into the layout's folders" };
    public static readonly Option<bool> NoDedupe = new("--no-dedupe") { Description = "Don't remove duplicates" };
    public static readonly Option<bool> VerifyFirst = new("--verify-first") { Description = "Verify the files that haven't been, then plan" };
    public static readonly Option<string?> Layout = new("--layout") { Description = "esde (default), romm, keep (convert and rename in place) or flat", HelpName = "layout" };

    static OrganizeCommand() => Layout.AcceptOnlyFromAmong("esde", "es-de", "romm", "keep", "same", "flat");

    internal static Option[] PlanOptions => [NewLibrary, NoCopyUnmatched, NoCopySaves, CopyOtherFiles, Layout, NoConvert, NoRename, NoMove, NoDedupe];

    public static Command Command()
    {
        var c = new Command("organize", "Rename, move and de-duplicate, with a plan first and undo after");
        c.Arguments.Add(PathsArg);
        foreach (var o in PlanOptions.Concat([PlanOut, PlanIn, VerifyFirst]).Concat(FormatOptions.All).Concat(PathOptions.All).Concat(KeyOptions.All)) c.Options.Add(o);
        return c;
    }

    public static HelpDoc Doc => new(
        "Convert, rename into the No-Intro / Redump names, move into system folders and remove duplicates.",
        "Shows the whole plan first, grouped by game, then asks before running it (--yes runs it straight away; a script without " +
        "--yes gets the plan and nothing else). Only files Verify has checked are renamed and moved (--verify-first checks the " +
        "others). Nothing is ever overwritten: a name clash is shown and skipped. Originals and duplicates go to a .noi-trash " +
        "folder beside them, so every run can be undone: noi runs undo last.",
        "noi organize <files or folders…> [options]",
        [
            ("noi organize D:\\Roms", "Show the plan for an ES-DE layout (roms/<system>/), then ask."),
            ("noi organize D:\\Roms --layout keep -y", "Convert and rename in place, without asking."),
            ("noi organize D:\\Roms --new-library E:\\Library", "Build a tidy copy on E:; D:\\Roms doesn't change."),
            ("noi organize D:\\Roms -n --plan-out plan.json", "Save the plan to review; nothing changes."),
            ("noi organize --plan plan.json -y", "Run exactly that plan later."),
        ],
        [
            new("PLAN", PlanOptions.Concat([VerifyFirst]).ToList()),
            new("PLAN FILES", [PlanOut, PlanIn]),
            new("WHAT TO MAKE", FormatOptions.All, "(-t takes a system's other targets: zip, 7z, bare, cso, zso, xcz…)"),
            new("WHICH FILES", PathOptions.All),
            new("OUTPUT", [GlobalOptions.DryRun, GlobalOptions.Yes, GlobalOptions.Quiet, GlobalOptions.Silent, GlobalOptions.Verbose, GlobalOptions.Json, GlobalOptions.Jobs]),
            new("KEYS", KeyOptions.All, "(for conversions; noi keys)"),
        ]);

    public static async Task<int> Run(ParseResult r, Session s)
    {
        var started = DateTime.UtcNow;
        Common.ApplyKeys(r, s);

        // A saved plan: made again from what it records, with its choices.
        if (r.GetValue(PlanIn) is { } planFile)
        {
            OrganizePipeline.PlanFile file;
            try { file = OrganizePipeline.Load(planFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                throw new UsageException($"Couldn't read the plan {planFile}: {ex.Message}");
            }
            var saved = file.AppSettings;
            saved.Performance = s.Settings.Performance;
            saved.Keys = s.Settings.Keys;
            var (made, problem) = OrganizePipeline.Replan(file, saved, s.Token);
            if (made is null)
            {
                s.Ui.Error(problem ?? "The plan can't be run.", "Nothing was run. Make the plan again: noi organize … --plan-out plan.json");
                return Exit.Blocked;
            }
            return await PlanAndRun(s, saved, made, file.Inputs, file.Folders, file.Request, started, "organize", null);
        }

        var paths = Common.Paths(r, PathsArg, "noi organize");
        var inputs = Common.Gather(s, paths, Inputs.Kind.Games, Common.Filter(r));
        var request = Prepare(r, s);
        if (r.GetValue(VerifyFirst))
        {
            var notChecked = inputs.Items.Where(i => Core.Dats.MatchStore.Default.Get(i.Path) is null).ToList();
            if (notChecked.Count > 0 && await VerifyCommand.Check(s, notChecked) is null) return Exit.Cancelled;
        }
        var plan = MakePlan(s, inputs, request);
        return await PlanAndRun(s, s.Settings, plan, inputs.Items.Select(i => i.Path).ToList(), inputs.Folders, request, started, "organize", r.GetValue(PlanOut));
    }

    /// <summary>The plan's settings from the options: layout, new library, step kinds, and the formats to make.</summary>
    internal static OrganizeRequest Prepare(ParseResult r, Session s, bool convertUnmatched = false)
    {
        if ((r.GetValue(Layout) ?? r.GetValue(SaveOptions.Layout)) is { } layout) s.Settings.Library.Layout = Common.LibraryLayoutOf(layout);
        ApplyFormats(r, s.Settings);
        var off = new List<CleanupKind>();
        if (r.GetValue(NoConvert)) off.Add(CleanupKind.Convert);
        if (r.GetValue(NoRename)) off.Add(CleanupKind.Rename);
        if (r.GetValue(NoMove)) off.Add(CleanupKind.Move);
        if (r.GetValue(NoDedupe)) off.Add(CleanupKind.RemoveDuplicate);
        var lib = r.GetValue(NewLibrary) is { } l ? Path.GetFullPath(l) : null;
        if (lib is not null) s.Settings.Library.NewLibraryFolder = lib;
        return new OrganizeRequest
        {
            NewLibrary = lib, CopyUnmatched = !r.GetValue(NoCopyUnmatched), ConvertUnmatched = convertUnmatched, Off = off,
            CopyCompanions = !r.GetValue(NoCopySaves), CopyOtherFiles = r.GetValue(CopyOtherFiles),
        };
    }

    internal static OrganizePlan MakePlan(Session s, InputList inputs, OrganizeRequest request)
    {
        s.Ui.Debug("Making the plan…");
        var plan = OrganizePipeline.Plan(inputs.Items.Select(i => i.Path).ToList(), inputs.Folders, s.Settings, request, s.Token);
        if (plan.Problem is { } problem) throw new UsageException(problem, "Choose a folder outside the ones being organized.");
        return plan;
    }

    /// <summary>Shows the plan, saves it with --plan-out, then runs it when confirmed. Returns the exit code.</summary>
    internal static async Task<int> PlanAndRun(Session s, AppSettings settings, OrganizePlan made, IReadOnlyList<string> inputs, IReadOnlyList<string> folders,
        OrganizeRequest request, DateTime started, string command, string? planOut)
    {
        var plan = made.Plan!;
        Show(s, plan);
        if (made.Unchecked.Count > 0)
            s.Ui.Warn($"{Words.Count(made.Unchecked.Count, "file")} {(made.Unchecked.Count == 1 ? "hasn't" : "haven't")} been verified, so {(made.Unchecked.Count == 1 ? "it isn't" : "they aren't")} renamed or moved. " +
                      "Add --verify-first, or use noi optimize, which verifies first.");
        if (planOut is not null)
        {
            OrganizePipeline.Save(OrganizePipeline.ToFile(plan, inputs, folders, request, settings), planOut);
            s.Ui.Info($"Plan saved: {Path.GetFullPath(planOut)}  (run it with: noi {command} --plan \"{planOut}\" -y)");
        }
        var runnable = made.Runnable;
        var games = plan.Steps.Where(x => x.Runs).Select(x => x.GameKey).Distinct().Count();
        if (runnable == 0 || s.DryRun)
        {
            return Report(s, command, started, plan, [], null, dryRun: s.DryRun, ran: false);
        }
        if (!s.Yes)
        {
            if (!s.CanAsk)
            {
                s.Ui.Answer($"Plan only: {Words.Count(runnable, "step")} for {Words.Count(games, "game")}. Nothing was changed; add --yes to run it.");
                return Report(s, command, started, plan, [], null, dryRun: true, ran: false);
            }
            if (!s.Ui.Confirm($"Run these {Words.Count(runnable, "step")} for {Words.Count(games, "game")}?"))
            {
                s.Ui.Answer("Not started: nothing was changed.");
                return Report(s, command, started, plan, [], null, dryRun: true, ran: false);
            }
        }
        var (jobs, runner) = OrganizePipeline.Jobs(plan);
        s.Ui.Status("Organizing", $"{Words.Count(runnable, "step")} for {Words.Count(jobs.Count, "game")}" +
                                  (runner.Run.NewLibrary is { } lib ? $", building a new library in {lib}" : ""), s.Ui.Paint.BoldCyan);
        // The run's settings (the plan's, for a saved plan) with this run's jobs and keys.
        await Common.RunAsync(WithSettings(s, settings), command, "Organizing", jobs, (job, prefix) => Finished(s, job, prefix));
        return Report(s, command, started, plan, jobs, runner.Run, dryRun: false, ran: true);
    }

    private static Session WithSettings(Session s, AppSettings settings)
    {
        if (!ReferenceEquals(s.Settings, settings))
        {
            settings.Performance = s.Settings.Performance;
            settings.General.VerboseLog = s.Settings.General.VerboseLog;
        }
        return s.With(settings);
    }

    private static void Show(Session s, CleanupPlan plan)
    {
        if (!s.Ui.ShowsResults || plan.Steps.Count == 0) return;
        var p = s.Ui.Paint;
        // Games with nothing to do are counted in the summary; --verbose lists them too.
        var shown = plan.Steps.Where(x => s.Ui.IsVerbose || x.Kind != CleanupKind.Untouched).GroupBy(x => x.GameKey).ToList();
        if (shown.Count > 0) s.Ui.Line(p.Bold("PLAN"));
        foreach (var game in shown)
        {
            var steps = game.ToList();
            s.Ui.Line($"  {p.Bold(steps[0].Game)} {p.Dim("(" + TargetFormats.NameOf(steps[0].SystemId) + ")")}");
            foreach (var step in steps)
            {
                var (kind, color) = step.Kind switch
                {
                    CleanupKind.Convert => ("convert", (Func<string, string>)p.Cyan),
                    CleanupKind.Rename => ("rename", p.Green),
                    CleanupKind.Move => ("move", p.Green),
                    CleanupKind.Copy => ("copy", p.Green),
                    CleanupKind.RemoveDuplicate => ("remove", p.Yellow),
                    CleanupKind.Clash => ("CLASH", p.BoldRed),
                    _ => ("leave", p.Dim),
                };
                var off = step.Enabled ? "" : p.Dim(" (off)");
                var from = string.Join(", ", step.Sources.Select(x => Relative(x, step.Root)));
                var line = step.Kind switch
                {
                    CleanupKind.Clash or CleanupKind.Untouched => $"{from}: {step.Reason}",
                    CleanupKind.RemoveDuplicate => $"{from}: {Words.LowerFirst(step.Reason)}",
                    _ => $"{from} {s.Ui.Sym.Arrow} {Relative(step.Target ?? "", step.WriteRoot)}",
                };
                s.Ui.Line($"    {color(kind.PadRight(8))}{line}{off}");
            }
        }
        if (shown.Count > 0) s.Ui.Line();
        s.Ui.Line(Summarize(plan, s));
    }

    private static string Relative(string path, string root) =>
        Path.GetRelativePath(root, path) is var rel && !rel.StartsWith("..") && !Path.IsPathRooted(rel) ? rel.Replace('\\', '/') : path;

    public static string Summarize(CleanupPlan plan, Session s)
    {
        var parts = new List<string>();
        void Add(CleanupKind kind, string what) { var n = plan.Steps.Count(x => x.Kind == kind && x.Runs); if (n > 0) parts.Add($"{n:N0} {what}"); }
        Add(CleanupKind.Convert, "to convert");
        Add(CleanupKind.Copy, "to copy");
        Add(CleanupKind.Rename, "to rename");
        Add(CleanupKind.Move, "to move");
        Add(CleanupKind.RemoveDuplicate, "duplicates to remove");
        var copies = plan.Steps.Any(x => x.Copies);
        var text = parts.Count == 0 ? "Nothing to do." : string.Join($" {s.Ui.Sym.Dot} ", parts) + (copies || plan.BytesSaved <= 0 ? "" : $" {s.Ui.Sym.Dot} frees about {Common.Size(plan.BytesSaved)}");
        var clashes = plan.Count(CleanupKind.Clash);
        var left = plan.Count(CleanupKind.Untouched);
        if (clashes > 0) text += $" {s.Ui.Sym.Dot} " + s.Ui.Paint.Red($"{Words.Count(clashes, "clash", "clashes")} (skipped: nothing is overwritten)");
        if (left > 0) text += $" {s.Ui.Sym.Dot} {left:N0} {(copies ? "left out" : "left alone")}";
        return text;
    }

    private static void Finished(Session s, RunJob job, string prefix)
    {
        var o = job.Outcome!;
        var steps = (List<CleanupStep>)job.Tag!;
        var (word, color) = Common.Look(s.Ui, o.Status, "Organized");
        var did = o.Status is JobStatus.Succeeded or JobStatus.Warning
            ? string.Join(", ", steps.Where(x => x.Runs).Select(x => x.Kind switch
            {
                CleanupKind.Convert => $"converted {s.Ui.Sym.Arrow} {Path.GetFileName(x.Target)}",
                CleanupKind.Rename => $"renamed {s.Ui.Sym.Arrow} {Path.GetFileName(x.Target)}",
                CleanupKind.Move => $"moved {s.Ui.Sym.Arrow} {Path.GetFileName(Path.GetDirectoryName(x.Target))}/",
                CleanupKind.Copy => $"copied {s.Ui.Sym.Arrow} {Relative(x.Target ?? "", x.WriteRoot)}",
                CleanupKind.RemoveDuplicate => "duplicate removed",
                _ => "",
            }).Where(t => t.Length > 0))
            : o.Summary;
        if (s.Ui.Term.LiveBars) s.Ui.Status(word, $"{job.Name} {s.Ui.Sym.Dot} {did}", color);
        else s.Ui.Line($"{prefix}{word.ToLowerInvariant(),-10} {job.Name}: {did}");
        if (o.Status == JobStatus.Failed) s.Ui.QuietFailure($"{job.Name}: {o.Summary}");
        s.Ui.Event("file-done", new JsonObject { ["name"] = job.Name, ["paths"] = Json.Array(job.Paths), ["result"] = o.Status.ToString().ToLowerInvariant(), ["summary"] = did });
    }

    private static int Report(Session s, string command, DateTime started, CleanupPlan plan, IReadOnlyList<RunJob> jobs, CleanupRun? run, bool dryRun, bool ran)
    {
        var elapsed = DateTime.UtcNow - started;
        var ok = jobs.Count(j => j.Outcome?.Status is JobStatus.Succeeded or JobStatus.Warning);
        var failed = jobs.Where(j => j.Outcome?.Status == JobStatus.Failed).ToList();
        var code = Exit.Combine(
            s.Token.IsCancellationRequested ? Exit.Cancelled : 0,
            failed.Any(j => !Common.MissingKeys(j.Outcome)) ? Exit.Failed : 0,
            failed.Any(j => Common.MissingKeys(j.Outcome)) ? Exit.Missing : 0);
        if (ran)
        {
            s.Ui.Line();
            s.Ui.Status("Finished", $"{Words.Count(ok, "game")} organized{(failed.Count > 0 ? $", {failed.Count} failed" : "")} in {Common.Elapsed(elapsed)}",
                failed.Count > 0 ? s.Ui.Paint.BoldYellow : s.Ui.Paint.BoldGreen);
            if (run is not null && run.Entries.Count > 0)
                s.Ui.Info(run.NewLibrary is null
                    ? $"Undo it with: noi runs undo {run.Id}   (originals and duplicates are in .noi-trash until: noi runs empty-trash {run.Id})"
                    : $"Undo it with: noi runs undo {run.Id}   (removes what was made in {run.NewLibrary})");
            if (s.Log.Path is { } log) s.Ui.Info($"Log: {log}");
            s.Ui.QuietSummary($"done: {Words.Count(ok, "game")} organized, {failed.Count} failed{(run is null ? "" : $", run {run.Id}")}, {Common.Elapsed(elapsed)}");
        }
        else s.Ui.QuietSummary(Summarize(plan, s));

        var steps = new JsonArray(plan.Steps.Select(x => (JsonNode)new JsonObject
        {
            ["kind"] = x.Kind.ToString().ToLowerInvariant(), ["game"] = x.Game, ["system"] = x.SystemId,
            ["sources"] = Json.Array(x.Sources), ["target"] = x.Target, ["reason"] = x.Reason, ["runs"] = x.Runs,
        }).ToArray());
        var doc = Common.Document(command, code, elapsed);
        doc["dryRun"] = dryRun;
        doc["ran"] = ran;
        doc["runId"] = run?.Id;
        doc["summary"] = new JsonObject
        {
            ["steps"] = plan.Steps.Count(x => x.Runs), ["games"] = jobs.Count, ["done"] = ok, ["failed"] = failed.Count,
            ["clashes"] = plan.Count(CleanupKind.Clash), ["leftAlone"] = plan.Count(CleanupKind.Untouched), ["bytesSaved"] = plan.BytesSaved,
        };
        doc["steps"] = steps;
        s.Ui.Json(doc);
        s.Ui.Event("summary", new JsonObject { ["exitCode"] = code, ["runId"] = run?.Id, ["done"] = ok, ["failed"] = failed.Count });
        return code;
    }

    // ---- What the conversions make: --keep, --to and --preset for Organize ----

    /// <summary>
    /// Organize converts each file to its system's target; --keep leaves a system, --to picks one of a system's
    /// other targets (zip, 7z, bare, cso…), and --preset picks one by a format preset (zso-opl, xcz-standard…) or the
    /// compression (smallest, fastest) of RVZ and 7z/zip.
    /// </summary>
    internal static void ApplyFormats(ParseResult r, AppSettings settings)
    {
        var overrides = settings.TargetFormats.Overrides;
        foreach (var keep in (r.GetValue(FormatOptions.Keep) ?? []).SelectMany(k => k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            overrides.Add(new TargetOverride(Common.ResolveSystem(keep), null, TargetOverrides.Leave));
        foreach (var t in r.GetValue(FormatOptions.To) ?? [])
        {
            var (system, word) = SplitPair(t);
            AddTarget(overrides, system, word.ToLowerInvariant(), $"--to {t}");
        }
        foreach (var p in r.GetValue(FormatOptions.Preset) ?? [])
        {
            var (system, word) = SplitPair(p);
            var w = word.ToLowerInvariant();
            var level = w.Split('-').Last();
            if (level is "smallest" or "fastest" or "balanced")
            {
                if (!w.StartsWith("7z") && !w.StartsWith("zip")) settings.Presets.Chosen[nameof(PresetAction.Rvz)] = level;
                if (!w.StartsWith("rvz")) settings.Presets.Chosen[nameof(PresetAction.Archive)] = level;
                continue;
            }
            var presets = (system is null ? TargetFormats.All.Select(x => x.Id) : [system])
                .SelectMany(id => FormatPresets.ForSystem(id).Select(fp => (System: id, Preset: fp)))
                .Where(x => x.Preset.Id.Equals(w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (presets.Count == 0) throw new UsageException($"Organize can't use the preset \"{word}\".", "It takes smallest, fastest or balanced, or a system's format preset (noi presets <system>).");
            foreach (var (id, preset) in presets)
                if (AlternativeFor(preset) is { } alt) AddTarget(overrides, id, alt, $"--preset {p}");
        }
    }

    private static (string? System, string Word) SplitPair(string text)
    {
        var i = text.IndexOf('=');
        return i > 0 ? (Common.ResolveSystem(text[..i]), text[(i + 1)..]) : (null, text);
    }

    private static void AddTarget(List<TargetOverride> overrides, string? system, string word, string said)
    {
        if (word is "unpack" or "files") word = "bare";
        if (word is "cue" or "bin") word = "cue-bin";
        if (system is not null)
        {
            if (!TargetOverrides.Options(TargetFormats.ById(system)).Any(o => o.Id == word && o.Producible))
                throw new UsageException($"{said}: {TargetFormats.NameOf(system)} has no \"{word}\" target.",
                    "Its targets: " + string.Join(", ", TargetOverrides.Options(TargetFormats.ById(system)).Where(o => o.Producible).Select(o => o.Id)));
            overrides.Add(new TargetOverride(system, null, word));
            return;
        }
        var systems = TargetFormats.All.Where(x => TargetOverrides.Options(x).Any(o => o.Id == word && o.Producible)).ToList();
        if (word is TargetOverrides.Recommended or TargetOverrides.Leave or "zip" or "7z" or "bare")
            overrides.Add(new TargetOverride(TargetOverrides.AllSystems, null, word));
        else if (systems.Count == 0)
            throw new UsageException($"{said}: no system has a \"{word}\" target.", "Targets: recommended, leave, zip, 7z, bare, cso, zso, iso, cue-bin, gdi, xcz, nsp, zcci, zar, cci, folder");
        else foreach (var x in systems) overrides.Add(new TargetOverride(x.Id, null, word));
    }

    private static string? AlternativeFor(FormatPreset preset) => preset.Values.Target switch
    {
        TargetKind.Zso => "zso",
        TargetKind.Cso => "cso",
        TargetKind.Iso => "iso",
        TargetKind.DiscImage => preset.Values.ExtractFormat switch { ExtractFormat.Gdi => "gdi", ExtractFormat.Iso => "iso", _ => "cue-bin" },
        TargetKind.Xcz => "xcz",
        TargetKind.Nsp => "nsp",
        TargetKind.Zcci => "zcci",
        TargetKind.Zar => "zar",
        TargetKind.XboxCci => "cci",
        TargetKind.SevenZip => "7z",
        TargetKind.Zip => "zip",
        TargetKind.Files => "bare",
        TargetKind.Ps3Folder => "folder",
        _ => TargetOverrides.Recommended,
    };
}
