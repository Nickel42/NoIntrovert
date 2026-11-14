using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NoIntrovert.Core;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Library;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// A page whose rows are files that a plan converts, renames and moves (Optimize and Organize): the plan
/// is made in the background whenever the rows or <see cref="Options"/> change, each row's CHANGES column
/// says what happens to it, and a run puts the plan on the queue, one job per game, its steps in order.
/// Rows follow their files to where the run put them, on every page.
/// </summary>
public abstract partial class PlanTabViewModel : BatchTabViewModel
{
    private readonly Coalesced _replan;
    private int _planVersion;

    protected PlanTabViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs, JobQueue queue, OrganizeOptions options)
        : base(settings, log, dialogs, queue)
    {
        Options = options;
        _replan = new Coalesced(() => _ = MakePlanAsync());
        Items.CollectionChanged += (_, _) => RequestPlan();
        options.Changed += RequestPlan;
    }

    /// <summary>The layout and where the files go, shared by Optimize and Organize.</summary>
    public OrganizeOptions Options { get; }

    public override bool ShowsChanges => true;

    public override OrganizeOptions? PlanOptions => Options;

    protected override IReadOnlyList<FormatInfo>? InputFormats => null;
    protected override bool Accepts(string path) => FormatRegistry.Default.Identify(path) is not null;

    /// <summary>A folder brings in every input the registry finds in it.</summary>
    protected override IEnumerable<string> ItemsForFolder(string folder) =>
        FormatRegistry.Default.Scan(folder, Settings.Output.ScanSubfolders).Select(s => s.Primary).ToList();

    protected override JobOutcome Work(JobContext context, JobItemViewModel item) => new(JobStatus.Skipped, "");

    /// <summary>Optimize: a file the databases don't know is still converted, under its own name.</summary>
    protected virtual bool ConvertUnmatched => false;

    // ---- The plan ----

    /// <summary>The plan for the rows as they are now; null until it's made, or when it can't be (see <see cref="PlanSummary"/>).</summary>
    public CleanupPlan? Plan { get; private set; }

    /// <summary>The library items the plan was made from, by row path.</summary>
    protected IReadOnlyDictionary<string, LibraryItem> ItemsByRow { get; private set; } = new Dictionary<string, LibraryItem>();

    /// <summary>Each row's steps, in the order they run.</summary>
    protected IReadOnlyDictionary<string, IReadOnlyList<CleanupStep>> StepsByRow { get; private set; } = new Dictionary<string, IReadOnlyList<CleanupStep>>();

    /// <summary>The rows Verify &amp; match had a result for when the plan was made.</summary>
    protected IReadOnlySet<string> Checked { get; private set; } = new HashSet<string>();

    /// <summary>The settings the plan was made with (read once per plan, not once per row).</summary>
    protected AppSettings PlanSettings { get; private set; } = new();

    [ObservableProperty] public partial bool IsPlanning { get; set; }

    /// <summary>What the plan does in all ("12 to convert · 30 to rename · frees about 4 GB"), or why it can't run.</summary>
    [ObservableProperty] public partial string PlanSummary { get; set; } = "";

    /// <summary>The latest plan being made (tests wait on it).</summary>
    internal Task PlanMade { get; private set; } = Task.CompletedTask;

    /// <summary>Makes the plan again soon (the page was opened: Verify may have matched files since).</summary>
    public void Replan() => RequestPlan();

    /// <summary>Makes the plan again soon (the rows or options changed); not while a run is changing the files.</summary>
    protected void RequestPlan()
    {
        if (!IsRunning) _replan.Request();
    }

    /// <summary>Makes the plan for the rows as they are now and shows it.</summary>
    public Task MakePlanAsync() => PlanMade = MakePlanCoreAsync();

    private async Task MakePlanCoreAsync()
    {
        var version = ++_planVersion;
        var rows = Items.ToList();
        if (rows.Count == 0)
        {
            Show(rows, null, new LibraryIndex(), null);
            return;
        }
        var paths = rows.Select(r => r.Path).ToList();
        var roots = Workspace?.Folders ?? [];
        var settings = PlanSettings = Settings.Snapshot();
        var options = (Options.BuildNewLibrary, Options.NewLibraryFolder, Options.CopyUnmatched);
        var convertUnmatched = ConvertUnmatched;
        IsPlanning = true;
        try
        {
            var (index, plan, problem, matched) = await Task.Run(() =>
            {
                var index = LibraryScanner.Describe(paths, roots, settings);
                // Which files Verify & match has a result for (a lookup reads the files' sizes and dates: not on the UI thread).
                var matched = index.Items.Where(i => MatchStore.Default.Get(i.Path) is not null).Select(i => i.Path).ToHashSet(PathNames.Comparer);
                var folders = index.Items.Select(i => i.Root).Distinct(PathNames.Comparer).ToList();
                var problem = options.BuildNewLibrary ? OrganizeOptions.NewLibraryProblem(options.NewLibraryFolder, folders) : null;
                var plan = problem is null
                    ? CleanupPlanner.Plan(index.Games(), settings, MatchStore.Default, options.BuildNewLibrary ? options.NewLibraryFolder : null,
                        options.CopyUnmatched, convertUnmatched)
                    : null;
                return (index, plan, problem, matched);
            });
            if (version != _planVersion) return; // a newer plan is on its way
            Checked = matched;
            Show(rows, plan, index, problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (version == _planVersion) PlanSummary = $"Couldn't make the plan: {ex.Message}";
        }
        finally
        {
            if (version == _planVersion) IsPlanning = false;
        }
    }

    /// <summary>Shows <paramref name="plan"/> as if it had been made for the rows (tests); a plan being made is dropped.</summary>
    internal void ShowPlan(CleanupPlan plan)
    {
        _planVersion++;
        IsPlanning = false;
        Show(Items.ToList(), plan, new LibraryIndex(), null);
    }

    private void Show(IReadOnlyList<JobItemViewModel> rows, CleanupPlan? plan, LibraryIndex index, string? problem)
    {
        Plan = plan;
        var items = new Dictionary<string, LibraryItem>(PathNames.Comparer);
        var ofMember = new Dictionary<string, string>(PathNames.Comparer);
        foreach (var item in index.Items)
        {
            items[item.Path] = item;
            foreach (var m in item.Members) ofMember.TryAdd(m, item.Path);
            ofMember[item.Path] = item.Path;
        }
        var steps = new Dictionary<string, List<CleanupStep>>(PathNames.Comparer);
        foreach (var step in plan?.Steps ?? [])
            foreach (var row in step.Sources.Select(s => ofMember.GetValueOrDefault(s)).OfType<string>().Distinct(PathNames.Comparer))
            {
                if (!steps.TryGetValue(row, out var list)) steps[row] = list = [];
                list.Add(step);
            }
        ItemsByRow = items;
        StepsByRow = steps.ToDictionary(p => p.Key, p => (IReadOnlyList<CleanupStep>)p.Value, PathNames.Comparer);
        foreach (var row in rows)
        {
            if (row.IsRunning) continue;
            var mine = StepsByRow.GetValueOrDefault(row.Path) ?? [];
            var item = items.GetValueOrDefault(row.Path);
            row.Changes = problem ?? ChangesFor(item, mine);
            row.ChangesDetails = problem ?? DetailsFor(item, mine);
        }
        PlanSummary = problem ?? (plan is null ? "" : Summarize(plan));
        PlanShown(plan);
    }

    /// <summary>Called with each plan shown (Organize's per-kind boxes follow it).</summary>
    protected virtual void PlanShown(CleanupPlan? plan) { }

    /// <summary>A row's CHANGES text: its steps, in order.</summary>
    protected virtual string ChangesFor(LibraryItem? item, IReadOnlyList<CleanupStep> steps) => StepsText(item, steps);

    /// <summary>A row's CHANGES tooltip: one step a line, each with why.</summary>
    protected virtual string DetailsFor(LibraryItem? item, IReadOnlyList<CleanupStep> steps) =>
        steps.Count == 0 ? StepsText(item, steps) : string.Join("\n", steps.Select(s => $"{Describe(s)}\n    {s.Reason}"));

    /// <summary>The steps as one line: "Convert → roms/psx/Crash (USA).chd", or why nothing happens.</summary>
    public static string StepsText(LibraryItem? item, IReadOnlyList<CleanupStep> steps)
    {
        if (item is null) return "Not a file the plan can use";
        if (steps.Count == 0) return "Nothing to change: already named and in place";
        var runs = steps.Where(s => s.Runs).ToList();
        if (runs.Count == 0)
        {
            var first = steps[0];
            return first.Kind is CleanupKind.Untouched or CleanupKind.Clash ? Describe(first) : $"Off (its box is unticked): {Describe(first)}";
        }
        return string.Join(" → ", runs.Select(Describe));
    }

    /// <summary>One step: "Convert → roms/psx/Crash (USA).chd", "Rename → Zelda (USA).nes", "Remove: a copy of …".</summary>
    public static string Describe(CleanupStep step)
    {
        string Where() => step.Target is null ? "" : Relative(step.Target, step.WriteRoot);
        var many = step.Sources.Count > 1 && step.Kind is CleanupKind.Convert or CleanupKind.Move or CleanupKind.Copy && step.Renames.Count > 1;
        return step.Kind switch
        {
            CleanupKind.Convert when many => $"Convert {step.Renames.Count} discs, with {Path.GetFileName(step.Target)} → {Where()}",
            CleanupKind.Convert => $"Convert → {Where()}",
            CleanupKind.Rename => $"Rename → {Path.GetFileName(step.Target)}",
            CleanupKind.Move when many => $"Move {step.Renames.Count} discs, with {Path.GetFileName(step.Target)} → {Where()}",
            CleanupKind.Move => $"Move → {Where()}",
            CleanupKind.Copy => $"Copy → {step.Target}",
            CleanupKind.RemoveDuplicate => $"Remove: {step.Reason.ToLowerInvariant()}",
            CleanupKind.Clash => $"Clash: {step.Reason}",
            _ => $"Left alone: {step.Reason}",
        };
    }

    private static string Relative(string path, string root) =>
        Path.GetRelativePath(root, path) is var r && !r.StartsWith("..") && !Path.IsPathRooted(r) ? r.Replace('\\', '/') : path;

    /// <summary>The plan in all, as the Organize wizard always said it.</summary>
    public static string Summarize(CleanupPlan plan)
    {
        var runs = plan.Steps.Count(s => s.Runs);
        var copies = plan.Steps.Any(s => s.Copies);
        var parts = CleanupKindToggle.RunnableKinds
            .Select(k => (Kind: k, Count: plan.Steps.Count(s => s.Kind == k && s.Runs)))
            .Where(p => p.Count > 0)
            .Select(p => $"{p.Count} {p.Kind switch { CleanupKind.Convert => "to convert", CleanupKind.Copy => "to copy", CleanupKind.Rename => "to rename", CleanupKind.Move => "to move", _ => "duplicate(s) to remove" }}");
        var left = plan.Count(CleanupKind.Untouched);
        var clashes = plan.Count(CleanupKind.Clash);
        return (runs == 0 ? "Nothing to do." : copies
                   ? string.Join(" · ", parts)
                   : $"{string.Join(" · ", parts)} · frees about {ChdJobs.Size(plan.BytesSaved)}")
               + (clashes > 0 ? $" · {clashes} clash(es) to sort out first" : "")
               + (left > 0 ? $" · {left} {(copies ? "left out" : "left alone")}" : "");
    }

    // ---- Running the plan ----

    /// <summary>
    /// Makes the plan for <paramref name="rows"/> as they are now and runs its ticked steps on the queue,
    /// one job per game; each row says what was done to it, and follows its file to its new path.
    /// </summary>
    protected async Task RunPlanAsync(IReadOnlyList<JobItemViewModel> rows, AppSettings snapshot)
    {
        await MakePlanAsync();
        if (Plan is null)
        {
            foreach (var row in rows) row.Finished(new JobOutcome(JobStatus.Skipped, PlanSummary));
            return;
        }
        var mine = new HashSet<string>(rows.Select(r => r.Path), PathNames.Comparer);
        var stepsOf = StepsByRow;
        // Every step of a game a chosen row is in: a game's discs and copies move together.
        var games = rows.SelectMany(r => stepsOf.GetValueOrDefault(r.Path) ?? []).Select(s => s.GameKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = new CleanupPlan(Plan.Steps.Where(s => games.Contains(s.GameKey)).ToList());
        var runner = new CleanupRunner();
        var jobs = runner.Jobs(plan);
        var rowsOf = rows.GroupBy(r => (stepsOf.GetValueOrDefault(r.Path) ?? []).FirstOrDefault(s => s.Runs)?.GameKey ?? "")
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rowsOf.GetValueOrDefault("") ?? [])
            row.Finished(RowOutcome(row, new JobOutcome(JobStatus.Skipped, StepsText(ItemsByRow.GetValueOrDefault(row.Path), stepsOf.GetValueOrDefault(row.Path) ?? []))));
        var keys = plan.Steps.Where(s => s.Runs).Select(s => s.GameKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var batch = jobs.Select((j, n) =>
        {
            var group = rowsOf.GetValueOrDefault(keys[n]) ?? [];
            IJobReport report = group.Count > 0 ? group[0] : new SilentReport();
            return new BatchJob(j.Name, report, j.Work,
                _ => Dispatcher.UIThread.Post(() => { foreach (var r in group) r.Started(); }),
                outcome => Dispatcher.UIThread.Post(() =>
                {
                    foreach (var r in group) r.Finished(RowOutcome(r, Done(outcome, stepsOf.GetValueOrDefault(r.Path) ?? [])));
                }),
                j.Cost);
        }).ToList();
        Log.Info($"{Title}: running {runner.Run.Steps} step(s) for {batch.Count} game(s)"
                 + (runner.Run.NewLibrary is { } lib ? $", building a new library in {lib}; the files themselves aren't changed" : ""));
        try
        {
            await SubmitAsync(Title, batch, snapshot);
        }
        finally
        {
            FollowFiles(runner.Run);
            RunFinished(runner.Run);
        }
    }

    /// <summary>What a row says once its game is done (Optimize puts what Verify found first).</summary>
    protected virtual JobOutcome RowOutcome(JobItemViewModel row, JobOutcome outcome) => outcome;

    /// <summary>A finished game's outcome as its rows say it: what was done to the file, or why it stopped.</summary>
    private static JobOutcome Done(JobOutcome outcome, IReadOnlyList<CleanupStep> steps)
    {
        if (outcome.Status is not (JobStatus.Succeeded or JobStatus.Warning)) return outcome;
        var done = steps.Where(s => s.Runs).Select(s => s.Kind switch
        {
            CleanupKind.Convert => $"converted → {Path.GetFileName(s.Target)}",
            CleanupKind.Rename => $"renamed → {Path.GetFileName(s.Target)}",
            CleanupKind.Move => $"moved → {Path.GetFileName(Path.GetDirectoryName(s.Target))}/",
            CleanupKind.Copy => $"copied → {s.Target}",
            CleanupKind.RemoveDuplicate => "removed (a duplicate)",
            _ => "",
        }).Where(t => t.Length > 0).ToList();
        var text = done.Count == 0 ? outcome.Summary : string.Join(", ", done);
        return outcome with { Summary = char.ToUpperInvariant(text[0]) + text[1..] };
    }

    /// <summary>After a run the plan is made again, for the files where they are now.</summary>
    protected override void AfterRun(IReadOnlyList<JobItemViewModel> items) => RequestPlan();

    /// <summary>Called once a run is over (its journal saved): Organize lists it under Past runs.</summary>
    protected virtual void RunFinished(CleanupRun run) { }

    /// <summary>
    /// Rows follow their files: from the run's journal, each file moved or converted takes its new path on
    /// every page, and a duplicate sent to the trash goes from the other pages' lists.
    /// </summary>
    private void FollowFiles(CleanupRun run)
    {
        var (moves, trashed) = MovesOf(run, Items.Select(i => i.Path));
        ReplaceRows(moves);
        if (Workspace is not { } workspace) return;
        workspace.Move(moves);
        if (trashed.Count > 0) workspace.Remove(trashed);
    }

    /// <summary>
    /// Old → new path for each row's file the run moved, renamed or converted (a conversion's original goes
    /// to the trash and the new file is made in the same step), and the rows whose files went to the trash
    /// with nothing made in their place (removed duplicates).
    /// </summary>
    internal static (Dictionary<string, string> Moves, List<string> Trashed) MovesOf(CleanupRun run, IEnumerable<string> rowPaths)
    {
        var rows = new HashSet<string>(rowPaths, PathNames.Comparer);
        var moves = new Dictionary<string, string>(PathNames.Comparer);
        var trashed = new List<string>();
        if (run.NewLibrary is not null) return (moves, trashed); // a new library leaves every file where it is
        foreach (var e in run.Entries.Where(e => e.Action == JournalAction.Moved && e.From is not null && rows.Contains(e.From)))
            moves[e.From!] = e.Path;
        foreach (var step in run.Entries.GroupBy(e => e.Step))
        {
            var gone = step.Where(e => e.Action == JournalAction.Trashed && e.From is not null && rows.Contains(e.From)).Select(e => e.From!).ToList();
            if (gone.Count == 0) continue;
            var made = step.Where(e => e.Action == JournalAction.Created && !e.Path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
                                       && !e.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path).ToList();
            if (made.Count == 0) trashed.AddRange(gone);
            else if (made.Count == gone.Count)
                for (var i = 0; i < gone.Count; i++) moves[gone[i]] = made[i];
        }
        return (moves, trashed);
    }

    private sealed class SilentReport : IJobReport
    {
        public void SetStage(string stage) { }
        public void SetProgress(double fraction) { }
    }
}
