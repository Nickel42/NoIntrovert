using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NoIntrovert.Cli.Cli;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Pipelines;

namespace NoIntrovert.Cli.Commands;

/// <summary>noi settings (§5.7): read and change the app's settings file, which noi --use-settings also reads.</summary>
public static class SettingsCommand
{
    private static readonly Argument<string> Key = new("key") { Description = "A setting, as section.name: output.saveTo, performance.parallelJobs…", HelpName = "key" };
    private static readonly Argument<string> Value = new("value") { Description = "The new value: true, false, a number or text", HelpName = "value" };
    private static readonly Argument<string?> ResetKey = new("key") { Description = "The setting to reset; leave out with --all", Arity = ArgumentArity.ZeroOrOne, HelpName = "key" };
    private static readonly Option<bool> ResetAll = new("--all") { Description = "Reset every setting" };

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static Command Command(out List<(Command, Handler)> subs)
    {
        var settings = new Command("settings", "Read and change settings (shared with the app)");
        var list = new Command("list", "Every setting and its value");
        var get = new Command("get", "One setting's value");
        get.Arguments.Add(Key);
        var set = new Command("set", "Change a setting");
        set.Arguments.Add(Key);
        set.Arguments.Add(Value);
        var path = new Command("path", "Where the settings file is");
        var reset = new Command("reset", "Put a setting (or all of them) back to the recommended value");
        reset.Arguments.Add(ResetKey);
        reset.Options.Add(ResetAll);
        foreach (var c in new[] { list, get, set, path, reset }) settings.Subcommands.Add(c);
        subs = [(list, List), (get, Get), (set, Set), (path, Path), (reset, Reset)];
        return settings;
    }

    public static HelpDoc Doc => new(
        "Read and change the app's settings.",
        "These are the settings the app saves as you change them. noi doesn't follow them unless you add --use-settings (it starts " +
        "from the recommended values, so scripts behave the same everywhere), but it reads your saved presets and key folders. " +
        "Keys are written section.name, as the settings file has them: output.saveTo, performance.parallelJobs, library.layout.",
        "noi settings [list]\nnoi settings get <key>\nnoi settings set <key> <value>\nnoi settings path\nnoi settings reset <key> | --all",
        [
            ("noi settings", "Every setting, grouped as the app's cards."),
            ("noi settings get performance.parallelJobs", ""),
            ("noi settings set performance.parallelJobs 4", ""),
            ("noi settings set keys.switchKeysPath \"D:\\Keys\\prod.keys\"", "Where the Switch keys are, for the app and noi."),
            ("noi settings reset --all", "Every setting back to the recommended value (asks first)."),
        ],
        [new("OPTIONS", [ResetAll, GlobalOptions.Yes, GlobalOptions.Json])]);

    private static JsonObject Current() => (JsonObject)JsonNode.Parse(AppSettings.Load().ToJson())!;

    public static Task<int> List(ParseResult r, Session s)
    {
        var json = Current();
        if (s.Ui.IsJson)
        {
            var doc = Common.Document("settings", Exit.Ok, TimeSpan.Zero);
            doc["path"] = AppSettings.FilePath;
            doc["settings"] = json.DeepClone();
            s.Ui.Json(doc);
            return Task.FromResult(Exit.Ok);
        }
        foreach (var (section, node) in json)
        {
            if (node is JsonObject obj)
            {
                s.Ui.Answer(s.Ui.Paint.Bold(Camel(section)));
                foreach (var (name, value) in obj) s.Ui.Answer($"  {Camel(section)}.{Camel(name),-28} {Show(value)}");
            }
            else s.Ui.Answer($"{Camel(section),-30} {Show(node)}");
        }
        s.Ui.Info($"File: {AppSettings.FilePath}");
        return Task.FromResult(Exit.Ok);
    }

    public static Task<int> Get(ParseResult r, Session s)
    {
        var (parent, name) = Find(Current(), r.GetValue(Key)!);
        var value = parent[name];
        if (s.Ui.IsJson)
        {
            var doc = Common.Document("settings get", Exit.Ok, TimeSpan.Zero);
            doc["key"] = r.GetValue(Key);
            doc["value"] = value?.DeepClone();
            s.Ui.Json(doc);
        }
        else s.Ui.Answer(Show(value));
        return Task.FromResult(Exit.Ok);
    }

