namespace NoIntrovert.Core.Formats;

/// <summary>What one input can be turned into on the Convert page.</summary>
public enum TargetKind
{
    /// <summary>A CHD (CD, DVD or GD-ROM, by the image).</summary>
    Chd,
    /// <summary>Dolphin's RVZ, for GameCube and Wii discs.</summary>
    Rvz,
    /// <summary>A plain ISO decoded from an RVZ, WIA or GCZ.</summary>
    Iso,
    /// <summary>A CHD extracted back to cue/bin, gdi, iso or img (by the Extract setting).</summary>
    DiscImage,
    /// <summary>A Wii U .wua (base game, update and DLC in one file).</summary>
    Wua,
    /// <summary>A Nintendo 3DS game as a bare, decrypted .cci (from an encrypted image, a .cia or a .zcci), by lib-3ds-kit.</summary>
    Cci,
    /// <summary>A Nintendo 3DS game as a decrypted .cci compressed in Azahar's Z3DS format (.zcci), by lib-3ds-kit.</summary>
    Zcci,
    /// <summary>A Switch package as a compressed .nsz, by lib-nx-kit.</summary>
    Nsz,
    /// <summary>A Switch package as a compressed cartridge image, .xcz, by lib-nx-kit.</summary>
    Xcz,
    /// <summary>A Switch package as an uncompressed .nsp, by lib-nx-kit (from a cartridge image: an .xci instead).</summary>
    Nsp,
    /// <summary>A Switch package as an uncompressed cartridge image, .xci, by lib-nx-kit.</summary>
    Xci,
    /// <summary>An original Xbox disc as an XISO (.iso): the game partition only, by lib-xiso-kit.</summary>
    Xiso,
    /// <summary>An original Xbox disc as a Cerbios CCI (.cci), split below the FATX 4 GiB limit, by lib-xiso-kit.</summary>
    XboxCci,
    /// <summary>An Xbox 360 disc's files in a ZArchive (.zar) for Xenia Canary: lib-xiso-kit reads them, lib-wii-u-kit writes the archive.</summary>
    Zar,
    /// <summary>A PS3 disc as a decrypted ISO (same size and layout as the Redump image), by lib-ps3-kit with its .dkey.</summary>
    Ps3Iso,
    /// <summary>A PS3 disc's files as a JB folder (PS3_DISC.SFB, PS3_GAME/…), by lib-ps3-kit with its .dkey.</summary>
    Ps3Folder,
    /// <summary>A PSP or PS2 disc as a CSO (deflate blocks), for a real PSP, Adrenaline and PPSSPP.</summary>
    Cso,
    /// <summary>A PS2 (or PSP) disc as a ZSO (LZ4 blocks), for OPL on a real PS2 and PCSX2.</summary>
    Zso,
    SevenZip,
    Zip,
    /// <summary>An archive's files, unpacked.</summary>
    Files,
    /// <summary>Not converted.</summary>
    Leave,
}

public sealed record TargetChoice(TargetKind Kind, string Label)
{
    public override string ToString() => Label;

    public static readonly TargetChoice Chd = new(TargetKind.Chd, "CHD");
    public static readonly TargetChoice Rvz = new(TargetKind.Rvz, "RVZ");
    public static readonly TargetChoice Iso = new(TargetKind.Iso, "ISO");
    public static readonly TargetChoice DiscImage = new(TargetKind.DiscImage, "Disc image");
    public static readonly TargetChoice Wua = new(TargetKind.Wua, ".wua");
    public static readonly TargetChoice Cci = new(TargetKind.Cci, ".cci");
    public static readonly TargetChoice Zcci = new(TargetKind.Zcci, ".zcci");
    public static readonly TargetChoice Nsz = new(TargetKind.Nsz, ".nsz");
    public static readonly TargetChoice Xcz = new(TargetKind.Xcz, ".xcz");
    public static readonly TargetChoice Nsp = new(TargetKind.Nsp, ".nsp");
    public static readonly TargetChoice Xci = new(TargetKind.Xci, ".xci");
    public static readonly TargetChoice Xiso = new(TargetKind.Xiso, "XISO");
    public static readonly TargetChoice XboxCci = new(TargetKind.XboxCci, "CCI (Cerbios)");
    public static readonly TargetChoice Zar = new(TargetKind.Zar, ".zar (Xenia Canary)");
    public static readonly TargetChoice Ps3Iso = new(TargetKind.Ps3Iso, "Decrypted ISO");
    public static readonly TargetChoice Ps3Folder = new(TargetKind.Ps3Folder, "JB folder");
    public static readonly TargetChoice Cso = new(TargetKind.Cso, "CSO");
    public static readonly TargetChoice Zso = new(TargetKind.Zso, "ZSO");
    public static readonly TargetChoice SevenZip = new(TargetKind.SevenZip, "7z");
    public static readonly TargetChoice Zip = new(TargetKind.Zip, "zip");
    public static readonly TargetChoice Files = new(TargetKind.Files, "Unpack files");
    public static readonly TargetChoice Leave = new(TargetKind.Leave, "Leave as is");
}

