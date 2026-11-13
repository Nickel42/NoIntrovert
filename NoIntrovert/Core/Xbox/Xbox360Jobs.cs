using System.Security.Cryptography;
using LibWiiUKit;
using LibXisoKit.Xdvdfs;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Xbox;

/// <summary>
/// Xbox 360 discs as a ZArchive (.zar), which Xenia Canary plays without unpacking. lib-xiso-kit reads the disc's
/// files (Redump XGD2/XGD3 or XISO) and lib-wii-u-kit writes them into the archive (the same format as Cemu's .wua).
/// The archive is written to a temporary name, then re-read: its own SHA-256 and every file's SHA-1 against the
/// disc's, and only then moved into place. A .zar holds the files, not the disc, so it can't become the Redump .iso
/// again; the Redump hashes are kept (R11) so Verify &amp; match still knows the game.
/// </summary>
public static class Xbox360Jobs
{
    public static JobOutcome ToZar(JobContext c, string source, string? outputFolder = null)
    {
        var s = c.Settings;
        var folder = outputFolder ?? OutputPaths.FolderFor(s, source);
        var wanted = Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + ".zar");
        var output = OutputPaths.Available(wanted, s.Output.Overwrite);
        if (output != wanted) c.Log.Info(OutputPaths.Renamed(wanted, output));
        var temp = Path.Combine(folder, $".noi-{Guid.NewGuid():N}.zar.tmp");

        try
        {
            using var volume = XdvdfsVolume.Open(source);
            var files = volume.EnumerateFiles().ToList();
            long total = files.Sum(f => (long)f.Size);
            var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            c.Report.SetStage("Writing the .zar");
            long done = 0;
            using (var writer = new ZArchiveWriter(temp))
            {
                foreach (var entry in volume.EnumerateEntries())
                {
                    c.Token.ThrowIfCancellationRequested();
                    var path = entry.Path.TrimStart('/');
                    if (path.Length == 0) continue;
                    if (entry.IsDirectory)
                    {
                        writer.AddDirectory(path);
                        continue;
                    }
                    var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(parent)) writer.AddDirectory(parent);
                    using var data = volume.OpenFile(entry);
                    using var hashing = new HashingStream(data);
                    writer.AddFile(path, hashing, n => c.Report.SetProgress(total > 0 ? 0.8 * (done += n) / total : 0), c.Token);
                    hashes[path] = hashing.Finish();
                }
                writer.Finish();
            }

            c.Report.SetStage("Checking the .zar");
            if (Check(temp, hashes, total, c) is { } problem)
            {
                Delete(temp);
                return JobOutcome.Failed("the new .zar failed its check: " + problem);
            }

            // The Redump image's hashes, for Verify & match (R11), read before the original may go.
            List<HashedFile>? original = null;
            if (s.TargetFormats.RecordOriginalHashes)
            {
                c.Report.SetStage("Hashing the original");
                original = OriginalHashes.Default.Get(source)?.Files.ToList() ?? Hasher.HashFile(source, c.Token);
            }
            volume.Dispose();
            File.Move(temp, output, overwrite: true);
            if (original is not null) OriginalHashes.Default.Set(output, source, original);
            if (s.Output.ReplacesOriginals && File.Exists(source))
            {
                c.Report.SetStage("Removing the original");
                FileRemover.Remove([source], s.Output.UseRecycleBin, c.Log);
            }

            var saved = new FileInfo(source).Exists ? new FileInfo(source).Length - new FileInfo(output).Length : 0;
            c.Log.Success($"{Path.GetFileName(source)} → {Path.GetFileName(output)}{(saved > 0 ? $" ({ChdJobs.Size(saved)} smaller)" : "")}");
            return new JobOutcome(JobStatus.Succeeded, Path.GetFileName(output));
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or ArgumentException or ZArchiveException)
        {
            Delete(temp);
            return JobOutcome.Failed(ex.Message);
        }
        catch (OperationCanceledException)
        {
            Delete(temp);
            throw;
        }
    }

    /// <summary>Null when the archive's own hash holds and every file reads back with the disc's SHA-1; else what's wrong.</summary>
    private static string? Check(string path, Dictionary<string, byte[]> expected, long total, JobContext c)
    {
        using var reader = ZArchiveReader.Open(path);
        if (!reader.CheckIntegrity(token: c.Token)) return "its SHA-256 doesn't match";
        long done = 0;
        int found = 0;
        foreach (var entry in reader.Walk().Where(e => e.IsFile))
        {
            if (!expected.TryGetValue(entry.Path.TrimStart('/'), out var sha1)) return $"{entry.Path} wasn't on the disc";
            using var hashing = new HashingStream(Stream.Null);
            reader.CopyFile(entry, hashing, n => c.Report.SetProgress(total > 0 ? 0.8 + 0.2 * (done += n) / total : 1), c.Token);
            if (!hashing.Finish().AsSpan().SequenceEqual(sha1)) return $"{entry.Path} reads back differently";
            found++;
        }
        return found == expected.Count ? null : $"{expected.Count - found} files are missing";
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Passes data through (reading from or writing to <paramref name="inner"/>) and takes its SHA-1 on the way.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        public byte[] Finish() => _sha1.GetHashAndReset();

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            _sha1.AppendData(buffer[..n]);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _sha1.AppendData(buffer);
            inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _sha1.Dispose();
            base.Dispose(disposing);
        }
    }
}
