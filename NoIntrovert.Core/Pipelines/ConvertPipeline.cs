using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Presets;

namespace NoIntrovert.Core.Pipelines;

/// <summary>A format named on the command line: what it becomes, and for a disc image which kind.</summary>
public sealed record FormatChoice(string Word, TargetKind Kind, ExtractFormat? Extract = null);

/// <summary>
/// What noi convert was asked to make: a preset or a format for every file, or per system, systems to leave,
/// and Custom values on top (<see cref="Tune"/>). Nothing set means each file's recommended preset.
/// </summary>
public sealed record ConvertRequest
{
    public string? Preset { get; init; }
    public FormatChoice? To { get; init; }
    public IReadOnlyDictionary<string, string> PresetBySystem { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, FormatChoice> ToBySystem { get; init; } = new Dictionary<string, FormatChoice>();
    public IReadOnlyCollection<string> Keep { get; init; } = [];

    /// <summary>Custom values (codecs, levels…) applied over the chosen preset's; null for none.</summary>
    public Action<PresetValues>? Tune { get; init; }

    /// <summary>--no-verify: new files aren't verified or tested (Replace the originals still verifies).</summary>
    public bool NoVerify { get; init; }
}

/// <summary>One input and what it becomes: its preset (null when it's left as it is) and why when it's skipped.</summary>
public sealed record ConvertRow(InputItem Item, FormatPreset? Preset, TargetKind Target, PresetValues? Values, bool OwnChecks, string? Skip);

/// <summary>The Convert page's run without a window: each input's preset, the planner's jobs, and what's left alone.</summary>
public static class ConvertPipeline
{
    /// <summary>The formats --to takes, with what each becomes.</summary>
    public static IReadOnlyList<FormatChoice> Formats { get; } =
    [
        new("chd", TargetKind.Chd), new("rvz", TargetKind.Rvz), new("iso", TargetKind.Iso),
        new("cue", TargetKind.DiscImage, ExtractFormat.CueBin), new("bin", TargetKind.DiscImage, ExtractFormat.CueBin),
        new("gdi", TargetKind.DiscImage, ExtractFormat.Gdi), new("img", TargetKind.DiscImage, ExtractFormat.Raw),
        new("disc", TargetKind.DiscImage, ExtractFormat.Auto),
        new("wua", TargetKind.Wua), new("cci", TargetKind.Cci), new("zcci", TargetKind.Zcci),
        new("nsz", TargetKind.Nsz), new("xcz", TargetKind.Xcz), new("nsp", TargetKind.Nsp), new("xci", TargetKind.Xci),
        new("xiso", TargetKind.Xiso), new("xbox-cci", TargetKind.XboxCci), new("zar", TargetKind.Zar),
        new("ps3-iso", TargetKind.Ps3Iso), new("ps3-folder", TargetKind.Ps3Folder), new("jb", TargetKind.Ps3Folder),
        new("cso", TargetKind.Cso), new("zso", TargetKind.Zso),
        new("7z", TargetKind.SevenZip), new("zip", TargetKind.Zip), new("unpack", TargetKind.Files), new("files", TargetKind.Files),
        new("leave", TargetKind.Leave),
    ];

