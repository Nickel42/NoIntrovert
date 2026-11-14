using System.Collections.Concurrent;
using NoIntrovert.Core.Emulators;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Presets;

/// <summary>What sort of entry a format preset is.</summary>
public enum PresetKind
{
    /// <summary>One of the app's presets for the system.</summary>
    Standard,
    /// <summary>One the user saved from Custom ("yours").</summary>
    Saved,
    /// <summary>The row's own values, edited in Preset settings.</summary>
    Custom,
    /// <summary>The row isn't converted.</summary>
    Leave,
}

/// <summary>
/// A target format plus all of its settings, named "Format · Preset"
/// ("CHD · Standard ★"); docs/requirements/ui-redesign.md, "2. Preset section".
/// <see cref="Warning"/> marks one that loses data for its system (listed at the
/// bottom under "⚠ May not play"); <see cref="Producible"/> is false for a format
/// the app can't write yet (PBP, CSO, WBFS), which stays visible but disabled.
/// </summary>
public sealed record FormatPreset(
    string Id,
    string Format,
    string Name,
    string Hint,
    PresetValues Values,
    bool Recommended = false,
    bool Warning = false,
    bool Producible = true,
    string? Extension = null,
    PresetKind Kind = PresetKind.Standard)
{
    public const string CustomId = "custom";
    public const string LeaveId = "leave";
    public const string SavedPrefix = "saved:";

    /// <summary>The name lists show: "CHD · Standard ★", "ISO · Data track only ⚠", a saved preset's own name.</summary>
    public string Label => Kind switch
    {
        PresetKind.Custom => "Custom",
        PresetKind.Leave => "Leave as is",
        PresetKind.Saved => Name,
        _ => $"{Format} · {Name}{(Recommended ? " ★" : "")}{(Warning ? " ⚠" : "")}",
    };

    public bool IsCustom => Kind == PresetKind.Custom;
    public bool IsLeave => Kind == PresetKind.Leave;
    public bool IsSaved => Kind == PresetKind.Saved;

    /// <summary>What a row with this preset becomes, or null when the app can't make it yet.</summary>
    public TargetKind? Target => Kind == PresetKind.Leave ? TargetKind.Leave : Producible ? Values.Target : null;

    /// <summary>The extension of the output for <paramref name="systemId"/>, for "Works with".</summary>
    public string? OutputExtension(string? systemId) => Extension ?? (Kind == PresetKind.Leave ? null : Values.OutputExtension(systemId));

    public override string ToString() => Label;
}

/// <summary>What one entry in a Preset dropdown is.</summary>
public enum PresetListItemKind
{
    Preset,
    /// <summary>The line before Custom.</summary>
    Divider,
    /// <summary>"⚠ May not play", above the presets that lose data.</summary>
    Group,
}

/// <summary>
/// One entry of a row's Preset dropdown: a preset with whether this file can
/// use it (and why not), and how many known emulators play its output ("6 of 9").
/// Dividers and the group label are disabled entries.
/// </summary>
public sealed record PresetListItem(PresetListItemKind Kind, FormatPreset? Preset, bool IsEnabled, string Note, string Plays)
{
    public const string MayNotPlay = "⚠ May not play";
    public const string NotAvailableYet = "Not available yet";
    public const string NotForThisFile = "Not for this file";

    public bool IsPreset => Kind == PresetListItemKind.Preset;
    public bool IsDivider => Kind == PresetListItemKind.Divider;
    public bool IsGroup => Kind == PresetListItemKind.Group;

    public string Label => Kind switch
    {
        PresetListItemKind.Group => MayNotPlay,
        PresetListItemKind.Divider => "",
        _ => Preset!.Label,
    };

    /// <summary>The one-line hint, or why the entry can't be chosen.</summary>
    public string Hint => Preset is null ? "" : IsEnabled ? Preset.Hint : Note;

    public bool HasPlays => Plays.Length > 0;

    public override string ToString() => Label;
}

