using System.Diagnostics;
using System.Security.Cryptography;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.WiiU;

/// <summary>
/// The Wii U tab's job: build one .wua (Cemu's ZArchive) per game from
/// extracted title folders and existing .wua files, holding the base game,
/// its newest update and its DLC.
/// </summary>
public static class WuaJobs
{
    /// <summary>
    /// Writes the .wua for one game from every title found in
    /// <paramref name="sources"/> that belongs to <paramref name="baseId"/>.
    /// Older versions of the same title are dropped in favour of the newest.
    /// The archive is written to a temporary file, verified, and only then
    /// moved into place (replacing an input .wua of the same name if needed).
    /// </summary>
    public static JobOutcome Build(JobContext c, ulong baseId, IReadOnlyList<string> sources)
    {
        var s = c.Settings;
        var log = c.Log;

        // Every title from every source, then the newest of each title ID.
        var all = sources.SelectMany(WiiUTitles.Read).Where(t => t.BaseId == baseId).ToList();
        if (all.Count == 0) return JobOutcome.Skipped("No titles for this game");
        var chosen = all.GroupBy(t => t.TitleId)
            .Select(g =>
            {
                var newest = g.OrderByDescending(t => t.Version).First();
                foreach (var older in g.Where(t => t != newest && t.Version != newest.Version))
                    log.Warn($"  {older.TitleId:X16}: using v{newest.Version}, not v{older.Version} from {Path.GetFileName(older.SourcePath)}");
                return newest;
            })
            .OrderBy(t => t.Kind).ThenBy(t => t.TitleId).ToList();
        if (chosen.All(t => t.Kind != WiiUTitleKind.Game) && chosen.Count == 1 && chosen[0].Kind == WiiUTitleKind.Update)
            log.Warn($"  {baseId:X16}: only an update was found, no base game; the .wua will hold just the update.");

        var name = chosen.FirstOrDefault(t => t.Kind is WiiUTitleKind.Game or WiiUTitleKind.Demo && t.Name.Length > 0)?.Name
            ?? chosen.FirstOrDefault(t => t.Name.Length > 0)?.Name ?? "";
        var outputFolder = OutputPaths.FolderFor(s, sources[0]);
        var output = Path.Combine(outputFolder, WiiUTitles.WuaFileName(baseId, name));
        var replacesInput = sources.Any(p => Path.GetFullPath(p).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase));
        if (File.Exists(output) && !replacesInput && !s.Overwrite)
        {
            log.Warn($"  {Path.GetFileName(output)} already exists; skipped (turn on Overwrite to replace it).");
            return JobOutcome.Skipped("Archive already exists");
        }

        log.Info($"Building {Path.GetFileName(output)}: {string.Join(", ", chosen.Select(t => $"{t.KindText} v{t.Version}"))}");
        foreach (var t in chosen) log.Debug($"  {t.ArchiveFolder} ← {t.SourcePath}{(t.FromWua ? $" ({t.WuaFolder})" : "")}");

        var temp = output + $".{Guid.NewGuid().ToString("N")[..8]}.tmp";
        var sw = Stopwatch.StartNew();
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long inputBytes = 0;
        try
        {
            var total = Math.Max(1, chosen.Sum(TitleSize));
            long done = 0;
            c.Report.SetStage("Packing");
            using (var writer = new ZArchiveWriter(temp))
            {
                foreach (var title in chosen)
                {
                    c.Token.ThrowIfCancellationRequested();
                    writer.AddDirectory(title.ArchiveFolder);
                    void Progress(long n) { done += n; c.Report.SetProgress(0.85 * done / total); }
                    if (title.FromWua) CopyFromWua(writer, title, hashes, Progress, c.Token);
                    else CopyFromFolder(writer, title, hashes, Progress, c.Token);
                }
                inputBytes = (long)writer.InputBytes;
                c.Report.SetStage("Finishing");
                writer.Finish();
            }
            sw.Stop();

            c.Report.SetStage("Verifying");
            Verify(temp, hashes, f => c.Report.SetProgress(0.85 + 0.15 * f), c.Token);
            File.Move(temp, output, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }

        var outputBytes = new FileInfo(output).Length;
        var stats = new OperationStats(sw.Elapsed, chosen.Sum(TitleSize), outputBytes);
        log.Info($"  wrote {Path.GetFileName(output)}: {hashes.Count} files, {ChdJobs.Size(stats.InputBytes)} → {ChdJobs.Size(outputBytes)} ({stats.Ratio:P1}) in {sw.Elapsed:mm\\:ss\\.f}");

        if (s.WiiUDeleteOriginals)
        {
            c.Report.SetStage("Removing originals");
            var originals = sources.Where(p => !Path.GetFullPath(p).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase));
            FileRemover.Remove(originals, s.UseRecycleBin, log);
        }