    public static Task<int> Set(ParseResult r, Session s)
    {
        var json = Current();
        var key = r.GetValue(Key)!;
        var (parent, name) = Find(json, key);
        var old = parent[name];
        var text = r.GetValue(Value)!;
        JsonNode? value = old switch
        {
            JsonValue v when v.TryGetValue<bool>(out _) => bool.TryParse(text, out var b) ? JsonValue.Create(b) : throw new UsageException($"{key} is true or false."),
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? JsonNode.Parse(text) : throw new UsageException($"{key} is a number."),
            JsonArray or JsonObject => JsonNode.Parse(text) ?? throw new UsageException($"{key} takes JSON: {Show(old)}"),
            _ => JsonValue.Create(text),
        };
        parent[name] = value;
        AppSettings settings;
        try { settings = AppSettings.FromJson(json.ToJsonString()); }
        catch (JsonException ex) { throw new UsageException($"{key} can't be \"{text}\": {ex.Message.Split('.')[0]}.", $"It's now {Show(old)}."); }
        if (s.DryRun) { s.Ui.Answer($"Would set {key} to {Show(value)}."); return Task.FromResult(Exit.Ok); }
        settings.Save();
        s.Ui.Status("Saved", $"{key} = {Show(value)}");
        return Task.FromResult(Exit.Ok);
    }

    public static Task<int> Path(ParseResult r, Session s)
    {
        s.Ui.Answer(AppSettings.FilePath);
        var doc = Common.Document("settings path", Exit.Ok, TimeSpan.Zero);
        doc["path"] = AppSettings.FilePath;
        doc["folder"] = AppSettings.SettingsDirectory;
        s.Ui.Json(doc);
        return Task.FromResult(Exit.Ok);
    }

    public static Task<int> Reset(ParseResult r, Session s)
    {
        var all = r.GetValue(ResetAll);
        var key = r.GetValue(ResetKey);
        if (!all && key is null) throw new UsageException("Name the setting to reset, or --all.", "noi settings reset performance.parallelJobs");
        if (!s.Yes)
        {
            if (!s.CanAsk) throw new UsageException("Resetting settings needs a confirmation, and nothing can be asked here.", "Add --yes to confirm.");
            if (!s.Ui.Confirm(all ? "Reset every setting to the recommended value?" : $"Reset {key} to the recommended value?")) { s.Ui.Answer("Not reset."); return Task.FromResult(Exit.Ok); }
        }
        var json = Current();
        var fresh = (JsonObject)JsonNode.Parse(new AppSettings().ToJson())!;
        if (all) json = fresh;
        else
        {
            var (parent, name) = Find(json, key!);
            var (freshParent, freshName) = Find(fresh, key!);
            parent[name] = freshParent[freshName]?.DeepClone();
        }
        if (s.DryRun) { s.Ui.Answer("Would reset."); return Task.FromResult(Exit.Ok); }
        AppSettings.FromJson(json.ToJsonString()).Save();
        s.Ui.Status("Reset", all ? "every setting" : key!);
        return Task.FromResult(Exit.Ok);
    }

    /// <summary>The object holding a setting and its name there, case-insensitively ("output.saveTo" → Output, "SaveTo").</summary>
    private static (JsonObject Parent, string Name) Find(JsonObject root, string key)
    {
        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        JsonObject node = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var name = node.Select(p => p.Key).FirstOrDefault(k => k.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                var guess = Suggest.Closest(parts[i], node.Select(p => Camel(p.Key)));
                throw new UsageException($"No setting \"{key}\".", (guess is null ? "" : $"Did you mean \"{string.Join('.', parts[..i].Append(guess))}\"?  ") + "noi settings lists them.");
            }
            if (i == parts.Length - 1) return (node, name);
            node = node[name] as JsonObject ?? throw new UsageException($"\"{string.Join('.', parts[..(i + 1)])}\" is a value, not a section.");
        }
        throw new UsageException("Name a setting: section.name.");
    }

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static string Show(JsonNode? node) => node switch
    {
        null => "(none)",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>() is { Length: > 0 } t ? t : "\"\"",
        _ => node.ToJsonString(),
    };
}

/// <summary>noi test (§5.7): the Library tests, for power users.</summary>
public static class TestsCommand
{
    private static readonly Argument<string[]> PathsArg = PathOptions.Paths();
    private static readonly Option<string?> Chdman = new("--chdman") { Description = "The chdman executable to compare with", HelpName = "exe" };
    private static readonly Option<string?> Csv = new("--csv") { Description = "Also write the results to a CSV file", HelpName = "file" };

    public static Command Command(out Command roundtrip, out Command compare)
    {
        var test = new Command("test", "Library tests: round trip, and comparison with chdman (power user)");
        roundtrip = new Command("roundtrip", "Create → verify → extract → compare with the original data");
        compare = new Command("compare", "Create with lib-chd-kit and with chdman, then compare byte for byte");
        foreach (var c in new[] { roundtrip, compare })
        {
            c.Arguments.Add(PathsArg);
            c.Options.Add(Csv);
            foreach (var o in PathOptions.All) c.Options.Add(o);
            test.Subcommands.Add(c);
        }
        compare.Options.Add(Chdman);
        return test;
    }

