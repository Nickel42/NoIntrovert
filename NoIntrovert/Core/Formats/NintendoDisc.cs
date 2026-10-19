using System.Buffers.Binary;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// GameCube and Wii disc images: plain ones (.iso/.gcm, recognised by their
/// header) and Dolphin's containers, which libdolphinrvz reads.
/// </summary>
internal static class NintendoDisc
{
    /// <summary>Containers only libdolphinrvz can read. WBFS and CISO can be read but not converted.</summary>
    public static readonly string[] Containers = [".gcm", ".gcz", ".wia", ".rvz", ".tgc", ".wbfs", ".ciso"];

    /// <summary>Compressed containers the Extract tab turns back into ISO.</summary>
    public static readonly string[] Decodable = [".rvz", ".wia", ".gcz"];

    private const uint WiiMagic = 0x5D1C9EA3;      // at 0x18
    private const uint GameCubeMagic = 0xC2339F3D; // at 0x1C

    public static bool IsContainer(string path) =>
        Containers.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsDecodable(string path) =>
        Decodable.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>A plain GameCube or Wii image (iso/gcm), by the magic words in its disc header.</summary>
    public static bool IsPlainDisc(string path)
    {
        Span<byte> head = stackalloc byte[0x20];
        try
        {
            using var f = File.OpenRead(path);
            if (f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return BinaryPrimitives.ReadUInt32BigEndian(head[0x18..]) == WiiMagic
            || BinaryPrimitives.ReadUInt32BigEndian(head[0x1C..]) == GameCubeMagic;
    }
}
