using System.Text.Json;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Systems;

/// <summary>
/// What the File section says about a system: its full name ("Sony PlayStation"),
/// a short name ("PlayStation"), maker, first release year, generation and media
/// tags, the console picture's file name, and the "Formats for" paragraph.
/// A null <see cref="Year"/>, <see cref="Generation"/> or <see cref="Media"/> is left out.
/// </summary>
public sealed record SystemFact(
    string Id,
    string Name,
    string Short,
    string Manufacturer,
    int? Year,
    string? Generation,
    string? Media,
    string Icon,
    string Formats)
{
    /// <summary>"Sony Computer Entertainment · 1994", or just the maker when the year isn't known.</summary>
    public string Subtitle => Year is { } y ? $"{Manufacturer} · {y}" : Manufacturer;
}

/// <summary>
/// The hand-checked system facts (Core/Systems/SystemFacts.json) and format
/// paragraphs (Core/Systems/SystemFormats.json), keyed by the ids in
/// Core/Formats/TargetFormats.json.
/// </summary>
public static class SystemFacts
{
    private sealed record Entry(string Id, string Name, string Short, string Manufacturer, int? Year, string? Generation, string? Media, string Icon);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Dictionary<string, SystemFact> ById = Load();

    /// <summary>Every system with facts.</summary>
    public static IReadOnlyCollection<SystemFact> All => ById.Values;

    /// <summary>The facts for <paramref name="systemId"/>, or null for an unknown or missing id.</summary>
    public static SystemFact? For(string? systemId) => systemId is null ? null : ById.GetValueOrDefault(systemId);

    /// <summary>
    /// The facts for <paramref name="systemId"/>; a registry system without
    /// hand-checked facts gets its registry name and nothing else, and an
    /// unknown id gets "Unknown system".
    /// </summary>
    public static SystemFact ForOrFallback(string? systemId) =>
        For(systemId) ?? new SystemFact(systemId ?? "", TargetFormats.NameOf(systemId), TargetFormats.NameOf(systemId), "", null, null, null, "", "");

    private static Dictionary<string, SystemFact> Load()
    {
        var entries = Read<List<Entry>>("NoIntrovert.SystemFacts.json");
        var formats = Read<Dictionary<string, string>>("NoIntrovert.SystemFormats.json");
        return entries.ToDictionary(
            e => e.Id,
            e => new SystemFact(e.Id, e.Name, e.Short, e.Manufacturer, e.Year, e.Generation, e.Media, e.Icon, formats.GetValueOrDefault(e.Id) ?? ""),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The format paragraphs by id, as written (for the test that checks every system has one).</summary>
    internal static IReadOnlyDictionary<string, string> FormatParagraphs() => Read<Dictionary<string, string>>("NoIntrovert.SystemFormats.json");

    private static T Read<T>(string resource)
    {
        using var stream = typeof(SystemFacts).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"{resource} is not embedded in the app.");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidOperationException($"{resource} is empty.");
    }
}
