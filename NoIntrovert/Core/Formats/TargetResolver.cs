namespace NoIntrovert.Core.Formats;

/// <summary>Where a file stands against its system's target (FR-4).</summary>
public enum TargetStatus
{
    /// <summary>Already in its target format and container.</summary>
    Optimal,
    /// <summary>The app can make the target.</summary>
    Convert,
    /// <summary>The target is known but the app can't make it yet (D8): no Convert button.</summary>
    NotYetSupported,
    /// <summary>Never touched: arcade romsets (R5, D10), Dreamcast .cdi (D7), WBFS and CISO images.</summary>
    LeftAsIs,
    /// <summary>The system couldn't be told (FR-2).</summary>
    UnknownSystem,
}

/// <summary>
/// What the rules make of one input: its system (a registry id, or null),
/// its status, the target the row starts on (<see cref="TargetChoice.Leave"/>
/// unless the status is <see cref="TargetStatus.Convert"/>), the registry's
/// alternatives this input can be turned into, and the status as the pages
/// show it ("Optimal", "Convert → .7z"…).
/// </summary>
public sealed record TargetPlan(
    string? System,
    TargetStatus Status,
    TargetChoice Choice,
    string Text,
    IReadOnlyList<TargetChoice> Alternatives)
{
    public string SystemName => TargetFormats.NameOf(System);
}

/// <summary>
/// Applies the rules of docs/target-formats.md to one input: finds its system,
/// looks inside a zip or 7z (members and their compression methods), and
/// decides whether it's already optimal or what it should become.
/// Phase 1 covers R1–R5, R8–R10 and R13 with the existing CHD, RVZ, WUA and
/// archive jobs; overrides (FR-12) aren't applied yet, so the registry's
/// target is the resolved one.
/// </summary>
public static class TargetResolver
{
    public static TargetPlan Resolve(FileSet set, AppSettings settings, string? root = null, Func<string, string?>? chdProbe = null)
    {
        if (set.Format == ConvertTargets.OtherFile || set.Format == ConvertTargets.PackedFolder)
            return Plan(null, TargetStatus.UnknownSystem, TargetChoice.Leave, TargetFormats.UnknownName);
        var id = SystemDetector.SystemOf(set, root, chdProbe);
        return Resolve(set, TargetFormats.ById(id), settings);
    }

