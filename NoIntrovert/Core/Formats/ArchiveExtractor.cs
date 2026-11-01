namespace NoIntrovert.Core.Formats;

/// <summary>
/// Unpacks zip, 7z, tar and gz archives into a work folder, and joins plain
/// split files (game.bin.001, .002, ...) back into one. RAR is refused.
/// The work is done by lib-7z-zip-kit's managed reader (<see cref="ArchiveReader"/>,
/// <see cref="ArchiveFiles"/>); this keeps the app's own wording and progress type.
/// </summary>
internal static class ArchiveExtractor
{
    public static IReadOnlyList<string> Extensions => ArchiveFiles.Extensions;

    public static bool IsArchive(string path) => ArchiveFiles.IsArchive(path);

    /// <summary>True for the first part of a split file: name.ext.001.</summary>
    public static bool IsFirstSplitPart(string path) => ArchiveFiles.IsFirstSplitPart(path);

    /// <summary>All parts of a split file, in order, starting from the .001.</summary>
    public static List<string> SplitParts(string firstPart) => ArchiveFiles.SplitParts(firstPart);

    /// <summary>
    /// RAR archives (and their .rNN / .partN.rar volumes) aren't supported: 7-Zip's
    /// RAR decoder is under the unRAR license, which NoIntrovert's GPL doesn't allow.
    /// </summary>
    public static bool IsRar(string path) => ArchiveFiles.IsRar(path);

    public const string RarNotSupported =
        "RAR archives aren't supported (the RAR decoder's license isn't compatible with NoIntrovert's GPL). Repack it as 7z or zip.";

    /// <summary>Later parts of a split file (.002, .003, ...), which the .001 brings in.</summary>
    public static bool IsSecondaryVolume(string path) => ArchiveFiles.IsSecondaryVolume(path);

    public static void Extract(string archivePath, string destination, IProgress<double>? progress, CancellationToken token)
    {
        // By name and by content, so a RAR renamed to .zip is refused too.
        if (IsRar(archivePath) || ArchiveFiles.HasRarSignature(archivePath)) throw new NotSupportedException(RarNotSupported);
        ArchiveReader.Extract(archivePath, destination, Report(progress), token);
    }

    public static string JoinSplitParts(string firstPart, string destinationDirectory, IProgress<double>? progress, CancellationToken token) =>
        ArchiveFiles.JoinSplitParts(firstPart, destinationDirectory, Report(progress), token);

    private static Action<double>? Report(IProgress<double>? progress) => progress is null ? null : progress.Report;
}
