using System.Text.Json;
using System.Xml.Linq;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Esde;

/// <summary>One system in ES-DE's es_systems.xml: its folder (<c>&lt;name&gt;</c>), full name and the extensions it accepts (lower case).</summary>
public sealed record EsdeSystem(string Name, string FullName, IReadOnlyList<string> Extensions);

/// <summary>
/// ES-DE's systems as of one release: <see cref="Ref"/> is the tag ("v3.5.0"),
/// <see cref="Commit"/> the last commit that changed es_systems.xml there.
/// The bundled snapshot and the cache under the settings folder have this shape.
/// </summary>
public sealed class EsdeSystemList
{
    public string Ref { get; set; } = "";
    public string Commit { get; set; } = "";
    public List<EsdeSystem> Systems { get; set; } = [];

    /// <summary>The system kept in folder <paramref name="name"/>; folder names are compared exactly, as ES-DE does on Linux.</summary>
    public EsdeSystem? Find(string name) => Systems.FirstOrDefault(s => s.Name == name);

    /// <summary>Reads ES-DE's <c>resources/systems/unix/es_systems.xml</c>.</summary>
    public static EsdeSystemList Parse(string xml, string @ref, string commit)
    {
        var systems = new List<EsdeSystem>();
        foreach (var system in XDocument.Parse(xml).Root?.Elements("system") ?? [])
        {
            var name = system.Element("name")?.Value.Trim();
            if (string.IsNullOrEmpty(name) || systems.Any(s => s.Name == name)) continue;
            var extensions = (system.Element("extension")?.Value ?? "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.ToLowerInvariant()).Distinct().ToList();
            systems.Add(new EsdeSystem(name, system.Element("fullname")?.Value.Trim() ?? name, extensions));
        }
        if (systems.Count == 0) throw new FormatException("es_systems.xml has no systems in it");
        return new EsdeSystemList { Ref = @ref, Commit = commit, Systems = systems };
    }
}

/// <summary>What the last ES-DE check found: when it ran, the latest release, and the ETags to ask with next time.</summary>
public sealed class EsdeState
{
    public DateTime? LastCheckedUtc { get; set; }
    public string? LatestRef { get; set; }
    public string? ReleasesETag { get; set; }

    /// <summary>The ETag of the commits listing for <see cref="CommitsRef"/>, and the commit it named.</summary>
    public string? CommitsETag { get; set; }
    public string? CommitsRef { get; set; }
    public string? LatestCommit { get; set; }
}

/// <summary>
/// The ES-DE system list on this machine, cached under
/// <c>&lt;settings folder&gt;/esde</c>: <c>es_systems.json</c> (the list, with
/// its release and commit) and <c>state.json</c> (the last check).
/// </summary>
public sealed class EsdeStore(string folder)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _lock = new();

    /// <summary>The store under the current settings folder.</summary>
    public static EsdeStore Default => new(Path.Combine(AppSettings.SettingsDirectory, "esde"));

    public string Folder { get; } = folder;

    private string StatePath => Path.Combine(Folder, "state.json");
    private string ListPath => Path.Combine(Folder, "es_systems.json");

    public EsdeState LoadState() => Read<EsdeState>(StatePath) ?? new EsdeState();

    public void SaveState(EsdeState state) => Write(StatePath, state);

    /// <summary>The cached list, or null when there's none (or it's unreadable).</summary>
    public EsdeSystemList? LoadList() => Read<EsdeSystemList>(ListPath) is { Systems.Count: > 0 } list ? list : null;

    public void SaveList(EsdeSystemList list) => Write(ListPath, list);

    private T? Read<T>(string path) where T : class
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            return null;
        }
    }

    private void Write<T>(string path, T value)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>"ES-DE systems: release v3.5.0, checked 2 hours ago (195 systems)."</summary>
    public string Describe()
    {
        var list = EsdeSystems.Load(this);
        var builtIn = ReferenceEquals(list, EsdeSystems.Snapshot) ? ", built in" : "";
        var checkedText = LoadState().LastCheckedUtc is { } t ? $"checked {DatStore.Ago(DateTime.UtcNow - t)}" : "not checked yet";
        var text = $"ES-DE systems: release {list.Ref}{builtIn}, {checkedText} ({list.Systems.Count} systems).";
        var problems = EsdeSystems.Problems(list);
        return problems.Count == 0 ? text : $"{text} {string.Join(" ", problems)}";
    }
}

