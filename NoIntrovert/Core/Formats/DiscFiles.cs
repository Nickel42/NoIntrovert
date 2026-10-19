using System.Text.RegularExpressions;
using NoIntrovert.Core.Cd;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Knows which files belong together: what a cue/gdi/toc/ccd/mds needs,
/// which split parts and archive volumes follow a first one, and which files
/// in a folder are the ones to convert (the "primary" inputs).
/// </summary>
internal static partial class DiscFiles
{
    public static readonly string[] Descriptors = [".cue", ".gdi", ".toc", ".ccd", ".mds"];
    public static readonly string[] Images = [.. new[] { ".iso", ".cso", ".zso", ".isz", ".pbp", ".ecm", ".bin", ".img", ".nrg" }
        .Concat(NintendoDisc.Containers)];

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return Descriptors.Contains(ext, StringComparer.OrdinalIgnoreCase)
            || Images.Contains(ext, StringComparer.OrdinalIgnoreCase)
            || ArchiveExtractor.IsArchive(path)
            || ArchiveExtractor.IsFirstSplitPart(path);
    }

    /// <summary>Every file that makes up this input, including itself (used for deleting originals).</summary>
    public static List<string> SetFiles(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var dir = Path.GetDirectoryName(path)!;
        var files = new List<string> { path };
        try
        {
            switch (ext)
            {
                case ".cue":
                    foreach (var name in CueReferences(path))
                        AddWithEcm(files, Path.Combine(dir, name));
                    break;
                case ".gdi":
                    files.AddRange(GdiReferences(path).Select(n => Path.Combine(dir, n)).Where(File.Exists));
                    break;
                case ".toc":
                    files.AddRange(TocReferences(path).Select(n => Path.Combine(dir, n)).Where(File.Exists));
                    break;
                case ".ccd":
                    files = CcdConverter.SetFiles(path).ToList();
                    break;
                case ".mds":
                    files = MdsConverter.SetFiles(path).ToList();
                    break;
                case ".001":
                    files = ArchiveExtractor.SplitParts(path);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException)
        {
            // A broken descriptor still counts as just itself.
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddWithEcm(List<string> files, string path)
    {
        if (File.Exists(path)) files.Add(path);
        else if (File.Exists(path + ".ecm")) files.Add(path + ".ecm");
    }

    /// <summary>
    /// Picks the files to convert from a set (a folder scan or an unpacked
    /// archive): descriptors win over the images they reference, and a .001
    /// over its later parts.
    /// </summary>
    public static List<string> SelectPrimaries(IEnumerable<string> candidates)
    {
        // Compare full paths: a folder given with "/" or mixed separators must
        // still match the members that SetFiles builds with Path.Combine.
        var all = candidates.Where(IsSupported).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in all)
        {
            // Later split parts are never primaries.
            if (ArchiveExtractor.IsSecondaryVolume(file))
            {
                claimed.Add(file);
                continue;
            }
            var ext = Path.GetExtension(file);
            if (Descriptors.Contains(ext, StringComparer.OrdinalIgnoreCase) || ext is ".001")
                foreach (var member in SetFiles(file).Skip(1))
                    claimed.Add(Normalize(member));
        }
        // A lone "game.bin.ecm" next to "game.cue" is claimed through the cue above;
        // a loose .img next to a .ccd of the same name too.
        return all.Where(f => !claimed.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    public static IEnumerable<string> CueReferences(string cuePath) =>
        CueSheet.Load(cuePath).Files.Select(f => f.Name);

    public static IEnumerable<string> GdiReferences(string gdiPath)
    {
        foreach (var line in File.ReadLines(gdiPath).Skip(1))
        {
            var m = GdiLine().Match(line);
            if (m.Success) yield return m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["n"].Value;
        }
    }

    public static IEnumerable<string> TocReferences(string tocPath)
    {
        foreach (Match m in TocFile().Matches(File.ReadAllText(tocPath)))
            yield return m.Groups[1].Value;
    }

    [GeneratedRegex("""^\s*\d+\s+\d+\s+\d+\s+\d+\s+(?:"(?<q>[^"]+)"|(?<n>\S+))""")]
    private static partial Regex GdiLine();

    [GeneratedRegex(""""(?:DATAFILE|FILE)\s+"([^"]+)"""")]
    private static partial Regex TocFile();
}
