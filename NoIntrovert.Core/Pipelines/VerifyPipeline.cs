using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Pipelines;

/// <summary>One input's Verify &amp; match result, as the Verify page's row shows it.</summary>
public sealed record VerifyResult(InputItem Item, MatchStatus? Status, MatchResult? Match, Core.Jobs.JobOutcome? Outcome)
{
    public string Text => Match?.Text ?? Outcome?.Summary ?? "";
}

/// <summary>The Verify page's run without a window: one job per input, each checked and looked up (VerifyJobs).</summary>
public static class VerifyPipeline
{
    public static IReadOnlyList<RunJob> Jobs(IReadOnlyList<InputItem> items) =>
        items.Select(i => new RunJob(i.Name, [i.Path], i.Size, c => VerifyJobs.Check(c, i.Path)) { Tag = i }).ToList();

    /// <summary>What each input came to, from the run's outcomes and what Verify &amp; match remembered.</summary>
    public static IReadOnlyList<VerifyResult> Results(IReadOnlyList<RunJob> jobs)
    {
        ThrottledJsonFile.FlushAll();
        return jobs.Select(j =>
        {
            var item = (InputItem)j.Tag!;
            var match = MatchStore.Default.Get(item.Path);
            var status = match?.Status ?? (j.Outcome?.Status == Core.Jobs.JobStatus.Failed ? MatchStatus.Damaged : null);
            return new VerifyResult(item, status, match, j.Outcome);
        }).ToList();
    }

    /// <summary>The DAT keys (no-intro/…, redump/…) the inputs' systems need; every system's when one is unknown.</summary>
    public static IReadOnlyList<string> DatKeysFor(IEnumerable<InputItem> items)
    {
        var systems = items.Select(i => i.System).Distinct().ToList();
        var known = systems.Select(TargetFormats.ById).OfType<SystemInfo>();
        return known.SelectMany(s => s.Dats ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
