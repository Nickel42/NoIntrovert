using NoIntrovert.Core.Archives;

namespace NoIntrovert.Core.Formats;

/// <summary>The formats each handler claims, and the sets of them each part of the app takes.</summary>
public static class KnownFormats
{
    public static readonly IReadOnlyList<FormatInfo> Descriptors = ByExtension(DiscFiles.Descriptors);
    public static readonly IReadOnlyList<FormatInfo> Images = ByExtension(DiscFiles.Images);
    public static readonly FormatInfo SplitFile = new("split", "Split file (.001)", ".001");
    public static readonly IReadOnlyList<FormatInfo> Archives = ByExtension(ArchiveExtractor.Extensions);
    public static readonly FormatInfo Chd = new("chd", "CHD", ".chd");

    /// <summary>
    /// Cartridge, handheld and computer ROM files (and the other per-system
    /// files the target registry lists, such as .nsp or .wad), by extension.
    /// Disc images, archives and Wii U files have handlers of their own.
    /// The rules (<see cref="TargetResolver"/>) say what each one becomes.
    /// </summary>
    public static readonly IReadOnlyList<FormatInfo> Cartridges = ByExtension(
        TargetFormats.All.SelectMany(s => s.Extensions)
            .Except([.. DiscFiles.Descriptors, .. DiscFiles.Images, .. ArchiveExtractor.Extensions, ".chd", ".wua", ".wud", ".wux", ".wuhb"],
                StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public static readonly FormatInfo Wua = new("wua", "Wii U archive (.wua)", ".wua");
    public static readonly FormatInfo WiiUDisc = new("wiiu-disc", "Wii U disc image", ".wud", ".wux");
    public static readonly FormatInfo WiiUFolder = new("wiiu-folder", "Wii U title folder");
    public static readonly FormatInfo Nus = new("nus", "Wii U NUS title");

    /// <summary>A PS Vita game as a NoNpDrm folder (sce_sys/param.sfo), zipped for Vita3K (FR-10).</summary>
    public static readonly FormatInfo VitaFolder = new("vita-folder", "PS Vita game folder");

    /// <summary>A PS Vita PlayStation Store package (game, DLC, update or PSM), told from PS3/PSP ones by its header (lib-psvita-kit).</summary>
    public static readonly FormatInfo VitaPkg = new("vita-pkg", "PS Vita package (.pkg)", ".pkg");

    /// <summary>A decrypted PS3 disc game as a folder (PS3_GAME/PARAM.SFO), named "Game.ps3" for ES-DE.</summary>
    public static readonly FormatInfo Ps3Folder = new("ps3-folder", "PS3 game folder");

    /// <summary>
    /// An Xbox 360 package (CON, LIVE or PIRS): an XBLA game, DLC, a title update or a Games on Demand header. Such
    /// files usually have no extension (they're named by content ID), so they're told by their first bytes.
    /// </summary>
    public static readonly FormatInfo XboxPackage = new("x360-package", "Xbox 360 package");

    /// <summary>Wii U inputs that are recognised only to say why they can't be used (NUS without a ticket, WUHB).</summary>
    public static readonly FormatInfo WiiUUnsupported = new("wiiu-unsupported", "Unsupported Wii U input", ".wuhb");

    /// <summary>What Convert (and the library tests) take: disc images, their descriptors, archives holding them, split files.</summary>
    public static readonly IReadOnlyList<FormatInfo> DiscInputs = [.. Descriptors, .. Images, .. Archives, SplitFile];

    /// <summary>What Extract takes: CHDs, and the GameCube/Wii containers that decode back to ISO.</summary>
    public static readonly IReadOnlyList<FormatInfo> Extractable =
        [Chd, .. NintendoDisc.Decodable.Select(e => Images.First(f => f.HasExtension("x" + e)))];

    /// <summary>What the Archive tab converts and extracts.</summary>
    public static readonly IReadOnlyList<FormatInfo> ConvertibleArchives =
        [.. ArchiveJobs.ConvertibleExtensions.Select(e => Archives.First(f => f.HasExtension("x" + e)))];

    public static readonly IReadOnlyList<FormatInfo> WiiUInputs = [Wua, WiiUDisc, WiiUFolder, Nus, WiiUUnsupported];

    /// <summary>What the Wii U tab can build from once unpacked from an archive.</summary>
    public static readonly IReadOnlyList<FormatInfo> WiiUBuildable = [Wua, WiiUDisc, WiiUFolder, Nus];

    /// <summary>
    /// A disc input for Convert. An archive counts when it holds one, or when
    /// its list couldn't be read (the job then says what's wrong), but not
    /// when it holds a Wii U title, whose files can look like disc images.
    /// </summary>
    public static bool TakesDisc(FileSet set) =>
        DiscInputs.Contains(set.Format) && (!Archives.Contains(set.Format) || set.Inside is null
            || (set.Holds(DiscInputs) && !set.Holds([WiiUFolder, Nus])));

    /// <summary>A Wii U input, or a zip/7z/tar holding a Wii U game.</summary>
    public static bool TakesWiiU(FileSet set) =>
        WiiUInputs.Contains(set.Format) || (Archives.Contains(set.Format) && set.Holds(WiiUBuildable));

    /// <summary>Every format recognised from a file name alone (what an archive's list can show).</summary>
    private static readonly IReadOnlyList<FormatInfo> ByNameFormats = [.. Descriptors, .. Images, Chd, .. Archives, Wua, WiiUDisc, WiiUUnsupported, .. Cartridges];

    /// <summary>A format by its <see cref="FormatInfo.Id"/>, for indexes saved to disk.</summary>
    public static FormatInfo? ById(string id) => AllById.GetValueOrDefault(id);

    private static readonly Dictionary<string, FormatInfo> AllById =
        ((FormatInfo[])[.. ByNameFormats, SplitFile, WiiUFolder, Nus, VitaFolder, VitaPkg, XboxPackage, ConvertTargets.OtherFile, ConvertTargets.PackedFolder])
            .GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>The format a file name says, without looking at the file (it may be inside an archive).</summary>
    public static FormatInfo? ByName(string fileName) =>
        ArchiveExtractor.IsFirstSplitPart(fileName) ? SplitFile : ByNameFormats.FirstOrDefault(f => f.HasExtension(fileName));

    /// <summary>
    /// Files that are already compressed (or encrypted), so packing them into
    /// 7z or zip again would cost time and save next to nothing.
    /// </summary>
    public static readonly IReadOnlyList<string> AlreadyCompressed =
    [
        ".chd", ".rvz", ".wia", ".gcz", ".cso", ".zso", ".jso", ".dax", ".isz", ".pbp", ".wua", ".wux", ".zar",
        ".7z", ".zip", ".rar", ".gz", ".tgz", ".xz", ".txz", ".bz2", ".zst", ".lz4", ".lzma", ".cab",
        ".jpg", ".jpeg", ".png", ".webp", ".mp3", ".ogg", ".opus", ".flac", ".mp4", ".mkv", ".webm",
    ];

    public static bool IsAlreadyCompressed(string path) =>
        AlreadyCompressed.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>File-picker patterns ("*.cue", ...) for <paramref name="formats"/>, in their order.</summary>
    public static List<string> Patterns(IEnumerable<FormatInfo> formats) =>
        formats.SelectMany(f => f.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).Select(e => "*" + e).ToList();

    private static List<FormatInfo> ByExtension(IEnumerable<string> extensions) =>
        extensions.Select(e => new FormatInfo(e.TrimStart('.'), e.TrimStart('.').ToUpperInvariant(), e)).ToList();
}

/// <summary>Claims single files by extension; subclasses say which files belong with each one.</summary>
public abstract class ExtensionHandler(string id, int priority, IReadOnlyList<FormatInfo> formats) : IFormatHandler
{
    public string Id { get; } = id;
    public int Priority { get; } = priority;
    public IReadOnlyList<FormatInfo> Formats { get; } = formats;

    public virtual FormatInfo? Recognize(string file) => Formats.FirstOrDefault(f => f.HasExtension(file));

    public virtual FormatInfo? RecognizeFolder(string folder) => null;

    /// <summary>The files that make up the input <paramref name="file"/>, itself first.</summary>
    protected virtual IEnumerable<string> MembersOf(string file) => [file];

    public virtual IEnumerable<FileSet> Claim(ScanSet set)
    {
        var claimed = new List<FileSet>();
        foreach (var file in set.UnclaimedFiles.ToList())
        {
            if (set.IsClaimed(file) || Recognize(file) is not { } format) continue;
            var members = MembersOf(file).Select(ScanSet.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var m in members) set.Claim(m);
            set.Claim(file);
            claimed.Add(new FileSet(file, members, format));
        }
        return claimed;
    }
}

/// <summary>
/// cue, gdi, toc, ccd and mds: each claims the images it references. All
/// references are gathered first, so a descriptor another one uses is never
/// an input of its own.
/// </summary>
public sealed class DiscDescriptorHandler() : ExtensionHandler("disc-descriptor", 300, KnownFormats.Descriptors)
{
    protected override IEnumerable<string> MembersOf(string file) => DiscFiles.SetFiles(file);

    public override IEnumerable<FileSet> Claim(ScanSet set)
    {
        var descriptors = set.UnclaimedFiles.Select(f => (File: f, Format: Recognize(f)))
            .Where(d => d.Format is not null)
            .Select(d => new FileSet(d.File, MembersOf(d.File).Select(ScanSet.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), d.Format!))
            .ToList();
        var referenced = new HashSet<string>(descriptors.SelectMany(d => d.Members.Skip(1)), StringComparer.OrdinalIgnoreCase);
        var claimed = descriptors.Where(d => !referenced.Contains(d.Primary)).ToList();
        foreach (var d in descriptors)
            foreach (var m in d.Members) set.Claim(m);
        return claimed;
    }
}

/// <summary>name.ext.001 and the parts that follow it.</summary>
public sealed class SplitFileHandler() : ExtensionHandler("split-file", 200, [KnownFormats.SplitFile])
{
    public override FormatInfo? Recognize(string file) => ArchiveExtractor.IsFirstSplitPart(file) ? KnownFormats.SplitFile : null;
    protected override IEnumerable<string> MembersOf(string file) => ArchiveExtractor.SplitParts(file);
}

/// <summary>Single-file disc images (iso, cso, bin, ...) and the GameCube/Wii containers.</summary>
public sealed class ImageHandler() : ExtensionHandler("disc-image", 100, KnownFormats.Images);

public sealed class ChdHandler() : ExtensionHandler("chd", 100, [KnownFormats.Chd]);

/// <summary>Cartridge and handheld ROMs (see <see cref="KnownFormats.Cartridges"/>): listed, never converted.</summary>
public sealed class CartridgeHandler() : ExtensionHandler("cartridge", 30, KnownFormats.Cartridges);

/// <summary>zip, 7z, tar and gz, each looked into for what it holds. RAR is never recognised.</summary>
public sealed class ArchiveHandler() : ExtensionHandler("archive", 50, KnownFormats.Archives)
{
    public override IEnumerable<FileSet> Claim(ScanSet set) =>
        base.Claim(set).Select(s => s with { Inside = ArchivePeek.Inside(s.Primary) }).ToList();
}

/// <summary>
/// Xbox 360 packages, by their CON, LIVE or PIRS header (lib-xiso-kit). Only files no other handler took are looked
/// at, so the scan reads the first bytes of leftovers only. A Games on Demand header claims the "&lt;name&gt;.data"
/// folder of disc data beside it.
/// </summary>
public sealed class Xbox360PackageHandler() : ExtensionHandler("x360-package", 10, [KnownFormats.XboxPackage])
{
    public override FormatInfo? Recognize(string file)
    {
        // Packages are named by content ID (no extension; a title update's looks like ".00000000000O2") or given
        // one people pick; never a known format's, and the usual pictures, documents and media aren't opened.
        if (KnownFormats.ByName(Path.GetFileName(file)) is not null || NotPackages.Contains(Path.GetExtension(file))) return null;
        return LibXisoKit.Stfs.StfsHeader.IsPackage(file) ? KnownFormats.XboxPackage : null;
    }

    private static readonly HashSet<string> NotPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".svg", ".txt", ".nfo", ".md", ".pdf", ".htm", ".html",
        ".xml", ".json", ".ini", ".cfg", ".log", ".dat", ".sfv", ".md5", ".sha1", ".m3u", ".srm", ".sav", ".state",
        ".mp3", ".mp4", ".mkv", ".avi", ".ogg", ".flac", ".wav", ".lnk", ".url", ".exe", ".dll", ".db", ".rar",
    };

    protected override IEnumerable<string> MembersOf(string file)
    {
        var data = file + ".data";
        return Directory.Exists(data) ? [file, data] : [file];
    }
}

/// <summary>
/// PS Vita games as NoNpDrm folders (claimed whole, never looked inside), and PS Vita .pkg files. A .pkg is
/// opened to tell it from a PS3 or PSP package, which this handler leaves alone.
/// </summary>
public sealed class VitaHandler() : ExtensionHandler("vita", 400, [KnownFormats.VitaFolder, KnownFormats.VitaPkg])
{
    public override FormatInfo? Recognize(string file) =>
        KnownFormats.VitaPkg.HasExtension(file) && Vita.VitaJobs.IsVitaPackage(file) ? KnownFormats.VitaPkg : null;

