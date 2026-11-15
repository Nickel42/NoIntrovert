using System.Globalization;
using System.Text;

namespace NoIntrovert.Core.Cd;

/// <summary>One INDEX line of a cue track, in frames relative to its FILE.</summary>
internal sealed record CueIndex(int Number, long Frames);

internal sealed class CueTrack
{
    public int Number { get; init; }
    public string Type { get; init; } = "MODE1/2352";
    public List<CueIndex> Indexes { get; } = [];
    public long? PregapFrames { get; set; }
    public long? PostgapFrames { get; set; }
    public bool IsAudio => Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase);
}

internal sealed class CueFile
{
    public string Name { get; set; } = "";
    public string Type { get; init; } = "BINARY";
    public List<CueTrack> Tracks { get; } = [];
}

/// <summary>
/// A minimal cue sheet model: enough to find the files a cue needs, rewrite
/// their paths, and write cues for images we rebuild from other formats.
/// Lines we don't model (CATALOG, REM, FLAGS, ...) are kept verbatim.
/// </summary>
internal sealed class CueSheet
{
    public List<CueFile> Files { get; } = [];

    /// <summary>Lines before the first FILE, kept as-is.</summary>
    public List<string> Header { get; } = [];

    /// <summary>Unmodelled lines inside a track, keyed by track number, kept as-is.</summary>
    public Dictionary<int, List<string>> ExtraTrackLines { get; } = [];

    public static CueSheet Load(string path) => Parse(File.ReadAllText(path));

    public static CueSheet Parse(string text)
    {
        var cue = new CueSheet();
        CueFile? file = null;
        CueTrack? track = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var keyword = FirstWord(line).ToUpperInvariant();
            switch (keyword)
            {
                case "FILE":
                {
                    var (name, rest) = ReadQuoted(line[4..]);
                    file = new CueFile { Name = name, Type = rest.Length > 0 ? rest : "BINARY" };
                    cue.Files.Add(file);
                    track = null;
                    break;
                }
                case "TRACK":
                {
                    if (file is null) throw new FormatException("Cue sheet has a TRACK before any FILE.");
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    track = new CueTrack
                    {
                        Number = int.Parse(parts[1], CultureInfo.InvariantCulture),
                        Type = parts.Length > 2 ? parts[2].ToUpperInvariant() : "MODE1/2352",
                    };
                    file.Tracks.Add(track);
                    break;
                }
                case "INDEX" when track is not null:
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    track.Indexes.Add(new CueIndex(int.Parse(parts[1], CultureInfo.InvariantCulture), CdSector.ParseMsf(parts[2])));
                    break;
                }
                case "PREGAP" when track is not null:
                    track.PregapFrames = CdSector.ParseMsf(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                    break;
                case "POSTGAP" when track is not null:
                    track.PostgapFrames = CdSector.ParseMsf(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                    break;
                default:
                    if (track is not null)
                    {
                        if (!cue.ExtraTrackLines.TryGetValue(track.Number, out var extra))
                            cue.ExtraTrackLines[track.Number] = extra = [];
                        extra.Add(line);
                    }
                    else if (file is null)
                    {
                        cue.Header.Add(line);
                    }
                    break;
            }
        }

        if (cue.Files.Count == 0)
            throw new FormatException("Cue sheet has no FILE lines.");
        return cue;
    }

    public IEnumerable<CueTrack> Tracks => Files.SelectMany(f => f.Tracks);

    public string Write()
    {
        var sb = new StringBuilder();
        foreach (var line in Header) sb.Append(line).Append("\r\n");
        foreach (var file in Files)
        {
            sb.Append("FILE \"").Append(file.Name).Append("\" ").Append(file.Type).Append("\r\n");
            foreach (var track in file.Tracks)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  TRACK {track.Number:00} {track.Type}\r\n");
                if (ExtraTrackLines.TryGetValue(track.Number, out var extra))
                    foreach (var line in extra) sb.Append("    ").Append(line).Append("\r\n");
                if (track.PregapFrames is { } pregap)
                    sb.Append("    PREGAP ").Append(CdSector.FormatMsf(pregap)).Append("\r\n");
                foreach (var index in track.Indexes)
                    sb.Append(CultureInfo.InvariantCulture, $"    INDEX {index.Number:00} {CdSector.FormatMsf(index.Frames)}\r\n");
                if (track.PostgapFrames is { } postgap)
                    sb.Append("    POSTGAP ").Append(CdSector.FormatMsf(postgap)).Append("\r\n");
            }
        }
        return sb.ToString();
    }

    public void Save(string path) => File.WriteAllText(path, Write(), new UTF8Encoding(false));

    /// <summary>Sector size implied by a cue track type such as MODE2/2352.</summary>
    public static int SectorSize(string trackType)
    {
        var slash = trackType.IndexOf('/');
        if (slash >= 0 && int.TryParse(trackType[(slash + 1)..], out var size)) return size;
        return trackType.Equals("AUDIO", StringComparison.OrdinalIgnoreCase) ? CdSector.RawSize : CdSector.UserSize;
    }

    private static string FirstWord(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0 ? line : line[..space];
    }

    private static (string Value, string Remainder) ReadQuoted(string s)
    {
        s = s.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 0) return (s[1..], "");
            return (s[1..end], s[(end + 1)..].Trim());
        }
        // Unquoted name: the type is the last word.
        var lastSpace = s.LastIndexOf(' ');
        return lastSpace < 0 ? (s, "") : (s[..lastSpace], s[(lastSpace + 1)..].Trim());
    }
}
