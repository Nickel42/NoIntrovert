namespace NoIntrovert.Core.Formats;

/// <summary>
/// Unpacks zip, 7z, tar and gz archives into a work folder, and joins plain
/// split files (game.bin.001, .002, ...) back into one. RAR is refused.
/// 7z and zip go through 7-Zip's native engine (<see cref="SevenZipEngine"/>) when it's
/// there, which decodes several times faster; everything else, and 7z/zip without the
/// engine, through lib-7z-zip-kit's managed reader (<see cref="ArchiveReader"/>,
/// <see cref="ArchiveFiles"/>). This keeps the app's own wording and progress type.
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

    /// <param name="threads">Threads 7-Zip may decode with; 0 for every processor.</param>
    public static void Extract(string archivePath, string destination, IProgress<double>? progress, CancellationToken token, int threads = 0)
    {
        // By name and by content, so a RAR renamed to .zip is refused too.
        if (IsRar(archivePath) || ArchiveFiles.HasRarSignature(archivePath)) throw new NotSupportedException(RarNotSupported);
        if (NativeEngineFor(archivePath) is { } engine)
        {
            using (engine) engine.Extract(archivePath, destination, threads, Report(progress), token);
            return;
        }
        ArchiveReader.Extract(archivePath, destination, Report(progress), token);
    }

    /// <summary>7-Zip's engine for a .7z or .zip, or null for other types or when its libraries are missing.</summary>
    private static SevenZipEngine? NativeEngineFor(string archivePath)
    {
        var ext = Path.GetExtension(archivePath);
        if (!ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        try { return new SevenZipEngine(); }
        catch (ArchiveException) { return null; }
    }

    public static string JoinSplitParts(string firstPart, string destinationDirectory, IProgress<double>? progress, CancellationToken token) =>
        ArchiveFiles.JoinSplitParts(firstPart, destinationDirectory, Report(progress), token);

    private static Action<double>? Report(IProgress<double>? progress) => progress is null ? null : progress.Report;
}
