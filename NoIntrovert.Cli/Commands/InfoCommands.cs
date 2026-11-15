using System.CommandLine;
using System.Text.Json.Nodes;
using NoIntrovert.Cli.Cli;
using NoIntrovert.Core.Ctr;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Emulators;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Pipelines;
using NoIntrovert.Core.Presets;
using NoIntrovert.Core.Systems;

namespace NoIntrovert.Cli.Commands;

/// <summary>noi info (§5.7): what a file is and what it can become, as the Convert page's side panel says it.</summary>
public static class InfoCommand
{
    private static readonly Argument<string[]> Files = new("files") { Description = "Files (or game folders) to describe", Arity = ArgumentArity.OneOrMore, HelpName = "file" };

    public static Command Command()
    {
        var c = new Command("info", "What a file is and what it can become");
        c.Arguments.Add(Files);
        return c;
    }

    public static HelpDoc Doc => new(
        "What a file is and what it can become.",
        "Its system, format and size, what Verify found (if it was checked), the format it would become, every preset for it " +
        "(★ is the recommended one), the keys it needs, and the emulators that play the result (\"Works with\").",
        "noi info <file…>",
        [("noi info \"Crash Bandicoot (USA).cue\"", ""), ("noi info game.nsp --json", "The same as JSON.")],
        [new("OUTPUT", [GlobalOptions.Json])]);

