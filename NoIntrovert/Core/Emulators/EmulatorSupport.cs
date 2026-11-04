using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Emulators;

/// <summary>Which group an emulator is listed under in "Works with".</summary>
public enum EmulatorGroup
{
    RetroArchCore,
    Standalone,
}

/// <summary>Whether an emulator opens the output: ✓, ✕ or ? in "Works with".</summary>
public enum SupportState
{
    Works,
    DoesNot,
    Unknown,
}

/// <summary>One line of "Works with": the emulator, its group, the state for the current output and the tooltip text.</summary>
public sealed record EmulatorSupportEntry(string Id, string Name, EmulatorGroup Group, SupportState State, string Detail);

/// <summary>
/// Decides which emulators open a system's output (docs/requirements/ui-redesign.md, "Works with").
/// ✓ when the extension is supported and so is every CHD codec beyond chdman's defaults;
/// ✕ when the extension or a codec isn't; ? when there's no reliable data.
/// </summary>
public static class EmulatorSupport
{
    /// <summary>At most this many standalone emulators are listed, most common first.</summary>
    public const int MaxStandalone = 8;

    private const string CoreKind = "core";
    private const string StandaloneKind = "standalone";

    /// <summary>The footer text for <see cref="EmulatorCatalog.Current"/>, e.g. "From libretro core info and ES-DE · 4 Nov 2026".</summary>
    public static string Source => EmulatorCatalog.Current.SourceText;

    /// <summary>
    /// Every RetroArch core for <paramref name="systemId"/>, then its most common
    /// standalone emulators, each with whether it opens a <paramref name="outputExtension"/>
    /// file (".chd" or "chd") made with <paramref name="codecs"/> (CHD only; empty for
    /// chdman's defaults). <paramref name="systemId"/> is a NoIntrovert system id
    /// ("playstation") or an ES-DE one ("psx"). An unknown system gives an empty list.
    /// </summary>
    public static IReadOnlyList<EmulatorSupportEntry> For(string systemId, string outputExtension, IReadOnlyCollection<string> codecs) =>
        For(EmulatorCatalog.Current, systemId, outputExtension, codecs);

