using SharpCompress.Archives;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// What an archive holds, from its file list alone (nothing is unpacked):
/// the formats of the files inside, by name, plus Wii U title and NUS
/// folders by the files that mark them. Lets the tabs tell a zip with an
/// ISO in it from a zip of cartridge ROMs.
/// </summary>
internal static class ArchivePeek
{
    /// <summary>The formats inside <paramref name="archive"/>, or null if its list can't be read.</summary>
    public static IReadOnlyList<FormatInfo>? Inside(string archive)
    {
        List<string> names;
        try
        {
            using var opened = ArchiveFactory.OpenArchive(archive);
            names = opened.Entries.Where(e => !e.IsDirectory && e.Key is not null)
                .Select(e => e.Key!.Replace('\\', '/'))
                .ToList();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return null; // damaged, password-protected header, or a type SharpCompress can't list
        }

        // A plain .gz holds one stream, often without a stored name: "game.iso.gz" holds "game.iso".
        if (Path.GetExtension(archive).Equals(".gz", StringComparison.OrdinalIgnoreCase) && names.Count <= 1)
            names = [Path.GetFileNameWithoutExtension(archive)];
        return FormatsOf(names);
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
