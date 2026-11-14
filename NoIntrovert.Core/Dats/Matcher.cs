using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoIntrovert.Core.Dats;

/// <summary>What checking an input against the databases found (FR-8).</summary>
public enum MatchStatus
{
    Verified,
    NameDiffers,
    NotInDatabase,
    NoDatabase,
    CantCheck,
    Damaged,
}

/// <summary>
/// One input's result: its status, the DAT and game it matched, the name it
/// should have (the game's name with the input's own extension), and the
/// text the pages show.
/// </summary>
public sealed record MatchResult(MatchStatus Status, string? Dat, string? Game, string? ExpectedName, string Text)
{
    public static MatchResult Of(MatchStatus status, string text) => new(status, null, null, null, text);
}

/// <summary>
/// Looks hashes up in DATs (FR-7): a file matches an entry by size and CRC32,
/// and by SHA-1 too where both sides have one. libretro's Redump DATs list one
/// track per disc, so an input matches when any of its files does.
/// </summary>
public static class Matcher
{
    public static MatchResult Match(string inputPath, IReadOnlyList<HashedFile> files, IReadOnlyList<DatFile> dats)
    {
        if (dats.Count == 0) return MatchResult.Of(MatchStatus.NoDatabase, "No database for this system yet");
        foreach (var dat in dats)
        {
            var index = IndexOf(dat);
            foreach (var file in files)
            {
                if (!index.TryGetValue((file.Size, file.Crc), out var candidates)) continue;
                var hit = candidates.FirstOrDefault(c => file.Sha1 is null || c.Rom.Sha1 is null || c.Rom.Sha1 == file.Sha1);
                if (hit.Game is null) continue;
                var expected = ExpectedName(inputPath, hit.Game.Name);
                var actual = Path.GetFileName(Path.TrimEndingDirectorySeparator(inputPath));
                var how = file.Sha1 is null ? " (by CRC32)" : "";
                return string.Equals(actual, expected, StringComparison.Ordinal)
                    ? new MatchResult(MatchStatus.Verified, dat.Name, hit.Game.Name, expected, $"Verified{how}: {hit.Game.Name}")
                    : new MatchResult(MatchStatus.NameDiffers, dat.Name, hit.Game.Name, expected, $"Verified{how}, name differs: should be {expected}");
            }
        }
        return new MatchResult(MatchStatus.NotInDatabase, null, null, null, $"Not in the database ({string.Join(", ", dats.Select(d => d.Name))})");
    }

    /// <summary>The game's name with the input's own extension ("Game (USA).7z", "Game (USA).chd").</summary>
    public static string ExpectedName(string inputPath, string game)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(inputPath));
        var ext = name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : Path.GetExtension(name);
        return game + ext;
    }

    private static readonly ConditionalWeakTable<DatFile, Dictionary<(long, string), List<(DatGame Game, DatRom Rom)>>> Indexes = new();

    private static Dictionary<(long, string), List<(DatGame Game, DatRom Rom)>> IndexOf(DatFile dat) =>
        Indexes.GetValue(dat, d =>
        {
            var map = new Dictionary<(long, string), List<(DatGame, DatRom)>>();
            foreach (var game in d.Games)
                foreach (var rom in game.Roms)
                {
                    if (!map.TryGetValue((rom.Size, rom.Crc), out var list)) map[(rom.Size, rom.Crc)] = list = [];
                    list.Add((game, rom));
                }
            return map;
        });
}

/// <summary>
/// The last result for each checked input (FR-11), in <c>matches.json</c>
/// beside the settings, keyed like the hash cache so a changed file's old
/// result isn't shown.
/// </summary>
public sealed class MatchStore
{
    public sealed record Entry(long Size, long Ticks, MatchResult Result);

    // Lazy, so a lookup during the startup warm-up waits for that load instead of reading matches.json a second time.
    private static readonly ConcurrentDictionary<string, Lazy<MatchStore>> Stores = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly ConcurrentDictionary<string, Entry> _entries;
    private readonly ThrottledJsonFile _file;

    private MatchStore(string path)
    {
        Dictionary<string, Entry>? loaded = null;
        try
        {
            if (File.Exists(path)) loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        _entries = new ConcurrentDictionary<string, Entry>(loaded ?? [], StringComparer.OrdinalIgnoreCase);
        _file = new ThrottledJsonFile(path, () => JsonSerializer.Serialize(_entries, JsonOptions));
    }

    public static MatchStore Default =>
        Stores.GetOrAdd(Path.Combine(AppSettings.SettingsDirectory, "matches.json"), p => new Lazy<MatchStore>(() => new MatchStore(p))).Value;

    /// <summary>
    /// Reads matches.json on a pool thread, so the first Library selection doesn't wait for it; returns at once.
    /// The task ends when it's read (tests wait for it); a read that fails leaves the store empty, as before.
    /// </summary>
    public static Task WarmInBackground() => Task.Run(() => { _ = Default; });

    /// <summary>The result for <paramref name="path"/> if it hasn't changed since it was checked.</summary>
    public MatchResult? Get(string path)
    {
        if (!_entries.TryGetValue(path, out var e)) return null;
        var (size, ticks) = HashCache.Stamp(path);
        return e.Size == size && e.Ticks == ticks ? e.Result : null;
    }

    /// <summary>Remembers <paramref name="path"/>'s result; matches.json is written at most every two seconds, and when the job group ends.</summary>
    public void Set(string path, MatchResult result)
    {
        var (size, ticks) = HashCache.Stamp(path);
        _entries[path] = new Entry(size, ticks, result);
        _file.Changed();
    }

    /// <summary>How many times matches.json has been written.</summary>
    public int Writes => _file.Writes;

    /// <summary>Writes matches.json now if it changed.</summary>
    public void Flush() => _file.Flush();
}