/// <summary>
/// The targets each input offers, and which one it starts on (FR-5). The
/// rules (<see cref="TargetResolver"/>) give the default: the system's target
/// when the input isn't already optimal, else "Leave as is". The list holds
/// that target, the registry's alternatives this input can become, what the
/// app could already do with the format (extract a CHD, decode an RVZ…), and
/// "Leave as is". A target the app can't make yet (D8) offers nothing else.
/// </summary>
public static class ConvertTargets
{
    public sealed record Options(IReadOnlyList<TargetChoice> Choices, TargetChoice Default, TargetPlan Plan);

    /// <summary>A file the registry doesn't recognise: it can only be packed.</summary>
    public static readonly FormatInfo OtherFile = new("file", "File");

    /// <summary>A folder added as one item, to be packed whole.</summary>
    public static readonly FormatInfo PackedFolder = new("folder", "Folder");

    public static Options For(FileSet set, AppSettings settings) => For(set, settings, TargetResolver.Resolve(set, settings));

    public static Options For(FileSet set, AppSettings settings, TargetPlan plan)
    {
        var (choices, initial) = ByFormat(set, settings);
        // Files the registry doesn't know, and folders, are dropped to be packed: they keep the packing default.
        if (set.Format == OtherFile || set.Format == PackedFolder) return new Options(choices, initial, plan);
        if (plan.Status == TargetStatus.NotYetSupported) choices = [];
        // D2: a 3DS game is never packed into a 7z or zip (or unpacked as loose files); it becomes a .cci or .zcci.
        if (plan.System == "nintendo-3ds")
            choices = choices.Where(c => c != TargetChoice.SevenZip && c != TargetChoice.Zip && c != TargetChoice.Files).ToList();
        // An Xbox disc becomes an XISO or a CCI, an Xbox 360 disc a .zar; CHD and the other disc targets aren't Xbox formats.
        if (plan.System is "xbox" or "xbox-360") choices = [];
        // A PS3 disc becomes a decrypted ISO or a JB folder; it's never made a CHD or packed.
        if (plan.System == "playstation-3") choices = [];
        var list = new List<TargetChoice>();
        if (plan.Choice != TargetChoice.Leave) list.Add(plan.Choice);
        foreach (var c in plan.Alternatives.Concat(choices))
            if (c != TargetChoice.Leave && !list.Contains(c)) list.Add(c);
        list.Add(TargetChoice.Leave);
        return new Options(list, plan.Choice, plan);
    }

    /// <summary>What the app can do with <paramref name="set"/>'s format, whatever its system, and the old default.</summary>
    private static (IReadOnlyList<TargetChoice> Choices, TargetChoice Default) ByFormat(FileSet set, AppSettings settings)
    {
        var f = set.Format;
        var nintendoDefault = settings.Nintendo.Target == Jobs.NintendoTarget.Rvz ? TargetChoice.Rvz : TargetChoice.Chd;

        if (f == OtherFile || f == PackedFolder || KnownFormats.Cartridges.Contains(f))
            return Make(settings.Archives.Format == ArchiveFormat.Zip ? TargetChoice.Zip : TargetChoice.SevenZip,
                TargetChoice.SevenZip, TargetChoice.Zip, TargetChoice.Leave);

        if (KnownFormats.WiiUInputs.Contains(f))
            return f == KnownFormats.WiiUUnsupported ? Make(TargetChoice.Leave, TargetChoice.Leave) : Make(TargetChoice.Wua, TargetChoice.Wua, TargetChoice.Leave);

        if (f == KnownFormats.Chd) return Make(TargetChoice.DiscImage, TargetChoice.DiscImage, TargetChoice.Leave);

        if (KnownFormats.Archives.Contains(f))
        {
            var other = f.HasExtension("x.zip") ? TargetChoice.SevenZip : f.HasExtension("x.7z") ? TargetChoice.Zip : null;
            var list = new List<TargetChoice>();
            if (set.Holds(KnownFormats.WiiUBuildable)) list.Add(TargetChoice.Wua);
            if (KnownFormats.TakesDisc(set)) list.Add(TargetChoice.Chd);
            if (other is not null) list.Add(other);
            list.Add(TargetChoice.Files);
            list.Add(TargetChoice.Leave);
            return (list, list[0]);
        }

        // GameCube / Wii: the containers by name, plain .iso/.gcm by the disc header.
        var ext = Path.GetExtension(set.Primary).ToLowerInvariant();
        if (ext is ".wbfs" or ".ciso") return Make(TargetChoice.Leave, TargetChoice.Leave); // they don't record the disc's exact size
        if (ext == ".rvz") return Make(TargetChoice.Iso, TargetChoice.Iso, TargetChoice.Chd, TargetChoice.Leave);
        if (ext is ".gcz" or ".wia") return Make(nintendoDefault, TargetChoice.Rvz, TargetChoice.Chd, TargetChoice.Iso, TargetChoice.Leave);
        if (ext is ".tgc" or ".gcm" || (ext == ".iso" && File.Exists(set.Primary) && NintendoDisc.IsPlainDisc(set.Primary)))
            return Make(nintendoDefault, TargetChoice.Rvz, TargetChoice.Chd, TargetChoice.Leave);

        if (KnownFormats.DiscInputs.Contains(f)) return Make(TargetChoice.Chd, TargetChoice.Chd, TargetChoice.Leave);
        return Make(TargetChoice.Leave, TargetChoice.Leave);
    }

    private static (IReadOnlyList<TargetChoice>, TargetChoice) Make(TargetChoice initial, params TargetChoice[] choices) => (choices, initial);
}
