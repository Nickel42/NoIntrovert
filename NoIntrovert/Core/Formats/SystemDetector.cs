namespace NoIntrovert.Core.Formats;

/// <summary>
/// Which registry system (see <see cref="TargetFormats"/>) an input is for:
/// from the file itself where that's quick and certain (Wii U formats, disc
/// headers), then its extension, then a folder name on the way up ("PS1",
/// "Mega Drive"…). An archive is judged by the files in it. A disc image of a
/// CD-only kind (cue, gdi, ccd…) or a CHD whose system can't be told goes to
/// the generic Redump CD/DVD entry, whose target is the same CHD; anything
/// else left undecided (a loose .bin, a bare .iso) is null: Unknown system.
/// </summary>
public static class SystemDetector
{
    public const string GenericDisc = "other-redump-cd-dvd-systems";

    /// <summary>
    /// The system of <paramref name="set"/>, or null. <paramref name="root"/>
    /// is the library folder the folder walk stops at (null: the whole path).
    /// <paramref name="chdProbe"/>, when given, looks inside a CHD whose folder
    /// says nothing (the Library uses it to find Dreamcast GD-ROMs).
    /// </summary>
    public static string? SystemOf(FileSet set, string? root = null, Func<string, string?>? chdProbe = null)
    {
        var f = set.Format;
        if (KnownFormats.WiiUInputs.Contains(f) || set.Holds(KnownFormats.WiiUBuildable)) return "wii-u";
        if (f == KnownFormats.VitaFolder) return "playstation-vita";

        if (KnownFormats.Archives.Contains(f))
        {
            // R5: in an arcade folder a zip is a romset, whatever its files are called.
            if (FromFolders(set.Primary, root, s => s.Follows("R5")) is { } arcade) return arcade;
            var members = ArchivePeek.Members(set.Primary);
            if (members is null || members.Count == 0) return FromFolders(set.Primary, root, null);
            // The archive's system is that of the files in it that say one; the rest (readme, nfo) don't count.
            var names = members.Select(m => m.Name).ToList();
            var found = names.Select(n => FromName(n, set.Primary, root)).Where(s => s is not null).Distinct().ToList();
            if (found.Count == 1) return found[0];
            if (found.Count > 1) return null;
            return FromFolders(set.Primary, root, null) ?? (names.Any(IsCdOnly) ? GenericDisc : null);
        }

        if (File.Exists(set.Primary) && IsDiscFile(set.Primary))
        {
            // A cue's header is in its first image; .cso and .zso are compressed, so they go by folder.
            var image = KnownFormats.Descriptors.Contains(f) ? set.Members.Skip(1).FirstOrDefault(File.Exists) : set.Primary;
            if (image is not null && DiscProbe.SystemOf(image) is { } probed) return probed;
        }
        var system = FromName(set.Primary, set.Primary, root);
        if (system is null && f == KnownFormats.Chd && File.Exists(set.Primary) && chdProbe is not null)
            system = chdProbe(set.Primary);
        return system ?? (IsCdOnly(set.Primary) ? GenericDisc : null);
    }

    /// <summary>The system a file name (on disk, or inside the archive at <paramref name="where"/>) points to.</summary>
    private static string? FromName(string name, string where, string? root)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".gdi") return "dreamcast";
        if (ext is ".gcz" or ".wia" or ".rvz" or ".ciso")
            return FromFolders(where, root, s => s.Id is "gamecube" or "wii") ?? (File.Exists(name) ? DiscProbe.SystemOf(name) : null);
        if (ext is ".cso" or ".zso")
            return FromFolders(where, root, s => s.Id == "playstation-2") ?? "playstation-portable";
        var candidates = TargetFormats.ByExtension(ext);
        if (candidates.Count == 1 && !Generic.Contains(ext)) return candidates[0].Id;
        if (candidates.Count > 1) return FromFolders(where, root, candidates.Contains);

        // Disc images (and CHDs) belong to whichever disc system the folder names.
        if (IsDiscFile(name) || ext == ".chd") return FromFolders(where, root, TakesDiscs);
        return null;
    }

    /// <summary>Extensions too common to name a system on their own (BIOS files, raw dumps): the folder has to.</summary>
    private static readonly string[] Generic = [".bin", ".rom"];

    /// <summary>
    /// The first system a folder name on the way up from <paramref name="path"/>
    /// stands for, among those <paramref name="accept"/> allows (all of them when null).
    /// </summary>
    private static string? FromFolders(string path, string? root, Func<SystemInfo, bool>? accept)
    {
        var dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        var stop = root is null ? null : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root));
        while (!string.IsNullOrEmpty(dir) && !string.Equals(dir, stop, StringComparison.OrdinalIgnoreCase))
        {
            var match = TargetFormats.ByFolder(Path.GetFileName(dir)).FirstOrDefault(s => accept?.Invoke(s) ?? true);
            if (match is not null) return match.Id;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>A system whose disc images are converted: its target is a disc format, or it names one for its CDs.</summary>
    private static bool TakesDiscs(SystemInfo s) =>
        s.Category == "disc" || s.DiscTarget is not null || s.Target.Format is ".chd" or ".rvz";

    private static bool IsDiscFile(string path) =>
        KnownFormats.Descriptors.Any(d => d.HasExtension(path)) || KnownFormats.Images.Any(i => i.HasExtension(path));

    /// <summary>Formats only CDs come in (cue/bin, ccd, mds, toc, nrg), and CHDs: whatever the system, the target is a CHD.</summary>
    private static bool IsCdOnly(string path) =>
        KnownFormats.Descriptors.Any(d => d.HasExtension(path)) || KnownFormats.Chd.HasExtension(path);
}
