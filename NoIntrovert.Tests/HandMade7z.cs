namespace NoIntrovert.Tests;

/// <summary>
/// Writes a one-file 7z by hand with any coder ID, so the rules can be tested
/// on archives the app's own engine never makes: "Copy" (stored, which 7-Zip
/// still unpacks) and zstd (7-Zip ZS's ID, which stock 7-Zip can only list).
/// </summary>
internal static class HandMade7z
{
    public static readonly byte[] Copy = [0x00];
    public static readonly byte[] Zstd = [0x04, 0xF7, 0x11, 0x01];

    public static void Write(string path, string name, byte[] data, byte[] coder)
    {
        var h = new MemoryStream();
        h.WriteByte(0x01); // Header
        h.WriteByte(0x04); // MainStreamsInfo
        h.WriteByte(0x06); Number(h, 0); Number(h, 1); h.WriteByte(0x09); Number(h, (ulong)data.Length); h.WriteByte(0x00); // PackInfo
        h.WriteByte(0x07); h.WriteByte(0x0B); Number(h, 1); h.WriteByte(0x00); // UnpackInfo: one folder, inline
        Number(h, 1); h.WriteByte((byte)coder.Length); h.Write(coder); // one simple coder
        h.WriteByte(0x0C); Number(h, (ulong)data.Length); h.WriteByte(0x00);
        h.WriteByte(0x08); h.WriteByte(0x0A); h.WriteByte(0x01); h.Write(BitConverter.GetBytes(Crc32(data))); h.WriteByte(0x00); // SubStreamsInfo: CRC
        h.WriteByte(0x00);
        h.WriteByte(0x05); Number(h, 1); // FilesInfo
        var utf16 = System.Text.Encoding.Unicode.GetBytes(name + "\0");
        h.WriteByte(0x11); Number(h, (ulong)utf16.Length + 1); h.WriteByte(0x00); h.Write(utf16);
        h.WriteByte(0x00);
        h.WriteByte(0x00);
        var header = h.ToArray();

        var start = new byte[20];
        BitConverter.TryWriteBytes(start.AsSpan(0), (ulong)data.Length); // next header offset (after the packed data)
        BitConverter.TryWriteBytes(start.AsSpan(8), (ulong)header.Length);
        BitConverter.TryWriteBytes(start.AsSpan(16), Crc32(header));
        using var f = File.Create(path);
        f.Write([(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0, 4]);
        f.Write(BitConverter.GetBytes(Crc32(start)));
        f.Write(start);
        f.Write(data);
        f.Write(header);
    }

    /// <summary>7z's variable-length NUMBER: the first byte's leading 1-bits say how many bytes follow.</summary>
    private static void Number(Stream s, ulong value)
    {
        if (value < 0x80) { s.WriteByte((byte)value); return; }
        var extra = 1;
        while (extra < 8 && value >= 1UL << (7 * (extra + 1))) extra++;
        var first = (byte)(0xFF << (8 - extra));
        if (extra < 8) first |= (byte)(value >> (8 * extra));
        s.WriteByte(first);
        for (var i = 0; i < extra; i++) s.WriteByte((byte)(value >> (8 * i)));
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