    public static FormatChoice? FormatNamed(string word) =>
        Formats.FirstOrDefault(f => f.Word.Equals(word.Trim().TrimStart('.'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Picks each input's preset as the request says, or its system's default when it says nothing.</summary>
    public static IReadOnlyList<ConvertRow> Choose(IReadOnlyList<InputItem> items, ConvertRequest request, AppSettings settings)
    {
        var rows = new List<ConvertRow>(items.Count);
        foreach (var item in items) rows.Add(ChooseOne(item, request, settings));
        return rows;
    }

    private static ConvertRow ChooseOne(InputItem item, ConvertRequest request, AppSettings settings)
    {
        var options = ConvertTargets.For(item.Set, settings);
        var system = options.Plan.System ?? item.System;
        var available = options.Choices.Select(c => c.Kind).ToList();
        var list = FormatPresets.ListFor(system, available, settings.Presets);
        var usable = list.Where(i => i.IsPreset && i.IsEnabled && !i.Preset!.IsCustom).Select(i => i.Preset!).ToList();

        ConvertRow Leave(string why) => new(item, null, TargetKind.Leave, null, false, why);

        if (system is not null && request.Keep.Contains(system, StringComparer.OrdinalIgnoreCase))
            return Leave("kept as it is (--keep)");

        string? presetWord = system is not null && request.PresetBySystem.TryGetValue(system, out var p) ? p : request.Preset;
        var to = system is not null && request.ToBySystem.TryGetValue(system, out var t) ? t : request.To;

        FormatPreset? chosen;
        if (presetWord is null && to is null)
        {
            var start = FormatPresets.Start(list, system, options.Default.Kind, settings.Presets);
            chosen = start.Preset;
            if (chosen is null || chosen.IsLeave)
                return Leave(options.Plan.Status == TargetStatus.Convert ? "left as it is" : Words.LowerFirst(options.Plan.Text));
        }
        else
        {
            var kind = to is null ? (TargetKind?)null : KindFor(to, available);
            if (to is not null && kind is null)
                return Leave($"can't become {to.Word}");
            if (kind == TargetKind.Leave) return Leave("left as it is (--to leave)");
            var candidates = kind is { } k ? usable.Where(u => u.Target == k).ToList() : usable;
            if (presetWord is not null)
            {
                chosen = Find(presetWord, candidates);
                if (chosen is null)
                    return Leave(Find(presetWord, list.Where(i => i.IsPreset).Select(i => i.Preset!).ToList()) is { } exists
                        ? $"the {exists.Label.TrimEnd('★', '⚠', ' ')} preset doesn't fit this file"
                        : $"no \"{presetWord}\" preset for {TargetFormats.NameOf(system)}");
                if (chosen.IsLeave) return Leave("left as it is (--preset leave)");
            }
            else
            {
                chosen = candidates.FirstOrDefault(c => c.Recommended)
                         ?? candidates.FirstOrDefault(c => !c.Warning && !c.IsSaved && !c.IsLeave)
                         ?? FormatPresets.StandardFor(kind!.Value);
            }
            if (to?.Extract is { } extract && chosen.Values.Target == TargetKind.DiscImage)
                chosen = chosen with { Values = chosen.Values with { ExtractFormat = extract } };
        }

        if (chosen.Target is not { } target)
            return Leave($"{chosen.Format} can't be made yet");
        var values = chosen.Values.Copy();
        values.Target = target;
        var own = chosen.IsSaved;
        if (request.Tune is not null)
        {
            request.Tune(values);
            own = true;
        }
        if (request.NoVerify)
        {
            values.Verify = false;
            values.TestArchives = false;
            own = true;
        }
        return new ConvertRow(item, chosen, target, values, own, null);
    }

    /// <summary>What <paramref name="to"/> means for a file that can become <paramref name="available"/>: "iso" is a CHD's disc image, an RVZ's ISO, a PS3 disc's decrypted ISO…</summary>
    private static TargetKind? KindFor(FormatChoice to, IReadOnlyList<TargetKind> available)
    {
        if (to.Kind == TargetKind.Leave) return TargetKind.Leave;
        if (available.Contains(to.Kind)) return to.Kind;
        TargetKind[] alternatives = to.Word.ToLowerInvariant() switch
        {
            "iso" => [TargetKind.DiscImage, TargetKind.Ps3Iso, TargetKind.Xiso],
            "cci" => [TargetKind.XboxCci],
            "nsp" => [TargetKind.Nsp],
            _ => [],
        };
        foreach (var k in alternatives)
            if (available.Contains(k)) return k;
        return null;
    }

    /// <summary>
    /// A preset by what was typed: its id ("rvz-smallest"), the end of its id ("smallest"), its name
    /// ("Smallest"), a saved preset's name, or "leave".
    /// </summary>
    public static FormatPreset? Find(string word, IReadOnlyList<FormatPreset> presets)
    {
        var w = word.Trim();
        if (w.Equals(FormatPreset.LeaveId, StringComparison.OrdinalIgnoreCase)) return FormatPresets.Leave;
        return presets.FirstOrDefault(p => p.Id.Equals(w, StringComparison.OrdinalIgnoreCase))
               ?? presets.FirstOrDefault(p => p.IsSaved && p.Name.Equals(w, StringComparison.OrdinalIgnoreCase))
               ?? presets.FirstOrDefault(p => p.Id.EndsWith("-" + w, StringComparison.OrdinalIgnoreCase))
               ?? presets.FirstOrDefault(p => p.Name.Equals(w, StringComparison.OrdinalIgnoreCase))
               ?? presets.FirstOrDefault(p => $"{p.Format} {p.Name}".Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="word"/> names a preset of any system (for checking --preset before a run).</summary>
    public static bool IsKnownPreset(string word, PresetSettings settings)
    {
        if (word.Equals(FormatPreset.LeaveId, StringComparison.OrdinalIgnoreCase)) return true;
        var all = TargetFormats.All.SelectMany(s => FormatPresets.ForSystem(s.Id))
            .Concat(Enum.GetValues<TargetKind>().Select(FormatPresets.StandardFor))
            .Concat(settings.Saved.Select(FormatPresets.ToPreset))
            .Where(p => !p.IsCustom && !p.IsLeave)
            .ToList();
        return Find(word, all) is not null;
    }

    /// <summary>The planner's jobs for the rows that convert, as run jobs; the rows it leaves come back too.</summary>
    public static (IReadOnlyList<RunJob> Jobs, ConvertPlan Plan, IReadOnlyList<ConvertRow> Converting) Plan(
        IReadOnlyList<ConvertRow> rows, AppSettings settings, ILog log)
    {
        var converting = rows.Where(r => r.Skip is null && r.Target != TargetKind.Leave).ToList();
        var inputs = converting.Select(r => new PlanInput(r.Item.Path, r.Target, r.Item.Size, r.Values, r.OwnChecks)).ToList();
        var plan = ConvertPlanner.Plan(inputs, settings, log);
        var jobs = plan.Jobs.Select(j => new RunJob(j.Name, j.Rows.Select(i => converting[i].Item.Path).ToList(), j.Cost, j.Work)
        {
            Tag = j.Rows.Select(i => converting[i]).ToList(),
        }).ToList();
        return (jobs, plan, converting);
    }
}
