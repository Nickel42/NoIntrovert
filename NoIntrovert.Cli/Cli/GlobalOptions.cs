using System.CommandLine;
using NoIntrovert.Cli.Output;

namespace NoIntrovert.Cli.Cli;

/// <summary>The options every command takes, in any position (docs/requirements/cli.md §4).</summary>
public static class GlobalOptions
{
    // ---- How much it prints ----
    public static readonly Option<bool> Verbose = new("--verbose", "-v") { Description = "Also show each step (hashing, codecs chosen, temp files)", Recursive = true };
    public static readonly Option<bool> Quiet = new("--quiet", "-q") { Description = "Only warnings, errors and a one-line summary; no progress", Recursive = true };
    public static readonly Option<bool> Silent = new("--silent", "-s") { Description = "Print nothing at all; the exit code and the log say how it went", Recursive = true };
    public static readonly Option<bool> Json = new("--json") { Description = "Results as one JSON document on stdout", Recursive = true };
    public static readonly Option<bool> Ndjson = new("--ndjson") { Description = "One JSON event per line as it happens (for front ends)", Recursive = true };

    // ---- Look ----
    public static readonly Option<ColorMode> Color = new("--color") { Description = "Colour: auto (on a terminal), always or never", HelpName = "when", Recursive = true, DefaultValueFactory = _ => ColorMode.Auto };
    public static readonly Option<bool> NoColor = new("--no-color") { Description = "Same as --color never", Recursive = true };
    public static readonly Option<ProgressStyle> Progress = new("--progress") { Description = "auto, bars, lines (one per file) or none", HelpName = "style", Recursive = true, DefaultValueFactory = _ => ProgressStyle.Auto };
    public static readonly Option<bool> Ascii = new("--ascii") { Description = "No Unicode symbols or box lines", Recursive = true };

    // ---- Running ----
    public static readonly Option<bool> DryRun = new("--dry-run", "-n") { Description = "Say what would happen; change nothing", Recursive = true };
    public static readonly Option<bool> Yes = new("--yes", "-y") { Description = "Answer yes to every confirmation", Recursive = true };
    public static readonly Option<bool> NoInput = new("--no-input") { Description = "Never ask; fail when an answer is needed", Recursive = true };
    public static readonly Option<int?> Jobs = new("--jobs", "-j") { Description = "Files converted at the same time (default 2)", HelpName = "n", Recursive = true };
    public static readonly Option<int?> Threads = new("--threads") { Description = "Threads per job; 0 shares the processors (default)", HelpName = "n", Recursive = true };
    public static readonly Option<string?> Temp = new("--temp") { Description = "Temp folder", HelpName = "folder", Recursive = true };
    public static readonly Option<bool> Offline = new("--offline") { Description = "Don't check for newer databases; use the ones here", Recursive = true };
    public static readonly Option<string?> SettingsFile = new("--settings") { Description = "Use the settings in this file", HelpName = "file", Recursive = true };
    public static readonly Option<bool> UseSettings = new("--use-settings") { Description = "Use the app's saved settings instead of the recommended values", Recursive = true };
    public static readonly Option<string?> LogFile = new("--log") { Description = "Write the log to this file", HelpName = "file", Recursive = true };
    public static readonly Option<bool> NoLog = new("--no-log") { Description = "Don't write a log file", Recursive = true };

    public static readonly Option<bool> Help = new("--help", "-h", "-?") { Description = "Show help (also /?)", Recursive = true };
    public static readonly Option<bool> HelpAll = new("--help-all") { Description = "Help with every option, Custom values included", Recursive = true, Hidden = true };
    public static readonly Option<bool> Version = new("--version") { Description = "Show the version and the libraries' versions" };

    public static IEnumerable<Option> All =>
    [
        Verbose, Quiet, Silent, Json, Ndjson, Color, NoColor, Progress, Ascii,
        DryRun, Yes, NoInput, Jobs, Threads, Temp, Offline, SettingsFile, UseSettings, LogFile, NoLog, Help, HelpAll,
    ];
}

/// <summary>The paths and the filters for them, taken by every command that works on files (§3.3).</summary>
public static class PathOptions
{
    public static Argument<string[]> Paths() => new("paths") { Description = "Files and folders (folders are searched with their subfolders). - reads paths from stdin.", Arity = ArgumentArity.ZeroOrMore, HelpName = "files or folders…" };
    public static readonly Option<bool> NoRecurse = new("--no-recurse") { Description = "Don't look in subfolders" };
    public static readonly Option<string[]> Include = new("--include") { Description = "Only files that match (repeatable): \"*.cue\"", HelpName = "glob", AllowMultipleArgumentsPerToken = false };
    public static readonly Option<string[]> Exclude = new("--exclude") { Description = "Skip files that match (repeatable): \"**/hidden/**\"", HelpName = "glob" };
    public static readonly Option<bool> Library = new("--library") { Description = "Use the folders in the app's Settings › Library" };
    public static readonly Option<string[]> System = new("--system") { Description = "Only games of these systems (repeatable or a comma list): psx,ps2", HelpName = "id" };

    public static Option[] All => [NoRecurse, Include, Exclude, Library, System];
}