    public static Task<int> Run(ParseResult r, Session s)
    {
        var all = new JsonArray();
        var code = Exit.Ok;
        foreach (var raw in r.GetValue(Files) ?? [])
        {
            var path = Path.GetFullPath(raw);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                s.Ui.Error($"{raw}: no such file or folder.");
                code = Exit.Usage;
                continue;
            }
            all.Add(Describe(s, path));
        }
        var doc = Common.Document("info", code, TimeSpan.Zero);
        doc["files"] = all;
        s.Ui.Json(doc);
        return Task.FromResult(code);
    }

    private static JsonObject Describe(Session s, string path)
    {
        var p = s.Ui.Paint;
        var set = FormatRegistry.Default.Identify(path) ?? new FileSet(path, [path], Directory.Exists(path) ? ConvertTargets.PackedFolder : ConvertTargets.OtherFile);
        var system = SystemDetector.SystemOf(set, Path.GetDirectoryName(path));
        var options = ConvertTargets.For(set, s.Settings);
        system ??= options.Plan.System;
        var match = MatchStore.Default.Get(path);
        var list = FormatPresets.ListFor(system, options.Choices.Select(c => c.Kind).ToList(), s.Settings.Presets);
        var start = FormatPresets.Start(list, system, options.Default.Kind, s.Settings.Presets);

        s.Ui.Answer(p.Bold(Path.GetFileName(Path.TrimEndingDirectorySeparator(path))));
        void Field(string name, string value) { if (value.Length > 0) s.Ui.Answer($"  {p.Dim(name.PadRight(12))}{value}"); }
        Field("System", TargetFormats.NameOf(system));
        Field("File", FileDescription.AboutFile(path));
        Field("Files", set.Members.Count > 1 ? string.Join(", ", set.Members.Select(Path.GetFileName)) : "");
        if (match is not null)
        {
            var (word, color, symbol) = VerifyCommand.Look(s.Ui, match.Status, null);
            Field("Verify", $"{symbol} {color(word)} {s.Ui.Sym.Dot} {match.Text}");
        }
        else Field("Verify", p.Dim("not checked yet (noi verify)"));
        Field("Status", options.Plan.Text);
        Field("Becomes", start.Preset is { IsLeave: false } chosen ? $"{chosen.Label}" : "left as it is");

        s.Ui.Answer($"  {p.Dim("Presets")}");
        foreach (var item in list.Where(i => i.IsPreset && !i.Preset!.IsCustom))
        {
            var pr = item.Preset!;
            var mark = ReferenceEquals(item, start) ? p.Green(s.Ui.Sym.Arrow) : " ";
            var label = item.IsEnabled ? pr.Label : p.Dim(pr.Label);
            var hint = item.IsEnabled ? pr.Hint : item.Note;
            s.Ui.Answer($"    {mark} {pr.Id,-20} {label}{(hint.Length > 0 ? p.Dim($"  {s.Ui.Sym.Dot} {hint}") : "")}{(item.HasPlays ? p.Dim($"  plays in {item.Plays}") : "")}");
        }

        var keys = KeysNeeded(s, system, path);
        if (keys is not null) Field("Keys", keys);

        var ext = start.Preset?.OutputExtension(system);
        var works = system is null || ext is null ? [] : EmulatorSupport.For(system, ext, start.Preset?.Values.Codecs ?? []);
        if (works.Count > 0)
        {
            var playing = works.Where(w => w.State == SupportState.Works).Select(w => w.Name).ToList();
            var unsure = works.Count(w => w.State == SupportState.Unknown);
            Field("Works with", $"{string.Join(", ", playing)}{(unsure > 0 ? p.Dim($" ({unsure} not confirmed)") : "")}");
            s.Ui.Answer($"  {p.Dim("".PadRight(12))}{p.Dim(EmulatorSupport.Source)}");
        }
        s.Ui.Answer();

        return new JsonObject
        {
            ["path"] = path, ["system"] = system, ["systemName"] = TargetFormats.NameOf(system), ["format"] = FileDescription.FormatName(path),
            ["about"] = FileDescription.AboutFile(path), ["members"] = Json.Array(set.Members), ["status"] = options.Plan.Text,
            ["verify"] = match is null ? null : new JsonObject { ["result"] = match.Status.ToString(), ["text"] = match.Text, ["game"] = match.Game, ["expectedName"] = match.ExpectedName },
            ["preset"] = start.Preset?.Id,
            ["presets"] = new JsonArray(list.Where(i => i.IsPreset && !i.Preset!.IsCustom).Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Preset!.Id, ["label"] = i.Preset.Label, ["usable"] = i.IsEnabled, ["hint"] = i.Hint, ["recommended"] = i.Preset.Recommended, ["plays"] = i.Plays,
            }).ToArray()),
            ["keys"] = keys,
            ["worksWith"] = Json.Array(works.Where(w => w.State == SupportState.Works).Select(w => w.Name)),
        };
    }

    /// <summary>For the systems that need the user's keys: which, and whether they were found.</summary>
    private static string? KeysNeeded(Session s, string? system, string path) => system switch
    {
        "nintendo-3ds" => ThreeDsNeedsKeys(path) ? CtrJobs.Describe(CtrJobs.LoadKeys(s.Settings.Keys.ThreeDsKeysFolder, out _)) : "not needed (already decrypted)",
        "switch" => Core.Switch.SwitchJobs.Describe(s.Settings.Keys.SwitchKeysPath),
        "playstation-3" => Core.Ps3.Ps3Jobs.KeyFileFor(path, s.Settings) is { } dkey ? $"{Path.GetFileName(dkey)} found" : "needs its .dkey (beside it, or in the folder set with --keys-ps3)",
        "playstation-vita" => Core.Vita.ZrifLibrary.Describe(s.Settings.Keys.VitaZrifPath),
        "wii-u" => Core.WiiU.WuaJobs.LoadKeys(s.Settings.Keys.WiiUKeysPath).Describe(),
        _ => null,
    };

    private static bool ThreeDsNeedsKeys(string path)
    {
        if (ArchiveExtractor.IsArchive(path)) return true;
        return CtrFacts.Of(path) is { } facts && (facts.Encrypted || facts.Type == Lib3dsKit.CtrFileType.Cia);
    }
}

