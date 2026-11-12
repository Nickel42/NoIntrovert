using LibWiiGameCubeKit;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Presets;

/// <summary>
/// Everything a format preset fixes: the target and every setting a job of
/// that target reads. A row's job runs with these written over a copy of the
/// settings (<see cref="ApplyTo"/>), so rows with different presets never get
/// in each other's way. Saved in settings.json for saved presets, so the
/// properties are plain and settable; a record, so two sets of values compare
/// by value.
/// </summary>
public sealed record PresetValues
{
    /// <summary>What the row becomes.</summary>
    public TargetKind Target { get; set; } = TargetKind.Chd;

    // ---- CHD ----
    /// <summary>chdman codecs; "" for chdman's defaults for the media type.</summary>
    public string Compression { get; set; } = "";
    /// <summary>0 = chdman's default.</summary>
    public uint HunkSize { get; set; }
    public IsoMode IsoMode { get; set; } = IsoMode.Auto;
    /// <summary>Verify each new CHD or RVZ.</summary>
    public bool Verify { get; set; } = true;

    // ---- CHD → disc image ----
    public ExtractFormat ExtractFormat { get; set; } = ExtractFormat.Auto;
    public bool SplitBin { get; set; } = true;

    // ---- RVZ ----
    public RvzCompression RvzCompression { get; set; } = RvzCompression.Zstd;
    public int RvzLevel { get; set; } = 5;
    public uint RvzBlockSize { get; set; } = 0x20000;
    public bool Scrub { get; set; }

    // ---- 7z and zip ----
    public ArchiveLevel ArchiveLevel { get; set; } = ArchiveLevel.Normal;
    public bool Solid { get; set; } = true;
    public bool TestArchives { get; set; } = true;
    public ArchiveGrouping Grouping { get; set; } = ArchiveGrouping.PerItem;
    public string SingleName { get; set; } = "Archive";

    // ---- Unpacking ----
    public bool UnpackToOwnFolder { get; set; } = true;

    /// <summary>Writes these values over <paramref name="s"/> (a copy a job runs with).</summary>
    public void ApplyTo(AppSettings s)
    {
        s.Chd.Compression = Compression;
        s.Chd.HunkSize = HunkSize;
        s.Chd.IsoMode = IsoMode;
        s.Chd.VerifyAfterCreate = Verify;
        s.Chd.ExtractFormat = ExtractFormat;
        s.Chd.SplitBin = SplitBin;
        s.Nintendo.Compression = RvzCompression;
        s.Nintendo.CompressionLevel = RvzLevel;
        s.Nintendo.BlockSize = RvzBlockSize;
        s.Nintendo.Scrub = Scrub;
        s.Archives.Level = ArchiveLevel;
        s.Archives.Solid = Solid;
        s.Archives.TestAfter = TestArchives;
        s.Archives.Grouping = Grouping;
        s.Archives.SingleName = SingleName;
        s.Archives.ExtractToOwnFolder = UnpackToOwnFolder;
    }

    /// <summary>The values <paramref name="s"/> hold for a job making <paramref name="target"/>.</summary>
    public static PresetValues From(AppSettings s, TargetKind target) => new()
    {
        Target = target,
        Compression = s.Chd.Compression,
        HunkSize = s.Chd.HunkSize,
        IsoMode = s.Chd.IsoMode,
        Verify = s.Chd.VerifyAfterCreate,
        ExtractFormat = s.Chd.ExtractFormat,
        SplitBin = s.Chd.SplitBin,
        RvzCompression = s.Nintendo.Compression,
        RvzLevel = s.Nintendo.CompressionLevel,
        RvzBlockSize = s.Nintendo.BlockSize,
        Scrub = s.Nintendo.Scrub,
        ArchiveLevel = s.Archives.Level,
        Solid = s.Archives.Solid,
        TestArchives = s.Archives.TestAfter,
        Grouping = s.Archives.Grouping,
        SingleName = s.Archives.SingleName,
        UnpackToOwnFolder = s.Archives.ExtractToOwnFolder,
    };

    /// <summary>A copy to change.</summary>
    public PresetValues Copy() => this with { };

    /// <summary>The CHD codecs these values use; empty for chdman's defaults (and for anything that isn't a CHD).</summary>
    public IReadOnlyList<string> Codecs =>
        Target == TargetKind.Chd && Compression.Length > 0
            ? Compression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>Whether the CHD codecs include Zstandard, which older readers can't open.</summary>
    public bool UsesZstandard => Codecs.Any(c => c.Contains("zs", StringComparison.OrdinalIgnoreCase) || c.Equals("zstd", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The extension of what a job with these values makes for
    /// <paramref name="systemId"/> (".chd", ".cue"…), or null when it can't be told.
    /// </summary>
    public string? OutputExtension(string? systemId) => Target switch
    {
        TargetKind.Chd => ".chd",
        TargetKind.Rvz => ".rvz",
        TargetKind.Iso => ".iso",
        TargetKind.Wua => ".wua",
        TargetKind.Cci => ".cci",
        TargetKind.Zcci => ".zcci",
        TargetKind.SevenZip => ".7z",
        TargetKind.Zip => ".zip",
        TargetKind.DiscImage => ExtractFormat switch
        {
            ExtractFormat.Iso or ExtractFormat.Raw => ".iso",
            ExtractFormat.Gdi => ".gdi",
            ExtractFormat.CueBin => ".cue",
            _ => systemId switch
            {
                "dreamcast" => ".gdi",
                "playstation-2" or "playstation-portable" => ".iso",
                _ => ".cue",
            },
        },
        TargetKind.Files => TargetFormats.ById(systemId)?.Target.Format is { } f && f.StartsWith('.') ? f : null,
        _ => null,
    };
}

/// <summary>A preset the user saved from Custom, for one system ("" for any system that offers its format).</summary>
public sealed class SavedPreset
{
    /// <summary>Stays the same when the preset is renamed.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string System { get; set; } = "";
    public PresetValues Values { get; set; } = new();
}