/// <summary>Where new files go: the app's Save to (§5.1).</summary>
public static class SaveOptions
{
    public static readonly Option<string?> Output = new("--output", "-o") { Description = "Save into this folder instead of next to the originals", HelpName = "folder" };
    public static readonly Option<bool> Replace = new("--replace") { Description = "Replace the originals ⚠ — each only after its new file verifies; they go to the Recycle Bin / Trash" };
    public static readonly Option<string?> Layout = new("--layout") { Description = "esde (default), romm, keep (the same subfolders) or flat", HelpName = "layout" };
    public static readonly Option<bool> Overwrite = new("--overwrite") { Description = "Overwrite files in the way (otherwise \"Name (2)\")" };
    public static readonly Option<bool> NoRecycleBin = new("--no-recycle-bin") { Description = "Delete removed originals instead of using the Recycle Bin / Trash" };

    public static Option[] All => [Output, Replace, Layout, Overwrite, NoRecycleBin];

    static SaveOptions() => Layout.AcceptOnlyFromAmong("esde", "es-de", "romm", "keep", "same", "flat");
}

/// <summary>What to make, for convert, organize and optimize (§5.2).</summary>
public static class FormatOptions
{
    public static readonly Option<string[]> Preset = new("--preset", "-p") { Description = "A preset for every file it fits, or <system>=<id> (repeatable). See \"noi presets\".", HelpName = "id" };
    public static readonly Option<string[]> To = new("--to", "-t") { Description = "A format with its standard preset: chd, rvz, iso, cue, gdi, 7z, zip, nsz, zso, cso, unpack, … or <system>=<format>", HelpName = "format" };
    public static readonly Option<string[]> Keep = new("--keep") { Description = "Leave a system's files as they are (repeatable)", HelpName = "system" };

    public static Option[] All => [Preset, To, Keep];
}

/// <summary>Custom values: the parameters the app shows only under Custom (noi convert --help-all).</summary>
public static class TuningOptions
{
    public static readonly Option<string?> ChdCodecs = new("--chd-codecs") { Description = "CHD codecs, e.g. cdlz,cdzl,cdfl (default: chdman's for the disc type)", HelpName = "list" };
    public static readonly Option<uint?> ChdHunk = new("--chd-hunk") { Description = "CHD hunk size in bytes; 0 = default", HelpName = "bytes" };
    public static readonly Option<string?> IsoMode = new("--iso-mode") { Description = "A CHD from an ISO: auto, cd or dvd", HelpName = "mode" };
    public static readonly Option<int?> RvzLevel = new("--rvz-level") { Description = "RVZ compression level, 1-22 (default 5)", HelpName = "1-22" };
    public static readonly Option<string?> RvzBlock = new("--rvz-block") { Description = "RVZ block size, e.g. 128K (default), 32K… 2M", HelpName = "size" };
    public static readonly Option<string?> RvzCodec = new("--rvz-codec") { Description = "RVZ compression: zstd (default), lzma, lzma2, bzip2, purge or none", HelpName = "codec" };
    public static readonly Option<bool> Scrub = new("--scrub") { Description = "RVZ: scrub unused data" };
    public static readonly Option<string?> ArchiveLevel = new("--archive-level") { Description = "7z / zip compression: fast, normal (default) or ultra", HelpName = "level" };
    public static readonly Option<bool> NoSolid = new("--no-solid") { Description = "7z: not solid" };
    public static readonly Option<string?> Group = new("--group") { Description = "7z / zip: per-item (default), per-entry or single", HelpName = "how" };
    public static readonly Option<string?> ArchiveName = new("--archive-name") { Description = "The archive's name with --group single (default Archive)", HelpName = "name" };
    public static readonly Option<string?> ExtractFormat = new("--extract-format") { Description = "CHD → disc image: auto (default), cue, gdi, iso or raw", HelpName = "format" };
    public static readonly Option<bool> SingleBin = new("--single-bin") { Description = "CHD → cue/bin: all tracks in one .bin" };
    public static readonly Option<bool> UnpackHere = new("--unpack-here") { Description = "Unpack each archive beside it, not into its own folder" };
    public static readonly Option<bool> NoVerify = new("--no-verify") { Description = "Don't verify new files (--replace always verifies)" };

    public static Option[] All => [ChdCodecs, ChdHunk, IsoMode, RvzLevel, RvzBlock, RvzCodec, Scrub, ArchiveLevel, NoSolid, Group, ArchiveName, ExtractFormat, SingleBin, UnpackHere, NoVerify];

    static TuningOptions()
    {
        IsoMode.AcceptOnlyFromAmong("auto", "cd", "dvd");
        RvzCodec.AcceptOnlyFromAmong("zstd", "lzma", "lzma2", "bzip2", "purge", "none");
        ArchiveLevel.AcceptOnlyFromAmong("fast", "normal", "ultra");
        Group.AcceptOnlyFromAmong("per-item", "per-entry", "single");
        ExtractFormat.AcceptOnlyFromAmong("auto", "cue", "gdi", "iso", "raw");
    }
}

/// <summary>Keys for one run (§5.2): paths, never key values.</summary>
public static class KeyOptions
{
    public static readonly Option<string?> WiiU = new("--keys-wiiu") { Description = "Wii U keys.txt (Cemu's format)", HelpName = "file" };
    public static readonly Option<string?> ThreeDs = new("--keys-3ds") { Description = "Folder with boot9.bin and aes_keys.txt", HelpName = "folder" };
    public static readonly Option<string?> Switch = new("--keys-switch") { Description = "Switch prod.keys", HelpName = "file" };
    public static readonly Option<string?> Ps3 = new("--keys-ps3") { Description = "Folder of PS3 .dkey files", HelpName = "folder" };
    public static readonly Option<string?> Zrif = new("--zrif") { Description = "PS Vita zRIF list (NoPayStation TSV or one per line)", HelpName = "file" };

    public static Option[] All => [WiiU, ThreeDs, Switch, Ps3, Zrif];
}
