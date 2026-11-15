using System.Buffers.Binary;
using System.Text;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// A PS Vita game as a NoNpDrm folder (FR-10): recognised by its
/// <c>sce_sys/param.sfo</c>, whose TITLE and TITLE_ID name the zip it becomes.
/// </summary>
public static class VitaGame
{
    public static bool IsGameFolder(string folder) => File.Exists(Path.Combine(folder, "sce_sys", "param.sfo"));

    /// <summary>The game's title and title ID ("PCSE00000"), from param.sfo.</summary>
    public static (string Title, string TitleId) Read(string folder)
    {
        var values = ReadSfo(File.ReadAllBytes(Path.Combine(folder, "sce_sys", "param.sfo")));
        var id = values.GetValueOrDefault("TITLE_ID") ?? throw new InvalidDataException("param.sfo has no TITLE_ID.");
        return (values.GetValueOrDefault("TITLE") ?? id, id);
    }

    /// <summary>"Title (PCSE00000)", with the characters a file name can't hold taken out.</summary>
    public static string ZipName(string title, string titleId)
    {
        var clean = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? ' ' : c).ToArray());
        return $"{string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries))} ({titleId})";
    }

    /// <summary>
    /// The text entries of an SFO: "\0PSF", version, key table start, data
    /// table start, count, then 16-byte index entries (key offset, format,
    /// length, max length, data offset).
    /// </summary>
    public static Dictionary<string, string> ReadSfo(byte[] sfo)
    {
        if (sfo.Length < 20 || !sfo.AsSpan(0, 4).SequenceEqual("\0PSF"u8)) throw new InvalidDataException("Not a param.sfo file.");
        var keys = (int)BinaryPrimitives.ReadUInt32LittleEndian(sfo.AsSpan(8));
        var data = (int)BinaryPrimitives.ReadUInt32LittleEndian(sfo.AsSpan(12));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(sfo.AsSpan(16));
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var entry = sfo.AsSpan(20 + i * 16, 16);
            var keyStart = keys + BinaryPrimitives.ReadUInt16LittleEndian(entry);
            var keyEnd = Array.IndexOf(sfo, (byte)0, keyStart);
            var key = Encoding.UTF8.GetString(sfo, keyStart, keyEnd - keyStart);
            var format = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var offset = data + (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (format is 0x0204 or 0x0004) // UTF-8 text, null-terminated or not
                values[key] = Encoding.UTF8.GetString(sfo, offset, length).TrimEnd('\0');
        }
        return values;
    }
}
