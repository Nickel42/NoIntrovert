using System.Buffers.Binary;
using System.Text;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Tells a disc's system from the first sectors of its image, where that's
/// quick and certain: GameCube and Wii headers (plain and in Dolphin's
/// containers), Xbox's XDVDFS, Saturn, Mega-CD and 3DO boot sectors, the PSP
/// UMD identifier, and PS1 vs PS2 by SYSTEM.CNF. Returns a registry id, or
/// null when the image doesn't say.
/// </summary>
internal static class DiscProbe
{
    private const uint WiiMagic = 0x5D1C9EA3;      // at 0x18 of a disc header
    private const uint GameCubeMagic = 0xC2339F3D; // at 0x1C

    /// <summary>The system of a disc image file (.iso, .bin, .img, a GameCube/Wii container), or null.</summary>
    public static string? SystemOf(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".wbfs" => "wii",
                ".tgc" => "gamecube",
                ".gcz" or ".wia" or ".rvz" or ".ciso" => NintendoContainer(s),
                _ => NintendoHeader(s, 0) ?? Xbox(s) ?? FromFileSystem(s),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"gamecube" or "wii" from a plain disc header at <paramref name="offset"/>.</summary>
    private static string? NintendoHeader(Stream s, long offset)
    {
        Span<byte> head = stackalloc byte[0x20];
        if (!ReadAt(s, offset, head)) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(head[0x18..]) == WiiMagic) return "wii";
        if (BinaryPrimitives.ReadUInt32BigEndian(head[0x1C..]) == GameCubeMagic) return "gamecube";
        return null;
    }

    /// <summary>
    /// Dolphin's containers record the console: GCZ in its sub-type (0 GameCube,
    /// 1 Wii), WIA and RVZ in their disc type (1, 2), and CISO keeps the disc
    /// header uncompressed after its 32 KiB block map.
    /// </summary>
    private static string? NintendoContainer(Stream s)
    {
        Span<byte> head = stackalloc byte[8];
        if (!ReadAt(s, 0, head)) return null;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (magic == 0x01C00BB1) // GCZ, little-endian 0xB10BC001
            return BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) switch { 0 => "gamecube", 1 => "wii", _ => null };
        if (magic is 0x57494101 or 0x52565A01) // "WIA\x1", "RVZ\x1"
            return ReadAt(s, 0x48, head[..4])
                ? BinaryPrimitives.ReadUInt32BigEndian(head) switch { 1 => "gamecube", 2 => "wii", _ => null }
                : null;
        if (head[..4].SequenceEqual("CISO"u8)) return NintendoHeader(s, 0x8000);
        return null;
    }

    /// <summary>An Xbox XISO: just the game partition, so XDVDFS starts at 64 KiB.</summary>
    public static bool IsXiso(string path)
    {
        Span<byte> magic = stackalloc byte[20];
        try
        {
            using var s = File.OpenRead(path);
            return ReadAt(s, 0x10000, magic) && magic.SequenceEqual("MICROSOFT*XBOX*MEDIA"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>An original Xbox image: XDVDFS at 64 KiB in an XISO, or past the video partition in a Redump image.</summary>
    private static string? Xbox(Stream s)
    {
        Span<byte> magic = stackalloc byte[20];
        foreach (var offset in new[] { 0x10000L, 0x18310000L })
            if (ReadAt(s, offset, magic) && magic.SequenceEqual("MICROSOFT*XBOX*MEDIA"u8)) return "xbox";
        return null;
    }

    /// <summary>
    /// Boot sector and ISO 9660 checks on a 2048-byte ISO or a raw 2352-byte
    /// bin (Mode 1 or Mode 2, told apart by the sector header).
    /// </summary>
    private static string? FromFileSystem(Stream s)
    {
        var raw = new byte[16];
        if (!ReadAt(s, 0, raw)) return null;
        var isRaw = raw[0] == 0 && raw.AsSpan(1, 10).IndexOfAnyExcept((byte)0xFF) < 0 && raw[11] == 0;
        var (size, offset) = !isRaw ? (2048, 0) : raw[15] == 2 ? (2352, 24) : (2352, 16);
        var sector = new byte[2048];
        bool Read(long lba) => ReadAt(s, lba * size + offset, sector);

        if (!Read(0)) return null;
        var boot = Encoding.ASCII.GetString(sector, 0, 16);
        if (boot.StartsWith("SEGA SEGASATURN")) return "saturn";
        if (boot.StartsWith("SEGADISCSYSTEM") || boot.StartsWith("SEGABOOTDISC")) return "mega-cd-sega-cd";
        if (sector[0] == 0x01 && sector.AsSpan(1, 5).IndexOfAnyExcept((byte)0x5A) < 0 && sector[6] == 0x01) return "3do";

        if (!Read(16) || sector[0] != 1 || Encoding.ASCII.GetString(sector, 1, 5) != "CD001") return null;
        var system = Encoding.ASCII.GetString(sector, 8, 32).Trim();
        if (system.Contains("PSP GAME", StringComparison.OrdinalIgnoreCase)) return "playstation-portable";
        if (!system.StartsWith("PLAYSTATION", StringComparison.OrdinalIgnoreCase)) return null;

        // PS1 and PS2 discs both say PLAYSTATION; SYSTEM.CNF names a BOOT2 executable only on PS2.
        var rootLba = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(156 + 2));
        var rootSize = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(156 + 10));
        for (var lba = rootLba; lba < rootLba + Math.Min(8, (rootSize + 2047) / 2048); lba++)
        {
            if (!Read(lba)) break;
            for (var p = 0; p < 2048 && sector[p] > 0;)
            {
                int length = sector[p], nameLength = sector[p + 32];
                if (p + 33 + nameLength > 2048) break;
                var name = Encoding.ASCII.GetString(sector, p + 33, nameLength);
                if (name.StartsWith("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase))
                {
                    var fileLba = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(p + 2));
                    if (!Read(fileLba)) return "playstation";
                    return Encoding.ASCII.GetString(sector).Contains("BOOT2", StringComparison.OrdinalIgnoreCase) ? "playstation-2" : "playstation";
                }
                p += length;
            }
        }
        return "playstation";
    }

    private static bool ReadAt(Stream s, long offset, Span<byte> buffer)
    {
        if (offset + buffer.Length > s.Length) return false;
        s.Position = offset;
        return s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }
}
