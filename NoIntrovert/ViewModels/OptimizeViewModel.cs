using Avalonia.Threading;
using NoIntrovert.Core;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Esde;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Library;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// Optimize: Verify, Convert and Organize in one click. A run first checks every file and looks it up in
/// the game databases (Verify &amp; match), then makes the Organize plan from what that found and runs it:
/// each game's files are converted, named as in the database and moved into the layout, in that order.
/// A file the databases don't know is still converted where it is, under its own name; a damaged one is
/// left alone. The CHANGES column says, for each file, what will happen to it.
/// </summary>
public sealed partial class OptimizeViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs, JobQueue queue, OrganizeOptions options)
    : PlanTabViewModel(settings, log, dialogs, queue, options)
{
    public override string Title => "Optimize";
    public override string StartLabel => "Optimize";
    public override string EmptyHint =>
        "Drop games here, or a folder of them: ROMs, archives, disc images, CHDs, GameCube/Wii discs.\n" +
        "One click checks each file against the No-Intro and Redump databases, converts it to its best format, " +
        "names it as the database does and moves it into its system's folder, in that order.";
    protected override string FilterName => "Games";
    protected override bool ConvertUnmatched => true;

    /// <summary>The first stage: Verify &amp; match, as the Verify page does it.</summary>
    protected override JobOutcome Work(JobContext context, JobItemViewModel item) => VerifyJobs.Check(context, item.Path);

    /// <summary>Verify &amp; match's preset (which databases, hashing) is the one Optimize's first stage uses.</summary>
    protected override Core.Presets.PresetAction? RunPreset => Core.Presets.PresetAction.Verify;

    protected override async Task RunJobsAsync(IReadOnlyList<JobItemViewModel> pending, AppSettings snapshot)
    {
        // 1. Verify & match every file: the plan's names and folders come from what it finds.
        RunStatus = $"Verifying {Words.Count(pending.Count, "file")}…";
        await base.RunJobsAsync(pending, snapshot);
        if (RunCancelled) return;
        // 2. Convert, rename and move, game by game, each game's steps in order. A file that couldn't be read is left out.
        var next = pending.Where(r => r.Status is not (JobStatus.Failed or JobStatus.Cancelled)).ToList();
        _verified = next.ToDictionary(r => r, r => (r.Status, r.Result));
        RunStatus = $"Converting and organizing {Words.Count(next.Count, "file")}…";
        foreach (var row in next) row.Reset();
        try { await RunPlanAsync(next, Settings.Snapshot()); }
        finally { _verified = []; }
    }

    /// <summary>What Verify said of each row in the run under way.</summary>
    private Dictionary<JobItemViewModel, (JobStatus Status, string Result)> _verified = [];

    /// <summary>Each row says what Verify found, then what was done to it; with nothing to do after Verify it keeps Verify's outcome.</summary>
    protected override JobOutcome RowOutcome(JobItemViewModel row, JobOutcome outcome)
    {
        if (!_verified.TryGetValue(row, out var verify)) return outcome;
        var said = verify.Result.Length > 0 ? $"{verify.Result} · {outcome.Summary}" : outcome.Summary;
        return outcome.Status == JobStatus.Skipped ? outcome with { Status = verify.Status, Summary = said } : outcome with { Summary = said };
    }

    /// <summary>"Verify → Convert → roms/psx/Crash (USA).chd", or, before a file is checked, what's likely.</summary>
    protected override string ChangesFor(LibraryItem? item, IReadOnlyList<CleanupStep> steps)
    {
        if (item is null) return "Not a file Optimize can use";
        if (!Checked.Contains(item.Path)) return Predicted(item, PlanSettings, Options.BuildNewLibrary ? Options.NewLibraryFolder : null);
        return "Verify → " + StepsText(item, steps);
    }

    protected override string DetailsFor(LibraryItem? item, IReadOnlyList<CleanupStep> steps)
    {
        if (item is null || Checked.Contains(item.Path))
            return "1. Verify & match: checked for damage, then looked up in the game databases.\n" + base.DetailsFor(item, steps);
        return "1. Verify & match: checked for damage, then looked up in the game databases.\n" +
               "2. Then, from what it finds: converted to its best format, named as in the database and moved into its system's folder.\n" +
               "A file the databases don't know is still converted where it is, under its own name. A damaged file is left alone.";
    }

    /// <summary>
    /// What happens to a file that hasn't been checked yet: verified, then (if it's matched) converted,
    /// named from the database and moved into its system's folder. The exact names come after Verify.
    /// </summary>
    internal static string Predicted(LibraryItem item, AppSettings settings, string? newLibrary)
    {
        var parts = new List<string> { "Verify" };
        var system = TargetFormats.ById(item.System);
        var ext = item.FormatStatus == TargetStatus.Convert ? ExtensionOf(item.Target) : null;
        if (ext is not null) parts.Add($"Convert → {ext}");
        if (system is null || system.Follows("R5"))
        {
            if (ext is null) parts.Add(system is null ? "Left alone: unknown system" : "Left alone: arcade romset");
            return string.Join(" → ", parts);
        }
        parts.Add("Name from the database");
        if (CleanupPlanner.CanArrange(system, settings))
        {
            var folder = CleanupPlanner.FolderFor(item, system, settings, newLibrary);
            var relative = Path.GetRelativePath(newLibrary ?? item.Root, folder).Replace('\\', '/');
            if (!PathNames.Same(folder, Path.GetDirectoryName(item.Path) ?? ""))
                parts.Add(newLibrary is not null ? $"Copy → {(relative == "." ? newLibrary : $"{relative}/ in the new library")}"
                    : relative == "." ? "Move → the top of its folder" : $"Move → {relative}/");
        }
        else if (newLibrary is not null) parts.Add($"Copy → {newLibrary}");
        return string.Join(" → ", parts);
    }

    private static string? ExtensionOf(TargetKind target) => target switch
    {
        TargetKind.Chd => ".chd",
        TargetKind.Rvz => ".rvz",
        TargetKind.Wua => ".wua",
        TargetKind.Cci => ".cci",
        TargetKind.Zcci => ".zcci",
        TargetKind.Nsz => ".nsz",
        TargetKind.Xcz => ".xcz",
        TargetKind.SevenZip => ".7z",
        TargetKind.Zip => ".zip",
        _ => null,
    };
}
