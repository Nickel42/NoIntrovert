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
/// It covers R1–R5, R8–R10 and R13 with the existing CHD, RVZ, WUA and
/// archive jobs, on the target your overrides resolve to (FR-12,
/// <see cref="TargetOverrides"/>).
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

        // D2, FR-9: lib-3ds-kit decrypts a 3DS image, makes a .cia a .cci and reads Azahar's .zcci on the way to its target.
        if (id == "nintendo-3ds" && !isArchive && ThreeDs(set, system, ext, alternatives, settings) is { } ctr) return ctr;
        if (id == "nintendo-3ds" && isArchive && ThreeDsInArchive(set, system, contents, alternatives, settings) is { } packed) return packed;
        // Original Xbox discs become an XISO or a CCI through lib-xiso-kit.
        if (id == "xbox" && !isArchive) return Xbox(system, set, ext, settings);
        // Xbox 360: a disc stays an .iso or becomes a .zar for Xenia Canary; packages (XBLA, DLC, updates) stay as they are.
        if (id == "xbox-360" && !isArchive) return Xbox360(system, set, ext, settings);
        // Switch packages convert between NSP, NSZ, XCI and XCZ through lib-nx-kit.
        if (id == "switch" && !isArchive) return Switch(system, set, ext, alternatives, settings);
        // PS3 discs become a decrypted ISO or a JB folder through lib-ps3-kit, with the disc's .dkey.
        if (id == "playstation-3" && !isArchive) return PlayStation3(system, set, ext, settings);

        // FR-12: your overrides, then the registry's target (and D1).
        var (resolved, overridden) = TargetOverrides.Resolve(system, set, settings.TargetFormats);
        if (resolved is not { } target) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (your setting)", alternatives);
        var isDisc = set.Format == KnownFormats.Chd || KnownFormats.TakesDisc(set) || KnownFormats.TakesWiiU(set)
                     || NintendoDisc.IsContainer(set.Primary) || contents.Any(c => NintendoDisc.IsContainer("x" + c));
        if (system.DiscTarget is { } discFormat && isDisc && !overridden)
            target = target with { Format = discFormat, Container = TargetContainer.Bare, Label = discFormat, Producible = true };

        if (target.Container == TargetContainer.Bare)
            return Bare(set, system, target, isArchive, contents, alternatives, settings);
        if (target.Container is TargetContainer.SevenZip or TargetContainer.Zip)
            return Packed(set, system, target, overridden, isArchive, members, ext, alternatives, settings.TargetFormats.NormalizeHeaders);

        // Folder targets: ScummVM games are unpacked.
        if (!target.Producible) return NotYet(id, target, alternatives);
        return isArchive
            ? Plan(id, TargetStatus.Convert, TargetChoice.Files, "Convert → folder (unpack)", alternatives)
            : Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives);
    }

    /// <summary>
    /// D2: a Nintendo 3DS file that isn't in an archive. Every target is a decrypted .cci: bare (the
    /// recommendation) or compressed as Azahar's .zcci; a 3DS game is never packed into a 7z or zip. An encrypted
    /// image, a .cia (games only; an update or DLC can't be a cartridge image) or a .zcci/.zcia goes through
    /// lib-3ds-kit first. Null leaves anything else (a .cxi, a .3dsx) to the usual rules.
    /// </summary>
    private static TargetPlan? ThreeDs(FileSet set, SystemInfo system, string ext, List<TargetChoice> alternatives, AppSettings settings)
    {
        var id = system.Id;
        var facts = File.Exists(set.Primary) ? Ctr.CtrFacts.Of(set.Primary) : null;
        if (facts is null)
            return ext is ".zcci" or ".zcia" or ".zcxi" or ".z3dsx"
                ? Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (not a Z3DS file NoIntrovert can read)") : null;
        if (facts.Type is not (Lib3dsKit.CtrFileType.Cci or Lib3dsKit.CtrFileType.Cia))
            return facts.Compressed ? Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, $"Left as is ({ext}: not a cartridge image)") : null;
        if (!facts.IsGame) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (an update or DLC .cia; only games become a .cci)");

        alternatives.RemoveAll(a => a == TargetChoice.Files || a == TargetChoice.SevenZip || a == TargetChoice.Zip);
        if (ext != ".zcci" || facts.Encrypted) alternatives.Insert(0, TargetChoice.Zcci);
        if (ext != ".cci" || facts.NeedsConversion) alternatives.Add(TargetChoice.Cci);

        var target = ThreeDsTarget(system, set, settings);
        if (target is null) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (your setting)", alternatives);
        var what = facts.Type == Lib3dsKit.CtrFileType.Cia ? "from the .cia" : facts.Encrypted ? "decrypted" : facts.Compressed ? "unpacked from Z3DS" : null;
        var note = what is null ? "" : $" ({what})";

        if (target.Format == ".zcci")
            return ext == ".zcci" && !facts.Encrypted
                ? Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives)
                : Plan(id, TargetStatus.Convert, TargetChoice.Zcci, $"Convert → .zcci{(facts.Encrypted || facts.Type == Lib3dsKit.CtrFileType.Cia ? note : "")}", alternatives);
        return ext == ".cci" && !facts.NeedsConversion
            ? Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives)
            : Plan(id, TargetStatus.Convert, TargetChoice.Cci, $"Convert → .cci{(what is null && ext == ".3ds" ? " (renamed from .3ds)" : note)}", alternatives);
    }

    /// <summary>
    /// A 3DS game's target: yours (.zcci, or leave as is), else the bare .cci. A 7z or zip from an override for every
    /// system doesn't apply: a 3DS game is never packed (D2).
    /// </summary>
    private static SystemTarget? ThreeDsTarget(SystemInfo system, FileSet set, AppSettings settings)
    {
        var (target, overridden) = TargetOverrides.Resolve(system, set, settings.TargetFormats);
        if (target is null && overridden) return null;
        return target is null || target.Container is TargetContainer.SevenZip or TargetContainer.Zip ? system.Target : target;
    }

    /// <summary>
    /// An original Xbox disc image (.iso, told apart by its XDVDFS header). XISO is the target: the game partition
    /// only, which xemu reads; CCI is for Cerbios on a modded console. A Redump image becomes either; an XISO is
    /// already optimal, or becomes a CCI. lib-xiso-kit does the work.
    /// </summary>
    private static TargetPlan Xbox(SystemInfo system, FileSet set, string ext, AppSettings settings)
    {
        var id = system.Id;
        if (ext != ".iso") return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, $"Left as is ({ext})");
        var xiso = File.Exists(set.Primary) && DiscProbe.IsXiso(set.Primary);
        var (target, _) = TargetOverrides.Resolve(system, set, settings.TargetFormats);
        if (target is null) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (your setting)");
        var cci = target.Format == ".cci";
        List<TargetChoice> alternatives = cci ? (xiso ? [] : [TargetChoice.Xiso]) : [TargetChoice.XboxCci];
        if (cci) return Plan(id, TargetStatus.Convert, TargetChoice.XboxCci, "Convert → .cci (Cerbios)", alternatives);
        return xiso
            ? Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives)
            : Plan(id, TargetStatus.Convert, TargetChoice.Xiso, "Convert → XISO (drops the video partition)", alternatives);
    }

    /// <summary>
    /// An Xbox 360 file that isn't in an archive. A disc image (.iso, Redump or XISO) is optimal as it is, which
    /// Xenia plays directly; choose .zar to have its files written into a ZArchive, which Xenia Canary reads without
    /// unpacking. A .zar can't be turned back into an .iso, and an STFS or Games on Demand package (XBLA, DLC, a title
    /// update) is what Xenia installs, so both are left as they are.
    /// </summary>
    private static TargetPlan Xbox360(SystemInfo system, FileSet set, string ext, AppSettings settings)
    {
        var id = system.Id;
        if (set.Format == KnownFormats.XboxPackage)
            return Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal");
        var (target, _) = TargetOverrides.Resolve(system, set, settings.TargetFormats);
        if (target is null) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (your setting)");
        var zar = target.Format == ".zar";
        if (ext == ".zar")
            return zar ? Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal")
                : Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (a .zar can't be turned back into an .iso)");
        if (ext != ".iso" || !File.Exists(set.Primary) || !DiscProbe.IsXdvdfs(set.Primary))
            return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, $"Left as is ({(ext.Length > 0 ? ext : "not a disc image")})");
        return zar
            ? Plan(id, TargetStatus.Convert, TargetChoice.Zar, "Convert → .zar (Xenia Canary)")
            : Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", [TargetChoice.Zar]);
    }

    /// <summary>
    /// A Switch package that isn't in an archive. NSZ is the target for everything (base games, updates and DLC
    /// share one format); XCZ keeps a cartridge image a cartridge image; "uncompressed" turns an .nsz back into an
    /// .nsp and an .xcz into an .xci. lib-nx-kit does each one, with the user's prod.keys.
    /// </summary>
    private static TargetPlan Switch(SystemInfo system, FileSet set, string ext, List<TargetChoice> alternatives, AppSettings settings)
    {
        var id = system.Id;
        var card = ext is ".xci" or ".xcz";
        var plain = card ? TargetChoice.Xci : TargetChoice.Nsp;
        foreach (var choice in new[] { TargetChoice.Nsz, TargetChoice.Xcz, plain })
            if ("." + choice.Kind.ToString().ToLowerInvariant() != ext && !alternatives.Contains(choice)) alternatives.Add(choice);

        var (target, _) = TargetOverrides.Resolve(system, set, settings.TargetFormats);
        if (target is null) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (your setting)", alternatives);
        var wanted = target.Format switch
        {
            ".xcz" => TargetChoice.Xcz,
            ".nsp" => plain,
            _ => TargetChoice.Nsz,
        };
        var wantedExt = "." + wanted.Kind.ToString().ToLowerInvariant();
        if (ext == wantedExt || (wanted == plain && ext is ".nsp" or ".xci"))
            return Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives);
        alternatives.Remove(wanted);
        return Plan(id, TargetStatus.Convert, wanted, $"Convert → {wantedExt}", alternatives);
    }

    /// <summary>
    /// A PS3 disc that isn't in an archive. A Redump image (.iso, encrypted) becomes a decrypted ISO, the target,
    /// which RPCS3 loads, or a JB folder (the disc's files); both need the disc's .dkey. A decrypted ISO is
    /// optimal, or becomes a JB folder without a key. A JB folder is optimal when that's your target; it can't be
    /// made back into an ISO, so it's left as it is otherwise.
    /// </summary>
    private static TargetPlan PlayStation3(SystemInfo system, FileSet set, string ext, AppSettings settings)
    {
        var id = system.Id;
        var (target, _) = TargetOverrides.Resolve(system, set, settings.TargetFormats);
        if (target is null) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (your setting)");
        var folder = target.Container == TargetContainer.Folder;

        if (set.Format == KnownFormats.Ps3Folder)
            return folder
                ? Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal")
                : Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (a JB folder can't be made into an ISO yet)");
        if (ext != ".iso" || !File.Exists(set.Primary)) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, $"Left as is ({(ext.Length > 0 ? ext : "not a disc image")})");

        var state = NoIntrovert.Core.Ps3.Ps3Jobs.StateOf(set.Primary);
        if (state is null) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (not a PS3 disc image)");
        var decrypted = state is LibPs3Kit.EncryptionState.Decrypted or LibPs3Kit.EncryptionState.NoEncryptedRegions;
        var noKey = !decrypted && NoIntrovert.Core.Ps3.Ps3Jobs.KeyFileFor(set.Primary, settings) is null ? "; needs its .dkey" : "";
        if (decrypted)
            return folder
                ? Plan(id, TargetStatus.Convert, TargetChoice.Ps3Folder, "Convert → JB folder")
                : Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", [TargetChoice.Ps3Folder]);
        return folder
            ? Plan(id, TargetStatus.Convert, TargetChoice.Ps3Folder, $"Convert → JB folder (decrypted{noKey})", [TargetChoice.Ps3Iso])
            : Plan(id, TargetStatus.Convert, TargetChoice.Ps3Iso, $"Convert → decrypted ISO{(noKey.Length > 0 ? " (needs its .dkey)" : "")}", [TargetChoice.Ps3Folder]);
    }

    /// <summary>
    /// D2: a 7z or zip holding one 3DS game becomes the bare (or .zcci) target, unpacked, and decrypted or
    /// converted on the way when it needs to be; an archive holding several games is left for unpacking first.
    /// It's never repacked as another 7z or zip.
    /// </summary>
    private static TargetPlan? ThreeDsInArchive(FileSet set, SystemInfo system, List<string> contents, List<TargetChoice> alternatives, AppSettings settings)
    {
        var id = system.Id;
        var games = contents.Count(c => c is ".3ds" or ".cci" or ".cia" or ".zcci" or ".zcia");
        if (games == 0) return null;
        alternatives.RemoveAll(a => a == TargetChoice.Files || a == TargetChoice.SevenZip || a == TargetChoice.Zip);
        var target = ThreeDsTarget(system, set, settings);
        if (target is null) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (your setting)", alternatives);
        if (games > 1) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (several games in one archive; unpack it first)", alternatives);
        foreach (var choice in new[] { TargetChoice.Cci, TargetChoice.Zcci })
            if (!alternatives.Contains(choice)) alternatives.Add(choice);
        var zcci = target.Format == ".zcci";
        var wanted = zcci ? TargetChoice.Zcci : TargetChoice.Cci;
        alternatives.Remove(wanted);
        return Plan(id, TargetStatus.Convert, wanted, $"Convert → {(zcci ? ".zcci" : ".cci")} (unpacked)", alternatives);
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
            // A plain image chosen in place of a CHD or RVZ: extract or decode it back.
            ".iso" or ".cue" or ".gdi" when set.Format == KnownFormats.Chd => TargetChoice.DiscImage,
            ".iso" when NintendoDisc.IsDecodable(set.Primary) => TargetChoice.Iso,
            // PSP and PS2 discs compressed for real hardware (a PSP, OPL on a PS2).
            ".cso" when Jobs.CsoJobs.TakesInput(set) => TargetChoice.Cso,
            ".zso" when Jobs.CsoJobs.TakesInput(set) => TargetChoice.Zso,
            _ => null,
        };
        var label = choice == TargetChoice.Chd ? ".chd" : targetExt == ".cue" ? "cue/bin" : targetExt;

        if (!isArchive && (ext == targetExt || (id == "wii-u" && set.Format == KnownFormats.Wua)))
            return Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives);
        if (!target.Producible) return NotYet(id, target, alternatives, ext == ".xci" && targetExt == ".nsz" ? "repack to NSP first" : null);
        if (choice == TargetChoice.DiscImage || choice == TargetChoice.Iso || choice == TargetChoice.Cso || choice == TargetChoice.Zso)
            return Plan(id, TargetStatus.Convert, choice, $"Convert → {label}", alternatives);

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

        // WBFS and CISO don't record the disc's exact size, which lib-wii-gamecube-kit needs (see Future.md).
        if (ext is ".wbfs" or ".ciso")
            return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, $"Left as is ({ext} can't be converted yet)");

        if (choice is null || !(KnownFormats.DiscInputs.Contains(set.Format) || NintendoDisc.IsContainer(set.Primary)))
            return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is", alternatives);
        return Plan(id, TargetStatus.Convert, choice, $"Convert → {label}", alternatives);
    }

    /// <summary>Single-file 7z (R1, R8, D9) and zip for the computer systems (R9).</summary>
    private static TargetPlan Packed(FileSet set, SystemInfo system, SystemTarget target, bool overridden, bool isArchive,
        IReadOnlyList<ArchiveMember>? members, string ext, IReadOnlyList<TargetChoice> alternatives, bool normalizeHeaders)
    {
        var id = system.Id;
        // The registry's per-file-type containers (MSX disks in zip) give way to a container you chose.
        TargetContainer ContainerFor(string e) => overridden ? target.Container : system.ContainerFor(e);
        if (!isArchive)
        {
            var wanted = ContainerFor(ext);
            if (wanted == TargetContainer.Bare) return Plan(id, TargetStatus.Optimal, TargetChoice.Leave, "Optimal", alternatives);
            if (!target.Producible) return NotYet(id, target, alternatives);
            // The name it gets inside (D2: .3ds → .cci; R12: .v64 → .z64…).
            var inside = File.Exists(set.Primary) && RomFixes.FixedExtension(set.Primary, normalizeHeaders) is { } fixedExt && fixedExt != ext ? $" (as {fixedExt})" : "";
            return Plan(id, TargetStatus.Convert, ChoiceFor(wanted), $"Convert → {Label(wanted)}{inside}", alternatives);
        }

        if (members is null) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (archive can't be read)", alternatives);
        var roms = members.Where(m => system.Extensions.Contains(Ext(m.Name))).ToList();
        if (roms.Count == 0) return Plan(id, TargetStatus.LeftAsIs, TargetChoice.Leave, "Left as is (no game files inside)", alternatives);
        // R6 (splitting several games apart) comes later; a computer game's disks belong together in one zip.
        if (roms.Count > 1 && system.Category == "cartridge")
            return Plan(id, TargetStatus.Convert, TargetChoice.SevenZip, "Convert → one .7z per game (R6)", alternatives);

        var has = Ext(set.Primary) switch { ".7z" => TargetContainer.SevenZip, ".zip" => TargetContainer.Zip, _ => TargetContainer.Leave };
        var want = ContainerFor(Ext(roms[0].Name));
        if (want == TargetContainer.Bare) return Plan(id, TargetStatus.Convert, TargetChoice.Files, "Convert → bare file (unpack)", alternatives);
        var optimal = overridden ? has == want : system.IsOptimalContainer(has, Ext(roms[0].Name));
        if (optimal && MethodsFit(has, members))
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
                "zcci" when system.Id == "nintendo-3ds" && isArchive => TargetChoice.Zcci, // D2: unpacked, then compressed for Azahar
                "iso" when NintendoDisc.IsDecodable(set.Primary) => TargetChoice.Iso,
                "iso" or "cue-bin" or "gdi" when set.Format == KnownFormats.Chd => TargetChoice.DiscImage,
                "cso" when ext != ".cso" && Jobs.CsoJobs.TakesInput(set) => TargetChoice.Cso,
                "zso" when ext != ".zso" && Jobs.CsoJobs.TakesInput(set) => TargetChoice.Zso,
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
