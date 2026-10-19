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
    public static readonly TargetChoice SevenZip = new(TargetKind.SevenZip, "7z");
    public static readonly TargetChoice Zip = new(TargetKind.Zip, "zip");
    public static readonly TargetChoice Files = new(TargetKind.Files, "Unpack files");
    public static readonly TargetChoice Leave = new(TargetKind.Leave, "Leave as is");
}

/// <summary>
/// The targets each input offers, and which one it starts on. The first
/// entry of <see cref="Options.Choices"/> that isn't "Leave as is" is the
/// natural one; <see cref="Options.Default"/> is what the row starts on,
/// following the settings (RVZ or CHD for GameCube/Wii, for example).
/// </summary>
public static class ConvertTargets
{
    public sealed record Options(IReadOnlyList<TargetChoice> Choices, TargetChoice Default);

    /// <summary>A file the registry doesn't recognise: it can only be packed.</summary>
    public static readonly FormatInfo OtherFile = new("file", "File");

    /// <summary>A folder added as one item, to be packed whole.</summary>
    public static readonly FormatInfo PackedFolder = new("folder", "Folder");

    public static Options For(FileSet set, AppSettings settings)
    {
        var f = set.Format;
        var nintendoDefault = settings.Nintendo.Target == Jobs.NintendoTarget.Rvz ? TargetChoice.Rvz : TargetChoice.Chd;

        if (f == OtherFile || f == PackedFolder)
            return Make(settings.Archives.Format == Archives.ArchiveFormat.Zip ? TargetChoice.Zip : TargetChoice.SevenZip,
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
            return new Options(list, list[0]);
        }

        // GameCube / Wii: the containers by name, plain .iso/.gcm by the disc header.
        var ext = Path.GetExtension(set.Primary).ToLowerInvariant();
        if (ext is ".wbfs" or ".ciso")
            return Make(TargetChoice.Leave, TargetChoice.Leave); // they don't record the disc's exact size
        if (ext == ".rvz") return Make(TargetChoice.Iso, TargetChoice.Iso, TargetChoice.Chd, TargetChoice.Leave);
        if (ext is ".gcz" or ".wia") return Make(nintendoDefault, TargetChoice.Rvz, TargetChoice.Chd, TargetChoice.Iso, TargetChoice.Leave);
        if (ext is ".tgc" or ".gcm" || (ext == ".iso" && File.Exists(set.Primary) && NintendoDisc.IsPlainDisc(set.Primary)))
            return Make(nintendoDefault, TargetChoice.Rvz, TargetChoice.Chd, TargetChoice.Leave);

        if (KnownFormats.DiscInputs.Contains(f)) return Make(TargetChoice.Chd, TargetChoice.Chd, TargetChoice.Leave);
        return Make(TargetChoice.Leave, TargetChoice.Leave);
    }

    private static Options Make(TargetChoice initial, params TargetChoice[] choices) => new(choices, initial);
}
