using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NoIntrovert.Cli.Cli;
using NoIntrovert.Cli.Output;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Pipelines;

namespace NoIntrovert.Cli.Commands;

/// <summary>noi verify: the Verify page (docs/requirements/cli.md §5.3).</summary>
public static class VerifyCommand
{
    private static readonly Argument<string[]> PathsArg = PathOptions.Paths();
    public static readonly Option<bool> FixHeaders = new("--fix-headers") { Description = "Rewrite a CHD's wrong header SHA-1 when its data is intact" };
    public static readonly Option<string?> Report = new("--report") { Description = "Also write the results to a .csv or .json file", HelpName = "file" };
    public static readonly Option<string[]> Only = new("--only") { Description = "Show only: damaged, misnamed, unknown, no-db, unchecked, ok (comma list)", HelpName = "results" };
    public static readonly Option<bool> Rehash = new("--rehash") { Description = "Hash every file again, ignoring the hash cache" };
    public static readonly Option<string?> DatFolder = new("--dat-folder") { Description = "A folder of your own DATs, used before the downloaded ones", HelpName = "folder" };

    public static Command Command()
    {
        var c = new Command("verify", "Check for damage and look games up in No-Intro and Redump");
        c.Arguments.Add(PathsArg);
        foreach (var o in new Option[] { FixHeaders, Report, Only, Rehash, DatFolder }.Concat(PathOptions.All)) c.Options.Add(o);
        return c;
    }

    public static HelpDoc Doc => new(
        "Check games for damage and look them up in the No-Intro and Redump databases.",
        "CHDs are checked against their stored SHA-1, 7z and zip archives by testing every file, GameCube/Wii discs by decoding " +
        "them. Then each game is hashed and looked up: a good dump, one with the wrong name (and the name it should have), or one " +
        "the databases don't know. Never changes a file, unless --fix-headers.\n\n" +
        "The databases come from libretro-database, only for the systems of the files given, and are checked for updates first " +
        "(--offline to skip that).",
        "noi verify <files or folders…> [options]",
        [
            ("noi verify D:\\Roms", "Check every game in a folder."),
            ("noi verify D:\\Roms --only damaged,misnamed", "Show just the problems."),
            ("noi verify D:\\Roms --report results.csv -q", "Write a CSV; print only problems and the summary."),
            ("noi verify -s D:\\Roms || echo damaged", "Silent: exit code 3 when something is damaged."),
        ],
        [
            new("VERIFY", [FixHeaders, Report, Only, Rehash, DatFolder]),
            new("WHICH FILES", PathOptions.All),
            new("OUTPUT", [GlobalOptions.Quiet, GlobalOptions.Silent, GlobalOptions.Verbose, GlobalOptions.Json, GlobalOptions.Ndjson, GlobalOptions.Offline, GlobalOptions.Jobs]),
        ],
        "Results: ✔ Verified · ≠ Name differs · ? Not in the database · – No database / can't be checked yet · ✖ Damaged. Exit code 3 when anything is damaged.");

    private static readonly string[] OnlyWords = ["damaged", "misnamed", "unknown", "no-db", "unchecked", "ok"];

