using System.Diagnostics;
using System.Security.Cryptography;
using NoIntrovert.Core.Formats;
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
    /// <param name="outputFolder">Where the .wua goes; by default the output folder setting or the first source's folder.</param>
    /// <param name="removeSources">Whether "remove originals" may remove <paramref name="sources"/> (not when they were unpacked from an archive).</param>
    public static JobOutcome Build(JobContext c, ulong baseId, IReadOnlyList<string> sources, string? outputFolder = null, bool removeSources = true)
    {
        var s = c.Settings;
        var log = c.Log;

        // Every title from every source, then the newest of each title ID.
        var keys = sources.Any(p => WiiUEncrypted.IsNusFolder(p) || WiiUEncrypted.IsDiscImage(p))
            ? WiiUKeys.Load(s.Keys.WiiUKeysPath) : WiiUKeys.Empty;
        var all = sources.SelectMany(p => WiiUTitles.Read(p, keys)).Where(t => t.BaseId == baseId).ToList();
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
        outputFolder ??= OutputPaths.FolderFor(s, sources[0]);
        Directory.CreateDirectory(outputFolder);
        var output = Path.Combine(outputFolder, WiiUTitles.WuaFileName(baseId, name));
        var replacesInput = sources.Any(p => Path.GetFullPath(p).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase));
        if (File.Exists(output) && !replacesInput && !s.Output.Overwrite)
        {
            log.Warn($"  {Path.GetFileName(output)} already exists; skipped (turn on Overwrite to replace it).");
            return JobOutcome.Skipped("Archive already exists");
        }

        log.Info($"Building {Path.GetFileName(output)}: {string.Join(", ", chosen.Select(t => $"{t.KindText} v{t.Version}"))}");
        foreach (var t in chosen) log.Debug($"  {t.ArchiveFolder} ← {t.SourcePath}{(t.FromWua ? $" ({t.WuaFolder})" : t.IsEncrypted ? $" ({t.Source}, decrypted)" : "")}");

        var temp = output + $".{Guid.NewGuid().ToString("N")[..8]}.tmp";
        var sw = Stopwatch.StartNew();
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long inputBytes = 0;
        try
        {
            var total = Math.Max(1, chosen.Sum(t => TitleSize(t, keys)));
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
                    else if (title.IsEncrypted) CopyFromEncrypted(writer, title, keys, hashes, Progress, c, c.Token);
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
        var stats = new OperationStats(sw.Elapsed, chosen.Sum(t => TitleSize(t, keys)), outputBytes);
        log.Info($"  wrote {Path.GetFileName(output)}: {hashes.Count} files, {ChdJobs.Size(stats.InputBytes)} → {ChdJobs.Size(outputBytes)} ({stats.Ratio:P1}) in {sw.Elapsed:mm\\:ss\\.f}");

        if (s.Output.RemoveWiiUOriginals && removeSources)
        {
            c.Report.SetStage("Removing originals");
            var originals = sources.Where(p => !Path.GetFullPath(p).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
                .SelectMany(OriginalFiles);
            FileRemover.Remove(originals, s.Output.UseRecycleBin, log);
        }

        log.Success($"{Path.GetFileName(output)}: done and verified");
        var parts = string.Join(" + ", chosen.Select(t => t.KindText).Distinct());
        return new JobOutcome(JobStatus.Succeeded, $"{Path.GetFileName(output)} · {parts}", stats);
    }

    /// <summary>
    /// A zip, 7z or tar holding Wii U games (a .wux, a .wua, title or NUS
    /// folders): unpacked into the temp folder, then one .wua per game, written
    /// next to the archive. "Remove originals" removes the archive once every
    /// game in it is built and verified.
    /// </summary>
    public static JobOutcome BuildFromArchive(JobContext c, string archive)
    {
        var s = c.Settings;
        Directory.CreateDirectory(s.Performance.EffectiveTempFolder);
        using var work = new WorkDirectory(s.Performance.EffectiveTempFolder);

        c.Band(0, 0.3);
        c.Report.SetStage("Unpacking archive");
        c.Log.Info($"Unpacking {Path.GetFileName(archive)}...");
        using (var engine = new Archives.ArchiveEngine())
            engine.Extract(archive, work.Path, c.Report.SetProgress, c.Token);

        var sources = FormatRegistry.Default.Scan(work.Path, recursive: true, maxDepth: 6)
            .Where(f => KnownFormats.WiiUBuildable.Contains(f.Format))
            .Select(f => f.Primary)
            .ToList();
        var keys = sources.Any(p => WiiUEncrypted.IsNusFolder(p) || WiiUEncrypted.IsDiscImage(p))
            ? WiiUKeys.Load(s.Keys.WiiUKeysPath) : WiiUKeys.Empty;
        var games = sources.SelectMany(p => WiiUTitles.Read(p, keys)).Select(t => t.BaseId).Distinct().ToList();
        // Nothing built means nothing verified, so the archive is never removed for it.
        if (games.Count == 0)
        {
            c.Log.Warn($"  {Path.GetFileName(archive)}: no Wii U game found (no .wux, .wud, .wua, NUS or title folder inside)");
            return JobOutcome.Skipped("No Wii U game found in the archive");
        }
        var outputFolder = OutputPaths.FolderFor(s, archive);

        var outcomes = new List<JobOutcome>();
        for (var i = 0; i < games.Count; i++)
        {
            c.Band(0.3 + 0.7 * i / games.Count, 0.7 / games.Count);
            outcomes.Add(Build(c, games[i], sources, outputFolder, removeSources: false));
        }

        var built = outcomes.Where(o => o.Status is JobStatus.Succeeded or JobStatus.Warning).ToList();
        if (s.Output.RemoveWiiUOriginals && built.Count == outcomes.Count)
        {
            c.Report.SetStage("Removing the archive");
            FileRemover.Remove([archive], s.Output.UseRecycleBin, c.Log);
        }
        if (outcomes.Count == 1) return outcomes[0];
        var failed = outcomes.Count(o => o.Status == JobStatus.Failed);
        var stats = built.Select(o => o.Stats).OfType<OperationStats>()
            .Aggregate((OperationStats?)null, (a, b) => a is null ? b : new OperationStats(a.Elapsed + b.Elapsed, a.InputBytes + b.InputBytes, a.OutputBytes + b.OutputBytes));
        return new JobOutcome(failed > 0 ? JobStatus.Failed : built.Count == outcomes.Count ? JobStatus.Succeeded : JobStatus.Warning,
            $"{built.Count} of {outcomes.Count} games built" + (failed > 0 ? $", {failed} failed" : ""), stats);
    }

    /// <summary>What removing a source means: a split WUD is all of its parts.</summary>
    private static IEnumerable<string> OriginalFiles(string source)
    {
        var m = WiiUEncrypted.SplitPart().Match(Path.GetFileName(source));
        if (!m.Success || !File.Exists(source)) return [source];
        var dir = Path.GetDirectoryName(source)!;
        var parts = new List<string>();
        for (var i = 1; File.Exists(Path.Combine(dir, $"{m.Groups[1].Value}_part{i}.wud")); i++)
            parts.Add(Path.Combine(dir, $"{m.Groups[1].Value}_part{i}.wud"));
        return parts;
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

    /// <summary>
    /// Decrypts an NUS title or a disc's game partition straight into the
    /// archive. Hashed clusters are checked block by block as they're read;
    /// an NUS title's raw contents are first checked against its TMD.
    /// </summary>
    private static void CopyFromEncrypted(ZArchiveWriter writer, WiiUTitle title, WiiUKeys keys, Dictionary<string, byte[]> hashes,
        Action<long> progress, JobContext c, CancellationToken token)
    {
        using var opened = WiiUTitles.OpenEncrypted(title.SourcePath, title.Source, keys);
        if (opened.Contents is { } contents)
        {
            c.Report.SetStage($"Checking {title.KindText} contents");
            var n = opened.Volume.VerifyRawClusters(contents, null, token);
            c.Log.Debug($"  {title.ArchiveFolder}: {n} raw content(s) match the TMD");
            c.Report.SetStage("Packing");
        }
        var links = 0;
        foreach (var entry in opened.Volume.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = title.ArchiveFolder + "/" + entry.Path;
            if (entry.IsDirectory)
            {
                writer.AddDirectory(path);
                continue;
            }
            if (entry.IsLink)
            {
                links++;
                continue;
            }
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            writer.AddFile(path, sink =>
            {
                using var tee = new TeeStream(sink, sha);
                opened.Volume.Copy(entry, tee, progress, token);
            });
            hashes[path] = sha.GetHashAndReset();
        }
        if (links > 0) c.Log.Warn($"  {title.ArchiveFolder}: skipped {links} linked file entr{(links == 1 ? "y" : "ies")} (they point at data elsewhere on the disc)");
    }

    private static long TitleSize(WiiUTitle title, WiiUKeys keys)
    {
        if (title.IsEncrypted)
        {
            using var opened = WiiUTitles.OpenEncrypted(title.SourcePath, title.Source, keys);
            return opened.Volume.Entries.Where(e => !e.IsDirectory && !e.IsLink).Sum(e => e.Size);
        }
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
