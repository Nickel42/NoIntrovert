namespace NoIntrovert.Core.Cd;

/// <summary>
/// Constants and helpers for raw 2352-byte CD sectors: sync pattern, MSF/LBA
/// conversion, and the EDC/ECC fields that ECM strips and we rebuild.
/// </summary>
internal static class CdSector
{
    public const int RawSize = 2352;
    public const int UserSize = 2048;
    public const int Mode2Size = 2336;
    public const int FramesPerSecond = 75;
    public const int LeadInFrames = 150; // the 2-second offset between LBA 0 and MSF 00:02:00

    private static readonly byte[] Sync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    private static readonly uint[] EdcTable = BuildEdcTable();
    private static readonly byte[] EccF = new byte[256];
    private static readonly byte[] EccB = new byte[256];

    static CdSector()
    {
        for (var i = 0; i < 256; i++)
        {
            var j = (i << 1) ^ ((i & 0x80) != 0 ? 0x11D : 0);
            EccF[i] = (byte)j;
            EccB[i ^ (byte)j] = (byte)i;
        }
    }

    private static uint[] BuildEdcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var edc = i;
            for (var k = 0; k < 8; k++)
                edc = (edc >> 1) ^ ((edc & 1) != 0 ? 0xD8018001u : 0u);
            table[i] = edc;
        }
        return table;
    }

    public static bool HasSync(ReadOnlySpan<byte> sector) =>
        sector.Length >= Sync.Length && sector[..Sync.Length].SequenceEqual(Sync);

    public static void WriteSync(Span<byte> sector) => Sync.CopyTo(sector);

    public static uint ComputeEdc(ReadOnlySpan<byte> data, uint edc = 0)
    {
        foreach (var b in data)
            edc = (edc >> 8) ^ EdcTable[(edc ^ b) & 0xFF];
        return edc;
    }

    public static void WriteEdc(Span<byte> sector, int start, int length, int at)
    {
        var edc = ComputeEdc(sector.Slice(start, length));
        sector[at] = (byte)edc;
        sector[at + 1] = (byte)(edc >> 8);
        sector[at + 2] = (byte)(edc >> 16);
        sector[at + 3] = (byte)(edc >> 24);
    }

    /// <summary>Writes the P and Q parity of a Mode 1 or Mode 2 Form 1 sector.</summary>
    /// <param name="zeroAddress">Mode 2 computes ECC as if the header address were zero.</param>
    public static void WriteEcc(Span<byte> sector, bool zeroAddress)
    {
        Span<byte> savedAddress = stackalloc byte[4];
        if (zeroAddress)
        {
            sector.Slice(12, 4).CopyTo(savedAddress);
            sector.Slice(12, 4).Clear();
        }
        ComputeEccBlock(sector[12..], 86, 24, 2, 86, sector[0x81C..]);
        ComputeEccBlock(sector[12..], 52, 43, 86, 88, sector[0x8C8..]);
        if (zeroAddress)
            savedAddress.CopyTo(sector.Slice(12, 4));
    }

    private static void ComputeEccBlock(Span<byte> src, int majorCount, int minorCount, int majorMult, int minorInc, Span<byte> dest)
    {
        var size = majorCount * minorCount;
        for (var major = 0; major < majorCount; major++)
        {
            var index = (major >> 1) * majorMult + (major & 1);
            byte a = 0, b = 0;
            for (var minor = 0; minor < minorCount; minor++)
            {
                var value = src[index];
                index += minorInc;
                if (index >= size) index -= size;
                a ^= value;
                b ^= value;
                a = EccF[a];
            }
            a = EccB[EccF[a] ^ b];
            dest[major] = a;
            dest[major + majorCount] = (byte)(a ^ b);
        }
    }

    public static string FormatMsf(long frames)
    {
        if (frames < 0) frames = 0;
        var m = frames / (60 * FramesPerSecond);
        var s = frames / FramesPerSecond % 60;
        var f = frames % FramesPerSecond;
        return $"{m:00}:{s:00}:{f:00}";
    }

    public static long ParseMsf(string msf)
    {
        var parts = msf.Split(':');
        if (parts.Length != 3)
            throw new FormatException($"'{msf}' is not an mm:ss:ff time.");
        return (long.Parse(parts[0]) * 60 + long.Parse(parts[1])) * FramesPerSecond + long.Parse(parts[2]);
    }

    public static int FromBcd(byte value) => (value >> 4) * 10 + (value & 0x0F);

    public static long BcdMsfToFrames(byte m, byte s, byte f) =>
        (FromBcd(m) * 60L + FromBcd(s)) * FramesPerSecond + FromBcd(f);

    /// <summary>
    /// Offset of the 2048 user bytes in a raw sector, or -1 for audio or a
    /// Mode 2 Form 2 sector (whose user data is not 2048 bytes).
    /// </summary>
    public static int UserDataOffset(ReadOnlySpan<byte> sector)
    {
        if (!HasSync(sector)) return -1;
        return sector[15] switch
        {
            1 => 16,
            2 => (sector[18] & 0x20) != 0 ? -1 : 24,
            _ => -1,
        };
    }
}