    public static async Task<int> Run(ParseResult r, Session s)
    {
        var started = DateTime.UtcNow;
        var only = (r.GetValue(Only) ?? []).SelectMany(o => o.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Select(o => o.ToLowerInvariant()).ToHashSet();
        foreach (var o in only.Where(o => !OnlyWords.Contains(o)))
            throw new UsageException($"--only: \"{o}\" isn't a result.", "Results: " + string.Join(", ", OnlyWords));
        if (r.GetValue(FixHeaders)) s.Settings.Chd.FixHeaderSha1 = true;
        if (r.GetValue(DatFolder) is { } dats) s.Settings.Dats.OwnFolder = Path.GetFullPath(dats);
        var report = r.GetValue(Report);
        if (report is not null && Path.GetExtension(report).ToLowerInvariant() is not (".csv" or ".json"))
            throw new UsageException("--report writes a .csv or a .json file.", $"For example: --report {Path.ChangeExtension(report, ".csv")}");

        var inputs = Common.Gather(s, Common.Paths(r, PathsArg, "noi verify"), Inputs.Kind.Games, Common.Filter(r));
        if (inputs.Items.Count == 0)
        {
            s.Ui.Answer("Nothing to verify: no games found in the paths given.");
            s.Ui.Json(Common.Document("verify", Exit.Ok, DateTime.UtcNow - started));
            return Exit.Ok;
        }
        var results = await Check(s, inputs.Items, r.GetValue(Rehash));
        if (results is null) return Exit.Cancelled;
        return Finish(s, started, results, only, report);
    }

    /// <summary>Verifies <paramref name="items"/> (updating their databases first), printing each result; null when cancelled.</summary>
    internal static async Task<IReadOnlyList<VerifyResult>?> Check(Session s, IReadOnlyList<InputItem> items, bool rehash = false)
    {
        await UpdateDats(s, items);
        if (rehash) foreach (var i in items) HashCache.Default.Forget(i.Path);
        var jobs = VerifyPipeline.Jobs(items);
        s.Ui.Status("Verifying", $"{Words.Count(items.Count, "file")} ({Common.Size(items.Sum(i => i.Size))})", s.Ui.Paint.BoldCyan);
        await Common.RunAsync(s, "verify", "Verifying", jobs, (job, prefix) => Finished(s, job, prefix));
        if (s.Token.IsCancellationRequested) return null;
        return VerifyPipeline.Results(jobs);
    }

    /// <summary>The DATs for the files' systems, checked for updates unless --offline.</summary>
    internal static async Task UpdateDats(Session s, IReadOnlyList<InputItem> items)
    {
        var keys = VerifyPipeline.DatKeysFor(items);
        if (keys.Count == 0) return;
        if (s.Offline)
        {
            var missing = keys.Count(k => !File.Exists(DatStore.Default.PathOf(k)));
            if (missing > 0) s.Ui.Warn($"--offline: {Words.Count(missing, "game database")} for these systems isn't here yet, so those games show \"No database\".");
            return;
        }
        s.Ui.Debug($"Checking {Words.Count(keys.Count, "game database")} for updates…");
        try { await Databases.UpdateDatsAsync(keys, s.Log, s.Token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (!s.Token.IsCancellationRequested) s.Ui.Warn($"Game databases: couldn't check for updates ({ex.Message}); using the copies already here.");
        }
    }

    private static void Finished(Session s, RunJob job, string prefix)
    {
        var item = (InputItem)job.Tag!;
        var match = MatchStore.Default.Get(item.Path);
        var status = match?.Status ?? (job.Outcome?.Status == JobStatus.Failed ? MatchStatus.Damaged : (MatchStatus?)null);
        var text = match?.Text ?? job.Outcome?.Summary ?? "";
        var (word, color, symbol) = Look(s.Ui, status, job.Outcome?.Status);
        if (s.Ui.Term.LiveBars) s.Ui.Status(word, $"{item.Name}{Detail(item, match, text, s.Ui)}", color);
        else s.Ui.Line($"{prefix}{symbol} {word.ToLowerInvariant(),-12} {item.Name}{Detail(item, match, text, s.Ui)}");
        if (status == MatchStatus.Damaged || job.Outcome?.Status == JobStatus.Failed) s.Ui.QuietFailure($"{item.Name}: {text}");
        s.Ui.Event("file-done", Entry(item, status, match, job.Outcome));
    }

    private static string Detail(InputItem item, MatchResult? match, string text, Ui ui) => match?.Status switch
    {
        MatchStatus.Verified => "",
        MatchStatus.NameDiffers => $" {ui.Sym.Arrow} {match.ExpectedName}",
        _ => text.Length > 0 ? $" {ui.Sym.Dot} {text}" : "",
    };

    public static (string Word, Func<string, string> Color, string Symbol) Look(Ui ui, MatchStatus? status, JobStatus? outcome) => status switch
    {
        MatchStatus.Verified => ("Verified", ui.Paint.BoldGreen, ui.Paint.Green(ui.Sym.Ok)),
        MatchStatus.NameDiffers => ("Misnamed", ui.Paint.BoldYellow, ui.Paint.Yellow(ui.Sym.Differs)),
        MatchStatus.NotInDatabase => ("Unknown", ui.Paint.BoldCyan, ui.Paint.Blue(ui.Sym.Unknown)),
        MatchStatus.NoDatabase => ("No database", ui.Paint.Dim, ui.Paint.Dim(ui.Sym.None)),
        MatchStatus.CantCheck => ("Not checked", ui.Paint.Dim, ui.Paint.Dim(ui.Sym.None)),
        MatchStatus.Damaged => ("Damaged", ui.Paint.BoldRed, ui.Paint.Red(ui.Sym.Fail)),
        _ when outcome == JobStatus.Cancelled => ("Cancelled", ui.Paint.BoldYellow, ui.Paint.Yellow(ui.Sym.None)),
        _ => ("Failed", ui.Paint.BoldRed, ui.Paint.Red(ui.Sym.Fail)),
    };

    private static string OnlyWord(MatchStatus? status) => status switch
    {
        MatchStatus.Verified => "ok",
        MatchStatus.NameDiffers => "misnamed",
        MatchStatus.NotInDatabase => "unknown",
        MatchStatus.NoDatabase => "no-db",
        MatchStatus.CantCheck => "unchecked",
        _ => "damaged",
    };

    private static JsonObject Entry(InputItem item, MatchStatus? status, MatchResult? match, JobOutcome? outcome) => new()
    {
        ["path"] = item.Path,
        ["system"] = item.System,
        ["result"] = status is null ? outcome?.Status.ToString().ToLowerInvariant() : OnlyWord(status),
        ["text"] = match?.Text ?? outcome?.Summary,
        ["dat"] = match?.Dat,
        ["game"] = match?.Game,
        ["expectedName"] = match?.ExpectedName,
        ["bytes"] = item.Size,
    };

    private static int Finish(Session s, DateTime started, IReadOnlyList<VerifyResult> results, HashSet<string> only, string? report)
    {
        var elapsed = DateTime.UtcNow - started;
        int Count(MatchStatus st) => results.Count(x => x.Status == st);
        var damaged = Count(MatchStatus.Damaged);
        var failed = results.Count(x => x.Status is null && x.Outcome?.Status == JobStatus.Failed);
        var code = Exit.Combine(failed > 0 ? Exit.Failed : 0, damaged > 0 ? Exit.Damaged : 0);

        if (only.Count > 0 && s.Ui.ShowsResults)
        {
            s.Ui.Line();
            foreach (var x in results.Where(x => only.Contains(OnlyWord(x.Status))))
            {
                var (word, _, symbol) = Look(s.Ui, x.Status, x.Outcome?.Status);
                s.Ui.Line($"{symbol} {word,-12} {x.Item.Path}{Detail(x.Item, x.Match, x.Text, s.Ui)}");
            }
        }
        s.Ui.Line();
        s.Ui.Status("Finished", $"{Words.Count(results.Count, "file")} in {Common.Elapsed(elapsed)}", damaged + failed > 0 ? s.Ui.Paint.BoldYellow : s.Ui.Paint.BoldGreen);
        var rows = new List<IReadOnlyList<string>>();
        void Row(string symbol, string label, int n, string note) { if (n > 0) rows.Add([$"{symbol} {label}", n.ToString("N0"), note]); }
        var p = s.Ui.Paint;
        Row(p.Green(s.Ui.Sym.Ok), "Verified", Count(MatchStatus.Verified), "good dumps, named as in the database");
        Row(p.Yellow(s.Ui.Sym.Differs), "Name differs", Count(MatchStatus.NameDiffers), "noi organize renames them");
        Row(p.Blue("?"), "Not in the database", Count(MatchStatus.NotInDatabase), "");
        Row(p.Dim(s.Ui.Sym.None), "No database", Count(MatchStatus.NoDatabase), "no DAT for the system yet");
        Row(p.Dim(s.Ui.Sym.None), "Can't be checked yet", Count(MatchStatus.CantCheck), "a format that isn't hashed");
        Row(p.Red(s.Ui.Sym.Fail), "Damaged", damaged, "see above");
        Row(p.Red(s.Ui.Sym.Fail), "Failed", failed, "couldn't be read");
        s.Ui.Table(["Result", "Files", ""], rows, new HashSet<int> { 1 });
        if (s.Log.Path is { } log) s.Ui.Info($"Log: {log}");
        s.Ui.QuietSummary($"done: {Words.Count(results.Count, "file")}, {Count(MatchStatus.Verified)} verified, {Count(MatchStatus.NameDiffers)} misnamed, " +
                          $"{Count(MatchStatus.NotInDatabase)} unknown, {damaged} damaged, {Common.Elapsed(elapsed)}");

        var files = new JsonArray(results.Select(x => (JsonNode)Entry(x.Item, x.Status, x.Match, x.Outcome)).ToArray());
        if (report is not null) WriteReport(s, report, results);
        var doc = Common.Document("verify", code, elapsed);
        doc["summary"] = new JsonObject
        {
            ["files"] = results.Count, ["verified"] = Count(MatchStatus.Verified), ["nameDiffers"] = Count(MatchStatus.NameDiffers),
            ["notInDatabase"] = Count(MatchStatus.NotInDatabase), ["noDatabase"] = Count(MatchStatus.NoDatabase),
            ["cantCheck"] = Count(MatchStatus.CantCheck), ["damaged"] = damaged, ["failed"] = failed,
        };
        doc["files"] = files;
        s.Ui.Json(doc);
        s.Ui.Event("summary", new JsonObject { ["exitCode"] = code, ["files"] = results.Count, ["damaged"] = damaged });
        return code;
    }

    private static void WriteReport(Session s, string path, IReadOnlyList<VerifyResult> results)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var arr = new JsonArray(results.Select(x => (JsonNode)Entry(x.Item, x.Status, x.Match, x.Outcome)).ToArray());
                File.WriteAllText(full, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                static string Q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
                var sb = new StringBuilder("Path,System,Result,Text,Database,Game,Expected name\n");
                foreach (var x in results)
                    sb.AppendLine(string.Join(",", Q(x.Item.Path), Q(TargetFormats.NameOf(x.Item.System)), Q(OnlyWord(x.Status)), Q(x.Text), Q(x.Match?.Dat), Q(x.Match?.Game), Q(x.Match?.ExpectedName)));
                File.WriteAllText(full, sb.ToString());
            }
            s.Ui.Info($"Report: {full}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            s.Ui.Error($"Couldn't write the report: {ex.Message}");
        }
    }
}
