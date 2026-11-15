using System.Buffers.Binary;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// The changes a cartridge ROM gets on its way into its 7z: the 3DS rename
/// (D2, never output .3ds) and, with header fixes on (R12, D6), the
/// No-Intro forms: big-endian .z64 from .v64/.n64, .sfc without a copier
/// header, and .md de-interleaved from .smd.
/// </summary>
public static class RomFixes
{
    /// <summary>The extension the ROM should have inside its archive, or null when it's fine as it is.</summary>
    public static string? FixedExtension(string path, bool normalizeHeaders)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".3ds") return ".cci";
        if (!normalizeHeaders || !File.Exists(path)) return null;
        return ext switch
        {
            ".v64" or ".n64" => ByteOrder(path) is N64Order.ByteSwapped or N64Order.LittleEndian ? ".z64" : null,
            ".z64" => ByteOrder(path) is N64Order.ByteSwapped or N64Order.LittleEndian ? ".z64" : null,
            ".smc" or ".swc" or ".fig" => ".sfc",
            ".sfc" => new FileInfo(path).Length % 1024 == 512 ? ".sfc" : null,
            ".smd" => ".md",
            _ => null,
        };
    }

    /// <summary>Whether the fix only renames the file (3DS, an already-headerless SNES ROM), so the bytes stay the same.</summary>
    public static bool RenameOnly(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".3ds" => true,
            ".smc" or ".swc" or ".fig" => new FileInfo(path).Length % 1024 != 512,
            _ => false,
        };

    /// <summary>Writes <paramref name="source"/> in its fixed form to <paramref name="target"/>.</summary>
    public static void WriteFixed(string source, string target, CancellationToken token)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        var bytes = File.ReadAllBytes(source);
        token.ThrowIfCancellationRequested();
        byte[] fixedBytes = ext switch
        {
            ".v64" or ".n64" or ".z64" => ToBigEndian(bytes),
            ".smc" or ".swc" or ".fig" or ".sfc" => bytes.Length % 1024 == 512 ? bytes[512..] : bytes,
            ".smd" => Deinterleave(bytes),
            _ => bytes,
        };
        File.WriteAllBytes(target, fixedBytes);
    }

    public enum N64Order { BigEndian, ByteSwapped, LittleEndian, Unknown }

    /// <summary>A N64 ROM's byte order, from its first word (0x80371240 in the canonical .z64 order).</summary>
    public static N64Order ByteOrder(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var s = File.OpenRead(path);
        if (s.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4) return N64Order.Unknown;
        return BinaryPrimitives.ReadUInt32BigEndian(head) switch
        {
            0x80371240 => N64Order.BigEndian,
            0x37804012 => N64Order.ByteSwapped,
            0x40123780 => N64Order.LittleEndian,
            _ => N64Order.Unknown,
        };
    }

    private static byte[] ToBigEndian(byte[] rom)
    {
        var order = BinaryPrimitives.ReadUInt32BigEndian(rom) switch
        {
            0x37804012 => N64Order.ByteSwapped,
            0x40123780 => N64Order.LittleEndian,
            _ => N64Order.BigEndian,
        };
        var output = (byte[])rom.Clone();
        for (var i = 0; i + 3 < output.Length; i += 4)
        {
            if (order == N64Order.ByteSwapped)
            {
                (output[i], output[i + 1]) = (rom[i + 1], rom[i]);
                (output[i + 2], output[i + 3]) = (rom[i + 3], rom[i + 2]);
            }
            else if (order == N64Order.LittleEndian)
            {
                output[i] = rom[i + 3]; output[i + 1] = rom[i + 2]; output[i + 2] = rom[i + 1]; output[i + 3] = rom[i];
            }
        }
        return output;
    }

    /// <summary>
    /// .smd: a 512-byte header, then 16 KiB blocks whose first half holds the
    /// odd bytes and second half the even ones.
    /// </summary>
    private static byte[] Deinterleave(byte[] smd)
    {
        var body = smd.Length % 16384 == 512 ? smd.AsSpan(512) : smd.AsSpan();
        var output = new byte[body.Length];
        for (var block = 0; block + 16384 <= body.Length; block += 16384)
            for (var j = 0; j < 8192; j++)
            {
                output[block + j * 2] = body[block + 8192 + j];
                output[block + j * 2 + 1] = body[block + j];
            }
        return output;
    }

    /// <summary>
    /// Whether a 3DS cartridge image (NCSD) is still encrypted: its first
    /// partition's NCCH header doesn't have the NoCrypto flag. Null when the
    /// file isn't an NCSD image (a .cia, or something unreadable).
    /// </summary>
    public static bool? Is3dsEncrypted(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            Span<byte> head = stackalloc byte[0x200];
            if (s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length || !head[0x100..0x104].SequenceEqual("NCSD"u8)) return null;
            var partition = BinaryPrimitives.ReadUInt32LittleEndian(head[0x120..]) * 0x200L;
            s.Position = partition;
            if (s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length || !head[0x100..0x104].SequenceEqual("NCCH"u8)) return null;
            return (head[0x188 + 7] & 0x04) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
