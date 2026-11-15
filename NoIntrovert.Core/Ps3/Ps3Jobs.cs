using System.Security.Cryptography;
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
        return $"{Words.Count(count, "disc key file")} in {folder}. {beside}";
    }

    public static JobOutcome Convert(JobContext c, string source, TargetKind kind, string? outputFolder = null)
    {
        var s = c.Settings;
        var folder = outputFolder ?? OutputPaths.FolderFor(s, source);
        var jb = kind == TargetKind.Ps3Folder;
        if (Directory.Exists(source))
            return jb ? JobOutcome.Skipped("already a JB folder") : FolderToIso(c, source, folder);
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
        // A decrypted ISO reads the whole image anyway: it hands the bytes over and they're hashed on the way. A JB
        // folder reads only the files, so the image is hashed beside it (or after it, on a hard drive).
        using var inline = hashSource && !jb ? new SourceHasher() : null;
        using var beside = hashSource && jb
            ? BackgroundHash.Start(source, token => FileHashes.Compute([source], null, token), c.Token)
            : null;
        c.Log.Info($"Converting {Path.GetFileName(source)} → {Path.GetFileName(output)}{(jb ? " (JB folder)" : "")}");
        var progress = ProgressFor(c, "Decrypting");
        var options = new Ps3Options
        {
            Verify = true,
            Overwrite = s.Output.Overwrite || swap,
            SourceBytes = inline is null ? null : inline.Append,
            // Small files several at once, unless a hard drive is involved (its head would jump between them).
            MaxParallelFiles = jb && !Drives.IsSpinning(source) && !Drives.IsSpinning(folder) ? 4 : 1,
        };
        Ps3Result result;
        FileHashes? sourceHashes = null;
        try
        {
            result = jb
                ? Ps3Converter.ToJbFolder(source, output, key, options, progress, c.Token)
                : Ps3Converter.ToDecryptedIso(source, output, key, options, progress, c.Token);
            if (inline is not null) sourceHashes = inline.Finish();
            else if (hashSource)
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

    /// <summary>The library's stages as the job's own, and its progress.</summary>
    private static InlineProgress<OperationProgress> ProgressFor(JobContext c, string writing) => new(p =>
    {
        c.Report.SetStage(p.Stage switch
        {
            OperationStage.CheckingKey => "Checking the disc key",
            OperationStage.Writing => writing,
            OperationStage.Extracting => "Extracting",
            OperationStage.Verifying => "Verifying",
            _ => p.Stage.ToString(),
        });
        if (p.Fraction is { } f) c.Report.SetProgress(f);
    });

    /// <summary>
    /// A JB folder made into a decrypted ISO beside it (or in the output folder), named after it without ".ps3".
    /// No key is needed: its files are decrypted already. The ISO is checked file by file before the folder goes.
    /// </summary>
    private static JobOutcome FolderToIso(JobContext c, string source, string folder)
    {
        var s = c.Settings;
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        var name = folderName.EndsWith(".ps3", StringComparison.OrdinalIgnoreCase) ? folderName[..^4] : folderName;
        var output = OutputPaths.Available(Path.Combine(folder, name + ".iso"), s.Output.Overwrite, isFolder: false);
        c.Log.Info($"Converting {folderName} → {Path.GetFileName(output)}");
        Ps3Result result;
        try
        {
            result = Ps3Converter.FolderToIso(source, output, new Ps3Options { Verify = true, Overwrite = s.Output.Overwrite },
                ProgressFor(c, "Making the ISO"), c.Token);
        }
        catch (Ps3Exception ex) { return JobOutcome.Failed(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return JobOutcome.Failed(ex.Message);
        }
        if (s.Output.ReplacesOriginals && Directory.Exists(source))
        {
            c.Report.SetStage("Removing the original");
            FileRemover.Remove([source], s.Output.UseRecycleBin, c.Log);
        }
        var made = Path.GetFileName(result.Output);
        var title = result.TitleId is null ? "" : $" [{result.TitleId}]";
        c.Log.Success($"{folderName} → {made} ({result.FilesWritten} files){title}");
        return new JobOutcome(JobStatus.Succeeded, made);
    }

    /// <summary>
    /// The size, CRC32, MD5 and SHA-1 of the bytes a conversion hands over as it reads the source
    /// (<see cref="Ps3Options.SourceBytes"/>): each chunk is copied and hashed on another thread while the next one is read.
    /// </summary>
    private sealed class SourceHasher : IDisposable
    {
        private readonly System.IO.Hashing.Crc32 _crc = new();
        private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        private byte[] _free = [], _busy = [];
        private Task _pending = Task.CompletedTask;
        private long _size;

        public void Append(ReadOnlySpan<byte> data)
        {
            if (_free.Length < data.Length) _free = new byte[data.Length];
            data.CopyTo(_free);
            _pending.GetAwaiter().GetResult(); // the hashes take the chunks in order
            (_free, _busy) = (_busy, _free);
            var chunk = _busy;
            var n = data.Length;
            _size += n;
            _pending = Task.Run(() => Parallel.Invoke(
                () => _crc.Append(chunk.AsSpan(0, n)),
                () => _md5.AppendData(chunk, 0, n),
                () => _sha1.AppendData(chunk, 0, n)));
        }

        public FileHashes Finish()
        {
            _pending.GetAwaiter().GetResult();
            return new FileHashes(_size, _crc.GetCurrentHashAsUInt32().ToString("x8"),
                System.Convert.ToHexStringLower(_md5.GetHashAndReset()), System.Convert.ToHexStringLower(_sha1.GetHashAndReset()));
        }

        public void Dispose()
        {
            try { _pending.Wait(); }
            catch (AggregateException) { }
            _md5.Dispose();
            _sha1.Dispose();
        }
    }
}