        log.Success($"{Path.GetFileName(output)}: done and verified");
        var parts = string.Join(" + ", chosen.Select(t => t.KindText).Distinct());
        return new JobOutcome(JobStatus.Succeeded, $"{Path.GetFileName(output)} · {parts}", stats);
    }

    private static void CopyFromFolder(ZArchiveWriter writer, WiiUTitle title, Dictionary<string, byte[]> hashes,
        Action<long> progress, CancellationToken token)
    {
        var root = title.SourcePath;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderBy(d => d, StringComparer.Ordinal))
            writer.AddDirectory(title.ArchiveFolder + "/" + Relative(root, dir));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var path = title.ArchiveFolder + "/" + Relative(root, file);
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var hashing = new HashingStream(src);
            writer.AddFile(path, hashing, progress, token);
            hashes[path] = hashing.Hash();
        }
    }

    private static void CopyFromWua(ZArchiveWriter writer, WiiUTitle title, Dictionary<string, byte[]> hashes,
        Action<long> progress, CancellationToken token)
    {
        using var reader = ZArchiveReader.Open(title.SourcePath);
        var folder = reader.Find(title.WuaFolder!) ?? throw new InvalidDataException($"{title.WuaFolder} vanished from {title.SourcePath}.");
        var prefix = folder.Path.Length + 1;
        foreach (var entry in reader.Walk(folder))
        {
            var path = title.ArchiveFolder + "/" + entry.Path[prefix..];
            if (!entry.IsFile)
            {
                writer.AddDirectory(path);
                continue;
            }
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            writer.AddFile(path, sink =>
            {
                using var tee = new TeeStream(sink, sha);
                reader.CopyFile(entry, tee, progress, token);
            });
            hashes[path] = sha.GetHashAndReset();
        }
    }

    /// <summary>
    /// Reopens a written archive and checks it the way a reader would: the
    /// footer's SHA-256 over the whole file, then every file decompressed and
    /// compared, by SHA-256, with what went in.
    /// </summary>
    public static void Verify(string archive, IReadOnlyDictionary<string, byte[]> expected, Action<double>? progress, CancellationToken token)
    {
        using var reader = ZArchiveReader.Open(archive);
        long done = 0;
        var total = Math.Max(1, reader.Length * 2);
        if (!reader.CheckIntegrity(n => { done += n; progress?.Invoke((double)done / total); }, token))
            throw new ZArchiveException("The new archive's SHA-256 doesn't match its contents.");

        var seen = 0;
        foreach (var entry in reader.Walk().Where(e => e.IsFile))
        {
            if (!expected.TryGetValue(entry.Path, out var want))
                throw new ZArchiveException($"The new archive has an unexpected file: {entry.Path}");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sink = new HashOnlyStream(sha);
            reader.CopyFile(entry, sink, n => { done += n; progress?.Invoke(Math.Min(1, (double)done / total)); }, token);
            if (!sha.GetHashAndReset().AsSpan().SequenceEqual(want))
                throw new ZArchiveException($"{entry.Path} reads back differently from its source.");
            seen++;
        }
        if (seen != expected.Count)
            throw new ZArchiveException($"The new archive holds {seen} files; {expected.Count} went in.");
        progress?.Invoke(1);
    }

    private static long TitleSize(WiiUTitle title)
    {
        if (!title.FromWua) return Archives.ArchiveJobs.SizeOf(title.SourcePath);
        using var reader = ZArchiveReader.Open(title.SourcePath);
        var folder = reader.Find(title.WuaFolder!);
        return folder is null ? 0 : reader.Walk(folder).Where(e => e.IsFile).Sum(e => (long)e.Size);
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Reads through to the source while hashing what passes.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public byte[] Hash() => _sha.GetHashAndReset();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _sha.AppendData(buffer, offset, n);
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _sha.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Writes through to a stream while hashing.</summary>
    private sealed class TeeStream(Stream inner, IncrementalHash sha) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            sha.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A write-only stream that only hashes.</summary>
    private sealed class HashOnlyStream(IncrementalHash sha) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => sha.AppendData(buffer, offset, count);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
