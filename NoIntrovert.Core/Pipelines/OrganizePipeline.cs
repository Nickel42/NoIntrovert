using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Library;

namespace NoIntrovert.Core.Pipelines;

/// <summary>How Organize (and Optimize after Verify) plans: where the games go and which kinds of step run.</summary>
public sealed record OrganizeRequest
{
    /// <summary>FR-8: build a new library in this folder instead of tidying the files where they are.</summary>
    public string? NewLibrary { get; init; }

    /// <summary>FR-8: with a new library, also copy games that aren't matched.</summary>
    public bool CopyUnmatched { get; init; } = true;

    /// <summary>FR-8: with a new library, copy a game's saves and states (same name, other extension) beside it.</summary>
    public bool CopyCompanions { get; init; } = true;

    /// <summary>FR-8: with a new library, also copy every other file in the folders into the matching system folder.</summary>
    public bool CopyOtherFiles { get; init; }

    /// <summary>Optimize: a file the databases don't know is still converted, under its own name.</summary>
    public bool ConvertUnmatched { get; init; }

    /// <summary>Kinds of step turned off (--no-convert, --no-rename, --no-move, --no-dedupe).</summary>
    public IReadOnlyCollection<CleanupKind> Off { get; init; } = [];
}

/// <summary>A plan made for some inputs, the library items it was made from, and what Verify hasn't checked.</summary>
public sealed record OrganizePlan(CleanupPlan? Plan, LibraryIndex Index, IReadOnlyList<string> Unchecked, string? Problem)
{
    public int Runnable => Plan?.Steps.Count(s => s.Runs) ?? 0;
    public int Clashes => Plan?.Count(CleanupKind.Clash) ?? 0;
}

/// <summary>Organize's plan and run without a window: the planner and runner the Organize and Optimize pages use.</summary>
public static class OrganizePipeline
{
    public static OrganizePlan Plan(IReadOnlyList<string> paths, IReadOnlyList<string> folders, AppSettings settings, OrganizeRequest request,
        CancellationToken token = default)
    {
        var index = LibraryScanner.Describe(paths, folders, settings, token);
        var notChecked = index.Items.Where(i => MatchStore.Default.Get(i.Path) is null).Select(i => i.Path).ToList();
        var roots = index.Items.Select(i => i.Root).Distinct(PathNames.Comparer).ToList();
        var problem = request.NewLibrary is { } lib ? NewLibraryProblem(lib, roots) : null;
        if (problem is not null) return new OrganizePlan(null, index, notChecked, problem);
        var plan = CleanupPlanner.Plan(index.Games(), settings, MatchStore.Default, request.NewLibrary, request.CopyUnmatched, request.ConvertUnmatched,
            request.CopyCompanions, request.CopyOtherFiles);
        foreach (var step in plan.Steps.Where(s => request.Off.Contains(s.Kind) || (s.Kind == CleanupKind.Copy && request.Off.Contains(CleanupKind.Move))))
            step.Enabled = false;
        return new OrganizePlan(plan, index, notChecked, null);
    }

