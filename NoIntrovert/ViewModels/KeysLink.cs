using NoIntrovert.Core.Ctr;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.ViewModels;

/// <summary>
/// Where people set up their keys (Settings › Keys), and which files need
/// them: a row that failed for missing keys offers a link there, and the
/// Convert side panel shows the 3DS and Switch key sections for files that
/// can't be converted without them.
/// </summary>
public static class KeysLink
{
    /// <summary>The place every missing-keys message names.</summary>
    public const string SettingsPath = "Settings › Keys";

    /// <summary>Whether a failure's text sends people to Settings › Keys.</summary>
    public static bool PointsToKeys(string? text) => text?.Contains(SettingsPath, StringComparison.Ordinal) == true;

    public const string ThreeDsSystem = "nintendo-3ds";
    public const string SwitchSystem = "switch";

    /// <summary>
    /// Whether converting this 3DS file needs the user's keys: an encrypted image or a .cia does; a
    /// decrypted .cci (or .zcci) doesn't. An archive can't be told from outside, so it's taken to need them.
    /// </summary>
    public static bool ThreeDsFileNeedsKeys(string path)
    {
        if (ArchiveExtractor.IsArchive(path)) return true;
        return CtrFacts.Of(path) is { } facts && (facts.Encrypted || facts.Type == Lib3dsKit.CtrFileType.Cia);
    }
}
