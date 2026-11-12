using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NoIntrovert.Core;

/// <summary>
/// Everything the app remembers between runs, saved as JSON under
/// %AppData%\NoIntrovert, in one section per area. A settings file from
/// before the sections (schema 1, every value at the top level) is converted
/// once when it's loaded, and a copy of it is kept as settings.v1.json.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; set; } = CurrentSchema;

    public GeneralSettings General { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public ChdSettings Chd { get; set; } = new();
    public NintendoSettings Nintendo { get; set; } = new();
    public ArchiveSettings Archives { get; set; } = new();
    public KeySettings Keys { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
    public LibrarySettings Library { get; set; } = new();
    public DatSettings Dats { get; set; } = new();
    public TargetFormatSettings TargetFormats { get; set; } = new();

    /// <summary>The preset chosen for each action (issue #33). A file from before presets gets them inferred when it loads.</summary>
    public Presets.PresetSettings Presets { get; set; } = new();

    /// <summary>
    /// Where settings and the library index live: %AppData%\NoIntrovert, or
    /// NOINTROVERT_SETTINGS_DIR when set (tests, screenshots, a portable copy).
    /// </summary>
    public static string SettingsDirectory =>
        SettingsDirectoryOverride
        ?? (Environment.GetEnvironmentVariable("NOINTROVERT_SETTINGS_DIR") is { Length: > 0 } dir ? dir : null)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoIntrovert");

    /// <summary>Tests point this at a scratch folder so they never touch the real settings file.</summary>
    internal static string? SettingsDirectoryOverride { get; set; }

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Where each session's log is saved (Settings › Log › Open log folder).</summary>
    public static string LogDirectory => Path.Combine(SettingsDirectory, "logs");

    /// <summary>Where a schema-1 file is kept after it's converted.</summary>
    internal static string V1BackupPath => Path.Combine(SettingsDirectory, "settings.v1.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            if (JsonNode.Parse(json) is JsonObject root && !root.ContainsKey(nameof(SchemaVersion)))
            {
                // Schema 1: keep the original, convert, and save the new layout.
                var migrated = FromV1(JsonSerializer.Deserialize<SettingsV1>(json, JsonOptions) ?? new SettingsV1());
                migrated.Presets = Core.Presets.ActionPresets.Infer(migrated);
                Core.Presets.FormatPresets.Migrate(migrated);
                if (!File.Exists(V1BackupPath)) File.Copy(SettingsPath, V1BackupPath);
                migrated.Save();
                return migrated;
            }
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            if (JsonNode.Parse(json) is JsonObject loaded)
            {
                var migrated = false;
                // Before presets: pick the preset each action's values already equal, or Custom, so nothing changes.
                if (!loaded.ContainsKey(nameof(Presets)))
                {
                    settings.Presets = Core.Presets.ActionPresets.Infer(settings);
                    migrated = true;
                }
                // Before Save to: the old "remove originals" switches and presets, and the output folder.
                migrated |= OutputSettings.Migrate(loaded, settings);
                // Before format presets: each action's chosen preset seeds the systems' defaults.
                if (loaded[nameof(Presets)] is not JsonObject presets || !presets.ContainsKey(nameof(Core.Presets.PresetSettings.DefaultBySystem)))
                {
                    Core.Presets.FormatPresets.Migrate(settings);
                    migrated = true;
                }
                // Saved straight away, so a migration runs once rather than on every start.
                if (migrated) settings.Save();
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file just means defaults.
        }
        return new AppSettings();
    }

    /// <summary>
    /// A copy that's edited but never written to disk, e.g. one row's Custom
    /// values behind the Preset settings controls. Not saved, and not copied by <see cref="Clone"/>.
    /// </summary>
    [JsonIgnore] public bool IsScratch { get; init; }

    public void Save()
    {
        if (IsScratch) return;
        try
        {
            var json = ToJson();
            if (File.Exists(SettingsPath) && File.ReadAllText(SettingsPath) == json) return; // nothing changed
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The JSON <see cref="Save"/> writes; comparing two of these tells whether anything changed.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>A deep copy: runs work on one, so later edits don't reach a running batch.</summary>
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(ToJson(), JsonOptions)!;

    /// <summary>Maps every schema-1 value to its section.</summary>
    internal static AppSettings FromV1(SettingsV1 v) => new()
    {
        General = new()
        {
            VerboseLog = v.VerboseLog, LogProgress = v.LogProgress, WindowWidth = v.WindowWidth, WindowHeight = v.WindowHeight,
        },
        Output = new()
        {
            Folder = v.OutputFolder, Overwrite = v.Overwrite, ScanSubfolders = v.ScanSubfolders, UseRecycleBin = v.UseRecycleBin,
            // Schema 1 had no presets, so a switch that was on removed originals. Replace stays, but is asked before its first run.
            SaveTo = v.DeleteOriginals || v.ArchiveDeleteOriginals || v.WiiUDeleteOriginals ? SaveTo.ReplaceOriginals
                : string.IsNullOrWhiteSpace(v.OutputFolder) ? SaveTo.NextToOriginals : SaveTo.Folder,
        },
        Performance = new() { ParallelJobs = v.ParallelJobs, ThreadsPerJob = v.ThreadsPerJob, TempFolder = v.TempFolder },
        Chd = new()
        {
            Compression = v.Compression, HunkSize = v.HunkSize, IsoMode = v.IsoMode, VerifyAfterCreate = v.VerifyAfterCreate,
            ExtractFormat = v.ExtractFormat, SplitBin = v.SplitBin, FixHeaderSha1 = v.FixHeaderSha1,
        },
        Nintendo = new()
        {
            Target = v.NintendoTarget, Compression = v.RvzCompression, CompressionLevel = v.RvzCompressionLevel,
            BlockSize = v.RvzBlockSize, Scrub = v.RvzScrub,
        },
        Archives = new()
        {
            Action = v.ArchiveAction, Format = v.ArchiveFormat, Level = v.ArchiveLevel, Solid = v.ArchiveSolid,
            Grouping = v.ArchiveGrouping, SingleName = v.ArchiveSingleName, ConvertTarget = v.ArchiveConvertTarget,
            TestAfter = v.ArchiveTestAfter, ExtractToOwnFolder = v.ArchiveExtractToOwnFolder,
        },
        Keys = new() { WiiUKeysPath = v.WiiUKeysPath },
        Diagnostics = new() { ChdmanExePath = v.ChdmanExePath },
    };
}

/// <summary>How the app looks and logs.</summary>
public sealed class GeneralSettings
{
    /// <summary>Shows Expert options, raw library messages and the developer test bench.</summary>
    public bool PowerUserMode { get; set; }

    public bool VerboseLog { get; set; }
    public bool LogProgress { get; set; }
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 820;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>The theme used when the app is dark, by id (Core/Themes/ThemeLibrary): a built-in one, or "user:" and a file in the themes folder.</summary>
    public string DarkTheme { get; set; } = Themes.ThemeLibrary.DefaultDarkId;

    /// <summary>The theme used when the app is light, by id, as <see cref="DarkTheme"/>.</summary>
    public string LightTheme { get; set; } = Themes.ThemeLibrary.DefaultLightId;

    /// <summary>The page the app opens on, by page id; empty = Optimize once the library has folders, otherwise Convert.</summary>
    public string StartPage { get; set; } = "";

    /// <summary>Whether the log is expanded (Show log) rather than collapsed to its bar.</summary>
    public bool LogExpanded { get; set; }

    /// <summary>Expands the log when a run logs an error.</summary>
    public bool OpenLogOnErrors { get; set; } = true;

    /// <summary>Days a saved session log is kept in <see cref="AppSettings.LogDirectory"/>.</summary>
    public int KeepLogsDays { get; set; } = 30;
}

public enum AppTheme { System, Dark, Light }

/// <summary>Where a run saves its results: the run bar's three choices, and the default Settings gives them.</summary>
public enum SaveTo
{
    /// <summary>In each source's own folder.</summary>
    NextToOriginals,
    /// <summary>In <see cref="OutputSettings.Folder"/>.</summary>
    Folder,
    /// <summary>Next to the originals, and each original is removed once its new file verifies.</summary>
    ReplaceOriginals,
}

/// <summary>Where results go, and what happens to the files they were made from.</summary>
public sealed class OutputSettings
{
    /// <summary>The run bar's default. This is the only setting that removes originals.</summary>
    public SaveTo SaveTo { get; set; } = SaveTo.NextToOriginals;

    /// <summary>Set once the user has confirmed Replace the originals, so it's asked only the first time.</summary>
    public bool ReplaceConfirmed { get; set; }

    public string Folder { get; set; } = ""; // the folder for SaveTo.Folder; the last one used

    /// <summary>Replace a file that's in the way; off, a taken name gets " (2)" (see <see cref="Jobs.OutputPaths.Available"/>).</summary>
    public bool Overwrite { get; set; }
    public bool ScanSubfolders { get; set; } = true;

    public bool UseRecycleBin { get; set; } = true;

    /// <summary>
    /// For one run, never set by the user: the sources mustn't change at all,
    /// not even renamed for a moment (the Cleanup wizard building a new
    /// library, FR-8). Kept through <see cref="AppSettings.Clone"/>, written only when on.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LeaveSourcesAlone { get; set; }

    /// <summary>Where jobs write: <see cref="Folder"/> when Save to is Folder (and one is chosen), otherwise null for each source's own folder.</summary>
    [JsonIgnore] public string? TargetFolder => SaveTo == SaveTo.Folder && !string.IsNullOrWhiteSpace(Folder) ? Folder : null;

    /// <summary>Whether jobs remove each original once its new file verifies (or its archive tests OK).</summary>
    [JsonIgnore] public bool ReplacesOriginals => SaveTo == SaveTo.ReplaceOriginals;

    /// <summary>
    /// A settings file from before Save to: when the user really had originals
    /// removed, Save to becomes Replace the originals, not yet confirmed (so the
    /// first run asks); otherwise a chosen output folder becomes Folder.
    /// "Really" means an action's chosen preset was a Replace one (Unpack's
    /// "remove"), or it was Custom with that action's "Remove the originals"
    /// switch on: every other preset wrote the switches off, so a switch left on
    /// from Custom did nothing. Before presets the switches always counted.
    /// The Replace presets go back to each action's preferred one.
    /// </summary>
    /// <returns>Whether anything was migrated (false for a file that already has Save to).</returns>
    internal static bool Migrate(JsonObject root, AppSettings settings)
    {
        if (root[nameof(AppSettings.Output)] is JsonObject output && output.ContainsKey(nameof(SaveTo))) return false;
        var o = root[nameof(AppSettings.Output)] as JsonObject;
        bool On(string key) => o?[key] is JsonValue v && v.TryGetValue<bool>(out var on) && on;
        var hadPresets = root.ContainsKey(nameof(AppSettings.Presets));
        var removed = !hadPresets && LegacyRemoveSwitches.Any(On);
        foreach (var (action, id, @switch) in LegacyReplacePresets)
        {
            if (!settings.Presets.Chosen.TryGetValue(action, out var chosen)) continue;
            if (chosen == id)
            {
                settings.Presets.Chosen.Remove(action);
                removed = true;
            }
            else if (chosen == Core.Presets.ActionPreset.CustomId && On(@switch)) removed = true;
        }
        settings.Output.SaveTo = removed ? SaveTo.ReplaceOriginals : !string.IsNullOrWhiteSpace(settings.Output.Folder) ? SaveTo.Folder : SaveTo.NextToOriginals;
        return true;
    }

    /// <summary>The old Output switches that removed originals.</summary>
    internal static readonly string[] LegacyRemoveSwitches = ["RemoveOriginals", "RemoveArchiveOriginals", "RemoveWiiUOriginals"];

    /// <summary>The old presets that removed originals, by action, and the switch that action's Custom used.</summary>
    internal static readonly (string Action, string Id, string Switch)[] LegacyReplacePresets =
    [
        ("Chd", "replace", "RemoveOriginals"), ("Rvz", "replace", "RemoveOriginals"), ("Wua", "replace", "RemoveWiiUOriginals"),
        ("Archive", "replace", "RemoveArchiveOriginals"), ("Unpack", "remove", "RemoveArchiveOriginals"),
    ];
}

/// <summary>How much of the machine the queue uses.</summary>
public sealed class PerformanceSettings
{
    /// <summary>Jobs the shared queue runs at once, across every tab. New settings start at 2; a saved file keeps its value.</summary>
    public int ParallelJobs { get; set; } = 2;

    public int ThreadsPerJob { get; set; } // 0 = all processors
    public string TempFolder { get; set; } = ""; // empty = %TEMP%\NoIntrovert

    [JsonIgnore] public string EffectiveTempFolder => string.IsNullOrWhiteSpace(TempFolder) ? WorkPaths.DefaultTempRoot : TempFolder;
}

/// <summary>Creating, verifying and extracting CHDs (lib-chd-kit).</summary>
public sealed class ChdSettings
{
    public string Compression { get; set; } = ""; // empty = chdman's default for the media type
    public uint HunkSize { get; set; }            // 0 = default
    public IsoMode IsoMode { get; set; } = IsoMode.Auto;
    public bool VerifyAfterCreate { get; set; } = true;
    public ExtractFormat ExtractFormat { get; set; } = ExtractFormat.Auto;
    public bool SplitBin { get; set; } = true;
    public bool FixHeaderSha1 { get; set; }
}

/// <summary>GameCube and Wii discs (lib-wii-gamecube-kit); the defaults are Dolphin's.</summary>
public sealed class NintendoSettings
{
    public Jobs.NintendoTarget Target { get; set; } = Jobs.NintendoTarget.Rvz;
    public LibWiiGameCubeKit.RvzCompression Compression { get; set; } = LibWiiGameCubeKit.RvzCompression.Zstd;
    public int CompressionLevel { get; set; } = 5;
    public uint BlockSize { get; set; } = 0x20000;
    public bool Scrub { get; set; }
}

/// <summary>7z and zip archives (7-Zip's engine).</summary>
public sealed class ArchiveSettings
{
    public Archives.ArchiveAction Action { get; set; } = Archives.ArchiveAction.Compress;
    public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZip;
    public ArchiveLevel Level { get; set; } = ArchiveLevel.Normal;
    public bool Solid { get; set; } = true;
    public Archives.ArchiveGrouping Grouping { get; set; } = Archives.ArchiveGrouping.PerItem;
    public string SingleName { get; set; } = "Archive";
    public Archives.ConvertTarget ConvertTarget { get; set; } = Archives.ConvertTarget.Swap;
    public bool TestAfter { get; set; } = true;
    public bool ExtractToOwnFolder { get; set; } = true;
}

/// <summary>Keys the user supplies. NoIntrovert never ships any.</summary>
public sealed class KeySettings
{
    public string WiiUKeysPath { get; set; } = ""; // empty = look in the usual places

    /// <summary>
    /// A folder holding your 3DS keys (boot9.bin, aes_keys.txt, seeddb.bin), searched before the usual
    /// places (Azahar's and Citra's sysdata, ~/.3ds); empty = only the usual places.
    /// </summary>
    public string ThreeDsKeysFolder { get; set; } = "";
}

/// <summary>Where the Cleanup wizard puts games inside a Library folder.</summary>
public enum LibraryLayout
{
    /// <summary>
    /// ES-DE format: <c>roms/&lt;system&gt;/&lt;game&gt;</c> in each Library folder, with ES-DE's
    /// system names (nes, psx, wiiu…). Only games that have to be folders stay folders.
    /// </summary>
    EsDe,
    /// <summary>Keep the current layout: each file stays in its folder; it's only renamed (and converted).</summary>
    InPlace,
}

/// <summary>The folders the Library page keeps track of.</summary>
public sealed class LibrarySettings
{
    public List<string> Folders { get; set; } = [];

    /// <summary>Look through the folders again (only changed files are reread) when the app starts.</summary>
    public bool RescanOnStart { get; set; } = true;

    /// <summary>Where the Cleanup wizard puts each game (M7 Q1, FR-6): ES-DE format unless you choose otherwise.</summary>
    public LibraryLayout Layout { get; set; } = LibraryLayout.EsDe;

    /// <summary>The folder the Cleanup wizard last built a new library in (FR-8); empty until one is chosen.</summary>
    public string NewLibraryFolder { get; set; } = "";
}

/// <summary>
/// Your changes to the recommended target formats (docs/target-formats.md,
/// FR-13), and the options of the rules. A settings file without this
/// section loads with these defaults, so the schema version stays 2.
/// </summary>
public sealed class TargetFormatSettings
{
    public List<TargetOverride> Overrides { get; set; } = [];

    /// <summary>R7: the subfolder multi-disc games' discs go in, beside their .m3u.</summary>
    public string MultiDiscFolder { get; set; } = "hidden";

    /// <summary>R12, D6: fix byte order and copier headers (.v64 → .z64, …).</summary>
    public bool NormalizeHeaders { get; set; }

    /// <summary>D1: Nintendo DS ROMs bigger than this stay bare; 0 = off.</summary>
    public int DsBareAboveMb { get; set; }

    /// <summary>R11: keep the original No-Intro/Redump hashes of converted files in the library index.</summary>
    public bool RecordOriginalHashes { get; set; } = true;

    public bool WriteVitaLauncher { get; set; }
    public string VitaLauncherExtension { get; set; } = ".psvita";
}

/// <summary>
/// One change to a target: for a system (a registry id, or "*" for every
/// system), optionally only for one file type, the target to use instead:
/// "recommended", "leave", or one of the system's alternatives ("zip", "7z",
/// "bare", "xcz"…).
/// </summary>
public sealed record TargetOverride(string System, string? Extension, string Target);

/// <summary>The game databases (No-Intro and Redump DATs) Verify &amp; match uses.</summary>
public sealed class DatSettings
{
    /// <summary>Look for newer DATs from libretro-database when the app starts (in the background).</summary>
    public bool CheckOnStart { get; set; } = true;

    /// <summary>A folder of the user's own DATs, which win over downloaded ones with the same name; empty = none.</summary>
    public string OwnFolder { get; set; } = "";
}

/// <summary>Developer tools: the lib-chd-kit test bench.</summary>
public sealed class DiagnosticsSettings
{
    public string ChdmanExePath { get; set; } = ""; // for the comparison tests only
}

/// <summary>The settings file before sections (schema 1): read once, to convert it.</summary>
internal sealed class SettingsV1
{
    public string OutputFolder { get; set; } = "";
    public bool Overwrite { get; set; }
    public bool ScanSubfolders { get; set; } = true;
    public string Compression { get; set; } = "";
    public uint HunkSize { get; set; }
    public IsoMode IsoMode { get; set; } = IsoMode.Auto;
    public bool VerifyAfterCreate { get; set; } = true;
    public bool DeleteOriginals { get; set; }
    public bool UseRecycleBin { get; set; } = true;
    public Jobs.NintendoTarget NintendoTarget { get; set; } = Jobs.NintendoTarget.Rvz;
    public LibWiiGameCubeKit.RvzCompression RvzCompression { get; set; } = LibWiiGameCubeKit.RvzCompression.Zstd;
    public int RvzCompressionLevel { get; set; } = 5;
    public uint RvzBlockSize { get; set; } = 0x20000;
    public bool RvzScrub { get; set; }
    public ExtractFormat ExtractFormat { get; set; } = ExtractFormat.Auto;
    public bool SplitBin { get; set; } = true;
    public bool FixHeaderSha1 { get; set; }
    public Archives.ArchiveAction ArchiveAction { get; set; } = Archives.ArchiveAction.Compress;
    public ArchiveFormat ArchiveFormat { get; set; } = ArchiveFormat.SevenZip;
    public ArchiveLevel ArchiveLevel { get; set; } = ArchiveLevel.Normal;
    public bool ArchiveSolid { get; set; } = true;
    public Archives.ArchiveGrouping ArchiveGrouping { get; set; } = Archives.ArchiveGrouping.PerItem;
    public string ArchiveSingleName { get; set; } = "Archive";
    public Archives.ConvertTarget ArchiveConvertTarget { get; set; } = Archives.ConvertTarget.Swap;
    public bool ArchiveTestAfter { get; set; } = true;
    public bool ArchiveDeleteOriginals { get; set; }
    public bool ArchiveExtractToOwnFolder { get; set; } = true;
    public bool WiiUDeleteOriginals { get; set; }
    public string WiiUKeysPath { get; set; } = "";
    public int ParallelJobs { get; set; } = 2; // as the new default; a real schema-1 file always has the value
    public int ThreadsPerJob { get; set; }
    public string TempFolder { get; set; } = "";
    public string ChdmanExePath { get; set; } = "";
    public bool VerboseLog { get; set; }
    public bool LogProgress { get; set; }
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 820;
}
