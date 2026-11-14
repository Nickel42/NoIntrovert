using System.CommandLine;
using System.Text.Json.Nodes;
using NoIntrovert.Cli.Cli;
using NoIntrovert.Core.Cleanup;

namespace NoIntrovert.Cli.Commands;

/// <summary>noi runs (§5.6): past Organize and Optimize runs, undo, and their trash.</summary>
public static class RunsCommand
{
    private static readonly Argument<string> Id = new("run") { Description = "A run's id (from noi runs), or last", HelpName = "id|last" };
    private static readonly Argument<string> TrashId = new("run") { Description = "A run's id, last, or all", HelpName = "id|last|all" };
    private static readonly Option<int> Limit = new("--limit") { Description = "How many runs to list (default 20)", HelpName = "n", DefaultValueFactory = _ => 20 };

    public static Command Command(out Command list, out Command show, out Command undo, out Command empty)
    {
        var runs = new Command("runs", "Undo an Organize or Optimize run, or empty its trash");
        runs.Options.Add(Limit);
        list = new Command("list", "The runs on record, newest first");
        list.Options.Add(Limit);
        show = new Command("show", "Every step of a run");
        show.Arguments.Add(Id);
        undo = new Command("undo", "Undo a run (asks first; --yes to skip)");
        undo.Arguments.Add(Id);
        empty = new Command("empty-trash", "Send a run's trash to the Recycle Bin / Trash (asks first; --yes)");
        empty.Arguments.Add(TrashId);
        foreach (var c in new[] { list, show, undo, empty }) runs.Subcommands.Add(c);
        return runs;
    }

    public static HelpDoc Doc => new(
        "Past Organize and Optimize runs: undo one, or empty its trash.",
        "Each run keeps a journal of every move, rename and removal. Removed files (originals of conversions, duplicates) wait in " +
        "a .noi-trash folder beside them until the trash is emptied, so a run can be undone, conversions included. The app lists " +
        "the same runs under Past runs.",
        "noi runs [list]\nnoi runs show <id|last>\nnoi runs undo <id|last>\nnoi runs empty-trash <id|last|all>",
        [
            ("noi runs", "The last 20 runs: id, when, what they did, trash size."),
            ("noi runs show last", "Every step of the newest run."),
            ("noi runs undo last", "Undo the newest run (asks first)."),
            ("noi runs empty-trash all -y", "Empty every run's trash; those runs can't be undone after that."),
        ],
        [new("OPTIONS", [Limit, GlobalOptions.Yes, GlobalOptions.Json])]);

    public static HelpDoc ShowDoc => Doc with { Summary = "Every step of a run, from its journal." };
    public static HelpDoc UndoDoc => Doc with { Summary = "Undo a run: moves go back, trashed files come back, conversions are reversed while their originals are in the trash." };
    public static HelpDoc EmptyDoc => Doc with { Summary = "Send a run's trash to the Recycle Bin / Trash. The run can't be undone after that." };

    public static Task<int> List(ParseResult r, Session s)
    {
        var runs = CleanupRunner.Runs().Take(Math.Max(1, r.GetValue(Limit))).ToList();
        if (runs.Count == 0) s.Ui.Answer("No runs yet. noi organize and noi optimize keep one for each run.");
        else
            s.Ui.Table(["Id", "When", "What it did", "Trash", "State"], runs.Select(run => (IReadOnlyList<string>)
            [
                run.Id, run.StartedUtc.ToLocalTime().ToString("g"), WhatItDid(run), Common.Size(TrashSize(run)),
                run.Undone ? s.Ui.Paint.Dim("undone") : run.TrashEmptied ? s.Ui.Paint.Dim("trash emptied") : s.Ui.Paint.Green("can be undone"),
            ]), new HashSet<int> { 3 }, answer: true);
        var doc = Common.Document("runs", Exit.Ok, TimeSpan.Zero);
        doc["runs"] = new JsonArray(runs.Select(run => (JsonNode)RunJson(run)).ToArray());
        s.Ui.Json(doc);
        return Task.FromResult(Exit.Ok);
    }

    public static Task<int> Show(ParseResult r, Session s)
    {
        var run = Find(r.GetValue(Id)!);
        s.Ui.Answer($"{s.Ui.Paint.Bold("Run " + run.Id)}  {run.Summary}");
        if (run.NewLibrary is { } lib) s.Ui.Answer($"New library: {lib}");
        foreach (var e in run.Entries)
        {
            var what = e.Action switch
            {
                JournalAction.Moved => $"moved    {e.From} {s.Ui.Sym.Arrow} {e.Path}",
                JournalAction.Trashed => $"trashed  {e.From} {s.Ui.Sym.Arrow} {e.Path}",
                JournalAction.Created => $"made     {e.Path}",
                _ => $"{e.Action.ToString().ToLowerInvariant(),-8} {e.Path}",
            };
            s.Ui.Answer($"  {s.Ui.Paint.Dim($"{e.Step,3}")} {what}");
        }
        var doc = Common.Document("runs show", Exit.Ok, TimeSpan.Zero);
        doc["run"] = RunJson(run);
        doc["entries"] = new JsonArray(run.Entries.Select(e => (JsonNode)new JsonObject
        {
            ["step"] = e.Step, ["action"] = e.Action.ToString().ToLowerInvariant(), ["path"] = e.Path, ["from"] = e.From, ["bytes"] = e.Size,
        }).ToArray());
        s.Ui.Json(doc);
        return Task.FromResult(Exit.Ok);
    }

