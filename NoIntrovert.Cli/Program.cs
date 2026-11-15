using System.CommandLine;
using System.Reflection;
using System.Text;
using NoIntrovert.Cli.Cli;
using NoIntrovert.Cli.Commands;
using NoIntrovert.Cli.Output;

namespace NoIntrovert.Cli;

/// <summary>noi: NoIntrovert's command line (docs/requirements/cli.md).</summary>
public static class Program
{
    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<int> Main(string[] args)
    {
        var app = new App();

        // Tab completion asks noi itself: System.CommandLine's [suggest] directive.
        if (args.Length > 0 && args[0].StartsWith("[suggest", StringComparison.Ordinal))
            return await app.Root.Parse(args).InvokeAsync();

        args = WindowsArgs.Normalize(args, app.OptionNames());
        // A help switch anywhere asks for help, even where an option would take it as its value (noi convert --to /?).
        string[] helpWords = ["--help", "-h", "-?"];
        var wantsHelp = args.Any(a => helpWords.Contains(a));
        var parse = app.Root.Parse(wantsHelp ? args.Where(a => !helpWords.Contains(a)).ToArray() : args, Parsing);
        var command = parse.CommandResult.Command;
        var silent = parse.GetValue(GlobalOptions.Silent);
        var paint = new Paint(!silent && Term.Detect(parse.GetValue(GlobalOptions.NoColor) ? ColorMode.Never : parse.GetValue(GlobalOptions.Color), ProgressStyle.None, parse.GetValue(GlobalOptions.Ascii)).Color);
        var width = HelpWidth();

        // Help first: it works even with options missing or wrong (noi convert --to /?).
        if (wantsHelp || parse.GetValue(GlobalOptions.HelpAll) || command.Name == "help")
        {
            if (!silent) Console.Out.Write(Help(app, parse, paint, width));
            return Exit.Ok;
        }
        if (parse.GetValue(GlobalOptions.Version))
        {
            if (!silent) Console.Out.WriteLine(VersionText());
            return Exit.Ok;
        }
        if (command is RootCommand && parse.Tokens.Count == 0)
        {
            if (!silent) Console.Out.Write(RootHelp(app, paint, width, all: false));
            return Exit.Ok;
        }
        if (parse.Errors.Count > 0 || UnknownOptions(parse).Any())
        {
            if (!silent) WriteErrors(app, parse, paint);
            return Exit.Usage;
        }
        if (!app.Handlers.TryGetValue(command, out var handler))
        {
            if (!silent) Console.Out.Write(HelpFor(app, command, paint, width, false));
            return Exit.Usage;
        }

        using var cts = new CancellationTokenSource();
        var presses = 0;
        Session? session = null;
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // The first Ctrl-C stops new jobs and cancels running ones (they delete what they were writing); a second one exits at once.
            if (Interlocked.Increment(ref presses) == 1)
            {
                e.Cancel = true;
                session?.Ui.Warn("Stopping: no new files are started, and those in progress are deleted. Press Ctrl-C again to quit at once.");
                cts.Cancel();
            }
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            session = Session.Create(parse, cts.Token);
            using (session)
            {
                var code = await handler(parse, session);
                if (cts.IsCancellationRequested) code = Exit.Combine(code, Exit.Cancelled);
                session.Log.Write(LogLevel.Debug, $"exit code {code}");
                return code;
            }
        }
        catch (UsageException ex)
        {
            if (!silent && session?.Ui.IsSilent != true) WriteError(paint, ex.Message, ex.Hints);
            session?.Log.Write(LogLevel.Error, ex.Message);
            return Exit.Usage;
        }
        catch (OperationCanceledException)
        {
            return Exit.Cancelled;
        }
        catch (Exception ex)
        {
            session?.Log.Write(LogLevel.Error, ex.ToString());
            if (!silent)
                WriteError(paint, $"Something went wrong: {ex.Message}",
                    [session?.Log.Path is { } log ? $"The details are in the log: {log}" : "", "Please report it at https://github.com/Nickel42/NoIntrovert/issues with the log."]);
            return Exit.Failed;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            Core.Dats.ThrottledJsonFile.FlushAll();
        }
    }

    /// <summary>@list.txt: one path (or option) per line, so paths with spaces stay whole; blank and # lines are skipped.</summary>
    internal static readonly ParserConfiguration Parsing = new()
    {
        ResponseFileTokenReplacer = (string token, out IReadOnlyList<string>? replacement, out string? error) =>
        {
            var path = token.TrimStart('@');
            replacement = null;
            error = null;
            try
            {
                replacement = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .Select(l => l.Length > 1 && l[0] == '"' && l[^1] == '"' ? l[1..^1] : l).ToList();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = $"Couldn't read the list {path}: {ex.Message}";
                return false;
            }
        },
    };

    private static int HelpWidth()
    {
        if (Console.IsOutputRedirected) return 80;
        try { return Math.Max(60, Console.WindowWidth - 1); }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException) { return 80; }
    }

    private static void WriteError(Paint paint, string message, IReadOnlyList<string> hints)
    {
        var sb = new StringBuilder($"{paint.BoldRed(Term.InCi || !Console.IsErrorRedirected ? "✖" : "error:")} {message}");
        foreach (var h in hints.Where(h => h.Length > 0)) sb.Append('\n').Append("  ").Append(h);
        Console.Error.WriteLine(sb.ToString());
    }

    /// <summary>Parse errors, each with a "did you mean" when what was typed is close to a command or option.</summary>
    private static void WriteErrors(App app, ParseResult parse, Paint paint)
    {
        var command = parse.CommandResult.Command;
        var path = HelpWriter.CommandPath(command);
        var helpHint = $"noi {(path.Length > 0 ? path + " " : "")}--help shows {(command is RootCommand ? "the commands" : "the options")}.";
        var unmatched = parse.UnmatchedTokens.ToList();
        // A command noi doesn't know: say just that, with the closest one.
        if (command is RootCommand && unmatched.Count > 0 && !unmatched[0].StartsWith('-'))
        {
            var guess = Suggest.Closest(unmatched[0], app.CommandNames());
            WriteError(paint, $"Unknown command \"{unmatched[0]}\".", [guess is null ? "" : $"Did you mean \"{guess}\"?", helpHint]);
            return;
        }
        var hints = new List<string>();
        var names = command.Options.Concat(app.Root.Options).SelectMany(o => o.Aliases.Append(o.Name)).Distinct().ToList();
        foreach (var token in unmatched.Concat(UnknownOptions(parse)).Where(t => t.StartsWith('-')).Distinct())
            if (Suggest.Closest(token.Split('=')[0], names) is { } guess) hints.Add($"Did you mean \"{guess}\" instead of \"{token}\"?");
        hints.Add(helpHint);
        var messages = parse.Errors.Select(e => e.Message).Where(m => !m.StartsWith("Required command was not provided", StringComparison.Ordinal) || unmatched.Count == 0)
            .Concat(UnknownOptions(parse).Select(t => $"Unknown option \"{t}\"."))
            .Distinct().ToList();
        if (messages.Count == 0) messages.Add("Choose what to do.");
        WriteError(paint, string.Join(Environment.NewLine + "  ", messages), hints);
    }

    /// <summary>Words that look like options but aren't any (a path argument takes them otherwise): "--onl".</summary>
    internal static IEnumerable<string> UnknownOptions(ParseResult parse) =>
        parse.CommandResult.Children.OfType<System.CommandLine.Parsing.ArgumentResult>()
            .SelectMany(a => a.Tokens).Select(t => t.Value)
            .Where(v => v.Length > 1 && v[0] == '-' && v != "-" && !File.Exists(v) && !Directory.Exists(v));

    private static string Help(App app, ParseResult parse, Paint paint, int width)
    {
        var command = parse.CommandResult.Command;
        var all = parse.GetValue(GlobalOptions.HelpAll);
        if (command.Name == "help")
        {
            var names = parse.GetValue(command.Arguments.OfType<Argument<string[]>>().First()) ?? [];
            all |= command.Options.OfType<Option<bool>>().Any(o => o.Name == "--all" && parse.GetValue(o));
            Command target = app.Root;
            foreach (var name in names)
                target = target.Subcommands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidOperationException();
            command = target;
        }
        return command is RootCommand ? RootHelp(app, paint, width, all) : HelpFor(app, command, paint, width, all);
    }

    private static string HelpFor(App app, Command command, Paint paint, int width, bool all)
    {
        if (!app.Docs.TryGetValue(command, out var doc)) return RootHelp(app, paint, width, all);
        // A subcommand without its own page (runs undo) shows its parent's, with its own usage.
        return HelpWriter.Render(command, doc, paint, all, width);
    }

    /// <summary>noi /?: the short help (§6.1), or with --all every command.</summary>
    internal static string RootHelp(App app, Paint p, int width, bool all)
    {
        string Cmd(string name, string text) => $"  {p.Green(name.PadRight(10))} {text}";
        var sb = new StringBuilder();
        sb.AppendLine($"{p.Bold($"NoIntrovert {Version}")} — tidy, verify and compress your game library");
        sb.AppendLine();
        sb.AppendLine(p.Bold("USAGE"));
        sb.AppendLine("  noi <command> <files or folders…> [options]");
        sb.AppendLine();
        sb.AppendLine(p.Bold("MAIN COMMANDS"));
        sb.AppendLine(Cmd("optimize", "Verify, convert, rename and file into system folders, in one go"));
        sb.AppendLine(Cmd("convert", "Convert to each system's best format (CHD, RVZ, 7z, NSZ…)"));
        sb.AppendLine(Cmd("verify", "Check for damage and look games up in No-Intro and Redump"));
        sb.AppendLine(Cmd("organize", "Rename, move and de-duplicate, with a plan first and undo after"));
        sb.AppendLine();
        sb.AppendLine(p.Bold("MORE"));
        sb.AppendLine(Cmd("runs", "Undo an Organize or Optimize run, or empty its trash"));
        sb.AppendLine(Cmd("info", "What a file is and what it can become"));
        sb.AppendLine(Cmd("presets", "The presets for each system"));
        sb.AppendLine(Cmd("db", "Game databases: status and update"));
        sb.AppendLine(Cmd("keys", "Which console keys were found"));
        sb.AppendLine(Cmd("settings", "Read and change settings (shared with the app)"));
        sb.AppendLine(Cmd("gui", "Open the app with these files"));
        if (all)
        {
            sb.AppendLine(Cmd("test", "Library tests: round trip, and comparison with chdman (power user)"));
            sb.AppendLine(Cmd("completion", "Tab completion for PowerShell, bash, zsh and fish"));
            sb.AppendLine(Cmd("help", "Help for a command (same as --help)"));
        }
        sb.AppendLine();
        sb.AppendLine(p.Bold("EXAMPLES"));
        (string, string)[] examples =
        [
            (@"noi verify D:\Roms", "Check every game in a folder"),
            (@"noi convert D:\Roms\PS1", "Discs → CHD, next to the originals"),
            ("noi convert . --to rvz -p smallest", "GameCube/Wii → smallest RVZ"),
            (@"noi optimize D:\Roms --new-library E:\", "Build a tidy ES-DE library on E:"),
            (@"noi convert D:\Roms --replace --yes -q", "Replace originals, no questions"),
        ];
        var column = examples.Max(e => e.Item1.Length) + 3;
        foreach (var (cmd, what) in examples)
            sb.AppendLine(column + 2 + what.Length <= width ? $"  {p.Cyan(cmd.PadRight(column))}{what}" : $"  {p.Cyan(cmd)}\n      {what}");
        sb.AppendLine();
        sb.AppendLine(p.Bold("COMMON OPTIONS"));
        (string, string)[] common =
        [
            ("-o, --output <folder>", "Save into a folder instead of next to the originals"),
            ("    --replace", "Replace the originals (each only after it verifies)"),
            ("-n, --dry-run", "Show what would happen; change nothing"),
            ("-y, --yes", "Don't ask for confirmation"),
            ("-q, --quiet", "Only problems and a one-line summary"),
            ("-s, --silent", "Print nothing; check the exit code"),
            ("    --json", "Results as JSON"),
            ("-j, --jobs <n>", "Files at the same time (default 2)"),
            ("    --use-settings", "Use the app's saved settings (default: recommended values)"),
        ];
        foreach (var (opt, what) in common) sb.AppendLine($"  {p.Green(opt.PadRight(24))}{what}");
        sb.AppendLine();
        sb.AppendLine(HelpWriter.Wrap("Run \"noi <command> --help\" for a command's options, \"noi help --all\" for every command. " +
                                      $"/? and /option work too on Windows. More: {HelpWriter.DocsUrl}", width, ""));
        sb.AppendLine(HelpWriter.Wrap("Exit codes: 0 done · 1 some files failed · 2 wrong options · 3 damaged files found · 4 missing keys or databases · " +
                                      "5 a saved plan no longer matches · 130 cancelled", width, ""));
        return sb.ToString();
    }

    private static string VersionText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"noi {Version} (NoIntrovert)");
        sb.AppendLine($"  {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
        string Lib(string name, Func<string> version)
        {
            try { return version(); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException) { return $"{name}: not found beside noi"; }
        }
        sb.AppendLine($"  {Lib("lib-chd-kit", () => LibChdKit.Chdman.Version)}");
        sb.AppendLine($"  {Lib("lib-wii-gamecube-kit", () => LibWiiGameCubeKit.WiiGameCubeKit.Version)}");
        sb.AppendLine($"  7-Zip engine {Lib("7-Zip engine", () => SevenZipEngine.IsAvailable(out var why) ? "available" : why ?? "not available")}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Windows-style options (Q4): /? and /h are help; /q, /s, /y, /v, /n are their -x forms; /output:folder,
/// /o:folder and /jobs=4 are their --name value forms. Only names noi knows are turned round, and never a
/// path that exists (on Linux and macOS /something is usually a path).
/// </summary>
public static class WindowsArgs
{
    public static string[] Normalize(IReadOnlyList<string> args, IReadOnlyCollection<string> optionNames)
    {
        var known = new HashSet<string>(optionNames, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(args.Count);
        foreach (var arg in args)
        {
            if (arg is "/?" or "/h" or "/H" or "/help")
            {
                result.Add("--help");
                continue;
            }
            if (arg.Length < 2 || arg[0] != '/' || arg[1] == '/' || File.Exists(arg) || Directory.Exists(arg))
            {
                result.Add(arg);
                continue;
            }
            var body = arg[1..];
            var split = body.IndexOfAny([':', '=']);
            var name = split > 0 ? body[..split] : body;
            var value = split > 0 ? body[(split + 1)..] : null;
            var dashed = name.Length == 1 ? "-" + name : "--" + name;
            var match = known.FirstOrDefault(k => k.Equals(dashed, StringComparison.OrdinalIgnoreCase));
            // A short Windows switch spelled long (/quiet) or a long one spelled short (/q) both work.
            match ??= known.FirstOrDefault(k => k.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
            if (match is null || (!OperatingSystem.IsWindows() && name.Contains('/')))
            {
                result.Add(arg);
                continue;
            }
            result.Add(match);
            if (value is not null) result.Add(value);
        }
        return [.. result];
    }
}
