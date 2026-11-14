using LibPs3Kit;
using LibXisoKit.Identity;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Ps3;

/// <summary>
/// PS3 discs, done by lib-ps3-kit: a Redump image is decrypted with its disc key (the .dkey Redump publishes) into a
/// decrypted ISO, which RPCS3 loads, or into a JB folder (PS3_DISC.SFB, PS3_GAME/…). The result is read back and
/// checked. NoIntrovert finds the key, picks the output's name and folder, keeps the Redump hashes the library
/// reports (R11), and removes the original afterwards when asked (Replace the originals).
/// </summary>
public static class Ps3Jobs
{
    public const string NoKeyMessage =
        "its disc key wasn't found: put the .dkey from Redump beside the image with the same name, or in the folder set in Settings › Keys › PS3.";

    /// <summary>The .dkey for <paramref name="iso"/>: beside it, then in your PS3 keys folder. Null when there's none.</summary>
    public static string? KeyFileFor(string iso, AppSettings settings) =>
        DiscKey.FindFileFor(iso, string.IsNullOrWhiteSpace(settings.Keys.Ps3KeysFolder) ? null : [settings.Keys.Ps3KeysFolder]);

    /// <summary>States already read, by file (and its size and time, so a changed file is read again).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, long Ticks, EncryptionState? State)> States =
        new(PathNames.Comparer);

    /// <summary>What a PS3 image is: still encrypted, already decrypted…; null when it isn't one lib-ps3-kit can open.</summary>
    public static EncryptionState? StateOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var full = info.FullName;
            if (States.TryGetValue(full, out var known) && known.Size == info.Length && known.Ticks == info.LastWriteTimeUtc.Ticks) return known.State;
            EncryptionState? state;
            try
            {
                using var disc = Ps3Disc.Open(full);
                state = disc.State;
            }
            catch (Ps3Exception) { state = null; }
            States[full] = (info.Length, info.LastWriteTimeUtc.Ticks, state);
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>How many .dkey files your PS3 keys folder holds, for Settings › Keys.</summary>
    public static string Describe(string folder)
    {
        const string beside = "Each image's .dkey is looked for beside it first (same name, e.g. Game (USA).dkey).";
        if (string.IsNullOrWhiteSpace(folder)) return beside;
        if (!Directory.Exists(folder)) return $"{folder} doesn't exist. {beside}";
        var count = Directory.EnumerateFiles(folder, "*.dkey").Count() + Directory.EnumerateFiles(folder, "*.key").Count();
        return $"{count} disc key file(s) in {folder}. {beside}";
    }

    public static JobOutcome Convert(JobContext c, string source, TargetKind kind, string? outputFolder = null)
    {
        var s = c.Settings;
        var folder = outputFolder ?? OutputPaths.FolderFor(s, source);
        var jb = kind == TargetKind.Ps3Folder;
        var name = Path.GetFileNameWithoutExtension(source);

        EncryptionState state;
        try
        {
            using var disc = Ps3Disc.Open(source);
            state = disc.State;
        }
        catch (Exception ex) when (ex is Ps3Exception or IOException or UnauthorizedAccessException) { return JobOutcome.Failed(ex.Message); }
        var decrypted = state is EncryptionState.Decrypted or EncryptionState.NoEncryptedRegions;
        if (!jb && decrypted) return JobOutcome.Skipped("already a decrypted ISO");

        DiscKey? key = null;
        if (!decrypted)
        {
            var keyFile = KeyFileFor(source, s);
            if (keyFile is null) return JobOutcome.Failed(NoKeyMessage);
            try { key = DiscKey.Load(keyFile); }
            catch (Ps3Exception ex) { return JobOutcome.Failed(ex.Message); }
        }

        // A decrypted ISO beside its Redump image has the same name: with Replace the originals it takes the
        // original's place once it's checked; otherwise it gets " (2)" like any taken name.
        var wanted = Path.Combine(folder, jb ? name : name + ".iso");
        var swap = !jb && PathNames.Same(wanted, source) && s.Output.ReplacesOriginals;
        var output = swap ? Path.Combine(folder, $"{name}.noi-decrypted.iso") : OutputPaths.Available(wanted, s.Output.Overwrite, isFolder: jb);
        if (PathNames.Same(output, source)) return JobOutcome.Skipped("already that format");

        // The Redump image's own hashes, for Verify & match (R11). A decrypted source has none worth keeping.
        var keepHashes = s.TargetFormats.RecordOriginalHashes && !decrypted;
        var recorded = OriginalHashes.Default.Get(source)?.Files; // read before the original may go
        var cached = keepHashes && recorded is null ? Xbox.XboxJobs.KnownWhole(source) : null;
        var hashSource = keepHashes && recorded is null && cached is null;
        using var beside = hashSource
            ? BackgroundHash.Start(source, token => FileHashes.Compute([source], null, token), c.Token)
            : null;
        var progress = new InlineProgress<OperationProgress>(p =>
        {
            c.Report.SetStage(p.Stage switch
            {
                OperationStage.CheckingKey => "Checking the disc key",
                OperationStage.Writing => "Decrypting",
                OperationStage.Extracting => "Extracting",
                OperationStage.Verifying => "Verifying",
                _ => p.Stage.ToString(),
            });
            if (p.Fraction is { } f) c.Report.SetProgress(f);
        });
        var options = new Ps3Options { Verify = true, Overwrite = s.Output.Overwrite || swap };
        Ps3Result result;
        FileHashes? sourceHashes = null;
        try
        {
            result = jb
                ? Ps3Converter.ToJbFolder(source, output, key, options, progress, c.Token)
                : Ps3Converter.ToDecryptedIso(source, output, key, options, progress, c.Token);
            if (hashSource)
            {
                c.Report.SetStage("Hashing the original");
                sourceHashes = beside?.Result() ?? FileHashes.Compute([source], null, c.Token);
            }
        }
        catch (Ps3Exception ex) { return JobOutcome.Failed(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return JobOutcome.Failed(ex.Message);
        }

        var made = result.Output;
        if (cached is null && !s.Output.ReplacesOriginals && sourceHashes is { } read)
            HashCache.Default.GetOrAdd(source, () =>
                [new HashedFile(Path.GetFileName(source), read.Size, read.Crc32.ToUpperInvariant(), read.Sha1.ToUpperInvariant(), Md5: read.Md5.ToUpperInvariant())]);
        if (swap)
        {
            c.Report.SetStage("Replacing the original");
            FileRemover.Remove([source], s.Output.UseRecycleBin, c.Log);
            File.Move(made, wanted);
            made = wanted;
        }
        if (keepHashes)
        {
            var original = recorded ?? cached;
            if (original is null && sourceHashes is { } h)
                original = [new HashedFile(Path.GetFileName(source), h.Size, h.Crc32.ToUpperInvariant(), h.Sha1.ToUpperInvariant(), Md5: h.Md5.ToUpperInvariant())];
            if (original is not null) OriginalHashes.Default.Set(made, source, original);
        }
        if (s.Output.ReplacesOriginals && !swap && File.Exists(source))
        {
            c.Report.SetStage("Removing the original");
            FileRemover.Remove([source], s.Output.UseRecycleBin, c.Log);
        }

        var shown = Path.GetFileName(made) + (jb ? $" ({result.FilesWritten} files)" : "");
        var title = result.TitleId is null ? "" : $" [{result.TitleId}]";
        c.Log.Success($"{Path.GetFileName(source)} → {shown}{title}");
        return new JobOutcome(JobStatus.Succeeded, Path.GetFileName(made));
    }
}
