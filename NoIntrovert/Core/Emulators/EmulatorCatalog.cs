using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NoIntrovert.Core.Emulators;

/// <summary>Where part of the snapshot came from (libretro core info, ES-DE).</summary>
public sealed class EmulatorDataSource
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Ref { get; init; }
    public string? Commit { get; init; }
    /// <summary>The source's last change, as yyyy-MM-dd, when known.</summary>
    public string? Date { get; init; }
    public string License { get; init; } = "";
}

/// <summary>One RetroArch core from libretro-core-info: the NoIntrovert systems it runs and the extensions it opens.</summary>
public sealed class RetroArchCore
{
    /// <summary>The .info file's name without "_libretro" ("mednafen_psx_hw").</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Systems { get; init; } = [];
    /// <summary>Lower case, without the dot.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public string License { get; init; } = "";
    /// <summary>True when RetroArch hands .zip/.7z to the core as is instead of unpacking it first.</summary>
    public bool BlockExtract { get; init; }
}

/// <summary>A standalone emulator ES-DE knows, by its EMULATOR_ name ("DUCKSTATION").</summary>
public sealed class StandaloneEmulator
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>False when es_find_rules.xml has no rule to find it.</summary>
    public bool Findable { get; init; } = true;
}

/// <summary>One ES-DE system: its extensions (for all of its emulators together) and its standalone emulators in ES-DE's order.</summary>
public sealed class EmulatorSystem
{
    public string Esde { get; init; } = "";
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public IReadOnlyList<string> Standalone { get; init; } = [];
}

/// <summary>The generated snapshot (Data/emulators.json, written by scripts/refresh-emulator-data.py).</summary>
public sealed class EmulatorSnapshot
{
    public DateOnly Generated { get; init; }
    public IReadOnlyList<EmulatorDataSource> Sources { get; init; } = [];
    public IReadOnlyList<RetroArchCore> Cores { get; init; } = [];
    public IReadOnlyList<StandaloneEmulator> Standalone { get; init; } = [];
    /// <summary>Keyed by NoIntrovert system id ("playstation").</summary>
    public IReadOnlyDictionary<string, EmulatorSystem> Systems { get; init; } = new Dictionary<string, EmulatorSystem>();
}

/// <summary>A standalone emulator no catalog lists for Windows (ePSXe, PCSX-Redux), with the extensions it opens.</summary>
public sealed class ExtraStandalone
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Systems { get; init; } = [];
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public string Source { get; init; } = "";
}

/// <summary>
/// One cited fact: whether <see cref="Emulator"/> opens <see cref="Extension"/>,
/// or reads CHDs made with <see cref="Codec"/>. <see cref="Supported"/> null means not confirmed.
/// </summary>
public sealed class EmulatorFact
{
    public string Emulator { get; init; } = "";
    /// <summary>"core" or "standalone".</summary>
    public string Kind { get; init; } = "";
    /// <summary>The NoIntrovert systems it applies to; null for all.</summary>
    public IReadOnlyList<string>? Systems { get; init; }
    public string? Extension { get; init; }
    public string? Codec { get; init; }
    public bool? Supported { get; init; }
    public string? Detail { get; init; }
    public string Source { get; init; } = "";
}