    public static HelpDoc Doc => new(
        "The Library tests (power user): check lib-chd-kit on your own discs.",
        "Round trip makes a CHD of each disc, verifies it, extracts it and compares the data with the original. Compare makes the " +
        "CHD with lib-chd-kit and with a chdman executable and checks they're byte for byte the same, with timings. Nothing is " +
        "written next to the discs.",
        "noi test roundtrip <discs…> [--csv file]\nnoi test compare <discs…> --chdman <exe> [--csv file]",
        [("noi test roundtrip D:\\Discs --csv results.csv", ""), ("noi test compare D:\\Discs --chdman C:\\Tools\\chdman.exe", "")],
        [new("OPTIONS", [Chdman, Csv, GlobalOptions.Jobs, GlobalOptions.Json])]);

    public static Task<int> Missing(ParseResult r, Session s) =>
        throw new UsageException("Choose a test: noi test roundtrip <discs…>, or noi test compare <discs…> --chdman <exe>.");

    public static Task<int> RoundTrip(ParseResult r, Session s) => Run(r, s, TestKind.RoundTrip);

    public static Task<int> Compare(ParseResult r, Session s)
    {
        var exe = r.GetValue(Chdman) ?? throw new UsageException("Compare needs a chdman executable.", "noi test compare <discs…> --chdman <path to chdman>");
        if (!File.Exists(exe)) throw new UsageException($"No chdman at {exe}.");
        s.Settings.Diagnostics.ChdmanExePath = Path.GetFullPath(exe);
        return Run(r, s, TestKind.CompareWithChdman);
    }

    private static async Task<int> Run(ParseResult r, Session s, TestKind kind)
    {
        var started = DateTime.UtcNow;
        var inputs = Common.Gather(s, Common.Paths(r, PathsArg, "noi test roundtrip"), Inputs.Kind.Discs, Common.Filter(r));
        var records = new System.Collections.Concurrent.ConcurrentQueue<TestRecord>();
        var jobs = inputs.Items.Select(i => new RunJob(i.Name, [i.Path], i.Size, c => TestJobs.Run(c, i.Path, kind, records.Enqueue))).ToList();
        if (jobs.Count == 0) { s.Ui.Answer("No disc images found."); return Exit.Ok; }
        if (kind == TestKind.CompareWithChdman) s.Ui.Info($"Comparing lib-chd-kit {LibChdKit.Chdman.Version} with {ChdmanExe.Version(s.Settings.Diagnostics.ChdmanExePath)}");
        await Common.RunAsync(s, "test", "Testing", jobs, (job, prefix) =>
        {
            var o = job.Outcome!;
            var (word, color) = Common.Look(s.Ui, o.Status, "Passed");
            if (o.Status == JobStatus.Failed) word = "Failed";
            s.Ui.Status(word, $"{prefix}{job.Name} {s.Ui.Sym.Dot} {o.Summary}", color);
            if (o.Status == JobStatus.Failed) s.Ui.QuietFailure($"{job.Name}: {o.Summary}");
        });
        var list = records.ToList();
        var passed = list.Count(x => x.Passed);
        s.Ui.Table(["Input", "Disc", "Test", "Result", "Time", "MB/s", "Notes"], list.Select(x => (IReadOnlyList<string>)
        [
            x.Input, x.Disc, x.Test, x.Passed ? s.Ui.Paint.Green("passed") : s.Ui.Paint.Red("FAILED"),
            x.LibTime.ToString(@"m\:ss\.f") + (x.ExeTime is { } e ? $" / {e:m\\:ss\\.f}" : ""), $"{x.LibMbps:0}" + (x.ExeMbps is { } m ? $" / {m:0}" : ""), x.Notes,
        ]));
        if (r.GetValue(Csv) is { } csv)
        {
            File.WriteAllText(csv, TestJobs.ToCsv(list));
            s.Ui.Info($"Results: {Path.GetFullPath(csv)}");
        }
        var code = Exit.Combine(s.Token.IsCancellationRequested ? Exit.Cancelled : 0, passed < list.Count || jobs.Any(j => j.Outcome?.Status == JobStatus.Failed) ? Exit.Failed : 0);
        s.Ui.QuietSummary($"done: {passed} of {list.Count} passed, {Common.Elapsed(DateTime.UtcNow - started)}");
        var doc = Common.Document($"test {(kind == TestKind.RoundTrip ? "roundtrip" : "compare")}", code, DateTime.UtcNow - started);
        doc["results"] = JsonSerializer.SerializeToNode(list);
        s.Ui.Json(doc);
        return code;
    }
}

