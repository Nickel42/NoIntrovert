using System.Text.Json;
using System.Text.Json.Serialization;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Library;

/// <summary>Where a game stands, before any DAT matching.</summary>
public enum GameStatus
{
    /// <summary>Has a source (cue/bin, ISO, title folder, a ROM not in its 7z…) and nothing converted yet.</summary>
    NeedsConverting,
    /// <summary>Converted (CHD, RVZ, .wua), but the source files are still here too.</summary>
    OriginalsRemain,
    /// <summary>Only converted files.</summary>
    Converted,
    /// <summary>The same disc in its converted form more than once (two folders, two copies).</summary>
    Duplicate,
    /// <summary>Nothing to convert: everything is optimal, left as is, or of an unknown system.</summary>
    Ready,
}

/// <summary>
/// One input the Library found: what the format registry saw, plus what the
/// scan worked out (system id, title, which game it belongs to, and where it
/// stands against its system's target: <see cref="FormatStatus"/>). Saved in the index,
/// and reused on the next scan while the files' size and date don't change.
/// </summary>
public sealed record LibraryItem(
    string Path,
    IReadOnlyList<string> Members,
    string FormatId,
    IReadOnlyList<string>? Inside,
    long Size,
    long ModifiedTicks,
    string Root,
    string? System,
    string Title,
    string GameKey,
    string Disc,
    TargetKind Target,
    TargetStatus FormatStatus,
    string FormatStatusText)
{
    public FormatInfo? Format => KnownFormats.ById(FormatId);

    [JsonIgnore]
    public string SystemName => TargetFormats.NameOf(System);

    /// <summary>In its converted form: a CHD, an RVZ or a .wua.</summary>
    [JsonIgnore]
    public bool IsFinal => FormatId is "chd" or "rvz" or "wua";

    /// <summary>Something the Convert page would turn into CHD, RVZ or .wua.</summary>
    [JsonIgnore]
    public bool IsSource => !IsFinal && Target is TargetKind.Chd or TargetKind.Rvz or TargetKind.Wua;

    /// <summary>How the format reads in a list: "cue/bin", "CHD", "zip", ".wua", "NES"…</summary>
    [JsonIgnore]
    public string FormatLabel => FormatId switch
    {
        "cue" => "cue/bin",
        "wiiu-folder" => "title folder",
        "nus" => "NUS",
        "wiiu-disc" => Path.EndsWith(".wux", StringComparison.OrdinalIgnoreCase) ? "WUX" : "WUD",
        "wua" => ".wua",
        "split" => "split file",
        "folder" => "folder",
        var id => Format?.Name ?? id.ToUpperInvariant(),
    };
}

/// <summary>A game: every library item that's the same title on the same system (all its discs and copies).</summary>
public sealed record Game(string Key, string Title, string? System, IReadOnlyList<LibraryItem> Items)
{
    public string SystemName => TargetFormats.NameOf(System);

    public long Size => Items.Sum(i => i.Size);

    public string Formats => string.Join(", ", Items.Select(i => i.FormatLabel).Distinct());

    /// <summary>How many discs, by their disc labels (a single-disc game has one, unlabelled).</summary>
    public int Discs => Math.Max(1, Items.Select(i => i.Disc).Distinct().Count());

    public GameStatus Status
    {
        get
        {
            var final = Items.Where(i => i.IsFinal).ToList();
            if (final.GroupBy(i => (i.Disc, i.FormatId)).Any(g => g.Count() > 1)) return GameStatus.Duplicate;
            var sources = Items.Any(i => i.IsSource || i.FormatStatus == TargetStatus.Convert);
            if (final.Count > 0 && sources) return GameStatus.OriginalsRemain;
            if (sources) return GameStatus.NeedsConverting;
            return final.Count > 0 ? GameStatus.Converted : GameStatus.Ready;
        }
    }
}

/// <summary>Everything the Library knows: the items found in its folders, saved between runs.</summary>
public sealed class LibraryIndex
{
    /// <summary>2: systems are registry ids, and each item has its target-format status.</summary>
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public DateTime ScannedUtc { get; set; }
    public List<LibraryItem> Items { get; set; } = [];

    /// <summary>Folders that couldn't be read at the last scan (unplugged drive, cloud folder offline).</summary>
    public List<string> MissingFolders { get; set; } = [];

    /// <summary>The items grouped into games, sorted by title.</summary>
    public List<Game> Games() =>
        Items.GroupBy(i => i.GameKey, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // The title the game shows: a base game's own name where Wii U titles disagree, else the first.
                var first = g.OrderBy(i => i.Disc, StringComparer.OrdinalIgnoreCase).First();
                return new Game(g.Key, first.Title, first.System, g.OrderBy(i => i.Disc).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList());
            })
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string IndexPath => Path.Combine(AppSettings.SettingsDirectory, "library.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The saved index, or an empty one (first run, or a file from another version).</summary>
    public static LibraryIndex Load()
    {
        try
        {
            if (File.Exists(IndexPath) &&
                JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(IndexPath), JsonOptions) is { SchemaVersion: CurrentSchema } index)
                return index;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged index is rebuilt by the next scan.
        }
        return new LibraryIndex();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            var temp = IndexPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
