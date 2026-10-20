namespace NoIntrovert.Core.Formats;

/// <summary>One choice for a system's target in Settings: "recommended", "leave" or an alternative.</summary>
public sealed record TargetOption(string Id, string Label, bool Producible)
{
    public override string ToString() => Producible ? Label : Label + " (not yet supported)";
}

/// <summary>
/// Resolves a file's target from your overrides (FR-12): a file-type override
/// for its system wins, then one for the whole system, then a file-type
/// override for every system ("*"), then the registry's target. D1 then
/// keeps a big Nintendo DS ROM bare. An override naming a system or target
/// the registry doesn't know is ignored (and reported by <see cref="Problems"/>).
/// </summary>
public static class TargetOverrides
{
    public const string AllSystems = "*";
    public const string Recommended = "recommended";
    public const string Leave = "leave";

    /// <summary>Targets an override for every system may name: they fit any system.</summary>
    private static readonly string[] Generic = [Recommended, Leave, "zip", "7z", "bare"];

    /// <summary>
    /// The target for <paramref name="set"/>: null for "Leave as is"; with
    /// <c>Overridden</c> when one of your settings decided it.
    /// </summary>
    public static (SystemTarget? Target, bool Overridden) Resolve(SystemInfo system, FileSet set, TargetFormatSettings settings)
    {
        var ext = FileType(system, set);
        var valid = settings.Overrides.Where(IsValid).ToList();
        var chosen = valid.FirstOrDefault(o => o.System == system.Id && Same(o.Extension, ext))
                     ?? valid.FirstOrDefault(o => o.System == system.Id && o.Extension is null)
                     ?? valid.FirstOrDefault(o => o.System == AllSystems && Same(o.Extension, ext));
        var target = chosen is null ? system.Target : TargetFor(system, chosen.Target);
        var overridden = chosen is not null && chosen.Target != Recommended;
        if (chosen?.Target == Leave) return (null, true);

        // D1: a Nintendo DS ROM above the size you set stays bare (it loads faster than from a 7z).
        if (system.Id == "nintendo-ds" && settings.DsBareAboveMb > 0 && target is { Container: TargetContainer.SevenZip or TargetContainer.Zip }
            && SizeOf(set) > settings.DsBareAboveMb * 1024L * 1024)
            return (target with { Container = TargetContainer.Bare, Label = target.Format }, true);
        return (target, overridden);
    }

    /// <summary>The choices Settings offers for <paramref name="system"/> (null: every system).</summary>
    public static IReadOnlyList<TargetOption> Options(SystemInfo? system)
    {
        var list = new List<TargetOption> { new(Recommended, "Recommended", true), new(Leave, "Leave as is", true) };
        if (system is null)
            list.AddRange([new("7z", "7z", true), new("zip", "zip", true), new("bare", "Bare (unpacked)", true)]);
        else
            list.AddRange(system.Alternatives.Select(a => new TargetOption(a.Id, a.Label, a.Producible)));
        return list;
    }

    public static bool IsValid(TargetOverride o)
    {
        if (o.System == AllSystems) return Generic.Contains(o.Target);
        return TargetFormats.ById(o.System) is { } system && Options(system).Any(t => t.Id == o.Target);
    }

    /// <summary>What's wrong with the saved overrides, for the log (FR-13): each one that's ignored.</summary>
    public static IEnumerable<string> Problems(TargetFormatSettings settings) =>
        settings.Overrides.Where(o => !IsValid(o)).Select(o => TargetFormats.ById(o.System) is null && o.System != AllSystems
            ? $"the override for \"{o.System}\" names a system NoIntrovert doesn't know; it's ignored."
            : $"the override for {Describe(o)} names a target (\"{o.Target}\") that system doesn't have; it's ignored.");

    public static string Describe(TargetOverride o) =>
        (o.System == AllSystems ? "every system" : TargetFormats.NameOf(o.System)) + (o.Extension is null ? "" : $" ({o.Extension})");

    /// <summary>What an alternative's id stands for as a target: a container for the same file, or another format.</summary>
    public static SystemTarget TargetFor(SystemInfo system, string id)
    {
        if (id == Recommended) return system.Target;
        var label = system.Alternatives.FirstOrDefault(a => a.Id == id)?.Label ?? id;
        var producible = system.Alternatives.FirstOrDefault(a => a.Id == id)?.Producible ?? Generic.Contains(id);
        return id switch
        {
            "zip" => new SystemTarget(system.Target.Format, TargetContainer.Zip, label, producible),
            "7z" => new SystemTarget(system.Target.Format, TargetContainer.SevenZip, label, producible),
            "bare" => new SystemTarget(system.Target.Format, TargetContainer.Bare, label, producible),
            "cue-bin" => new SystemTarget(".cue", TargetContainer.Bare, label, producible),
            "folder" => new SystemTarget("folder", TargetContainer.Folder, label, producible),
            _ => new SystemTarget("." + id, TargetContainer.Bare, label, producible),
        };
    }

    /// <summary>The file type an override is matched on: a ROM's own extension, also inside a zip or 7z.</summary>
    private static string FileType(SystemInfo system, FileSet set)
    {
        var ext = Path.GetExtension(set.Primary).ToLowerInvariant();
        if (!KnownFormats.Archives.Contains(set.Format)) return ext;
        var inside = ArchivePeek.Members(set.Primary)?.Select(m => Path.GetExtension(m.Name).ToLowerInvariant())
            .FirstOrDefault(e => system.Extensions.Contains(e));
        return inside ?? ext;
    }

    private static long SizeOf(FileSet set)
    {
        if (KnownFormats.Archives.Contains(set.Format))
            return ArchivePeek.Members(set.Primary)?.Sum(m => m.Size) ?? 0;
        try { return new FileInfo(set.Primary).Length; }
        catch (IOException) { return 0; }
    }

    private static bool Same(string? overrideExt, string ext) =>
        overrideExt is not null && string.Equals("." + overrideExt.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase);
}