    /// <summary>
    /// Why the new library can't be built in <paramref name="folder"/>, or null when it can: it can't be inside
    /// a folder being organized or hold one, or the files would change after all (FR-8).
    /// </summary>
    public static string? NewLibraryProblem(string folder, IEnumerable<string> loadedFolders)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Choose a folder for the new library.";
        static string Full(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        var dest = Full(folder);
        foreach (var lib in loadedFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Full))
        {
            if (dest.StartsWith(lib, PathNames.Comparison)) return $"The new library can't be inside a folder in the list ({lib.TrimEnd(Path.DirectorySeparatorChar)}).";
            if (lib.StartsWith(dest, PathNames.Comparison)) return $"The new library's folder can't hold a folder in the list ({lib.TrimEnd(Path.DirectorySeparatorChar)}).";
        }
        return null;
    }

    /// <summary>The plan's ticked steps as run jobs, one per game, its steps in order; the runner keeps the journal.</summary>
    public static (IReadOnlyList<RunJob> Jobs, CleanupRunner Runner) Jobs(CleanupPlan plan)
    {
        var runner = new CleanupRunner();
        var games = plan.Steps.Where(s => s.Runs).GroupBy(s => s.GameKey).ToList();
        var work = runner.Jobs(plan);
        var jobs = work.Select((w, n) =>
        {
            var steps = games[n].ToList();
            return new RunJob(w.Name, steps.SelectMany(s => s.Sources).Distinct(PathNames.Comparer).ToList(), w.Cost, w.Work) { Tag = steps };
        }).ToList();
        return (jobs, runner);
    }

    // ---- Plan files (noi organize --plan-out / --plan) ----

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record PlanFileStep(CleanupKind Kind, string Game, IReadOnlyList<string> Sources, string? Target, string Reason, bool Enabled);

    public sealed record PlanFileSource(string Path, long Size, long Ticks);

    /// <summary>A saved plan: how it was made (inputs, folders, settings, options), its steps, and the files as they were.</summary>
    public sealed record PlanFile
    {
        public int Schema { get; init; } = 1;
        public string Command { get; init; } = "organize";
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public IReadOnlyList<string> Inputs { get; init; } = [];
        public IReadOnlyList<string> Folders { get; init; } = [];
        public OrganizeRequest Request { get; init; } = new();
        public JsonNode? Settings { get; init; }
        public IReadOnlyList<PlanFileStep> Steps { get; init; } = [];
        public IReadOnlyList<PlanFileSource> Files { get; init; } = [];

        [JsonIgnore] public AppSettings AppSettings =>
            Settings is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(Settings.ToJsonString(), AppSettingsJson) ?? new AppSettings();
    }

    private static readonly JsonSerializerOptions AppSettingsJson = new() { Converters = { new JsonStringEnumConverter() } };

    public static PlanFile ToFile(CleanupPlan plan, IReadOnlyList<string> inputs, IReadOnlyList<string> folders, OrganizeRequest request, AppSettings settings)
    {
        var sources = plan.Steps.SelectMany(s => s.Sources).Distinct(PathNames.Comparer)
            .Select(p => { var (size, ticks) = HashCache.Stamp(p); return new PlanFileSource(p, size, ticks); })
            .ToList();
        return new PlanFile
        {
            Inputs = inputs,
            Folders = folders,
            Request = request,
            Settings = JsonNode.Parse(settings.ToJson()),
            Steps = plan.Steps.Select(s => new PlanFileStep(s.Kind, s.Game, s.Sources, s.Target, s.Reason, s.Enabled)).ToList(),
            Files = sources,
        };
    }

    public static void Save(PlanFile file, string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
    }

    public static PlanFile Load(string path) =>
        JsonSerializer.Deserialize<PlanFile>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("The plan file is empty.");

    /// <summary>
    /// The plan made again for a saved plan's inputs, with the saved plan's choices applied: a step taken out of
    /// the file (or unticked in it) doesn't run. Null and the reason when it can't be run exactly: a file changed,
    /// or the plan would now be different.
    /// </summary>
    public static (OrganizePlan? Plan, string? Problem) Replan(PlanFile file, AppSettings settings, CancellationToken token = default)
    {
        foreach (var source in file.Files)
        {
            var (size, ticks) = HashCache.Stamp(source.Path);
            if (size != source.Size || ticks != source.Ticks)
                return (null, $"{source.Path} changed (or is gone) since the plan was saved; make the plan again.");
        }
        var made = Plan(file.Inputs, file.Folders, settings, file.Request, token);
        if (made.Plan is null) return (null, made.Problem);
        static string Key(CleanupKind kind, IEnumerable<string> sources, string? target) =>
            $"{kind}|{string.Join("|", sources.Select(s => s.ToLowerInvariant()))}|{target?.ToLowerInvariant()}";
        var saved = file.Steps.GroupBy(s => Key(s.Kind, s.Sources, s.Target)).ToDictionary(g => g.Key, g => g.First());
                var now = made.Plan.Steps.Select(s => Key(s.Kind, s.Sources, s.Target)).ToHashSet();
        foreach (var (key, step) in saved)
            if (step.Enabled && !now.Contains(key))
                return (null, $"The plan for {step.Game} would be different now; make the plan again.");
        foreach (var step in made.Plan.Steps)
            step.Enabled = saved.TryGetValue(Key(step.Kind, step.Sources, step.Target), out var s) && s.Enabled && step.Enabled;
        return (made, null);
    }
}
