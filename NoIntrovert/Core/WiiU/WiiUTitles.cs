using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NoIntrovert.Core.WiiU;

public enum WiiUTitleKind { Game, Update, Dlc, Demo, Other }

/// <summary>
/// One Wii U title (a base game, an update or a DLC), found either as an
/// extracted folder (code/content/meta) or as a title folder inside a .wua.
/// </summary>
public sealed record WiiUTitle(ulong TitleId, uint Version, string Name, string SourcePath, string? WuaFolder)
{
    /// <summary>The base game's title ID: updates and DLC share its low 32 bits.</summary>
    public ulong BaseId => 0x0005000000000000UL | (TitleId & 0xFFFFFFFFUL);

    public WiiUTitleKind Kind => (TitleId >> 32) switch
    {
        0x00050000 => WiiUTitleKind.Game,
        0x0005000E => WiiUTitleKind.Update,
        0x0005000C => WiiUTitleKind.Dlc,
        0x00050002 => WiiUTitleKind.Demo,
        _ => WiiUTitleKind.Other,
    };

    /// <summary>The folder a .wua keeps this title in (Cemu's naming: 16 hex digits, _v, decimal version).</summary>
    public string ArchiveFolder => $"{TitleId:x16}_v{Version.ToString(CultureInfo.InvariantCulture)}";

    public bool FromWua => WuaFolder is not null;

    public string KindText => Kind switch
    {
        WiiUTitleKind.Game => "game",
        WiiUTitleKind.Update => "update",
        WiiUTitleKind.Dlc => "DLC",
        WiiUTitleKind.Demo => "demo",
        _ => "title",
    };

    public override string ToString() => $"{TitleId:X16} v{Version} ({KindText}{(Name.Length > 0 ? ", " + Name : "")})";
}

/// <summary>Finds Wii U titles in folders and .wua archives.</summary>
public static partial class WiiUTitles
{
    /// <summary>Whether a folder looks like an extracted title (has code/app.xml or meta/meta.xml).</summary>
    public static bool IsTitleFolder(string folder) =>
        File.Exists(Path.Combine(folder, "code", "app.xml")) || File.Exists(Path.Combine(folder, "meta", "meta.xml"));

    public static bool IsWua(string path) =>
        File.Exists(path) && Path.GetExtension(path).Equals(".wua", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Formats phase 1 can't read yet, so they can be named in a message
    /// instead of being skipped silently: encrypted NUS folders and disc images.
    /// </summary>
    public static string? UnsupportedReason(string path)
    {
        if (Directory.Exists(path) && File.Exists(Path.Combine(path, "title.tmd")))
            return "Encrypted NUS/installable title (title.tmd); decrypting these isn't supported yet";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (File.Exists(path) && ext is ".wud" or ".wux")
            return $"{ext[1..].ToUpperInvariant()} disc image; decrypting these isn't supported yet";
        if (File.Exists(path) && ext == ".wuhb")
            return "WUHB homebrew bundle; Cemu runs these directly, there is nothing to convert";
        return null;
    }

    /// <summary>
    /// The paths in <paramref name="folder"/> worth listing: title folders and
    /// .wua files, looking a few levels down but not inside a title.
    /// </summary>
    public static List<string> FindSources(string folder, bool recursive, int maxDepth = 4)
    {
        var found = new List<string>();
        if (IsTitleFolder(folder) || UnsupportedReason(folder) is not null)
        {
            found.Add(folder);
            return found;
        }
        Scan(folder, recursive ? maxDepth : 1, found);
        return found;
    }

    private static void Scan(string folder, int depth, List<string> found)
    {
        IEnumerable<string> files, dirs;
        try
        {
            files = Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            dirs = Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        found.AddRange(files.Where(f => IsWua(f) || UnsupportedReason(f) is not null));
        foreach (var dir in dirs)
        {
            if (IsTitleFolder(dir) || UnsupportedReason(dir) is not null) found.Add(dir);
            else if (depth > 1) Scan(dir, depth - 1, found);
        }
    }

    /// <summary>The titles in one source: a title folder (one title) or a .wua (any number).</summary>
    public static List<WiiUTitle> Read(string path)
    {
        if (Directory.Exists(path)) return [FromFolder(path)];
        if (IsWua(path)) return FromWua(path);
        throw new InvalidDataException($"{Path.GetFileName(path)} is neither a Wii U title folder nor a .wua archive.");
    }

    public static WiiUTitle FromFolder(string folder)
    {
        var app = TryLoad(Path.Combine(folder, "code", "app.xml"));
        var meta = TryLoad(Path.Combine(folder, "meta", "meta.xml"));
        return Describe(app, meta, folder, null)
            ?? throw new InvalidDataException($"{Path.GetFileName(folder)}: no title ID in code/app.xml or meta/meta.xml.");
    }

    public static List<WiiUTitle> FromWua(string path)
    {
        using var reader = ZArchiveReader.Open(path);
        var titles = new List<WiiUTitle>();
        foreach (var folder in reader.Children(reader.Root).Where(e => !e.IsFile))
        {
            var m = ArchiveFolderName().Match(folder.Name);
            if (!m.Success) continue;
            var app = TryLoad(reader, folder.Name + "/code/app.xml");
            var meta = TryLoad(reader, folder.Name + "/meta/meta.xml");
            var described = Describe(app, meta, path, folder.Name);
            // The folder name is what Cemu goes by; trust it over the XML if they differ.
            var id = ulong.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var version = uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            titles.Add(new WiiUTitle(id, version, described?.Name ?? "", path, folder.Name));
        }
        if (titles.Count == 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} holds no Wii U title folders (expected <titleid>_v<version>).");
        return titles;
    }

    private static WiiUTitle? Describe(XElement? app, XElement? meta, string source, string? wuaFolder)
    {
        // Like Cemu: the ID and version come from code/app.xml, the name from meta/meta.xml.
        var idText = Value(app, "title_id") ?? Value(meta, "title_id");
        if (idText is null || !ulong.TryParse(idText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            return null;
        var versionText = Value(app, "title_version") ?? Value(meta, "title_version") ?? "0";
        if (!uint.TryParse(versionText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
            version = 0;
        var name = (Value(meta, "longname_en") ?? Value(meta, "shortname_en") ?? "").ReplaceLineEndings(" ").Trim();
        return new WiiUTitle(id, version, name, source, wuaFolder);
    }

    private static string? Value(XElement? root, string name) =>
        root?.Element(name)?.Value is { Length: > 0 } v ? v : null;

    private static XElement? TryLoad(string path)
    {
        try { return File.Exists(path) ? XDocument.Load(path).Root : null; }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { return null; }
    }

    private static XElement? TryLoad(ZArchiveReader reader, string path)
    {
        var entry = reader.Find(path);
        if (entry is not { IsFile: true } || entry.Size > 1 << 20) return null;
        using var ms = new MemoryStream();
        reader.CopyFile(entry, ms);
        ms.Position = 0;
        try { return XDocument.Load(ms).Root; }
        catch (System.Xml.XmlException) { return null; }
    }

    [GeneratedRegex("^([0-9a-fA-F]{16})_v([0-9]+)$")]
    private static partial Regex ArchiveFolderName();

    /// <summary>A file name for a game's .wua: its name and base title ID, made safe for every platform.</summary>
    public static string WuaFileName(ulong baseId, string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
        clean = MultipleSpaces().Replace(clean, " ").Trim().TrimEnd('.');
        return (clean.Length > 0 ? $"{clean} [{baseId:X16}]" : $"{baseId:X16}") + ".wua";
    }

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultipleSpaces();
}
