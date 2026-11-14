using System.Diagnostics;
using System.Security.Cryptography;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Jobs;

/// <summary>
/// PSP and PS2 discs as CSO (deflate, for a real PSP and Adrenaline) or ZSO (LZ4, for OPL on a real PS2):
/// the image is brought to a plain ISO (decoded from a CSO/ZSO, extracted from a DVD CHD or unpacked from
/// an archive), compressed by <see cref="CsoEncoder"/> in 2048-byte blocks, then checked by decoding it
/// again and comparing its SHA-1 with the ISO's.
/// </summary>
public static class CsoJobs
{
    /// <summary>The extensions a CSO/ZSO can be made from.</summary>
    public static bool TakesInput(FileSet set) =>
        Path.GetExtension(set.Primary).ToLowerInvariant() is ".iso" or ".cso" or ".zso" or ".chd"
        || (KnownFormats.Archives.Contains(set.Format) && KnownFormats.TakesDisc(set));

    public static JobOutcome Convert(JobContext c, string source, bool zso, string? outputFolder = null)
    {
        var s = c.Settings;
        var log = c.Log;
        var name = Path.GetFileName(source);
        var ext = zso ? ".zso" : ".cso";
        outputFolder ??= OutputPaths.FolderFor(s, source);
        Directory.CreateDirectory(outputFolder);
        Directory.CreateDirectory(s.Performance.EffectiveTempFolder);
        log.Info($"Converting {name} to {ext}");

        // R11: the source's own hashes, so the CSO can still be checked against Redump.
        IReadOnlyList<Dats.HashedFile>? originals = null;
        if (s.TargetFormats.RecordOriginalHashes)
        {
            c.Report.SetStage("Hashing the original");
            originals = Dats.VerifyJobs.HashesOf(c, source);
        }

        var replace = s.Output.ReplacesOriginals;
        var mustVerify = s.Chd.VerifyAfterCreate || replace;
        var isIso = Path.GetExtension(source).Equals(".iso", StringComparison.OrdinalIgnoreCase);
        var prepShare = isIso ? 0.0 : 0.2;
        var encodeShare = (1 - prepShare) * (mustVerify ? 0.7 : 1);

        using var work = new WorkDirectory(s.Performance.EffectiveTempFolder);
        string iso;
        string outputName;
        try
        {
            c.Band(0, prepShare);
            (iso, outputName) = IsoOf(c, source, work);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            return JobOutcome.Failed(ex.Message);
        }

        var wanted = Path.Combine(outputFolder, outputName + ext);
        var output = OutputPaths.Available(wanted, s.Output.Overwrite);
        if (PathNames.Same(output, source)) return JobOutcome.Skipped($"already {ext}");
        if (output != wanted) log.Info(OutputPaths.Renamed(wanted, output));

        c.Band(prepShare, encodeShare);
        c.Report.SetStage(zso ? "Compressing (LZ4)" : CsoEncoder.UsesSevenZipDeflate ? "Compressing (7-Zip deflate)" : "Compressing (deflate)");
        var sw = Stopwatch.StartNew();
        using var sourceSha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        try
        {
            CsoEncoder.Encode(iso, output, zso, new InlineProgress<double>(c.Report.SetProgress), c.Token,
                s.Performance.EffectiveThreadsPerJob, sourceSha1);
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        var stats = new OperationStats(sw.Elapsed, new FileInfo(iso).Length, new FileInfo(output).Length);
        log.Info($"  created {Path.GetFileName(output)}: {ChdJobs.Size(stats.InputBytes)} → {ChdJobs.Size(stats.OutputBytes)} ({stats.Ratio:P1}) in {stats.Elapsed:mm\\:ss\\.f}");

        if (mustVerify)
        {
            c.Band(prepShare + encodeShare, 1 - prepShare - encodeShare);
            c.Report.SetStage("Verifying");
            using var check = new Sha1Sink();
            try
            {
                CsoDecoder.Decode(output, check, new InlineProgress<double>(c.Report.SetProgress), c.Token, s.Performance.EffectiveThreadsPerJob);
            }
            catch (InvalidDataException ex)
            {
                log.Error($"  {Path.GetFileName(output)} could not be read back: {ex.Message}");
                return new JobOutcome(JobStatus.Failed, replace ? "Verification failed, so the original was kept" : "Created, but verification failed", stats);
            }
            var expected = System.Convert.ToHexString(sourceSha1.GetHashAndReset());
            var actual = System.Convert.ToHexString(check.Finish());
            if (expected != actual)
            {
                log.Error($"  verification of {Path.GetFileName(output)} failed: SHA-1 {actual}, expected {expected}");
                return new JobOutcome(JobStatus.Failed, replace ? "Verification failed, so the original was kept" : "Created, but verification failed", stats);
            }
            log.Debug($"  verified, SHA-1 {actual}");
        }

        if (originals is not null) Dats.OriginalHashes.Default.Set(output, source, originals);
        if (replace)
        {
            c.Report.SetStage("Removing originals");
            FileRemover.Remove(DiscFiles.SetFiles(source), s.Output.UseRecycleBin, log);
        }

        log.Success($"{name}: done{(mustVerify ? " and verified" : "")}");
        return new JobOutcome(JobStatus.Succeeded, $"{Path.GetFileName(output)} · {stats.Ratio:P0}", stats);
    }

    /// <summary>A plain ISO of <paramref name="source"/> (the file itself when it is one), and the name the output takes.</summary>
    private static (string Iso, string Name) IsoOf(JobContext c, string source, WorkDirectory work)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext == ".iso") return (source, Path.GetFileNameWithoutExtension(source));

        var progress = new InlineProgress<(string Stage, double Fraction)>(p => { c.Report.SetStage(p.Stage); c.Report.SetProgress(p.Fraction); });
        if (ext == ".chd")
        {
            c.TrackChdman("Extracting the ISO");
            var info = c.Chdman.Info(source, cancellationToken: c.Token);
            if (info.MediaType != "dvd")
                throw new InvalidDataException($"This CHD is a {ChdService.Describe(info.MediaType)}; only a DVD or UMD image can be made a CSO.");
            var (files, _) = ChdService.Extract(c.Chdman, source, work.Path, ExtractFormat.Iso, splitBin: false, overwrite: true, c.Log, c.Token);
            return (files[0], Path.GetFileNameWithoutExtension(source));
        }

        // CSO/ZSO, archives: decoded or unpacked to an ISO in the work folder.
        var preparer = new InputPreparer(work.Path, IsoMode.Dvd, c.Log) { Threads = c.Settings.Performance.EffectiveThreadsPerJob };
        var prepared = preparer.Prepare(source, progress, c.Token);
        var discs = prepared.Discs.Where(d => Path.GetExtension(d.InputPath).Equals(".iso", StringComparison.OrdinalIgnoreCase)).ToList();
        if (discs.Count != 1 || prepared.Discs.Count != 1)
            throw new InvalidDataException(prepared.Discs.Count > 1
                ? "This input holds several discs; convert each disc on its own."
                : "This input isn't an ISO image, so it can't be made a CSO (a CD image in bin/cue can't).");
        return (discs[0].InputPath, discs[0].OutputName);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A write-only stream that only takes the SHA-1 of what's written.</summary>
    private sealed class Sha1Sink : Stream
    {
        private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        public byte[] Finish() => _sha1.GetHashAndReset();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _sha1.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _sha1.AppendData(buffer);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _sha1.Dispose();
            base.Dispose(disposing);
        }
    }
}