/// <summary>noi presets (§5.7): each system's presets, and the default the app starts new files on.</summary>
public static class PresetsCommand
{
    private static readonly Argument<string?> System = new("system") { Description = "A system (id, ES-DE or RomM name); all of them when left out", Arity = ArgumentArity.ZeroOrOne, HelpName = "system" };
    private static readonly Option<string[]> Default = new("--default") { Description = "Set the app's default preset for a system: --default <system> <id> (writes the app's settings)", Arity = new ArgumentArity(2, 2), AllowMultipleArgumentsPerToken = true, HelpName = "system id" };

    public static Command Command()
    {
        var c = new Command("presets", "The presets for each system");
        c.Arguments.Add(System);
        c.Options.Add(Default);
        return c;
    }

    public static HelpDoc Doc => new(
        "The presets for each system: what -p takes.",
        "A preset is a format and its settings (\"RVZ · Smallest\"). ★ marks the recommended one, which noi uses unless you choose " +
        "another; ⚠ marks one that loses data for its system. -p takes a preset's id, the end of it (smallest), or the name of one " +
        "you saved from Custom in the app.",
        "noi presets [system]\nnoi presets --default <system> <id>",
        [("noi presets", "Every system with its presets."), ("noi presets ps2", "PS2's presets."), ("noi presets --default ps2 zso-opl", "The app (and noi --use-settings) start PS2 files on ZSO.")],
        [new("OPTIONS", [Default, GlobalOptions.Json])]);

    public static Task<int> Run(ParseResult r, Session s)
    {
        if (r.GetValue(Default) is { Length: 2 } pair) return Task.FromResult(SetDefault(s, pair[0], pair[1]));
        var systems = r.GetValue(System) is { } one ? [TargetFormats.ById(Common.ResolveSystem(one))!] : TargetFormats.All.Where(x => FormatPresets.ForSystem(x.Id).Count > 0).ToList();
        var app = AppSettings.Peek();
        var json = new JsonArray();
        foreach (var system in systems)
        {
            var presets = FormatPresets.ForSystem(system.Id).Concat(FormatPresets.Saved(app.Presets, system.Id)).ToList();
            var chosen = FormatPresets.Default(system.Id, app.Presets);
            s.Ui.Answer($"{s.Ui.Paint.Bold(system.Name)} {s.Ui.Paint.Dim($"({system.Id}{(system.Esde is { } e && e != system.Id ? $", {e}" : "")})")}");
            foreach (var p in presets)
            {
                var mark = p.Recommended ? s.Ui.Paint.Yellow(s.Ui.Sym.Star) : p.Warning ? s.Ui.Paint.Red(s.Ui.Sym.Warn) : " ";
                var dflt = chosen is not null && chosen.Id == p.Id && !p.Recommended ? s.Ui.Paint.Cyan(" (the app's default)") : "";
                var label = p.Producible ? $"{p.Format} · {p.Name}" : s.Ui.Paint.Dim($"{p.Format} · {p.Name} (not available yet)");
                s.Ui.Answer($"  {mark} {p.Id,-22} {label}{dflt}  {s.Ui.Paint.Dim(p.Hint)}");
                json.Add(new JsonObject
                {
                    ["system"] = system.Id, ["id"] = p.Id, ["format"] = p.Format, ["name"] = p.Name, ["hint"] = p.Hint,
                    ["recommended"] = p.Recommended, ["warning"] = p.Warning, ["available"] = p.Producible, ["saved"] = p.IsSaved,
                });
            }
        }
        var doc = Common.Document("presets", Exit.Ok, TimeSpan.Zero);
        doc["presets"] = json;
        s.Ui.Json(doc);
        return Task.FromResult(Exit.Ok);
    }