/// <summary>
/// Each system's format presets, drafted in docs/requirements/emulator-sources-and-presets.md §4
/// and mapped onto what the app makes (<see cref="TargetKind"/> plus the values the old
/// action presets used). Systems without a draft get one preset per target the
/// registry gives them. Every CHD preset keeps chdman's default codecs; Zstandard
/// is only in Custom.
/// </summary>
public static class FormatPresets
{
    /// <summary>The systems with drafted lists, most common first: the Settings page's Default presets card.</summary>
    public static IReadOnlyList<string> DraftedSystems { get; } =
    [
        "playstation", "playstation-2", "playstation-portable", "dreamcast", "saturn", "gamecube", "wii",
        "mega-cd-sega-cd", "pc-engine-cd-turbografx-cd", "3do", "pc-fx", "neo-geo-cd", "cd-i", "jaguar-cd", "amiga-cd32-cdtv",
        "playdia", "other-redump-cd-dvd-systems",
    ];

    /// <summary>Custom: the row's own values.</summary>
    public static FormatPreset Custom { get; } = new(FormatPreset.CustomId, "Custom", "Custom", "Pick the format and settings yourself", new(), Kind: PresetKind.Custom);

    /// <summary>Leave as is: the row isn't converted.</summary>
    public static FormatPreset Leave { get; } = new(FormatPreset.LeaveId, "Leave as is", "Leave as is", "Not converted", new() { Target = TargetKind.Leave }, Kind: PresetKind.Leave);

    // ---- Values ----

    private static PresetValues Chd() => new() { Target = TargetKind.Chd };
    private static PresetValues Rvz(int level) => new() { Target = TargetKind.Rvz, RvzLevel = level };
    private static PresetValues Extract(ExtractFormat format, bool split = true) => new() { Target = TargetKind.DiscImage, ExtractFormat = format, SplitBin = split };
    private static PresetValues Kind(TargetKind kind) => new() { Target = kind };
    private static PresetValues NotYet() => new() { Target = TargetKind.Leave };

    // ---- Shared presets ----

    private static FormatPreset ChdStandard(string hint) => new("chd-standard", "CHD", "Standard", hint, Chd(), Recommended: true);
    private static FormatPreset BinCue(string hint) => new("bincue-redump", "BIN/CUE", "Redump layout", hint, Extract(ExtractFormat.CueBin));
    private static FormatPreset IsoDataTrack(string hint) => new("iso-data", "ISO", "Data track only", hint, Extract(ExtractFormat.Iso), Warning: true);
    private static FormatPreset IsoPlain() => new("iso-plain", "ISO", "Plain", "Largest, but readable everywhere", Extract(ExtractFormat.Iso));

    private static IReadOnlyList<FormatPreset> CdSystem(string chdHint, string binCueHint, string isoHint) =>
        [ChdStandard(chdHint), BinCue(binCueHint), IsoDataTrack(isoHint)];

    private static IReadOnlyList<FormatPreset> Nintendo(bool wii) =>
    [
        new("rvz-balanced", "RVZ", "Balanced", "Lossless, Dolphin's own format", Rvz(5), Recommended: true),
        new("rvz-smallest", "RVZ", "Smallest", "Zstandard level 19: slower, a little smaller", Rvz(19)),
        new("rvz-fastest", "RVZ", "Fastest", "Zstandard level 1: quicker, a little bigger", Rvz(1)),
        new("iso-loaders", "ISO", "For Nintendont / Swiss / USB loaders", "Plain image for real hardware", Kind(TargetKind.Iso)),
        .. wii ? [new FormatPreset("wbfs-loaders", "WBFS", "For USB loaders", "For USB loaders on a real Wii", NotYet(), Producible: false, Extension: ".wbfs")] : Array.Empty<FormatPreset>(),
        new("chd-archive", "CHD", "Archive only", "Dolphin has no CHD reader", Chd(), Warning: true),
    ];

