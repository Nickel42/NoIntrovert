using SharpCompress.Archives;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// What an archive holds, from its file list alone (nothing is unpacked):
/// the formats of the files inside, by name, plus Wii U title and NUS
/// folders by the files that mark them. Lets the tabs tell a zip with an
/// ISO in it from a zip of cartridge ROMs.
/// </summary>
/// <summary>One file in an archive, and how it's compressed there (see <see cref="ArchivePeek.Members"/>).</summary>
public sealed record ArchiveMember(string Name, long Size, string Method);

internal static class ArchivePeek
{
    // What unchanged archives were seen to hold (seeded from the library index), so a rescan doesn't open them again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, long Ticks, IReadOnlyList<string> Ids)> Known =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records what <paramref name="archive"/> holds while it keeps this size and date.</summary>
    public static void Remember(string archive, long size, long ticks, IReadOnlyList<string> formatIds) =>
        Known[archive] = (size, ticks, formatIds);

    /// <summary>The formats inside <paramref name="archive"/>, or null if its list can't be read.</summary>
    public static IReadOnlyList<FormatInfo>? Inside(string archive)
    {
        if (Known.TryGetValue(archive, out var known))
        {
            if (Unchanged(archive, known.Size, known.Ticks))
                return known.Ids.Select(KnownFormats.ById).OfType<FormatInfo>().ToList();
            Known.TryRemove(archive, out _);
        }
        return Members(archive) is { } members ? FormatsOf(members.Select(m => m.Name).ToList()) : null;
    }

    // Member lists read this session, so the rules (FR-3) don't open an archive the scan just listed.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, long Ticks, IReadOnlyList<ArchiveMember> Members)> Listed =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The files in <paramref name="archive"/> (with / separators) and how
    /// each is compressed, or null if its list can't be read. Nothing is unpacked.
    /// </summary>
    public static IReadOnlyList<ArchiveMember>? Members(string archive)
    {
        if (Listed.TryGetValue(archive, out var listed) && Unchanged(archive, listed.Size, listed.Ticks)) return listed.Members;
        List<ArchiveMember> members;
        long size, ticks;
        try
        {
            var info = new FileInfo(archive);
            (size, ticks) = (info.Length, info.LastWriteTimeUtc.Ticks);
            using var opened = ArchiveFactory.OpenArchive(archive);
            members = opened.Entries.Where(e => !e.IsDirectory && e.Key is not null)
                .Select(e => new ArchiveMember(e.Key!.Replace('\\', '/'), e.Size, MethodOf(e)))
                .ToList();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return null; // damaged, password-protected header, or a type SharpCompress can't list
        }

        // A plain .gz holds one stream, often without a stored name: "game.iso.gz" holds "game.iso".
        if (Path.GetExtension(archive).Equals(".gz", StringComparison.OrdinalIgnoreCase) && members.Count <= 1)
            members = [new ArchiveMember(Path.GetFileNameWithoutExtension(archive), members.FirstOrDefault()?.Size ?? 0, "GZip")];
        Listed[archive] = (size, ticks, members);
        return members;
    }

    /// <summary>
    /// SharpCompress's name for a member's method: "LZMA" (LZMA and LZMA2 alike),
    /// "Deflate", "None" (stored), "PPMd"… and "Unknown" for one it can't
    /// decode (a 7z made with zstd), which counts as not LZMA either way.
    /// </summary>
    private static string MethodOf(IArchiveEntry entry)
    {
        try { return entry.CompressionType.ToString(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException)) { return "Unknown"; }
    }

    private static bool Unchanged(string path, long size, long ticks)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == ticks;
        }
        catch (IOException) { return false; }
    }

    /// <summary>The formats a list of archive entry names (with / separators) holds.</summary>
    public static List<FormatInfo> FormatsOf(IReadOnlyList<string> names)
    {
        var found = names.Select(n => KnownFormats.ByName(Path.GetFileName(n))).OfType<FormatInfo>().ToList();
        static bool Ends(string name, string tail) =>
            name.Equals(tail, StringComparison.OrdinalIgnoreCase) || name.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase);
        if (names.Any(n => Ends(n, "code/app.xml") || Ends(n, "meta/meta.xml"))) found.Add(KnownFormats.WiiUFolder);
        if (names.Any(n => Ends(n, "title.tmd")) && names.Any(n => Ends(n, "title.tik"))) found.Add(KnownFormats.Nus);
        return found.Distinct().ToList();
    }
}