    public override FormatInfo? RecognizeFolder(string folder) => VitaGame.IsGameFolder(folder) ? KnownFormats.VitaFolder : null;

    public override IEnumerable<FileSet> Claim(ScanSet set)
    {
        var claimed = base.Claim(set).ToList();
        foreach (var folder in set.UnclaimedFolders.ToList())
        {
            if (RecognizeFolder(folder) is not { } format) continue;
            set.Claim(folder);
            claimed.Add(new FileSet(folder, [folder], format));
        }
        return claimed;
    }
}

/// <summary>PS3 games as decrypted disc folders: claimed whole, never looked inside.</summary>
public sealed class Ps3Handler() : ExtensionHandler("ps3", 400, [KnownFormats.Ps3Folder])
{
    public override FormatInfo? Recognize(string file) => null;

    public override FormatInfo? RecognizeFolder(string folder) => IsGameFolder(folder) ? KnownFormats.Ps3Folder : null;

    /// <summary>A disc game's folder: PS3_GAME/PARAM.SFO or PS3_DISC.SFB inside it.</summary>
    public static bool IsGameFolder(string folder) =>
        File.Exists(Path.Combine(folder, "PS3_GAME", "PARAM.SFO")) || File.Exists(Path.Combine(folder, "PS3_DISC.SFB"));