/// <summary>Where a system's games go in the ES-DE layout: <see cref="Name"/> is null when they stay where they are.</summary>
public sealed record EsdeFolder(string? Name, string? Problem);

/// <summary>
/// ES-DE's system folders (docs/requirements/cleanup-wizard.md, FR-6): the
/// registry maps each of our systems to an ES-DE folder (<see cref="SystemInfo.Esde"/>),
/// checked against the latest list (the cache, else the bundled snapshot).
/// A folder ES-DE no longer has is reported and the snapshot's name used;
/// games are never filed into a folder neither list has.
/// </summary>
public static class EsdeSystems
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Lock CurrentLock = new();
    private static (string Folder, EsdeSystemList List)? _current;

    /// <summary>The list built into the app (Core/Esde/es_systems.json).</summary>
    public static EsdeSystemList Snapshot { get; } = LoadSnapshot();

    /// <summary>The list the Cleanup wizard uses: the cache under the current settings folder, else the snapshot.</summary>
    public static EsdeSystemList Current
    {
        get
        {
            var store = EsdeStore.Default;
            lock (CurrentLock)
            {
                if (_current is { } c && c.Folder == store.Folder) return c.List;
                var list = Load(store);
                _current = (store.Folder, list);
                return list;
            }
        }
    }

    /// <summary>Forgets the list in memory, after an update.</summary>
    public static void Reload()
    {
        lock (CurrentLock) _current = null;
    }

    /// <summary>A store's cached list, or the snapshot.</summary>
    public static EsdeSystemList Load(EsdeStore store) => store.LoadList() ?? Snapshot;

    /// <summary>The ES-DE folder for <paramref name="system"/>'s games, by the current list.</summary>
    public static string? FolderOf(SystemInfo system) => Resolve(system, Current).Name;

    /// <summary>
    /// <paramref name="system"/>'s ES-DE folder in <paramref name="list"/>: its
    /// mapped name when the list has it; otherwise the snapshot's, with a problem
    /// naming the likely new folder (same full name or extensions); otherwise none.
    /// </summary>
    public static EsdeFolder Resolve(SystemInfo system, EsdeSystemList list, EsdeSystemList? snapshot = null)
    {
        snapshot ??= Snapshot;
        if (system.Esde is not { } mapped) return new EsdeFolder(null, null);
        if (list.Find(mapped) is not null) return new EsdeFolder(mapped, null);
        var known = snapshot.Find(mapped);
        var renamed = known is null ? null : list.Systems.FirstOrDefault(s => s.FullName == known.FullName)
                                             ?? list.Systems.FirstOrDefault(s => s.Extensions.Count > 0 && s.Extensions.SequenceEqual(known.Extensions));
        var problem = $"ES-DE {list.Ref} has no \"{mapped}\" folder for {system.Name}"
                      + (renamed is null ? "" : $" (renamed to \"{renamed.Name}\"?)")
                      + (known is null ? "; its games stay where they are." : $"; using \"{mapped}\" from {snapshot.Ref}.");
        return new EsdeFolder(known is null ? null : mapped, problem);
    }

    /// <summary>Every mapped folder <paramref name="list"/> doesn't have, one line each (one per folder).</summary>
    public static IReadOnlyList<string> Problems(EsdeSystemList list, EsdeSystemList? snapshot = null) =>
        TargetFormats.All.Where(s => s.Esde is not null).DistinctBy(s => s.Esde)
            .Select(s => Resolve(s, list, snapshot).Problem).OfType<string>().ToList();

    private static EsdeSystemList LoadSnapshot()
    {
        using var stream = typeof(EsdeSystems).Assembly.GetManifestResourceStream("NoIntrovert.es_systems.json")
                           ?? throw new InvalidOperationException("es_systems.json is not embedded in the app.");
        return JsonSerializer.Deserialize<EsdeSystemList>(stream, JsonOptions)
               ?? throw new InvalidOperationException("es_systems.json is empty.");
    }
}