/// <summary>noi gui: opens the app with files loaded, as dropping them on its window does.</summary>
public static class GuiCommand
{
    private static readonly Argument<string[]> PathsArg = new("paths") { Arity = ArgumentArity.ZeroOrMore, HelpName = "files or folders…", Description = "Files and folders to load" };
    private static readonly Option<string?> Page = new("--page") { Description = "The page to open: optimize, convert, verify, organize, settings or tests", HelpName = "page" };

    static GuiCommand() => Page.AcceptOnlyFromAmong("optimize", "convert", "verify", "organize", "settings", "tests");

    public static Command Command()
    {
        var c = new Command("gui", "Open the app with these files");
        c.Arguments.Add(PathsArg);
        c.Options.Add(Page);
        return c;
    }

    public static HelpDoc Doc => new("Open the NoIntrovert app with these files loaded.", "", "noi gui [files or folders…] [--page <page>]",
        [("noi gui D:\\Roms --page verify", "The app, on the Verify page, with D:\\Roms loaded.")], [new("OPTIONS", [Page])]);

    public static Task<int> Run(ParseResult r, Session s)
    {
        var app = AppPath() ?? throw new UsageException("The NoIntrovert app isn't beside noi.", $"Looked in {AppContext.BaseDirectory}");
        var info = new ProcessStartInfo(app) { UseShellExecute = false };
        foreach (var p in r.GetValue(PathsArg) ?? []) info.ArgumentList.Add(Path.GetFullPath(p));
        if (r.GetValue(Page) is { } page) { info.ArgumentList.Add("--page"); info.ArgumentList.Add(page); }
        if (s.DryRun) { s.Ui.Answer($"Would start {app}"); return Task.FromResult(Exit.Ok); }
        Process.Start(info);
        s.Ui.Status("Opened", "NoIntrovert");
        return Task.FromResult(Exit.Ok);
    }

    /// <summary>The app beside noi: NoIntrovert.exe on Windows, NoIntrovert in the same folder (or the .app's MacOS folder).</summary>
    private static string? AppPath()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "NoIntrovert.exe" } : ["NoIntrovert"])
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>noi completion: tab completion scripts that ask noi itself for suggestions.</summary>
public static class CompletionCommand
{
    private static readonly Argument<string> Shell = new("shell") { Description = "powershell, bash, zsh or fish", HelpName = "shell" };

    static CompletionCommand() => Shell.AcceptOnlyFromAmong("powershell", "pwsh", "bash", "zsh", "fish");

    public static Command Command()
    {
        var c = new Command("completion", "Tab completion for PowerShell, bash, zsh and fish");
        c.Arguments.Add(Shell);
        return c;
    }

    public static HelpDoc Doc => new(
        "Tab completion for noi's commands and options.",
        "Prints a script for your shell; load it from your profile.",
        "noi completion <powershell|bash|zsh|fish>",
        [
            ("noi completion powershell >> $PROFILE", "PowerShell: completion in every new window."),
            ("noi completion bash >> ~/.bashrc", ""),
            ("noi completion zsh >> ~/.zshrc", ""),
            ("noi completion fish > ~/.config/fish/completions/noi.fish", ""),
        ], []);

    public static Task<int> Run(ParseResult r, Session s)
    {
        var script = r.GetValue(Shell) switch
        {
            "powershell" or "pwsh" => """
                # noi tab completion for PowerShell
                Register-ArgumentCompleter -Native -CommandName noi, noi.exe -ScriptBlock {
                    param($wordToComplete, $commandAst, $cursorPosition)
                    noi "[suggest:$cursorPosition]" "$commandAst" | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                    }
                }
                """,
            "bash" => """
                # noi tab completion for bash
                _noi_complete() {
                    local IFS=$'\n'
                    COMPREPLY=( $(noi "[suggest:${#COMP_LINE}]" "$COMP_LINE" 2>/dev/null) )
                }
                complete -o default -F _noi_complete noi
                """,
            "zsh" => """
                # noi tab completion for zsh
                _noi_complete() {
                    local -a completions
                    completions=("${(@f)$(noi "[suggest:${#BUFFER}]" "$BUFFER" 2>/dev/null)}")
                    compadd -a completions
                }
                compdef _noi_complete noi
                """,
            _ => """
                # noi tab completion for fish
                complete -c noi -f -a '(noi "[suggest:"(string length (commandline -cp))"]" (commandline -cp) 2>/dev/null)'
                """,
        };
        s.Ui.Stdout.WriteLine(script);
        return Task.FromResult(Exit.Ok);
    }
}