    /// <inheritdoc cref="For(string, string, IReadOnlyCollection{string})"/>
    public static IReadOnlyList<EmulatorSupportEntry> For(EmulatorCatalog catalog, string? systemId, string? outputExtension, IReadOnlyCollection<string>? codecs)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(outputExtension)) return [];
        var system = ResolveSystem(catalog, systemId.Trim());
        if (system is null) return [];
        var extension = outputExtension.Trim().TrimStart('.').ToLowerInvariant();
        var facts = catalog.Facts;
        var extraCodecs = (codecs ?? [])
            .Select(c => c.Trim().ToLowerInvariant())
            .Where(c => c.Length > 0 && !facts.BaselineCodecs.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Select(c => facts.CodecFamilies.TryGetValue(c, out var family) ? family : c)
            .Distinct()
            .ToList();

        var entries = new List<EmulatorSupportEntry>();

        var cores = catalog.Snapshot.Cores
            .Where(c => c.Systems.Contains(system, StringComparer.OrdinalIgnoreCase) && !facts.HiddenCores.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
            .Select((c, index) => (Core: c, Index: index))
            .OrderBy(c => Rank(facts.PopularCores, c.Core.Id))
            .ThenBy(c => c.Core.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (core, _) in cores)
        {
            var (state, detail) = Evaluate(facts, CoreKind, core.Id, system, extension, extraCodecs, core.Extensions, unpacksArchives: !core.BlockExtract);
            entries.Add(new EmulatorSupportEntry(core.Id, core.Name, EmulatorGroup.RetroArchCore, state, detail));
        }

        var names = catalog.Snapshot.Standalone.ToDictionary(e => e.Id, e => e.Name, StringComparer.OrdinalIgnoreCase);
        var standalone = new List<(string Id, string Name, IReadOnlyList<string> Extensions)>();
        if (catalog.Snapshot.Systems.TryGetValue(system, out var esde))
            standalone.AddRange(esde.Standalone.Select(id => (id, names.GetValueOrDefault(id, id), esde.Extensions)));
        standalone.AddRange(facts.ExtraStandalone
            .Where(e => e.Systems.Contains(system, StringComparer.OrdinalIgnoreCase))
            .Select(e => (e.Id, e.Name, e.Extensions)));
        var listed = standalone
            .Where(e => !facts.HiddenStandalone.Contains(e.Id, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select((e, index) => (Emulator: e, Index: index))
            .OrderBy(e => Rank(facts.PopularStandalone, e.Emulator.Id))
            .ThenBy(e => e.Index)
            .Take(MaxStandalone);
        foreach (var ((id, name, extensions), _) in listed)
        {
            var (state, detail) = Evaluate(facts, StandaloneKind, id, system, extension, extraCodecs, extensions, unpacksArchives: false);
            entries.Add(new EmulatorSupportEntry(id, name, EmulatorGroup.Standalone, state, detail));
        }
        return entries;
    }

    /// <summary>A NoIntrovert system id for <paramref name="id"/> (itself, or the first registry system with that ES-DE id), or null.</summary>
    private static string? ResolveSystem(EmulatorCatalog catalog, string id)
    {
        if (TargetFormats.ById(id) is { } byId) return byId.Id;
        if (catalog.Snapshot.Systems.ContainsKey(id)) return id;
        return TargetFormats.All.FirstOrDefault(s => string.Equals(s.Esde, id, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static int Rank(IReadOnlyList<string> popular, string id)
    {
        for (var i = 0; i < popular.Count; i++)
            if (string.Equals(popular[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    private static (SupportState State, string Detail) Evaluate(
        EmulatorFacts facts, string kind, string id, string system, string extension,
        IReadOnlyList<string> extraCodecs, IReadOnlyList<string> extensions, bool unpacksArchives)
    {
        var upper = extension.ToUpperInvariant();
        var notes = new List<string>();

        var extensionFact = Find(facts, kind, id, system, f => string.Equals(f.Extension, extension, StringComparison.OrdinalIgnoreCase));
        if (extensionFact is { Supported: false }) return (SupportState.DoesNot, extensionFact.Detail ?? $"No {upper} support");
        if (extensionFact is { Supported: null }) return (SupportState.Unknown, extensionFact.Detail ?? $"{upper} support not confirmed");
        if (extensionFact is null)
        {
            if (extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) { }
            else if (unpacksArchives && extension is "zip" or "7z") notes.Add($"RetroArch unpacks the .{extension} first");
            else return (SupportState.DoesNot, $"No {upper} support");
        }
        else if (extensionFact.Detail is { Length: > 0 } note) notes.Add(note);

        var state = SupportState.Works;
        if (extension == "chd")
        {
            foreach (var codec in extraCodecs)
            {
                var name = facts.CodecNames.GetValueOrDefault(codec, codec);
                var codecFact = Find(facts, kind, id, system, f => string.Equals(f.Codec, codec, StringComparison.OrdinalIgnoreCase));
                switch (codecFact?.Supported)
                {
                    case false:
                        return (SupportState.DoesNot, codecFact.Detail ?? $"No {name} CHD support · use chdman's default codecs");
                    case true:
                        if (codecFact.Detail is { Length: > 0 } codecNote) notes.Add(codecNote);
                        break;
                    default:
                        state = SupportState.Unknown;
                        notes.Add(codecFact?.Detail ?? $"{name} CHDs not confirmed");
                        break;
                }
            }
        }
        var detail = state == SupportState.Works ? $"Opens .{extension}" : "";
        detail = string.Join(" · ", new[] { detail }.Concat(notes).Where(s => s.Length > 0));
        return (state, detail);
    }

    private static EmulatorFact? Find(EmulatorFacts facts, string kind, string id, string system, Func<EmulatorFact, bool> about) =>
        facts.Facts.FirstOrDefault(f =>
            string.Equals(f.Kind, kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(f.Emulator, id, StringComparison.OrdinalIgnoreCase)
            && (f.Systems is null || f.Systems.Contains(system, StringComparer.OrdinalIgnoreCase))
            && about(f));
}