/// <summary>The hand-kept facts (Data/emulator-facts.json): codec support, overrides and the popularity order.</summary>
public sealed class EmulatorFacts
{
    public DateOnly Updated { get; init; }
    /// <summary>Codecs every CHD reader handles (chdman's defaults).</summary>
    public IReadOnlyList<string> BaselineCodecs { get; init; } = [];
    /// <summary>Codecs that share one fact ("cdzs" and "zstd" are both Zstandard).</summary>
    public IReadOnlyDictionary<string, string> CodecFamilies { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> CodecNames { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> HiddenCores { get; init; } = [];
    public IReadOnlyList<string> HiddenStandalone { get; init; } = [];
    public IReadOnlyList<string> PopularCores { get; init; } = [];
    public IReadOnlyList<string> PopularStandalone { get; init; } = [];
    public IReadOnlyList<ExtraStandalone> ExtraStandalone { get; init; } = [];
    public IReadOnlyList<EmulatorFact> Facts { get; init; } = [];
}

/// <summary>What <see cref="EmulatorCatalog.CheckNowAsync"/> did.</summary>
public sealed record EmulatorCheckResult(bool Updated, string Source, string? Problem);

/// <summary>
/// The emulator data behind "Works with": the generated snapshot of
/// libretro-core-info and ES-DE plus the hand-kept facts, both embedded in
/// the app. A newer copy downloaded by <see cref="CheckNowAsync"/> to
/// <see cref="Folder"/> wins over the embedded one.
/// </summary>
public sealed class EmulatorCatalog
{
    public const string SnapshotFile = "emulators.json";
    public const string FactsFile = "emulator-facts.json";

    /// <summary>Where <see cref="CheckNowAsync"/> gets newer copies: the files in the app's repository, refreshed by scripts/refresh-emulator-data.py.</summary>
    public const string UpdateUrl = "https://raw.githubusercontent.com/Nickel42/NoIntrovert/main/NoIntrovert/Core/Emulators/Data/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static EmulatorCatalog? current;

    public EmulatorSnapshot Snapshot { get; }
    public EmulatorFacts Facts { get; }

    public EmulatorCatalog(EmulatorSnapshot snapshot, EmulatorFacts facts)
    {
        Snapshot = snapshot;
        Facts = facts;
    }

    /// <summary>The settings folder's copy of newer downloads.</summary>
    public static string Folder => Path.Combine(AppSettings.SettingsDirectory, "emulators");

    /// <summary>The catalog the app uses, loaded on first use.</summary>
    public static EmulatorCatalog Current
    {
        get => Volatile.Read(ref current) ?? Reload();
    }

    /// <summary>Loads the catalog again (after a download) and makes it <see cref="Current"/>.</summary>
    public static EmulatorCatalog Reload()
    {
        var catalog = Load();
        Volatile.Write(ref current, catalog);
        return catalog;
    }

    /// <summary>The embedded data, or the settings folder's copy of a file where that one is newer.</summary>
    public static EmulatorCatalog Load()
    {
        var snapshot = Embedded<EmulatorSnapshot>(SnapshotFile);
        if (FromFolder<EmulatorSnapshot>(SnapshotFile) is { } s && s.Generated > snapshot.Generated) snapshot = s;
        var facts = Embedded<EmulatorFacts>(FactsFile);
        if (FromFolder<EmulatorFacts>(FactsFile) is { } f && f.Updated > facts.Updated) facts = f;
        return new EmulatorCatalog(snapshot, facts);
    }

    /// <summary>The footer text, e.g. "From libretro core info and ES-DE · 4 Nov 2026".</summary>
    public string SourceText
    {
        get
        {
            var names = Snapshot.Sources.Select(s => s.Name).Where(n => n.Length > 0).ToList();
            var from = names.Count switch
            {
                0 => "From the bundled emulator list",
                1 => "From " + names[0],
                _ => "From " + string.Join(", ", names[..^1]) + " and " + names[^1],
            };
            return $"{from} · {FormatDate(Snapshot.Generated)}";
        }
    }

    /// <summary>"4 Nov 2026", the same in every culture.</summary>
    public static string FormatDate(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// Downloads the latest snapshot and facts (from <see cref="UpdateUrl"/>)
    /// into <see cref="Folder"/>, keeps each only if it parses and is newer
    /// than what the app has, then reloads <see cref="Current"/>.
    /// </summary>
    public static async Task<EmulatorCheckResult> CheckNowAsync(CancellationToken token = default, HttpMessageHandler? handler = null)
    {
        await OneAtATime.WaitAsync(token);
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NoIntrovert", "1.0"));
            var before = Current;
            var updated = false;
            string? problem = null;
            try
            {
                var snapshotText = await http.GetStringAsync(UpdateUrl + SnapshotFile, token);
                var snapshot = Parse<EmulatorSnapshot>(snapshotText);
                var factsText = await http.GetStringAsync(UpdateUrl + FactsFile, token);
                var facts = Parse<EmulatorFacts>(factsText);
                Directory.CreateDirectory(Folder);
                if (snapshot.Generated > before.Snapshot.Generated)
                {
                    await File.WriteAllTextAsync(Path.Combine(Folder, SnapshotFile), snapshotText, token);
                    updated = true;
                }
                if (facts.Updated > before.Facts.Updated)
                {
                    await File.WriteAllTextAsync(Path.Combine(Folder, FactsFile), factsText, token);
                    updated = true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UnauthorizedAccessException
                                           or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                problem = ex is TaskCanceledException ? "GitHub didn't answer in time" : ex.Message;
            }
            var after = updated ? Reload() : before;
            return new EmulatorCheckResult(updated, after.SourceText, problem);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    internal static T Parse<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new JsonException($"{typeof(T).Name} is empty");

    private static T Embedded<T>(string file) where T : class
    {
        using var stream = typeof(EmulatorCatalog).Assembly.GetManifestResourceStream("NoIntrovert.Emulators." + file)
                           ?? throw new InvalidOperationException($"{file} is not embedded in the app.");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidOperationException($"{file} is empty.");
    }

    /// <summary>A downloaded copy, or null when there's none or it can't be read.</summary>
    private static T? FromFolder<T>(string file) where T : class
    {
        try
        {
            var path = Path.Combine(Folder, file);
            return File.Exists(path) ? Parse<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