    private static readonly Dictionary<string, IReadOnlyList<FormatPreset>> Drafted = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playstation"] =
        [
            ChdStandard("Smallest lossless file that most emulators read"),
            new("pbp-standard", "PBP", "Standard", "For PSP / PS Vita (Adrenaline), one file per disc", NotYet(), Producible: false, Extension: ".pbp"),
            new("pbp-multidisc", "PBP", "Multi-disc in one file", "All discs of a game in one .pbp", NotYet(), Producible: false, Extension: ".pbp"),
            BinCue("Raw Redump tracks, for tools that need them"),
            IsoDataTrack("Drops CD audio and XA/FMV sectors, so most games break"),
        ],
        ["playstation-2"] =
        [
            ChdStandard("Smallest lossless file; PCSX2 reads it"),
            new("zso-opl", "ZSO", "For OPL & real PS2", "OPL on a real PS2 reads ZSO, not CHD; PCSX2 reads it too", Kind(TargetKind.Zso)),
            IsoPlain(),
        ],
        ["playstation-portable"] =
        [
            ChdStandard("Smallest lossless file; PPSSPP 1.17+ reads it"),
            new("cso-psp", "CSO", "For real PSP / Adrenaline", "Real hardware can't read CHD", Kind(TargetKind.Cso)),
            IsoPlain(),
        ],
        ["dreamcast"] =
        [
            ChdStandard("Lossless; Flycast and Redream read it"),
            new("gdi-gdemu", "GDI", "For GDEMU / Redream / Demul", "Split tracks for optical-drive emulators", Extract(ExtractFormat.Gdi)),
            BinCue("Matches the Redump DAT"),
        ],
        ["saturn"] = CdSystem("Lossless; Ymir, ares and the Beetle cores read it", "Needed for standalone Mednafen", "Drops the CD audio most games use for music"),
        ["mega-cd-sega-cd"] = CdSystem("Lossless; jgenesis, ares and the Genesis cores read it", "Readable by every emulator", "Drops the CD audio soundtrack"),
        ["pc-engine-cd-turbografx-cd"] = CdSystem("Lossless; Geargrafx, ares and the Beetle cores read it", "Needed for standalone Mednafen and Mesen 2", "Drops the CD audio these games rely on"),
        ["3do"] = CdSystem("Lossless; the Opera core reads it", "Safest for standalone emulators", "Drops CD audio tracks, if the disc has any"),
        ["pc-fx"] = CdSystem("Smallest lossless file", "Needed for standalone Mednafen", "Drops CD audio tracks"),
        ["neo-geo-cd"] = CdSystem("Smallest lossless file", "Readable by every emulator", "Drops CD audio tracks"),
        ["cd-i"] = CdSystem("Smallest lossless file", "Readable by every emulator", "Drops CD audio tracks"),
        ["jaguar-cd"] = CdSystem("Smallest lossless file", "Readable by every emulator", "Drops CD audio tracks"),
        ["amiga-cd32-cdtv"] = CdSystem("Smallest lossless file", "Readable by every emulator", "Drops CD audio tracks"),
        ["playdia"] = CdSystem("Smallest lossless file", "Readable by every emulator", "Drops CD audio tracks"),
        ["other-redump-cd-dvd-systems"] = CdSystem("Smallest lossless file", "Readable by every emulator", "Drops CD audio tracks"),
        ["gamecube"] = Nintendo(wii: false),
        ["wii"] = Nintendo(wii: true),
        ["wii-u"] = [new("wua-standard", "WUA", "Standard", "Base game, update and DLC in one file", Kind(TargetKind.Wua), Recommended: true)],
        // D2: always decrypted (Azahar plays nothing else); encrypted images, .cia and .zcci files are converted by lib-3ds-kit.
        ["nintendo-3ds"] =
        [
            new("cci-standard", "CCI", "Bare", "The raw decrypted image; every 3DS emulator opens it as it is", Kind(TargetKind.Cci), Recommended: true),
            new("zcci-standard", "ZCCI", "Standard", "Azahar plays it without unpacking; other emulators can't read it", Kind(TargetKind.Zcci)),
        ],
        // Original Xbox discs through lib-xiso-kit.
        ["xbox"] =
        [
            new("xiso-standard", "XISO", "Game partition", "xemu and modded consoles read it; drops only the unused video partition", Kind(TargetKind.Xiso), Recommended: true),
            new("xbox-cci-standard", "CCI", "Cerbios", "Compressed, split for the console's hard drive; Cerbios BIOS only, not xemu", Kind(TargetKind.XboxCci)),
        ],
        // Xbox 360 discs: the files read by lib-xiso-kit, written into a ZArchive by lib-wii-u-kit.
        ["xbox-360"] =
        [
            new("zar-standard", "ZAR", "Compressed", "Xenia Canary plays it without unpacking; other emulators and the console can't read it", Kind(TargetKind.Zar)),
        ],
        // PS3 discs through lib-ps3-kit, with each disc's .dkey from Redump.
        ["playstation-3"] =
        [
            new("ps3-iso-decrypted", "ISO", "Decrypted", "RPCS3 loads it as it is; same size as the Redump image", Kind(TargetKind.Ps3Iso), Recommended: true),
            new("ps3-jb-folder", "JB folder", "Disc files", "PS3_GAME and the rest as plain files, for RPCS3 or a console's internal drive", Kind(TargetKind.Ps3Folder)),
        ],
        // Switch packages through lib-nx-kit, with your own prod.keys.
        ["switch"] =
        [
            new("nsz-standard", "NSZ", "Standard", "Smallest; Ryujinx, the yuzu family and installers read it", Kind(TargetKind.Nsz), Recommended: true),
            new("xcz-standard", "XCZ", "Standard", "A compressed cartridge image; keeps a cartridge dump a cartridge", Kind(TargetKind.Xcz)),
            new("nsp-standard", "NSP/XCI", "Uncompressed", "Back to a plain .nsp (or .xci for a cartridge image)", Kind(TargetKind.Nsp)),
        ],
    };

    /// <summary>The standard preset for a target, used for systems without a draft and for a file's other targets.</summary>
    public static FormatPreset StandardFor(TargetKind kind) => kind switch
    {
        TargetKind.Chd => new("chd-standard", "CHD", "Standard", "Smallest lossless file", Chd()),
        TargetKind.Rvz => new("rvz-balanced", "RVZ", "Balanced", "Lossless, Dolphin's own format", Rvz(5)),
        TargetKind.Iso => new("iso-standard", "ISO", "Standard", "Decoded back to a plain image", Kind(TargetKind.Iso)),
        TargetKind.DiscImage => new("disc-standard", "Disc image", "Best for the disc", "cue/bin, gdi or iso by the disc, one .bin per track", Extract(ExtractFormat.Auto)),
        TargetKind.Wua => new("wua-standard", "WUA", "Standard", "Base game, update and DLC in one file", Kind(TargetKind.Wua)),
        TargetKind.SevenZip => new("7z-standard", "7z", "Standard", "Normal compression, tested after packing", Kind(TargetKind.SevenZip)),
        TargetKind.Zip => new("zip-standard", "zip", "Standard", "Opens anywhere, tested after packing", Kind(TargetKind.Zip)),
        TargetKind.Files => new("unpack-standard", "Unpacked", "Standard", "Each archive's files in their own folder", Kind(TargetKind.Files)),
        TargetKind.Cci => new("cci-standard", "CCI", "Bare", "A decrypted 3DS cartridge image", Kind(TargetKind.Cci)),
        TargetKind.Zcci => new("zcci-standard", "ZCCI", "Standard", "A decrypted 3DS cartridge image, compressed for Azahar", Kind(TargetKind.Zcci)),
        TargetKind.Nsz => new("nsz-standard", "NSZ", "Standard", "A compressed Switch package", Kind(TargetKind.Nsz)),
        TargetKind.Xcz => new("xcz-standard", "XCZ", "Standard", "A compressed Switch cartridge image", Kind(TargetKind.Xcz)),
        TargetKind.Nsp => new("nsp-standard", "NSP", "Uncompressed", "A plain Switch package", Kind(TargetKind.Nsp)),
        TargetKind.Xci => new("xci-standard", "XCI", "Uncompressed", "A plain Switch cartridge image", Kind(TargetKind.Xci)),
        TargetKind.Xiso => new("xiso-standard", "XISO", "Game partition", "An Xbox disc's game partition", Kind(TargetKind.Xiso)),
        TargetKind.XboxCci => new("xbox-cci-standard", "CCI", "Cerbios", "A compressed Xbox disc for Cerbios", Kind(TargetKind.XboxCci)),
        TargetKind.Zar => new("zar-standard", "ZAR", "Compressed", "An Xbox 360 disc's files in a ZArchive for Xenia Canary", Kind(TargetKind.Zar)),
        TargetKind.Ps3Iso => new("ps3-iso-decrypted", "ISO", "Decrypted", "A PS3 disc decrypted with its .dkey", Kind(TargetKind.Ps3Iso)),
        TargetKind.Ps3Folder => new("ps3-jb-folder", "JB folder", "Disc files", "A PS3 disc's files, decrypted, in a folder", Kind(TargetKind.Ps3Folder)),
        TargetKind.Cso => new("cso-standard", "CSO", "Standard", "A compressed ISO for a real PSP and Adrenaline", Kind(TargetKind.Cso)),
        TargetKind.Zso => new("zso-standard", "ZSO", "Standard", "A compressed ISO for OPL on a real PS2", Kind(TargetKind.Zso)),
        _ => Leave,
    };

    private static readonly ConcurrentDictionary<string, IReadOnlyList<FormatPreset>> Derived = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A system's own presets in list order, ⚠ ones included (not Custom, Leave
    /// or saved ones). Empty for an unknown system.
    /// </summary>
    public static IReadOnlyList<FormatPreset> ForSystem(string? systemId)
    {
        if (systemId is null) return [];
        if (Drafted.TryGetValue(systemId, out var list)) return list;
        return Derived.GetOrAdd(systemId, id => Derive(TargetFormats.ById(id)));
    }

    /// <summary>One preset per target the registry gives a system without a draft; the first is recommended.</summary>
    private static IReadOnlyList<FormatPreset> Derive(SystemInfo? system)
    {
        if (system is null) return [];
        var list = new List<FormatPreset>();
        void Add(TargetKind kind)
        {
            if (list.Any(p => p.Values.Target == kind)) return;
            list.Add(StandardFor(kind) with { Recommended = list.Count == 0 });
        }
        var t = system.Target;
        if (!t.Producible)
        {
            var format = t.Format.StartsWith('.') ? t.Format.TrimStart('.').ToUpperInvariant() : "Folder";
            list.Add(new($"{format.ToLowerInvariant()}-standard", format, "Standard", t.Label, NotYet(), Producible: false, Extension: t.Format.StartsWith('.') ? t.Format : null));
        }
        else if (t.Container == TargetContainer.SevenZip) Add(TargetKind.SevenZip);
        else if (t.Container == TargetContainer.Zip) Add(TargetKind.Zip);
        else if (t.Container == TargetContainer.Folder) Add(TargetKind.Files);
        else if (t.Format == ".chd") Add(TargetKind.Chd);
        else if (t.Format == ".rvz") Add(TargetKind.Rvz);
        else if (t.Format == ".wua") Add(TargetKind.Wua);
        if (system.DiscTarget == ".chd") Add(TargetKind.Chd);
        foreach (var alt in system.Alternatives.Where(a => a.Producible))
            switch (alt.Id)
            {
                case "7z": Add(TargetKind.SevenZip); break;
                case "zip": Add(TargetKind.Zip); break;
                case "bare": Add(TargetKind.Files); break;
                case "cue-bin": if (!list.Any(p => p.Id == "bincue-redump")) list.Add(BinCue("Readable by every emulator")); break;
            }
        return list;
    }

    /// <summary>The recommended preset (★) for a system, or null when it has none.</summary>
    public static FormatPreset? Recommended(string? systemId) => ForSystem(systemId).FirstOrDefault(p => p.Recommended);

    /// <summary>The user's saved presets for <paramref name="systemId"/>: its own, and those for any system that offer one of its formats.</summary>
    public static IReadOnlyList<FormatPreset> Saved(PresetSettings settings, string? systemId)
    {
        var kinds = ForSystem(systemId).Where(p => p.Producible).Select(p => p.Values.Target).ToHashSet();
        return settings.Saved
            .Where(s => s.System.Length > 0
                ? string.Equals(s.System, systemId, StringComparison.OrdinalIgnoreCase)
                : kinds.Contains(s.Values.Target))
            .Select(ToPreset)
            .ToList();
    }

    /// <summary>A saved preset as a list entry.</summary>
    public static FormatPreset ToPreset(SavedPreset saved) =>
        new(FormatPreset.SavedPrefix + saved.Id, FormatOf(saved.Values.Target), saved.Name, "Yours", saved.Values.Copy(), Kind: PresetKind.Saved);

    /// <summary>The format's short name, as presets name it ("CHD", "BIN/CUE"…).</summary>
    public static string FormatOf(TargetKind kind) => kind == TargetKind.Leave ? "Leave as is" : StandardFor(kind).Format;

    /// <summary>The preset with <paramref name="id"/> for a system (its own, saved, Custom or Leave), or null.</summary>
    public static FormatPreset? Find(string? id, string? systemId, PresetSettings settings) => id switch
    {
        null => null,
        FormatPreset.CustomId => Custom,
        FormatPreset.LeaveId => Leave,
        _ => ForSystem(systemId).Concat(Saved(settings, systemId)).FirstOrDefault(p => p.Id == id),
    };

    /// <summary>The preset new rows of a system start on: the one chosen in Settings, else the recommended one.</summary>
    public static FormatPreset? Default(string? systemId, PresetSettings settings) =>
        (systemId is not null && settings.DefaultBySystem.TryGetValue(systemId, out var id) ? Find(id, systemId, settings) : null)
        ?? Recommended(systemId);

    /// <summary>
    /// A row's Preset dropdown: the system's presets (and a standard one for any
    /// other target this file offers), the user's saved ones, Leave as is, a
    /// divider, Custom, then "⚠ May not play" and the presets that lose data.
    /// A preset this file can't become is listed but disabled, saying why.
    /// </summary>
    public static IReadOnlyList<PresetListItem> ListFor(string? systemId, IReadOnlyCollection<TargetKind> available, PresetSettings settings)
    {
        var own = ForSystem(systemId);
        var extras = available
            .Where(k => k != TargetKind.Leave && !own.Any(p => p.Producible && p.Values.Target == k))
            .Select(StandardFor);
        var normal = own.Where(p => !p.Warning).Concat(extras).ToList();
        var warned = own.Where(p => p.Warning).ToList();

        var items = new List<PresetListItem>();
        items.AddRange(normal.Select(p => Item(p, systemId, available)));
        items.AddRange(Saved(settings, systemId).Select(p => Item(p, systemId, available)));
        if (available.Contains(TargetKind.Leave) || available.Count == 0) items.Add(new(PresetListItemKind.Preset, Leave, true, "", ""));
        items.Add(new(PresetListItemKind.Divider, null, false, "", ""));
        var canCustomize = available.Any(k => k != TargetKind.Leave);
        items.Add(new(PresetListItemKind.Preset, Custom, canCustomize, PresetListItem.NotForThisFile, ""));
        if (warned.Count > 0)
        {
            items.Add(new(PresetListItemKind.Group, null, false, "", ""));
            items.AddRange(warned.Select(p => Item(p, systemId, available)));
        }
        return items;
    }

    private static PresetListItem Item(FormatPreset p, string? systemId, IReadOnlyCollection<TargetKind> available)
    {
        var enabled = p.Producible && available.Contains(p.Values.Target);
        var note = !p.Producible ? PresetListItem.NotAvailableYet : NotForThisFileOrEmpty(enabled);
        return new(PresetListItemKind.Preset, p, enabled, note, Plays(systemId, p.OutputExtension(systemId), p.Values.Codecs));
    }

    private static string NotForThisFileOrEmpty(bool enabled) => enabled ? "" : PresetListItem.NotForThisFile;

    /// <summary>
    /// The entry a new row starts on: Leave as is when the rules leave it, else
    /// the system's default preset when this file can use it, else the first
    /// usable preset for the rules' target.
    /// </summary>
    public static PresetListItem Start(IReadOnlyList<PresetListItem> list, string? systemId, TargetKind planned, PresetSettings settings)
    {
        PresetListItem? ById(string id) => list.FirstOrDefault(i => i.IsPreset && i.IsEnabled && i.Preset!.Id == id);
        if (planned != TargetKind.Leave)
        {
            if (Default(systemId, settings) is { } chosen && ById(chosen.Id) is { } d) return d;
            if (list.FirstOrDefault(i => i.IsPreset && i.IsEnabled && !i.Preset!.IsCustom && !i.Preset.Warning && i.Preset.Target == planned) is { } match)
                return match;
            if (list.FirstOrDefault(i => i.IsPreset && i.IsEnabled && i.Preset!.Target == planned) is { } any) return any;
        }
        return ById(FormatPreset.LeaveId) ?? list.First(i => i.IsPreset);
    }

    private static readonly ConcurrentDictionary<string, string> PlaysCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"6 of 9": how many of the system's known emulators play the output ("" with no data).</summary>
    public static string Plays(string? systemId, string? extension, IReadOnlyList<string> codecs)
    {
        if (systemId is null || extension is null) return "";
        var key = $"{systemId}|{extension}|{string.Join(',', codecs)}";
        return PlaysCache.GetOrAdd(key, _ =>
        {
            try
            {
                var entries = EmulatorSupport.For(systemId, extension, codecs.ToList());
                return entries.Count == 0 ? "" : $"{entries.Count(e => e.State == SupportState.Works)} of {entries.Count}";
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
            {
                return "";
            }
        });
    }

    /// <summary>Forgets the "N of M" counts (after the emulator data changes).</summary>
    public static void ForgetCounts() => PlaysCache.Clear();

    /// <summary>
    /// Settings from before format presets: each action's chosen preset (the old
    /// <see cref="PresetSettings.Chosen"/>) seeds the default of the systems that
    /// use it. A choice no system preset matches (Custom) becomes a saved preset
    /// for any system, "My CHD settings", and the default of those systems, so
    /// nothing converts differently. The old choices stay for what still reads them.
    /// </summary>
    public static void Migrate(AppSettings settings)
    {
        var p = settings.Presets;
        // "GameCube and Wii discs become CHD" (Settings › Default targets) is now their CHD preset.
        if (settings.Nintendo.Target == Jobs.NintendoTarget.Chd)
            foreach (var id in new[] { "gamecube", "wii" })
                p.DefaultBySystem.TryAdd(id, "chd-archive");
        foreach (var (action, kind) in new[] { (PresetAction.Chd, TargetKind.Chd), (PresetAction.Rvz, TargetKind.Rvz), (PresetAction.Archive, TargetKind.SevenZip) })
        {
            var chosen = ActionPresets.Selected(settings, action);
            if (chosen.IsPreferred) continue;
            var values = PresetValues.From(ActionPresets.Effective(settings, action), kind);
            SavedPreset? saved = null;
            foreach (var system in TargetFormats.All)
            {
                var own = ForSystem(system.Id);
                if (own.FirstOrDefault(x => x.Recommended)?.Values.Target != kind || p.DefaultBySystem.ContainsKey(system.Id)) continue;
                if (own.FirstOrDefault(x => x.Producible && Same(x.Values, values)) is { } match)
                {
                    if (!match.Recommended) p.DefaultBySystem[system.Id] = match.Id;
                    continue;
                }
                // One saved preset per set of values: reuse one already there (a migration run before), never add a twin.
                saved ??= p.Saved.FirstOrDefault(x => x.System.Length == 0 && Same(x.Values, values));
                if (saved is null)
                {
                    saved = new SavedPreset { Name = $"My {FormatOf(kind)} settings", Values = values };
                    p.Saved.Add(saved);
                }
                p.DefaultBySystem[system.Id] = FormatPreset.SavedPrefix + saved.Id;
            }
        }
    }

    /// <summary>Whether two sets of values make the same output (only the settings their target reads count).</summary>
    public static bool Same(PresetValues a, PresetValues b)
    {
        if (a.Target != b.Target) return false;
        return a.Target switch
        {
            TargetKind.Chd => a.Compression == b.Compression && a.HunkSize == b.HunkSize && a.IsoMode == b.IsoMode && a.Verify == b.Verify,
            TargetKind.Rvz => a.RvzCompression == b.RvzCompression && a.RvzLevel == b.RvzLevel && a.RvzBlockSize == b.RvzBlockSize && a.Scrub == b.Scrub && a.Verify == b.Verify,
            TargetKind.DiscImage => a.ExtractFormat == b.ExtractFormat && a.SplitBin == b.SplitBin,
            TargetKind.SevenZip or TargetKind.Zip => a.ArchiveLevel == b.ArchiveLevel && a.Solid == b.Solid && a.TestArchives == b.TestArchives
                                                     && a.Grouping == b.Grouping && a.SingleName == b.SingleName,
            TargetKind.Files => a.UnpackToOwnFolder == b.UnpackToOwnFolder,
            _ => true,
        };
    }
}
