using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoIntrovert.Core;

/// <summary>Everything the app remembers between runs, saved as JSON under %AppData%\NoIntrovert.</summary>
public sealed class AppSettings
{
    // Where results go
    public string OutputFolder { get; set; } = ""; // empty = next to each source file
    public bool Overwrite { get; set; }
    public bool ScanSubfolders { get; set; } = true;

    // Conversion
    public string Compression { get; set; } = ""; // empty = chdman's default for the media type
    public uint HunkSize { get; set; }            // 0 = default
    public IsoMode IsoMode { get; set; } = IsoMode.Auto;
    public bool VerifyAfterCreate { get; set; } = true;
    public bool DeleteOriginals { get; set; }
    public bool UseRecycleBin { get; set; } = true;

    // GameCube / Wii (libdolphinrvz); defaults are Dolphin's
    public Jobs.NintendoTarget NintendoTarget { get; set; } = Jobs.NintendoTarget.Rvz;
    public LibDolphinRvz.RvzCompression RvzCompression { get; set; } = LibDolphinRvz.RvzCompression.Zstd;
    public int RvzCompressionLevel { get; set; } = 5;
    public uint RvzBlockSize { get; set; } = 0x20000;
    public bool RvzScrub { get; set; }

    // Extraction
    public ExtractFormat ExtractFormat { get; set; } = ExtractFormat.Auto;
    public bool SplitBin { get; set; } = true;

    // Verification
    public bool FixHeaderSha1 { get; set; }

    // Archives (7z / zip)
    public Archives.ArchiveAction ArchiveAction { get; set; } = Archives.ArchiveAction.Compress;
    public Archives.ArchiveFormat ArchiveFormat { get; set; } = Archives.ArchiveFormat.SevenZip;
    public Archives.ArchiveLevel ArchiveLevel { get; set; } = Archives.ArchiveLevel.Normal;
    public bool ArchiveSolid { get; set; } = true;
    public Archives.ArchiveGrouping ArchiveGrouping { get; set; } = Archives.ArchiveGrouping.PerItem;
    public string ArchiveSingleName { get; set; } = "Archive";
    public Archives.ConvertTarget ArchiveConvertTarget { get; set; } = Archives.ConvertTarget.Swap;
    public bool ArchiveTestAfter { get; set; } = true;
    public bool ArchiveDeleteOriginals { get; set; }
    public bool ArchiveExtractToOwnFolder { get; set; } = true;

    // Wii U (.wua)
    public bool WiiUDeleteOriginals { get; set; }
    public string WiiUKeysPath { get; set; } = ""; // empty = look in the usual places

    // Performance
    public int ParallelJobs { get; set; } = 1;
    public int ThreadsPerJob { get; set; } // 0 = all processors

    // Paths
    public string TempFolder { get; set; } = ""; // empty = %TEMP%\NoIntrovert
    public string ChdmanExePath { get; set; } = ""; // for the comparison tests only

    // Logging
    public bool VerboseLog { get; set; }
    public bool LogProgress { get; set; }

    // Window
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 820;

    [JsonIgnore] public string EffectiveTempFolder => string.IsNullOrWhiteSpace(TempFolder) ? WorkPaths.DefaultTempRoot : TempFolder;

    public static string SettingsDirectory =>
        SettingsDirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoIntrovert");

    /// <summary>Tests point this at a scratch folder so they never touch the real settings file.</summary>
    internal static string? SettingsDirectoryOverride { get; set; }

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file just means defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
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

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
