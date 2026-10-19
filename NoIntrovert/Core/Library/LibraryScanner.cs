using System.Text.RegularExpressions;
using LibChdman;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.WiiU;

namespace NoIntrovert.Core.Library;

/// <summary>
/// Looks through the library folders and works out, for each input, which
/// system it's for, its title and which game it belongs to. Inputs whose
/// files haven't changed since the last scan (same size and date) are taken
/// from the old index without being read again, archives included.
/// </summary>
public static partial class LibraryScanner
{
    public static LibraryIndex Scan(IReadOnlyList<string> roots, LibraryIndex? previous, IProgress<int>? progress, CancellationToken token)
    {
        var old = (previous?.Items ?? []).GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Archives that haven't changed keep what they were seen to hold, so they're not opened again.
        foreach (var item in old.Values.Where(i => i.Inside is not null))
            ArchivePeek.Remember(item.Path, item.Size, item.ModifiedTicks, item.Inside!);

        var index = new LibraryIndex { ScannedUtc = DateTime.UtcNow };
        var settings = new AppSettings();
        Chdman? chdman = null;
        try
        {
            foreach (var root in roots.Select(ScanSet.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(root))
                {
                    index.MissingFolders.Add(root);
                    continue;
                }
                List<FileSet> sets;
                try { sets = FormatRegistry.Default.Scan(root, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    index.MissingFolders.Add(root);
                    continue;
                }
                foreach (var set in sets)
                {
                    token.ThrowIfCancellationRequested();
                    var (size, ticks) = Stamp(set);
                    if (old.TryGetValue(set.Primary, out var known) && known.Size == size && known.ModifiedTicks == ticks
                        && known.FormatId == set.Format.Id && string.Equals(known.Root, root, StringComparison.OrdinalIgnoreCase))
                        index.Items.Add(known);
                    else
                        index.Items.Add(Describe(set, root, size, ticks, settings, ref chdman));
                    progress?.Report(index.Items.Count);
                }
            }
        }
        finally
        {
            chdman?.Dispose();
        }
        return index;
    }

    /// <summary>Total size and newest change of an input's files (a cue and its bins, a title folder's files).</summary>
    private static (long Size, long Ticks) Stamp(FileSet set)
    {
        long size = 0, ticks = 0;
        foreach (var m in set.Members)
        {
            try
            {
                if (Directory.Exists(m))
                {
                    var dir = new DirectoryInfo(m);
                    foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        size += f.Length;
                        ticks = Math.Max(ticks, f.LastWriteTimeUtc.Ticks);
                    }
                }
                else
                {
                    var f = new FileInfo(m);
                    if (!f.Exists) continue;
                    size += f.Length;
                    ticks = Math.Max(ticks, f.LastWriteTimeUtc.Ticks);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (size, ticks);
    }

    internal static LibraryItem Describe(FileSet set, string root, long size, long ticks, AppSettings settings, ref Chdman? chdman)
    {
        var system = SystemOf(set, root, ref chdman);
        var (title, disc) = TitleOf(set.Primary);
        var key = $"{system}|{title}";

        // Wii U: base game, update and DLC share the low half of their title ID, so they're one game.
        if (system == "Wii U" && WiiUIdentity(set) is { } wiiu)
        {
            key = $"Wii U|{wiiu.BaseId:X16}";
            if (wiiu.Name.Length > 0) title = wiiu.Name;
            disc = "";
        }
        var target = ConvertTargets.For(set, settings).Default.Kind;
        return new LibraryItem(set.Primary, set.Members, set.Format.Id, set.Inside?.Select(f => f.Id).ToList(), size, ticks,
            root, system, title, key.ToLowerInvariant(), disc, target);
    }

    // ---- Titles ----

    /// <summary>
    /// The game's title from a file name, with disc markers taken off (and
    /// returned separately), so "Game (USA) (Disc 2).cue" is disc 2 of
    /// "Game (USA)". A generic name (disc.gdi, track01.bin) uses its folder's.
    /// </summary>
    public static (string Title, string Disc) TitleOf(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var stem = Directory.Exists(path) ? name : Path.GetFileNameWithoutExtension(name);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) || stem.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        if (GenericName().IsMatch(stem) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is { } parent)
            stem = Path.GetFileName(parent);
        var disc = "";
        var m = DiscMarker().Match(stem);
        if (m.Success)
        {
            disc = $"Disc {int.Parse(m.Groups["n"].Value)}";
            stem = stem.Remove(m.Index, m.Length);
        }
        return (Spaces().Replace(stem, " ").Trim(), disc);
    }

    [GeneratedRegex(@"^(disc|disk|game|image|rom|default|cd|dvd|track\s*0*1|\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericName();

    [GeneratedRegex(@"\s*(?:[\(\[](?:disc|disk|cd|dvd)\s*(?<n>\d+)(?:\s*of\s*\d+)?[\)\]]|-\s*(?:disc|disk|cd)\s*(?<n>\d+)\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex DiscMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // ---- Systems ----

    private static readonly Dictionary<string, string> CartridgeSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        [".nes"] = "NES", [".fds"] = "NES", [".unf"] = "NES", [".sfc"] = "SNES", [".smc"] = "SNES",
        [".gb"] = "Game Boy", [".gbc"] = "Game Boy Color", [".gba"] = "Game Boy Advance",
        [".nds"] = "Nintendo DS", [".dsi"] = "Nintendo DS", [".3ds"] = "Nintendo 3DS", [".cia"] = "Nintendo 3DS",
        [".n64"] = "Nintendo 64", [".z64"] = "Nintendo 64", [".v64"] = "Nintendo 64",
        [".md"] = "Genesis", [".gen"] = "Genesis", [".smd"] = "Genesis", [".sms"] = "Master System", [".gg"] = "Game Gear",
        [".32x"] = "32X", [".pce"] = "PC Engine", [".sgx"] = "PC Engine", [".a26"] = "Atari 2600", [".a52"] = "Atari 5200",
        [".a78"] = "Atari 7800", [".lnx"] = "Lynx", [".ws"] = "WonderSwan", [".wsc"] = "WonderSwan Color",
        [".ngp"] = "Neo Geo Pocket", [".vb"] = "Virtual Boy", [".min"] = "Pokémon Mini", [".col"] = "ColecoVision",
        [".int"] = "Intellivision", [".vec"] = "Vectrex", [".j64"] = "Jaguar",
    };

    /// <summary>Folder names people use for each system, compared without spaces, dashes or case.</summary>
    private static readonly (string System, string[] Names)[] FolderNames =
    [
        ("PlayStation 2", ["ps2", "playstation2", "sonyplaystation2"]),
        ("PlayStation", ["ps1", "psx", "psone", "playstation", "playstation1", "sonyplaystation"]),
        ("PSP", ["psp", "playstationportable"]),
        ("Saturn", ["saturn", "segasaturn", "ss"]),
        ("Dreamcast", ["dreamcast", "dc", "segadreamcast"]),
        ("Sega CD", ["segacd", "megacd", "segamegacd"]),
        ("GameCube", ["gamecube", "gc", "ngc", "nintendogamecube"]),
        ("Wii U", ["wiiu", "nintendowiiu"]),
        ("Wii", ["wii", "nintendowii"]),
        ("PC Engine CD", ["pcenginecd", "turbografxcd", "tg16cd", "pcecd"]),
        ("Neo Geo CD", ["neogeocd"]),
        ("3DO", ["3do"]),
        ("NES", ["nes", "famicom", "nintendoentertainmentsystem"]),
        ("SNES", ["snes", "superfamicom", "supernintendo", "sfc"]),
        ("Nintendo 64", ["n64", "nintendo64"]),
        ("Genesis", ["genesis", "megadrive", "segagenesis", "md"]),
        ("Game Boy Advance", ["gba", "gameboyadvance"]),
        ("Game Boy Color", ["gbc", "gameboycolor"]),
        ("Game Boy", ["gb", "gameboy"]),
        ("Nintendo DS", ["nds", "nintendods"]),
        ("Xbox", ["xbox"]),
    ];

    /// <summary>
    /// The system: from the file itself where that's quick and certain (Wii U
    /// formats, GameCube/Wii and PSP disc headers, GD-ROM CHDs, cartridge
    /// extensions), else from a folder name on the way up to the library
    /// folder ("PS1", "Saturn"…), else "Unknown".
    /// </summary>
    public static string SystemOf(FileSet set, string root, ref Chdman? chdman)
    {
        var f = set.Format;
        var ext = Path.GetExtension(set.Primary).ToLowerInvariant();
        if (KnownFormats.WiiUInputs.Contains(f) || set.Holds(KnownFormats.WiiUBuildable)) return "Wii U";
        if (CartridgeSystems.TryGetValue(ext, out var cart)) return cart;
        if (f.Id == "gdi") return "Dreamcast";
        var folder = FromFolders(set.Primary, root);
        if (ext is ".iso" or ".gcm" && File.Exists(set.Primary))
        {
            var console = ConsoleOfDisc(set.Primary);
            if (console is not null) return console;
            if (IsoProbe.LooksLikeDvd(set.Primary, out var why) && why == "PSP UMD image") return "PSP";
        }
        if (ext is ".cso" or ".zso") return folder == "PlayStation 2" ? folder : "PSP";
        if (ext is ".gcz" or ".wia" or ".rvz" or ".tgc" or ".wbfs" or ".ciso") return folder is "GameCube" or "Wii" ? folder : "GameCube / Wii";
        if (folder is not null) return folder;
        if (f == KnownFormats.Chd && File.Exists(set.Primary))
        {
            try
            {
                chdman ??= new Chdman();
                if (chdman.Info(set.Primary, hunkStats: false).Metadata.Any(m => m.TagString == "CHGD")) return "Dreamcast";
            }
            catch (Exception ex) when (ex is ChdmanException or IOException or DllNotFoundException) { }
        }
        return "Unknown";
    }

    private static string? FromFolders(string path, string root)
    {
        var dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        var stop = Path.GetDirectoryName(root);
        while (dir is not null && !string.Equals(dir, stop, StringComparison.OrdinalIgnoreCase))
        {
            var name = new string(Path.GetFileName(dir).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            foreach (var (system, names) in FolderNames)
                if (names.Contains(name)) return system;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>"GameCube" or "Wii" from a plain disc's header magic, or null.</summary>
    private static string? ConsoleOfDisc(string path)
    {
        Span<byte> head = stackalloc byte[0x20];
        try
        {
            using var s = File.OpenRead(path);
            if (s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[0x18..]) == 0x5D1C9EA3) return "Wii";
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[0x1C..]) == 0xC2339F3D) return "GameCube";
        return null;
    }

    /// <summary>A Wii U input's base title ID and name, where they can be read without keys.</summary>
    private static (ulong BaseId, string Name)? WiiUIdentity(FileSet set)
    {
        try
        {
            if (set.Format == KnownFormats.Nus)
            {
                var tmd = WiiUTicket.ParseTmd(File.ReadAllBytes(Path.Combine(set.Primary, "title.tmd")));
                return (0x0005000000000000UL | (tmd.TitleId & 0xFFFFFFFFUL), "");
            }
            if (set.Format == KnownFormats.WiiUFolder || set.Format == KnownFormats.Wua)
            {
                var titles = WiiUTitles.Read(set.Primary);
                if (titles.Count == 0) return null;
                var main = titles.FirstOrDefault(t => t.Kind == WiiUTitleKind.Game) ?? titles[0];
                return (main.BaseId, titles.FirstOrDefault(t => t.Name.Length > 0)?.Name ?? "");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or KeyNotFoundException or NotSupportedException or FormatException) { }
        return null;
    }
}
