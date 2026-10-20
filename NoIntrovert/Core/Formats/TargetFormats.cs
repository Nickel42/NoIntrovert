using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoIntrovert.Core.Formats;

/// <summary>How a system's target is stored on disk.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TargetContainer>))]
public enum TargetContainer
{
    /// <summary>The file on its own (.chd, .rvz, .wua, .nsz).</summary>
    [JsonStringEnumMemberName("bare")] Bare,
    [JsonStringEnumMemberName("7z")] SevenZip,
    [JsonStringEnumMemberName("zip")] Zip,
    /// <summary>An unpacked folder (ScummVM games, PS3 discs).</summary>
    [JsonStringEnumMemberName("folder")] Folder,
    /// <summary>Not touched at all.</summary>
    [JsonStringEnumMemberName("leave")] Leave,
}

/// <summary>
/// What a system's games should become: <see cref="Format"/> is the file
/// (inside the container, for 7z and zip), <see cref="Label"/> the guide's
/// wording. <see cref="Producible"/> is false where NoIntrovert can't make it yet (D8).
/// </summary>
public sealed record SystemTarget(string Format, TargetContainer Container, string Label, bool Producible);

/// <summary>Another target a system can be given instead (in Settings, Phase 2).</summary>
public sealed record TargetAlternative(string Id, string Label, bool Producible);

/// <summary>
/// One system in the registry. <see cref="Extensions"/> are the files it's
/// recognised by (an extension several systems share is decided by folder),
/// <see cref="Folders"/> the folder names people use for it, compared as
/// lower-case letters and digits only. <see cref="Dats"/> names the
/// libretro-database DATs that cover it ("no-intro/Nintendo - Game Boy").
/// </summary>
public sealed record SystemInfo(
    string Id,
    string Name,
    string Category,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Folders,
    SystemTarget Target,
    IReadOnlyList<TargetAlternative> Alternatives,
    IReadOnlyList<string> Rules,
    IReadOnlyDictionary<string, TargetContainer>? ContainerByExtension = null,
    IReadOnlyList<TargetContainer>? AlsoOptimal = null,
    string? DiscTarget = null,
    IReadOnlyList<string>? Dats = null)
{
    public bool Follows(string rule) => Rules.Contains(rule);

    /// <summary>The container a file with <paramref name="extension"/> goes in (MSX disks in zip, WHDLoad .lha bare).</summary>
    public TargetContainer ContainerFor(string extension) =>
        ContainerByExtension is not null && ContainerByExtension.TryGetValue(extension.ToLowerInvariant(), out var container) ? container : Target.Container;

    public bool IsOptimalContainer(TargetContainer container, string memberExtension) =>
        container == ContainerFor(memberExtension) || (AlsoOptimal?.Contains(container) ?? false);
}

/// <summary>
/// The target-format registry (Core/Formats/TargetFormats.json): one entry
/// per system in docs/target-formats.md, which a test keeps in step with it.
/// </summary>
public static class TargetFormats
{
    public const string UnknownName = "Unknown system";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static IReadOnlyList<SystemInfo> All { get; } = Load();

    private static readonly Dictionary<string, SystemInfo> ByIdMap = All.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly ILookup<string, SystemInfo> ByExtensionMap =
        All.SelectMany(s => s.Extensions.Select(e => (Ext: e, System: s))).ToLookup(p => p.Ext, p => p.System, StringComparer.OrdinalIgnoreCase);

    private static readonly ILookup<string, SystemInfo> ByFolderMap =
        All.SelectMany(s => s.Folders.Select(f => (Folder: f, System: s))).ToLookup(p => p.Folder, p => p.System, StringComparer.Ordinal);

    public static SystemInfo? ById(string? id) => id is null ? null : ByIdMap.GetValueOrDefault(id);

    /// <summary>A system's display name, or "Unknown system".</summary>
    public static string NameOf(string? id) => ById(id)?.Name ?? UnknownName;

    /// <summary>Every system that lists <paramref name="extension"/> (with the dot).</summary>
    public static IReadOnlyList<SystemInfo> ByExtension(string extension) => ByExtensionMap[extension].ToList();

    /// <summary>The systems a folder name stands for ("PS1", "Mega Drive"…), compared as letters and digits.</summary>
    public static IReadOnlyList<SystemInfo> ByFolder(string folderName) => ByFolderMap[FolderKey(folderName)].ToList();

    public static string FolderKey(string folderName) =>
        new string(folderName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static List<SystemInfo> Load()
    {
        using var stream = typeof(TargetFormats).Assembly.GetManifestResourceStream("NoIntrovert.TargetFormats.json")
                           ?? throw new InvalidOperationException("TargetFormats.json is not embedded in the app.");
        return JsonSerializer.Deserialize<List<SystemInfo>>(stream, JsonOptions)
               ?? throw new InvalidOperationException("TargetFormats.json is empty.");
    }
}