    public static Task<int> Undo(ParseResult r, Session s)
    {
        var run = Find(r.GetValue(Id)!);
        if (run.Undone) throw new UsageException($"Run {run.Id} was already undone.");
        var useBin = s.Settings.Output.UseRecycleBin;
        if (s.DryRun)
        {
            s.Ui.Answer($"Would undo run {run.Id}: {run.Summary}");
            return Task.FromResult(Exit.Ok);
        }
        if (!s.Yes)
        {
            if (!s.CanAsk) throw new UsageException("Undo changes files, and nothing can be asked here.", $"Add --yes: noi runs undo {run.Id} --yes");
            if (!s.Ui.Confirm($"Undo run {run.Id} ({run.Summary})?")) { s.Ui.Answer("Not undone."); return Task.FromResult(Exit.Ok); }
        }
        var problems = CleanupRunner.Undo(run, useBin, s.Log);
        foreach (var p in problems) s.Ui.Warn(p);
        s.Ui.Status(problems.Count == 0 ? "Undone" : "Undone", problems.Count == 0 ? $"run {run.Id}" : $"run {run.Id}, except {Words.Count(problems.Count, "step")} (above)",
            problems.Count == 0 ? s.Ui.Paint.BoldGreen : s.Ui.Paint.BoldYellow);
        s.Ui.QuietSummary($"undone: run {run.Id}{(problems.Count > 0 ? $", {problems.Count} steps couldn't be undone" : "")}");
        var code = problems.Count > 0 ? Exit.Failed : Exit.Ok;
        var doc = Common.Document("runs undo", code, TimeSpan.Zero);
        doc["run"] = run.Id;
        doc["problems"] = Json.Array(problems);
        s.Ui.Json(doc);
        return Task.FromResult(code);
    }

    public static Task<int> EmptyTrash(ParseResult r, Session s)
    {
        var which = r.GetValue(TrashId)!;
        var runs = which.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? CleanupRunner.Runs().Where(x => !x.TrashEmptied && !x.Undone && x.TrashFolders.Count > 0).ToList()
            : [Find(which)];
        if (runs.Count == 0) { s.Ui.Answer("No trash to empty."); return Task.FromResult(Exit.Ok); }
        var bytes = runs.Sum(TrashSize);
        var useBin = s.Settings.Output.UseRecycleBin;
        if (s.DryRun) { s.Ui.Answer($"Would empty the trash of {Words.Count(runs.Count, "run")} ({Common.Size(bytes)})."); return Task.FromResult(Exit.Ok); }
        if (!s.Yes)
        {
            if (!s.CanAsk) throw new UsageException("Emptying the trash can't be undone, and nothing can be asked here.", "Add --yes to confirm.");
            var where = useBin && Core.Jobs.FileRemover.CanUseBin ? $"to the {Core.Jobs.FileRemover.BinName}" : "deleted permanently";
            if (!s.Ui.Confirm($"Empty the trash of {Words.Count(runs.Count, "run")} ({Common.Size(bytes)}, {where})? Those runs can't be undone after.")) { s.Ui.Answer("Not emptied."); return Task.FromResult(Exit.Ok); }
        }
        foreach (var run in runs) CleanupRunner.EmptyTrash(run, useBin, s.Log);
        s.Ui.Status("Emptied", $"the trash of {Words.Count(runs.Count, "run")} ({Common.Size(bytes)})");
        s.Ui.QuietSummary($"emptied: {Words.Count(runs.Count, "run")}, {Common.Size(bytes)}");
        var doc = Common.Document("runs empty-trash", Exit.Ok, TimeSpan.Zero);
        doc["runs"] = Json.Array(runs.Select(x => x.Id));
        doc["bytes"] = bytes;
        s.Ui.Json(doc);
        return Task.FromResult(Exit.Ok);
    }

    private static CleanupRun Find(string id)
    {
        var runs = CleanupRunner.Runs();
        if (runs.Count == 0) throw new UsageException("No runs yet.", "noi organize and noi optimize keep one for each run.");
        if (id.Equals("last", StringComparison.OrdinalIgnoreCase)) return runs[0];
        return runs.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? runs.FirstOrDefault(x => x.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase))
               ?? throw new UsageException($"No run \"{id}\".", "noi runs lists them.");
    }

    private static string WhatItDid(CleanupRun run)
    {
        var moved = run.Entries.Count(e => e.Action == JournalAction.Moved);
        var made = run.Entries.Count(e => e.Action == JournalAction.Created);
        var trashed = run.Entries.Count(e => e.Action == JournalAction.Trashed);
        var parts = new List<string> { Words.Count(run.Steps, "step") };
        if (made > 0) parts.Add($"{made} made");
        if (moved > 0) parts.Add($"{moved} moved");
        if (trashed > 0) parts.Add($"{trashed} to trash");
        return string.Join(", ", parts) + (run.NewLibrary is { } lib ? $" in {lib}" : "");
    }

    private static long TrashSize(CleanupRun run) =>
        run.TrashEmptied || run.Undone ? 0 : run.Entries.Where(e => e.Action == JournalAction.Trashed && File.Exists(e.Path)).Sum(e => e.Size);

    private static JsonObject RunJson(CleanupRun run) => new()
    {
        ["id"] = run.Id, ["started"] = run.StartedUtc.ToString("O"), ["steps"] = run.Steps, ["undone"] = run.Undone,
        ["trashEmptied"] = run.TrashEmptied, ["newLibrary"] = run.NewLibrary, ["trashBytes"] = TrashSize(run), ["summary"] = run.Summary,
    };
}
