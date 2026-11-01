using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Presets;

/// <summary>Something the app does to a game that has its own presets (issue #33).</summary>
public enum PresetAction
{
    /// <summary>A disc image becomes a CHD.</summary>
    Chd,
    /// <summary>A GameCube or Wii disc becomes an RVZ.</summary>
    Rvz,
    /// <summary>Wii U games (WUX, WUD, NUS, title folders) become a .wua.</summary>
    Wua,
    /// <summary>Files and folders are packed into 7z or zip, or archives converted between them.</summary>
    Archive,
    /// <summary>A CHD is extracted back to a disc image.</summary>
    Extract,
    /// <summary>An archive's files are unpacked.</summary>
    Unpack,
    /// <summary>Verify &amp; match: checking for damage and looking games up.</summary>
    Verify,
}

/// <summary>
/// A named set of values for one action's parameters. <see cref="Values"/>
/// writes every parameter the action uses, so a preset gives the same result
/// whatever was set before; Custom has none and uses the values in Settings.
/// </summary>
public sealed record ActionPreset(PresetAction Action, string Id, string Name, string Summary, Action<AppSettings>? Values)
{
    public const string CustomId = "custom";

    public bool IsCustom => Values is null;

    /// <summary>The first preset of each action is the one it starts on.</summary>
    public bool IsPreferred => ActionPresets.Preferred(Action) == this;

    /// <summary>The name as a list shows it: the recommended preset is starred (a word wouldn't fit beside the longer names).</summary>
    public string Label => IsPreferred ? $"{Name} ★" : Name;

    /// <summary>Whether <paramref name="settings"/> already hold exactly this preset's values.</summary>
    public bool Matches(AppSettings settings)
    {
        if (Values is null) return false;
        var copy = settings.Clone();
        Values(copy);
        return copy.ToJson() == settings.ToJson();
    }

    public override string ToString() => Label;
}

/// <summary>
/// Every action's presets, and the one chosen for each. A run applies each
/// job's own action preset to a copy of the settings
/// (<see cref="Effective"/>), so the presets of different actions never get in
/// each other's way, and the values under Custom are never overwritten.
/// </summary>
public static class ActionPresets
{
    public static IReadOnlyList<PresetAction> Actions { get; } = Enum.GetValues<PresetAction>();

    private static readonly Dictionary<PresetAction, IReadOnlyList<ActionPreset>> ByAction = new()
    {
        [PresetAction.Chd] =
        [
            new(PresetAction.Chd, "standard", "Standard", "chdman's codecs for the disc type · verify · keep the originals", s => Chd(s, remove: false)),
            new(PresetAction.Chd, "replace", "Replace the originals", "Standard, then the originals are removed once the CHD verifies", s => Chd(s, remove: true)),
            Custom(PresetAction.Chd),
        ],
        [PresetAction.Rvz] =
        [
            new(PresetAction.Rvz, "balanced", "Balanced", "Zstandard level 5, 128 KiB blocks · verify · keep the originals", s => Rvz(s, 5, remove: false)),
            new(PresetAction.Rvz, "smallest", "Smallest", "Zstandard level 19: slower, a little smaller", s => Rvz(s, 19, remove: false)),
            new(PresetAction.Rvz, "fastest", "Fastest", "Zstandard level 1: quicker, a little bigger", s => Rvz(s, 1, remove: false)),
            new(PresetAction.Rvz, "replace", "Replace the originals", "Balanced, then the originals are removed once the RVZ verifies", s => Rvz(s, 5, remove: true)),
            Custom(PresetAction.Rvz),
        ],
        [PresetAction.Wua] =
        [
            new(PresetAction.Wua, "wua", "WUX, WUD or NUS → .wua", "Base game, update and DLC in one .wua · keep the originals", s => s.Output.RemoveWiiUOriginals = false),
            new(PresetAction.Wua, "replace", "Replace the originals", "Build the .wua, then remove what it was built from once it verifies", s => s.Output.RemoveWiiUOriginals = true),
            Custom(PresetAction.Wua),
        ],
        [PresetAction.Archive] =
        [
            new(PresetAction.Archive, "balanced", "Balanced", "Normal compression, solid 7z · test each archive · one archive per item · keep the originals",
                s => Archive(s, ArchiveLevel.Normal, remove: false)),
            new(PresetAction.Archive, "smallest", "Smallest", "Ultra compression: slower, a little smaller", s => Archive(s, ArchiveLevel.Ultra, remove: false)),
            new(PresetAction.Archive, "fastest", "Fastest", "Fast compression: quicker, a little bigger", s => Archive(s, ArchiveLevel.Fast, remove: false)),
            new(PresetAction.Archive, "replace", "Replace the originals", "Balanced, then the originals are removed once the archive tests OK",
                s => Archive(s, ArchiveLevel.Normal, remove: true)),
            Custom(PresetAction.Archive),
        ],
        [PresetAction.Extract] =
        [
            new(PresetAction.Extract, "best", "Best format for the disc", "cue/bin, gdi or iso by the disc, with one .bin per track", s => Extract(s, split: true)),
            new(PresetAction.Extract, "single-bin", "One .bin", "As above, with all the tracks in a single .bin", s => Extract(s, split: false)),
            Custom(PresetAction.Extract),
        ],
        [PresetAction.Unpack] =
        [
            new(PresetAction.Unpack, "own-folder", "Into its own folder", "Each archive's files in a folder named after it · keep the archive", s => Unpack(s, remove: false)),
            new(PresetAction.Unpack, "remove", "Unpack and remove the archive", "Into its own folder, then the archive is removed", s => Unpack(s, remove: true)),
            Custom(PresetAction.Unpack),
        ],
        [PresetAction.Verify] =
        [
            new(PresetAction.Verify, "check", "Check and match", "Check for damage, then look each game up · never changes a file", s => s.Chd.FixHeaderSha1 = false),
            new(PresetAction.Verify, "fix", "Check, match and fix CHD headers", "Also rewrites a CHD's wrong header SHA-1 when its data is intact",
                s => s.Chd.FixHeaderSha1 = true),
            Custom(PresetAction.Verify),
        ],
    };