    private static int SetDefault(Session s, string systemWord, string id)
    {
        var system = Common.ResolveSystem(systemWord);
        var settings = AppSettings.Load();
        var preset = FormatPresets.Find(id, system, settings.Presets)
                     ?? ConvertPipeline.Find(id, FormatPresets.ForSystem(system).Concat(FormatPresets.Saved(settings.Presets, system)).ToList())
                     ?? throw new UsageException($"Unknown preset \"{id}\" for {TargetFormats.NameOf(system)}.", $"Presets for it: noi presets {system}");
        if (s.DryRun) { s.Ui.Answer($"Would make {preset.Label} the default for {TargetFormats.NameOf(system)}."); return Exit.Ok; }
        if (preset.Recommended) settings.Presets.DefaultBySystem.Remove(system);
        else settings.Presets.DefaultBySystem[system] = preset.Id;
        settings.Save();
        s.Ui.Status("Saved", $"{TargetFormats.NameOf(system)} files start on {preset.Label} in the app (and noi --use-settings)");
        return Exit.Ok;
    }
}

/// <summary>noi db (§5.7): the game databases, ES-DE's system list and the emulator data.</summary>
public static class DbCommand
{
    private static readonly Option<bool> All = new("--all") { Description = "Every system's game databases, not just the ones already here" };
    private static readonly Option<string[]> Systems = new("--system") { Description = "Only these systems' game databases (repeatable or comma list)", HelpName = "id" };

    public static Command Command(out Command status, out Command update)
    {
        var db = new Command("db", "Game databases: status and update");
        status = new Command("status", "Where the databases come from and how old they are");
        update = new Command("update", "Check for newer databases now");
        update.Options.Add(All);
        update.Options.Add(Systems);
        db.Subcommands.Add(status);
        db.Subcommands.Add(update);
        return db;
    }

    public static HelpDoc Doc => new(
        "The game databases (No-Intro and Redump DATs), ES-DE's system folders and the emulator data, as the app's Settings › Library shows them.",
        "Verify, Organize and Optimize fetch the DATs for the systems they meet on their own; noi db update checks everything now.",
        "noi db [status]\nnoi db update [--all | --system <id>]",
        [("noi db", "How old each database is."), ("noi db update", "Update the DATs already here, ES-DE's list and the emulator data."), ("noi db update --system psx,ps2", "Fetch PS1 and PS2's DATs.")],
        [new("UPDATE", [All, Systems, GlobalOptions.Json])]);

    public static HelpDoc UpdateDoc => Doc;

    public static Task<int> Status(ParseResult r, Session s)
    {
        s.Ui.Answer($"{s.Ui.Paint.Bold("Game databases")}  {Databases.DatStatus}");
        s.Ui.Answer($"{s.Ui.Paint.Bold("ES-DE systems")}   {Databases.EsdeStatus}");
        s.Ui.Answer($"{s.Ui.Paint.Bold("Emulator data")}   {Databases.EmulatorStatus}");
        var doc = Common.Document("db", Exit.Ok, TimeSpan.Zero);
        doc["dats"] = Databases.DatStatus;
        doc["esde"] = Databases.EsdeStatus;
        doc["emulators"] = Databases.EmulatorStatus;
        doc["cached"] = Json.Array(DatStore.Default.CachedKeys());
        s.Ui.Json(doc);
        return Task.FromResult(Exit.Ok);
    }

