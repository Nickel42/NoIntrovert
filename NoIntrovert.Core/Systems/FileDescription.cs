using NoIntrovert.Core.Cd;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Library;

namespace NoIntrovert.Core.Systems;

/// <summary>Whether a file matched a known good dump.</summary>
public enum Verification
{
    /// <summary>Not checked yet, or it couldn't be checked.</summary>
    Unknown,
    /// <summary>Checked, and no database entry matched.</summary>
    NotVerified,
    /// <summary>Byte-for-byte identical to a database entry.</summary>
    Verified,
}

/// <summary>The database a match came from, which decides the badge's tooltip.</summary>
public enum VerifyDatabase
{
    /// <summary>Redump: verified disc dumps.</summary>
    Redump,
    /// <summary>No-Intro: verified cartridge and digital dumps.</summary>
    NoIntro,
}

/// <summary>Builds the File section's "About file" line and reads verification results.</summary>
public static class FileDescription
{
    /// <summary>
    /// "BIN/CUE · 2 tracks · 657.9 MB · Disc 3 of 3": the format, track count
    /// (cue and gdi only), the size of every file in the set, and the disc
    /// number when the name has one. Never throws; unreadable parts are left out.
    /// </summary>
    public static string AboutFile(string path)
    {
        var parts = new List<string> { FormatName(path) };
        if (TrackCount(path) is { } tracks) parts.Add(tracks == 1 ? "1 track" : $"{tracks} tracks");
        if (SizeOf(path) is { } size) parts.Add(ChdJobs.Size(size));
        if (DiscText(path) is { Length: > 0 } disc) parts.Add(disc);
        return string.Join(" · ", parts);
    }

    /// <summary>The format's everyday name: "BIN/CUE", "CHD", "7z archive", "Folder"…</summary>
    public static string FormatName(string path)
    {
        if (Directory.Exists(path)) return "Folder";
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".bin.ecm")) return "BIN (ECM)";
        if (name.EndsWith(".nkit.iso")) return "NKit ISO";
        if (name.EndsWith(".tar.gz")) return "tar.gz archive";
        return Path.GetExtension(name) switch
        {
            ".cue" => "BIN/CUE",
            ".gdi" => "GDI",
            ".ccd" => "CloneCD (CCD/IMG)",
            ".mds" => "Alcohol (MDS/MDF)",
            ".toc" => "TOC/BIN",
            ".7z" => "7z archive",
            ".zip" => "ZIP archive",
            ".rar" => "RAR archive",
            ".001" => "Split file",
            "" => "File",
            var ext => ext.TrimStart('.').ToUpperInvariant(),
        };
    }

    /// <summary>How many tracks a cue sheet or gdi lists; null for other formats or an unreadable file.</summary>
    public static int? TrackCount(string path)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".cue":
                    var count = CueSheet.Load(path).Tracks.Count();
                    return count > 0 ? count : null;
                case ".gdi":
                    var first = File.ReadLines(path).FirstOrDefault();
                    return int.TryParse(first?.Trim(), out var n) && n > 0 ? n : null;
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException) { return null; }
    }

    /// <summary>The size of the whole set (a cue and its bins), or of a folder's files.</summary>
    public static long? SizeOf(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            if (!File.Exists(path)) return null;
            return DiscFiles.SetFiles(path).Where(File.Exists).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// "Disc 3 of 3" when the folder holds the other discs (same title, same
    /// extension), "Disc 3" when it doesn't, "" when the name has no disc marker.
    /// </summary>
    public static string DiscText(string path)
    {
        var (title, disc) = LibraryScanner.TitleOf(path);
        if (disc.Length == 0 || !int.TryParse(disc.AsSpan(5), out var number)) return disc;
        try
        {
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is not { } dir) return disc;
            var ext = Path.GetExtension(path);
            var highest = Directory.EnumerateFiles(dir, "*" + ext)
                .Where(f => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase))
                .Select(LibraryScanner.TitleOf)
                .Where(t => string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase) && t.Disc.Length > 5)
                .Select(t => int.TryParse(t.Disc.AsSpan(5), out var n) ? n : 0)
                .DefaultIfEmpty(number)
                .Max();
            return highest > 1 && highest >= number ? $"{disc} of {highest}" : disc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return disc; }
    }

    /// <summary>
    /// What a Verify &amp; match result means for the badge. A match whose
    /// name differs is still the same bytes, so it counts as verified.
    /// </summary>
    public static Verification VerificationOf(MatchResult? match) => match?.Status switch
    {
        MatchStatus.Verified or MatchStatus.NameDiffers => Verification.Verified,
        MatchStatus.NotInDatabase or MatchStatus.Damaged => Verification.NotVerified,
        _ => Verification.Unknown,
    };

    /// <summary>
    /// Which database vouches for a system's games: Redump for disc systems
    /// (and any system whose DATs are Redump's), No-Intro for the rest.
    /// </summary>
    public static VerifyDatabase DatabaseFor(string? systemId)
    {
        var system = TargetFormats.ById(systemId);
        if (system?.Dats is { Count: > 0 } dats)
            return dats.Any(d => d.StartsWith("redump/", StringComparison.OrdinalIgnoreCase)) ? VerifyDatabase.Redump : VerifyDatabase.NoIntro;
        return system?.Category == "disc" ? VerifyDatabase.Redump : VerifyDatabase.NoIntro;
    }
}
