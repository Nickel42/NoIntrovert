using NoIntrovert.Core.Archives;
using NoIntrovert.Core.WiiU;

namespace NoIntrovert.Core.Formats;

/// <summary>The formats each handler claims, and the sets of them each part of the app takes.</summary>
public static class KnownFormats
{
    public static readonly IReadOnlyList<FormatInfo> Descriptors = ByExtension(DiscFiles.Descriptors);
    public static readonly IReadOnlyList<FormatInfo> Images = ByExtension(DiscFiles.Images);
    public static readonly FormatInfo SplitFile = new("split", "Split file (.001)", ".001");
    public static readonly IReadOnlyList<FormatInfo> Archives = ByExtension(ArchiveExtractor.Extensions);
    public static readonly FormatInfo Chd = new("chd", "CHD", ".chd");

    public static readonly FormatInfo Wua = new("wua", "Wii U archive (.wua)", ".wua");
    public static readonly FormatInfo WiiUDisc = new("wiiu-disc", "Wii U disc image", ".wud", ".wux");
    public static readonly FormatInfo WiiUFolder = new("wiiu-folder", "Wii U title folder");
    public static readonly FormatInfo Nus = new("nus", "Wii U NUS title");

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

/// <summary>zip, 7z, tar and gz. RAR is never recognised.</summary>
public sealed class ArchiveHandler() : ExtensionHandler("archive", 50, KnownFormats.Archives);

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