    public static async Task<int> Update(ParseResult r, Session s)
    {
        var systems = (r.GetValue(Systems) ?? []).SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Select(Common.ResolveSystem).ToList();
        var keys = r.GetValue(All) ? Databases.AllDatKeys()
            : systems.Count > 0 ? systems.Select(TargetFormats.ById).OfType<SystemInfo>().SelectMany(x => x.Dats ?? []).Distinct().ToList()
            : DatStore.Default.CachedKeys();
        var problems = new List<string>();
        s.Ui.Status("Checking", keys.Count == 0 ? "ES-DE's system list and the emulator data (no game databases here yet: --all or --system to fetch some)" : $"{Words.Count(keys.Count, "game database")}, ES-DE's system list and the emulator data", s.Ui.Paint.BoldCyan);
        try
        {
            var dats = await Databases.UpdateDatsAsync(keys, s.Log, s.Token);
            if (dats.Problem is { } p1) problems.Add(p1);
            else s.Ui.Status("Updated", dats.Changed ? $"{Words.Count(dats.Downloaded.Count, "game database")} downloaded" : "game databases already up to date");
            var esde = await Databases.UpdateEsdeAsync(true, s.Log, s.Token);
            if (esde?.Problem is { } p2) problems.Add(p2);
            else if (esde is not null) s.Ui.Status("Updated", esde.Downloaded ? $"ES-DE systems to release {esde.Ref}" : $"ES-DE systems already at release {esde.Ref}");
            var emu = await Databases.UpdateEmulatorDataAsync(s.Log, s.Token);
            if (emu.Problem is { } p3) problems.Add(p3);
            else s.Ui.Status("Updated", emu.Updated ? $"emulator data ({emu.Source})" : "emulator data already up to date");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (s.Token.IsCancellationRequested) return Exit.Cancelled;
            problems.Add(ex.Message);
        }
        var code = problems.Count > 0 ? Exit.Failed : Exit.Ok;
        s.Ui.QuietSummary(problems.Count == 0 ? "databases up to date" : $"couldn't update: {string.Join("; ", problems)}");
        var doc = Common.Document("db update", code, TimeSpan.Zero);
        doc["problems"] = Json.Array(problems);
        doc["dats"] = Databases.DatStatus;
        s.Ui.Json(doc);
        return code;
    }
}

/// <summary>noi keys (§5.7): which console keys were found and where. Never prints a key.</summary>
public static class KeysCommand
{
    public static Command Command()
    {
        var c = new Command("keys", "Which console keys were found");
        foreach (var o in KeyOptions.All) c.Options.Add(o);
        return c;
    }

    public static HelpDoc Doc => new(
        "Which Wii U, 3DS, Switch, PS3 and PS Vita keys noi found, and where.",
        "NoIntrovert ships no keys. It finds the ones your emulators use (Cemu, Azahar/Citra, Ryujinx and the yuzu family), or the " +
        "files and folders set in the app's Settings › Keys, or the ones given to a command with --keys-wiiu, --keys-3ds, " +
        "--keys-switch, --keys-ps3 and --zrif. Key values are never printed.",
        "noi keys",
        [("noi keys", ""), ("noi convert game.nsp --keys-switch D:\\Keys\\prod.keys", "Use other keys for one run.")],
        [new("OPTIONS", KeyOptions.All.Concat([GlobalOptions.Json]).ToList())]);

    public static Task<int> Run(ParseResult r, Session s)
    {
        Common.ApplyKeys(r, s);
        var k = s.Settings.Keys;
        var rows = new List<(string Platform, string Status)>
        {
            ("Wii U", Core.WiiU.WuaJobs.LoadKeys(k.WiiUKeysPath).Describe()),
            ("Nintendo 3DS", CtrJobs.Describe(CtrJobs.LoadKeys(k.ThreeDsKeysFolder, out _))),
            ("Switch", Core.Switch.SwitchJobs.Describe(k.SwitchKeysPath)),
            ("PlayStation 3", Core.Ps3.Ps3Jobs.Describe(k.Ps3KeysFolder)),
            ("PS Vita", Core.Vita.ZrifLibrary.Describe(k.VitaZrifPath)),
        };
        s.Ui.Table(["Platform", "Keys"], rows.Select(x => (IReadOnlyList<string>)[x.Platform, x.Status]), answer: true);
        var doc = Common.Document("keys", Exit.Ok, TimeSpan.Zero);
        doc["keys"] = new JsonArray(rows.Select(x => (JsonNode)new JsonObject { ["platform"] = x.Platform, ["status"] = x.Status }).ToArray());
        s.Ui.Json(doc);
        return Task.FromResult(Exit.Ok);
    }
}
