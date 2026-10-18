using System.Globalization;
using NoIntrovert.Core.Cd;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Turns a CloneCD image (.ccd descriptor + .img raw sectors, optional .sub)
/// into a cue sheet that points at the .img, so the image needs no copying.
/// </summary>
/// <remarks>
/// The .ccd is an INI file. Tracks come from the [TRACK n] sections (MODE=0
/// audio, 1 Mode 1, 2 Mode 2; INDEX 0/1 as LBAs) or, in files without them,
/// from the [Entry n] TOC entries (Point = track, Control bit 2 = data, PLBA
/// = start). The .sub subchannel file is not needed for CHD.
/// </remarks>
internal static class CcdConverter
{
    public static string FindImage(string ccdPath)
    {
        var img = Path.ChangeExtension(ccdPath, ".img");
        if (File.Exists(img)) return img;
        var alt = Directory.EnumerateFiles(Path.GetDirectoryName(ccdPath)!, Path.GetFileNameWithoutExtension(ccdPath) + ".*")
            .FirstOrDefault(p => p.EndsWith(".img", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
        return alt ?? throw new FileNotFoundException($"The CloneCD image file '{Path.GetFileName(img)}' that goes with this .ccd was not found.", img);
    }

    /// <summary>Files that make up the CloneCD set (for deleting originals).</summary>
    public static IEnumerable<string> SetFiles(string ccdPath)
    {
        yield return ccdPath;
        foreach (var ext in new[] { ".img", ".sub" })
        {
            var p = Path.ChangeExtension(ccdPath, ext);
            if (File.Exists(p)) yield return p;
        }
    }

    public static string WriteCue(string ccdPath, WorkDirectory work)
    {
        var sections = ParseIni(File.ReadAllLines(ccdPath));
        var image = FindImage(ccdPath);

        var tracks = new SortedDictionary<int, (string Type, long? Index0, long Index1)>();
        foreach (var (name, values) in sections)
        {
            if (!name.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase)) continue;
            var number = int.Parse(name[6..].Trim(), CultureInfo.InvariantCulture);
            var mode = values.TryGetValue("MODE", out var m) ? ParseInt(m) : 1;
            long? index0 = values.TryGetValue("INDEX 0", out var i0) ? ParseInt(i0) : null;
            var index1 = values.TryGetValue("INDEX 1", out var i1) ? ParseInt(i1) : 0;
            tracks[number] = (ModeToCueType(mode), index0, index1);
        }

        if (tracks.Count == 0)
        {
            // Older files: rebuild the track list from the TOC entries.
            foreach (var (name, values) in sections)
            {
                if (!name.StartsWith("Entry ", StringComparison.OrdinalIgnoreCase)) continue;
                var point = ParseInt(values.GetValueOrDefault("Point", "0"));
                if (point is < 1 or > 99) continue;
                var control = ParseInt(values.GetValueOrDefault("Control", "0"));
                var plba = ParseInt(values.GetValueOrDefault("PLBA", "0"));
                tracks[(int)point] = ((control & 0x04) != 0 ? "MODE1/2352" : "AUDIO", null, plba);
            }
            if (tracks.Count > 0 && tracks.First().Value.Type != "AUDIO")
                tracks[tracks.First().Key] = (DetectDataMode(image), null, tracks.First().Value.Index1);
        }

        if (tracks.Count == 0)
            throw new InvalidDataException("The .ccd file lists no tracks.");

        var cuePath = work.CuePathFor(Path.GetFileNameWithoutExtension(ccdPath) + ".cue", [image]);
        var cue = new CueSheet();
        var file = new CueFile { Name = WorkDirectory.CueReference(cuePath, image) };
        cue.Files.Add(file);
        foreach (var (number, t) in tracks)
        {
            var track = new CueTrack { Number = number, Type = t.Type };
            if (t.Index0 is { } i0 && i0 < t.Index1) track.Indexes.Add(new CueIndex(0, i0));
            track.Indexes.Add(new CueIndex(1, t.Index1));
            file.Tracks.Add(track);
        }

        cue.Save(cuePath);
        return cuePath;
    }

    private static string ModeToCueType(long mode) => mode switch
    {
        0 => "AUDIO",
        2 => "MODE2/2352",
        _ => "MODE1/2352",
    };

    private static string DetectDataMode(string image)
    {
        var sector = new byte[CdSector.RawSize];
        using var fs = File.OpenRead(image);
        if (fs.Read(sector) == sector.Length && CdSector.HasSync(sector) && sector[15] == 2) return "MODE2/2352";
        return "MODE1/2352";
    }

    private static long ParseInt(string s)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(s, CultureInfo.InvariantCulture);
    }

    private static List<(string Name, Dictionary<string, string> Values)> ParseIni(IEnumerable<string> lines)
    {
        var sections = new List<(string, Dictionary<string, string>)>();
        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add((line[1..^1].Trim(), current));
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq > 0 && current is not null)
                current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return sections;
    }
}