    /// <summary>The rules for <paramref name="set"/> once its system is known (null: unknown).</summary>
    public static TargetPlan Resolve(FileSet set, SystemInfo? system, AppSettings settings)
    {
        if (system is null) return Plan(null, TargetStatus.UnknownSystem, TargetChoice.Leave, TargetFormats.UnknownName);
        var id = system.Id;

        // R5, D10: romsets are reported, never changed.
        if (system.Follows("R5")) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (arcade romset)");

        var isArchive = KnownFormats.Archives.Contains(set.Format);
        var members = isArchive ? ArchivePeek.Members(set.Primary) : null;
        var ext = Ext(set.Primary);
        var contents = members?.Select(m => Ext(m.Name)).ToList() ?? (isArchive ? [] : [ext]);
        var alternatives = Alternatives(system, set, isArchive);

        // D7: chdman can't convert DiscJuggler images reliably.
        if (id == "dreamcast" && contents.Contains(".cdi")) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (.cdi, D7)");

        var target = system.Target;
        var isDisc = set.Format == KnownFormats.Chd || KnownFormats.TakesDisc(set) || KnownFormats.TakesWiiU(set)
                     || NintendoDisc.IsContainer(set.Primary) || contents.Any(c => NintendoDisc.IsContainer("x" + c));
        if (system.DiscTarget is { } discFormat && isDisc)
            target = target with { Format = discFormat, Container = TargetContainer.Bare, Label = discFormat, Producible = true };

        if (target.Container == TargetContainer.Bare)
            return Bare(set, system, target, isArchive, contents, alternatives, settings);
        if (target.Container is TargetContainer.SevenZip or TargetContainer.Zip)
            return Packed(set, system, target, isArchive, members, ext, alternatives);

        // Folder targets: ScummVM games are unpacked; PS3 folders can't be made yet.
        if (!target.Producible) return NotYet(id, target, alternatives);
        return isArchive
            ? Plan(id, TargetStatus.Convert, TargetChoice.Files, "Convert → folder (unpack)", alternatives)
            : Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives);
    }

    /// <summary>Discs and other bare targets (R2, R3, R4, R13): .chd, .rvz, .wua, .wad, .nsz, .iso.</summary>
    private static TargetPlan Bare(FileSet set, SystemInfo system, SystemTarget target, bool isArchive, List<string> contents,
        IReadOnlyList<TargetChoice> alternatives, AppSettings settings)
    {
        var id = system.Id;
        var ext = Ext(set.Primary);
        var targetExt = target.Format;

        // GameCube/Wii discs follow the RVZ-or-CHD setting until overrides (Phase 2) take its place.
        var choice = targetExt switch
        {
            ".chd" => TargetChoice.Chd,
            ".rvz" when settings.Nintendo.Target == Jobs.NintendoTarget.Chd => TargetChoice.Chd,
            ".rvz" => TargetChoice.Rvz,
            ".wua" => TargetChoice.Wua,
            _ => null,
        };
        var label = choice == TargetChoice.Chd ? ".chd" : targetExt;

        if (!isArchive && (ext == targetExt || (id == "wii-u" && set.Format == KnownFormats.Wua)))
            return id == "xbox" && !DiscProbe.IsXiso(set.Primary)
                ? NotYet(id, target, alternatives)
                : Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives);
        if (!target.Producible) return NotYet(id, target, alternatives, ext == ".xci" ? "repack to NSP first" : null);

        if (isArchive)
        {
            // R13: an archive holding the finished file is unpacked, never repacked.
            if (contents.Contains(targetExt) || (choice == TargetChoice.Chd && contents.Contains(".chd")))
                return Plan(id, TargetStatus.Convert, TargetChoice.Files, $"Convert → bare {label} (unpack)", alternatives);
            if (choice is null || (choice == TargetChoice.Wua ? !KnownFormats.TakesWiiU(set) : !KnownFormats.TakesDisc(set)))
                return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (nothing to convert inside)", alternatives);
            return Plan(id, TargetStatus.Convert, choice, $"Convert → {label}", alternatives);
        }

        if (choice == TargetChoice.Wua)
            return set.Format == KnownFormats.WiiUUnsupported
                ? Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (unsupported Wii U input)")
                : Plan(id, TargetStatus.Convert, choice, "Convert → .wua", alternatives);

        // WBFS and CISO don't record the disc's exact size, which libdolphinrvz needs (see Future.md).
        if (ext is ".wbfs" or ".ciso")
            return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, $"Left as is ({ext} can't be converted yet)");

        if (choice is null || !(KnownFormats.DiscInputs.Contains(set.Format) || NintendoDisc.IsContainer(set.Primary)))
            return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is", alternatives);
        return Plan(id, TargetStatus.Convert, choice, $"Convert → {label}", alternatives);
    }

    /// <summary>Single-file 7z (R1, R8, D9) and zip for the computer systems (R9).</summary>
    private static TargetPlan Packed(FileSet set, SystemInfo system, SystemTarget target, bool isArchive, IReadOnlyList<ArchiveMember>? members,
        string ext, IReadOnlyList<TargetChoice> alternatives)
    {
        var id = system.Id;
        if (!isArchive)
        {
            var wanted = system.ContainerFor(ext);
            if (wanted == TargetContainer.Bare) return Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives);
            if (!target.Producible) return NotYet(id, target, alternatives);
            return Plan(id, TargetStatus.Convert, ChoiceFor(wanted), $"Convert → {Label(wanted)}", alternatives);
        }

        if (members is null) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (archive can't be read)", alternatives);
        var roms = members.Where(m => system.Extensions.Contains(Ext(m.Name))).ToList();
        if (roms.Count == 0) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (no game files inside)", alternatives);
        // R6 (splitting several games apart) comes later; a computer game's disks belong together in one zip.
        if (roms.Count > 1 && system.Category == "cartridge")
            return Plan(id, TargetStatus.NotYetSupported, TargetChoice.Leave, "Recommended: one .7z per game (not yet supported)", alternatives);

        var has = Ext(set.Primary) switch { ".7z" => TargetContainer.SevenZip, ".zip" => TargetContainer.Zip, _ => TargetContainer.Leave };
        var want = system.ContainerFor(Ext(roms[0].Name));
        if (want == TargetContainer.Bare) return Plan(id, TargetStatus.Convert, TargetChoice.Files, "Convert → bare file (unpack)", alternatives);
        if (system.IsOptimalContainer(has, Ext(roms[0].Name)) && MethodsFit(has, members))
            return Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives);
        if (!target.Producible) return NotYet(id, target, alternatives);
        if (has == want)
            return Plan(id, TargetStatus.Convert, ChoiceFor(want), $"Convert → {Label(want)} (recompress as {(want == TargetContainer.Zip ? "Deflate" : "LZMA2")})", alternatives);
        return Plan(id, TargetStatus.Convert, ChoiceFor(want), $"Convert → {Label(want)}", alternatives);
    }

    /// <summary>
    /// R8: what RetroArch opens. A 7z's members must be LZMA or LZMA2
    /// (SharpCompress names both "LZMA"), a zip's Deflate or stored.
    /// </summary>
    public static bool MethodsFit(TargetContainer container, IReadOnlyList<ArchiveMember> members) => container switch
    {
        TargetContainer.SevenZip => members.All(m => m.Method == "LZMA"),
        TargetContainer.Zip => members.All(m => m.Method is "Deflate" or "None"),
        _ => false,
    };

    /// <summary>The registry's alternatives that this input can be turned into today.</summary>
    private static List<TargetChoice> Alternatives(SystemInfo system, FileSet set, bool isArchive)
    {
        var ext = Ext(set.Primary);
        var list = new List<TargetChoice>();
        foreach (var alt in system.Alternatives.Where(a => a.Producible))
        {
            var choice = alt.Id switch
            {
                "zip" when ext != ".zip" => TargetChoice.Zip,
                "7z" when ext != ".7z" => TargetChoice.SevenZip,
                "bare" when isArchive => TargetChoice.Files,
                "iso" when NintendoDisc.IsDecodable(set.Primary) => TargetChoice.Iso,
                "iso" or "cue-bin" or "gdi" when set.Format == KnownFormats.Chd => TargetChoice.DiscImage,
                _ => null,
            };
            if (choice is not null && !list.Contains(choice)) list.Add(choice);
        }
        return list;
    }

    private static TargetPlan NotYet(string id, SystemTarget target, IReadOnlyList<TargetChoice> alternatives, string? note = null) =>
        Plan(id, TargetStatus.NotYetSupported, TargetChoice.Leave,
            $"Recommended: {target.Format} (not yet supported{(note is null ? "" : "; " + note)})", alternatives);

    private static TargetChoice ChoiceFor(TargetContainer container) => container == TargetContainer.Zip ? TargetChoice.Zip : TargetChoice.SevenZip;

    private static string Label(TargetContainer container) => container == TargetContainer.Zip ? ".zip" : ".7z";

    private static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

    private static TargetPlan Plan(string? system, TargetStatus status, TargetChoice choice, string text,
        IReadOnlyList<TargetChoice>? alternatives = null) =>
        new(system, status, choice, text, alternatives ?? []);
}
