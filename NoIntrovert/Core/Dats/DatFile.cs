using System.Text;
using System.Xml;

namespace NoIntrovert.Core.Dats;

/// <summary>One file a DAT lists for a game: a ROM, a track, a disc image. CRC32 and SHA-1 are upper-case hex.</summary>
public sealed record DatRom(string Name, long Size, string Crc, string? Sha1);

/// <summary>One game in a DAT, with its files.</summary>
public sealed record DatGame(string Name, IReadOnlyList<DatRom> Roms);

/// <summary>
/// A No-Intro or Redump DAT: its header name ("Nintendo - Game Boy"),
/// version and games. Reads the ClrMamePro text format libretro-database
/// uses and the Logiqx XML the publishers' own downloads use.
/// </summary>
public sealed class DatFile
{
    public string Name { get; private init; } = "";
    public string Version { get; private init; } = "";
    public IReadOnlyList<DatGame> Games { get; private init; } = [];

    /// <summary>Reads a DAT in either format, told apart by its first character.</summary>
    public static DatFile Load(string path)
    {
        var text = File.ReadAllText(path);
        return text.TrimStart().StartsWith('<') ? ParseXml(text) : ParseClrMamePro(text);
    }

    /// <summary>
    /// ClrMamePro: <c>clrmamepro ( name "…" version "…" )</c> then
    /// <c>game ( name "…" rom ( name "…" size N crc X sha1 X ) )</c> blocks.
    /// </summary>
    public static DatFile ParseClrMamePro(string text)
    {
        var tokens = Tokens(text).GetEnumerator();
        string name = "", version = "";
        var games = new List<DatGame>();
        while (tokens.MoveNext())
        {
            var block = tokens.Current;
            if (!Next(tokens, out var open) || open != "(") break;
            var fields = ReadBlock(tokens, out var roms);
            if (block.Equals("clrmamepro", StringComparison.OrdinalIgnoreCase))
            {
                name = fields.GetValueOrDefault("name", "");
                version = fields.GetValueOrDefault("version", "");
            }
            else if (block is "game" or "machine" && fields.TryGetValue("name", out var gameName) && roms.Count > 0)
                games.Add(new DatGame(gameName, roms));
        }
        return new DatFile { Name = name, Version = version, Games = games };
    }

    /// <summary>The fields of a block whose "(" was just read, up to its ")"; rom (and disk) sub-blocks become ROMs.</summary>
    private static Dictionary<string, string> ReadBlock(IEnumerator<string> tokens, out List<DatRom> roms)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        roms = [];
        while (Next(tokens, out var key) && key != ")")
        {
            if (!Next(tokens, out var value)) break;
            if (value == "(")
            {
                var sub = ReadBlock(tokens, out _);
                if (key is "rom" && Rom(sub) is { } rom) roms.Add(rom);
                continue;
            }
            fields.TryAdd(key, value);
        }
        return fields;
    }

    private static DatRom? Rom(IReadOnlyDictionary<string, string> f) =>
        f.TryGetValue("name", out var name) && f.TryGetValue("size", out var size) && long.TryParse(size, out var bytes)
            && f.TryGetValue("crc", out var crc)
            ? new DatRom(name, bytes, crc.ToUpperInvariant(), f.GetValueOrDefault("sha1")?.ToUpperInvariant())
            : null;

    private static bool Next(IEnumerator<string> tokens, out string token)
    {
        var more = tokens.MoveNext();
        token = more ? tokens.Current : "";
        return more;
    }

    /// <summary>Words, quoted strings (quotes removed) and parentheses.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')') { i++; yield return c.ToString(); continue; }
            if (c == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length;
                yield return text[(i + 1)..end];
                i = end + 1;
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')')) i++;
            yield return text[start..i];
        }
    }

    /// <summary>Logiqx XML: <c>&lt;header&gt;&lt;name&gt;</c>, then <c>&lt;game name&gt;&lt;rom name size crc sha1/&gt;</c>.</summary>
    public static DatFile ParseXml(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        string name = "", version = "";
        var games = new List<DatGame>();
        string? game = null;
        List<DatRom> roms = [];
        string? headerField = null;
        var inHeader = false;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.Name == "header":
                    inHeader = true;
                    break;
                case XmlNodeType.Element when inHeader:
                    headerField = reader.Name;
                    break;
                case XmlNodeType.Text when inHeader && headerField == "name":
                    name = reader.Value;
                    break;
                case XmlNodeType.Text when inHeader && headerField == "version":
                    version = reader.Value;
                    break;
                case XmlNodeType.EndElement when reader.Name == "header":
                    inHeader = false;
                    break;
                case XmlNodeType.Element when reader.Name is "game" or "machine":
                    game = reader.GetAttribute("name");
                    roms = [];
                    if (reader.IsEmptyElement) game = null;
                    break;
                case XmlNodeType.Element when reader.Name == "rom" && game is not null:
                    if (long.TryParse(reader.GetAttribute("size"), out var size) && reader.GetAttribute("crc") is { } crc)
                        roms.Add(new DatRom(reader.GetAttribute("name") ?? "", size, crc.ToUpperInvariant(), reader.GetAttribute("sha1")?.ToUpperInvariant()));
                    break;
                case XmlNodeType.EndElement when reader.Name is "game" or "machine":
                    if (game is not null && roms.Count > 0) games.Add(new DatGame(game, roms));
                    game = null;
                    break;
            }
        }
        return new DatFile { Name = name, Version = version, Games = games };
    }

    /// <summary>The header name of a DAT, read from its start without parsing the games.</summary>
    public static string? HeaderName(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            var head = new char[4096];
            var text = new string(head, 0, reader.ReadBlock(head, 0, head.Length));
            var m = System.Text.RegularExpressions.Regex.Match(text, text.TrimStart().StartsWith('<')
                ? @"<header>[\s\S]*?<name>([^<]*)</name>" : @"clrmamepro\s*\(\s*[\s\S]*?\bname\s+""([^""]*)""");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch (IOException) { return null; }
    }
}
