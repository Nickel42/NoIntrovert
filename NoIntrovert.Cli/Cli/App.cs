using System.CommandLine;
using NoIntrovert.Cli.Commands;

namespace NoIntrovert.Cli.Cli;

/// <summary>A command's work: parse result and session in, exit code out.</summary>
public delegate Task<int> Handler(ParseResult result, Session session);

/// <summary>
/// noi's commands (docs/requirements/cli.md §3.2), each with its options, its help page and its handler. Built
/// once per run; <see cref="Program"/> parses with it and runs the handler of the command that was asked for.
/// </summary>
public sealed class App
{
    public RootCommand Root { get; } = new("NoIntrovert: tidy, verify and compress your game library");
    public Dictionary<Command, HelpDoc> Docs { get; } = [];
    public Dictionary<Command, Handler> Handlers { get; } = [];

    /// <summary>Commands left out of the short help (power user), listed by noi help --all.</summary>
    public HashSet<Command> Advanced { get; } = [];

    public App()
    {
        // noi's own help and version replace System.CommandLine's, so every page follows §6.
        foreach (var o in Root.Options.ToList()) Root.Options.Remove(o);
        foreach (var o in GlobalOptions.All) Root.Options.Add(o);
        Root.Options.Add(GlobalOptions.Version);

        Add(OptimizeCommand.Command(), OptimizeCommand.Doc, OptimizeCommand.Run);
        Add(ConvertCommand.Command(), ConvertCommand.Doc, ConvertCommand.Run);
        Add(VerifyCommand.Command(), VerifyCommand.Doc, VerifyCommand.Run);
        Add(OrganizeCommand.Command(), OrganizeCommand.Doc, OrganizeCommand.Run);
        AddRuns();
        Add(InfoCommand.Command(), InfoCommand.Doc, InfoCommand.Run);
        Add(PresetsCommand.Command(), PresetsCommand.Doc, PresetsCommand.Run);
        AddDb();
        Add(KeysCommand.Command(), KeysCommand.Doc, KeysCommand.Run);
        AddSettings();
        AddTests();
        Add(GuiCommand.Command(), GuiCommand.Doc, GuiCommand.Run);
        Add(CompletionCommand.Command(), CompletionCommand.Doc, CompletionCommand.Run);
        var help = new Command("help", "Help for a command (same as --help)");
        help.Arguments.Add(new Argument<string[]>("command") { Arity = ArgumentArity.ZeroOrMore, HelpName = "command" });
        help.Options.Add(new Option<bool>("--all") { Description = "Every command and option" });
        Root.Subcommands.Add(help);
        Docs[help] = new HelpDoc("Help for noi or one of its commands.", "", "noi help [command]\nnoi <command> --help",
            [("noi help convert", "The options for convert."), ("noi help --all", "Every command, the power-user ones too.")], []);

        // noi runs its handlers itself; an action on each command only tells the parser "noi runs" on its own is complete.
        foreach (var command in Handlers.Keys.Append(help))
            command.SetAction(_ => 0);
    }

    private void Add(Command command, HelpDoc doc, Handler handler, Command? parent = null)
    {
        (parent ?? Root).Subcommands.Add(command);
        Docs[command] = doc;
        Handlers[command] = handler;
    }

    private void AddRuns()
    {
        var runs = RunsCommand.Command(out var list, out var show, out var undo, out var empty);
        Root.Subcommands.Add(runs);
        Docs[runs] = RunsCommand.Doc;
        Handlers[runs] = RunsCommand.List;
        Docs[list] = RunsCommand.Doc;
        Handlers[list] = RunsCommand.List;
        Docs[show] = RunsCommand.ShowDoc;
        Handlers[show] = RunsCommand.Show;
        Docs[undo] = RunsCommand.UndoDoc;
        Handlers[undo] = RunsCommand.Undo;
        Docs[empty] = RunsCommand.EmptyDoc;
        Handlers[empty] = RunsCommand.EmptyTrash;
    }

    private void AddDb()
    {
        var db = DbCommand.Command(out var status, out var update);
        Root.Subcommands.Add(db);
        Docs[db] = DbCommand.Doc;
        Handlers[db] = DbCommand.Status;
        Docs[status] = DbCommand.Doc;
        Handlers[status] = DbCommand.Status;
        Docs[update] = DbCommand.UpdateDoc;
        Handlers[update] = DbCommand.Update;
    }

    private void AddSettings()
    {
        var settings = SettingsCommand.Command(out var subs);
        Root.Subcommands.Add(settings);
        Docs[settings] = SettingsCommand.Doc;
        Handlers[settings] = SettingsCommand.List;
        foreach (var (command, handler) in subs)
        {
            Docs[command] = SettingsCommand.Doc;
            Handlers[command] = handler;
        }
    }

    private void AddTests()
    {
        var test = TestsCommand.Command(out var roundtrip, out var compare);
        Root.Subcommands.Add(test);
        Advanced.Add(test);
        Docs[test] = TestsCommand.Doc;
        Handlers[test] = TestsCommand.Missing;
        Docs[roundtrip] = TestsCommand.Doc;
        Handlers[roundtrip] = TestsCommand.RoundTrip;
        Docs[compare] = TestsCommand.Doc;
        Handlers[compare] = TestsCommand.Compare;
    }

    /// <summary>Every option and command name noi knows, for /option on Windows and for "did you mean".</summary>
    public IReadOnlyCollection<string> OptionNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(Command c)
        {
            foreach (var o in c.Options)
            {
                names.Add(o.Name);
                foreach (var a in o.Aliases) names.Add(a);
            }
            foreach (var s in c.Subcommands) Walk(s);
        }
        Walk(Root);
        return names;
    }

    public IReadOnlyCollection<string> CommandNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(Command c)
        {
            foreach (var s in c.Subcommands)
            {
                names.Add(s.Name);
                Walk(s);
            }
        }
        Walk(Root);
        return names;
    }
}