    public override IEnumerable<FileSet> Claim(ScanSet set)
    {
        var claimed = new List<FileSet>();
        foreach (var folder in set.UnclaimedFolders.ToList())
        {
            if (RecognizeFolder(folder) is not { } format) continue;
            set.Claim(folder);
            claimed.Add(new FileSet(folder, [folder], format));
        }
        return claimed;
    }
}

/// <summary>
/// Wii U titles: extracted and NUS folders (claimed whole, never looked
/// inside), .wua files and WUD/WUX disc images.
/// </summary>
public sealed class WiiUHandler() : ExtensionHandler("wiiu", 400, KnownFormats.WiiUInputs)
{
    public override FormatInfo? Recognize(string file)
    {
        if (WiiUTitles.IsWua(file)) return KnownFormats.Wua;
        if (WiiUEncrypted.IsDiscImage(file)) return KnownFormats.WiiUDisc;
        if (File.Exists(file) && WiiUTitles.UnsupportedReason(file) is not null) return KnownFormats.WiiUUnsupported;
        return null;
    }

    public override FormatInfo? RecognizeFolder(string folder)
    {
        if (WiiUEncrypted.IsNusFolder(folder)) return KnownFormats.Nus;
        if (WiiUTitles.IsTitleFolder(folder)) return KnownFormats.WiiUFolder;
        if (WiiUTitles.UnsupportedReason(folder) is not null) return KnownFormats.WiiUUnsupported;
        return null;
    }

    public override IEnumerable<FileSet> Claim(ScanSet set)
    {
        var claimed = base.Claim(set).ToList();
        foreach (var folder in set.UnclaimedFolders.ToList())
        {
            if (RecognizeFolder(folder) is not { } format) continue;
            set.Claim(folder);
            claimed.Add(new FileSet(folder, [folder], format));
        }
        return claimed;
    }
}
