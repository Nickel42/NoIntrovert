using System.Collections.Concurrent;
using System.Text.Json;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Dats;

/// <summary>What the last DAT check found: when it ran, and each folder's ETag and file list.</summary>
public sealed class DatState
{
    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>The GitHub ETag of each folder's listing ("no-intro", "redump").</summary>
    public Dictionary<string, string> ETags { get; set; } = [];

    /// <summary>Each folder's files (name without .dat → git blob SHA), as GitHub last listed them.</summary>
    public Dictionary<string, Dictionary<string, string>> Listings { get; set; } = [];

    /// <summary>The blob SHA of each cached file ("no-intro/Nintendo - Game Boy" → SHA).</summary>
    public Dictionary<string, string> Cached { get; set; } = [];
}

/// <summary>
/// The DATs on this machine: libretro-database copies cached under
/// <c>&lt;settings folder&gt;/dats</c>, plus any in the user's own DAT folder,
/// which win when their header names match (FR-3). Parsed DATs are kept in
/// memory while their file doesn't change.
/// </summary>
public sealed class DatStore(string folder)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<string, (DateTime Stamp, DatFile Dat)> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();

    private static readonly ConcurrentDictionary<string, DatStore> Stores = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The store under the current settings folder (one per folder, so parsed DATs are shared).</summary>
    public static DatStore Default => Stores.GetOrAdd(Path.Combine(AppSettings.SettingsDirectory, "dats"), f => new DatStore(f));

    public string Folder { get; } = folder;

    private string StatePath => Path.Combine(Folder, "state.json");

    /// <summary>Where the cached copy of <paramref name="key"/> ("redump/Sony - PlayStation") lives.</summary>
    public string PathOf(string key) => Path.Combine(Folder, key.Replace('/', Path.DirectorySeparatorChar) + ".dat");

    public DatState LoadState()
    {
        lock (_stateLock)
        {
            try
            {
                if (File.Exists(StatePath))
                    return JsonSerializer.Deserialize<DatState>(File.ReadAllText(StatePath), JsonOptions) ?? new DatState();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            return new DatState();
        }
    }

    public void SaveState(DatState state)
    {
        lock (_stateLock)
        {
            Directory.CreateDirectory(Folder);
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, StatePath, overwrite: true);
        }
    }

    /// <summary>The DATs cached so far, by key.</summary>
    public IReadOnlyList<string> CachedKeys() =>
        new[] { "no-intro", "redump" }
            .Where(f => Directory.Exists(Path.Combine(Folder, f)))
            .SelectMany(f => Directory.EnumerateFiles(Path.Combine(Folder, f), "*.dat").Select(p => f + "/" + Path.GetFileNameWithoutExtension(p)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The DATs for <paramref name="keys"/>: from <paramref name="ownFolder"/>
    /// when one there has the same header name, else the cached copy. Keys
    /// with neither are left out.
    /// </summary>
    public List<DatFile> Load(IEnumerable<string> keys, string? ownFolder)
    {
        var own = OwnDats(ownFolder);
        var dats = new List<DatFile>();
        foreach (var key in keys)
        {
            var header = key[(key.IndexOf('/') + 1)..];
            var path = own.GetValueOrDefault(header) ?? PathOf(key);
            if (Parse(path) is { } dat) dats.Add(dat);
        }
        return dats;
    }

    /// <summary>The DATs covering <paramref name="system"/>, or every DAT on hand when it's unknown.</summary>
    public List<DatFile> For(SystemInfo? system, string? ownFolder) =>
        system is null
            ? Load(CachedKeys(), ownFolder).Concat(OwnDats(ownFolder).Values.Select(Parse).OfType<DatFile>()).DistinctBy(d => d.Name).ToList()
            : Load(system.Dats ?? [], ownFolder);

    /// <summary>The user's own DATs by header name (FR-3).</summary>
    private static Dictionary<string, string> OwnDats(string? ownFolder)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(ownFolder) || !Directory.Exists(ownFolder)) return map;
        foreach (var file in Directory.EnumerateFiles(ownFolder, "*.dat", SearchOption.AllDirectories))
            if (DatFile.HeaderName(file) is { Length: > 0 } name) map.TryAdd(name, file);
        return map;
    }

    private DatFile? Parse(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_parsed.TryGetValue(path, out var known) && known.Stamp == stamp) return known.Dat;
            var dat = DatFile.Load(path);
            _parsed[path] = (stamp, dat);
            return dat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }
    }

    /// <summary>"libretro-database, updated 2 hours ago (12 systems)", or what's missing.</summary>
    public string Describe()
    {
        var state = LoadState();
        var count = CachedKeys().Count;
        if (count == 0) return state.LastCheckedUtc is null ? "Game databases: not downloaded yet." : "Game databases: none needed yet (no systems in the Library have one).";
        var when = state.LastCheckedUtc is { } t ? Ago(DateTime.UtcNow - t) : "at an unknown time";
        return $"Game databases: libretro-database, updated {when} ({count} {(count == 1 ? "database" : "databases")}).";
    }

    private static string Ago(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)span.TotalMinutes} min ago",
        < 60 * 24 => $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago",
        _ => $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")} ago",
    };
}
