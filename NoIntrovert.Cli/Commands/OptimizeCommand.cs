using System.CommandLine;
using NoIntrovert.Cli.Cli;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Pipelines;

namespace NoIntrovert.Cli.Commands;

/// <summary>
/// noi optimize (§5.5): the Optimize page. Verify every file, then Organize's plan from what that found (a file the
/// databases don't know is still converted, under its own name), shown and run game by game.
/// </summary>
public static class OptimizeCommand
{
    private static readonly Argument<string[]> PathsArg = PathOptions.Paths();

    public static Command Command()
    {
        var c = new Command("optimize", "Verify, convert, rename and file into system folders, in one go");
        c.Arguments.Add(PathsArg);
        foreach (var o in OrganizeCommand.PlanOptions.Concat([OrganizeCommand.PlanOut]).Concat(FormatOptions.All).Concat(PathOptions.All).Concat(KeyOptions.All))
            c.Options.Add(o);
        return c;
    }

    public static HelpDoc Doc => new(
        "Verify, convert, rename and file every game into its system's folder, in one go.",
        "Each file is checked and looked up in the No-Intro and Redump databases first. Then, game by game, it's converted to " +
        "its best format, named as in the database and moved into its system's folder (roms/<system>/ for ES-DE by default). " +
        "A file the databases don't know is still converted, keeping its name; a damaged one is left alone.\n\n" +
        "The plan is shown before anything changes, and asked about (--yes to run straight away; a script without --yes gets " +
        "the plan only). Originals go to a .noi-trash folder beside them, so a run can be undone: noi runs undo last.",
        "noi optimize <files or folders…> [options]",
        [
            ("noi optimize D:\\Roms", "Verify, then show the plan and ask."),
            ("noi optimize D:\\Roms --new-library E:\\", "Build a tidy ES-DE library on E:; D:\\Roms doesn't change."),
            ("noi optimize D:\\Roms --layout romm -y", "RomM's folder names; no questions."),
            ("noi optimize D:\\Roms -n", "Verify and show the plan; change nothing."),
        ],
        [
            new("PLAN", OrganizeCommand.PlanOptions),
            new("PLAN FILES", [OrganizeCommand.PlanOut]),
            new("WHAT TO MAKE", FormatOptions.All),
            new("WHICH FILES", PathOptions.All),
            new("OUTPUT", [GlobalOptions.DryRun, GlobalOptions.Yes, GlobalOptions.Quiet, GlobalOptions.Silent, GlobalOptions.Verbose, GlobalOptions.Json, GlobalOptions.Offline, GlobalOptions.Jobs]),
            new("KEYS", KeyOptions.All),
        ]);

    public static async Task<int> Run(ParseResult r, Session s)
    {
        var started = DateTime.UtcNow;
        Common.ApplyKeys(r, s);
        var inputs = Common.Gather(s, Common.Paths(r, PathsArg, "noi optimize"), Inputs.Kind.Games, Common.Filter(r));
        var request = OrganizeCommand.Prepare(r, s, convertUnmatched: true);
        if (inputs.Items.Count == 0)
        {
            s.Ui.Answer("Nothing to optimize: no games found in the paths given.");
            return Exit.Ok;
        }

        // 1. Verify every file: the plan's names and folders come from what it finds.
        var verified = await VerifyCommand.Check(s, inputs.Items);
        if (verified is null) return Exit.Cancelled;
        var usable = verified.Where(v => v.Outcome?.Status is not (JobStatus.Failed or JobStatus.Cancelled)).Select(v => v.Item).ToList();
        var damaged = verified.Count - usable.Count;
        if (damaged > 0) s.Ui.Warn($"{Words.Count(damaged, "file")} couldn't be read or {(damaged == 1 ? "is" : "are")} damaged, so {(damaged == 1 ? "it's" : "they're")} left alone.");
        s.Ui.Line();

        // 2. The plan from what Verify found, shown, then run when confirmed.
        var plan = OrganizeCommand.MakePlan(s, inputs with { Items = usable }, request);
        var code = await OrganizeCommand.PlanAndRun(s, s.Settings, plan, usable.Select(i => i.Path).ToList(), inputs.Folders, request, started, "optimize",
            r.GetValue(OrganizeCommand.PlanOut));
        return Exit.Combine(code, damaged > 0 ? Exit.Damaged : 0);
    }
}