    public static IReadOnlyList<ActionPreset> For(PresetAction action) => ByAction[action];

    public static ActionPreset Preferred(PresetAction action) => ByAction[action][0];

    /// <summary>The action's name, as the preset pickers title it.</summary>
    public static string Title(PresetAction action) => action switch
    {
        PresetAction.Chd => "Disc images → CHD",
        PresetAction.Rvz => "GameCube / Wii → RVZ",
        PresetAction.Wua => "Wii U → .wua",
        PresetAction.Archive => "7z and zip",
        PresetAction.Extract => "CHD → disc image",
        PresetAction.Unpack => "Unpacking archives",
        PresetAction.Verify => "Checking games", // the Verify & match page is already titled that
        _ => action.ToString(),
    };

    /// <summary>The preset <paramref name="settings"/> choose for <paramref name="action"/>: the preferred one unless another was picked.</summary>
    public static ActionPreset Selected(AppSettings settings, PresetAction action) =>
        settings.Presets.Chosen.TryGetValue(action.ToString(), out var id) && ByAction[action].FirstOrDefault(p => p.Id == id) is { } chosen
            ? chosen
            : Preferred(action);

    /// <summary>Chooses <paramref name="preset"/> for its action; the preferred one isn't written down.</summary>
    public static void Select(AppSettings settings, ActionPreset preset)
    {
        var key = preset.Action.ToString();
        if (preset.IsPreferred) settings.Presets.Chosen.Remove(key);
        else settings.Presets.Chosen[key] = preset.Id;
    }

    /// <summary>Writes the chosen preset's values for <paramref name="action"/> into <paramref name="settings"/> (nothing for Custom).</summary>
    public static void Apply(AppSettings settings, PresetAction action) => Selected(settings, action).Values?.Invoke(settings);

    /// <summary>A copy of <paramref name="settings"/> as a job of <paramref name="action"/> runs with them.</summary>
    public static AppSettings Effective(AppSettings settings, PresetAction action)
    {
        var copy = settings.Clone();
        Apply(copy, action);
        return copy;
    }

    /// <summary>The action a Convert target runs, or null for a target without parameters (ISO, Leave as is).</summary>
    public static PresetAction? ForTarget(TargetKind target) => target switch
    {
        TargetKind.Chd => PresetAction.Chd,
        TargetKind.Rvz => PresetAction.Rvz,
        TargetKind.Wua => PresetAction.Wua,
        TargetKind.SevenZip or TargetKind.Zip => PresetAction.Archive,
        TargetKind.DiscImage => PresetAction.Extract,
        TargetKind.Files => PresetAction.Unpack,
        _ => null,
    };

    /// <summary>
    /// For settings from before presets: each action gets the preset its
    /// current values equal, or Custom, so nothing behaves differently.
    /// </summary>
    public static PresetSettings Infer(AppSettings settings)
    {
        var inferred = new PresetSettings();
        foreach (var action in Actions)
        {
            var preset = ByAction[action].FirstOrDefault(p => p.Matches(settings)) ?? ByAction[action][^1];
            if (!preset.IsPreferred) inferred.Chosen[action.ToString()] = preset.Id;
        }
        return inferred;
    }

    private static ActionPreset Custom(PresetAction action) =>
        new(action, ActionPreset.CustomId, "Custom", "Your own choices:", null);

    private static void Chd(AppSettings s, bool remove)
    {
        s.Chd.Compression = "";
        s.Chd.HunkSize = 0;
        s.Chd.IsoMode = IsoMode.Auto;
        s.Chd.VerifyAfterCreate = true;
        s.Output.RemoveOriginals = remove;
    }

    private static void Rvz(AppSettings s, int level, bool remove)
    {
        s.Nintendo.Compression = LibWiiGameCubeKit.RvzCompression.Zstd;
        s.Nintendo.CompressionLevel = level;
        s.Nintendo.BlockSize = 0x20000;
        s.Nintendo.Scrub = false;
        s.Chd.VerifyAfterCreate = true;
        s.Output.RemoveOriginals = remove;
    }

    private static void Archive(AppSettings s, ArchiveLevel level, bool remove)
    {
        s.Archives.Level = level;
        s.Archives.Solid = true;
        s.Archives.TestAfter = true;
        s.Archives.Grouping = ArchiveGrouping.PerItem;
        s.Output.RemoveArchiveOriginals = remove;
    }

    private static void Extract(AppSettings s, bool split)
    {
        s.Chd.ExtractFormat = ExtractFormat.Auto;
        s.Chd.SplitBin = split;
    }

    private static void Unpack(AppSettings s, bool remove)
    {
        s.Archives.ExtractToOwnFolder = true;
        s.Output.RemoveArchiveOriginals = remove;
    }
}

/// <summary>The preset chosen for each action, by action name; an action not listed uses its preferred preset.</summary>
public sealed class PresetSettings
{
    public Dictionary<string, string> Chosen { get; set; } = [];
}
