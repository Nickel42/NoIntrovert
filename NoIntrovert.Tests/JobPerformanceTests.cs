using LibChdKit;
using LibWiiGameCubeKit;
using NoIntrovert.Core;
using NoIntrovert.Core.Ctr;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Xbox;

namespace NoIntrovert.Tests;

/// <summary>
/// Fixes from the 2026-11-12 review of conversion jobs: threads per job shared between parallel jobs, CSO/ZSO and
/// PBP decoded in parallel batches with the same output, errors and cancellation as one block at a time, and
/// sources whose hashes are already known (hash cache, originals.json) not read again.
/// </summary>
internal static class JobPerformanceTests
{
    public delegate JobOutcome RunJobFn(JobWork work, Action<AppSettings>? configure);

    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir, ILog log, RunJobFn runJob, bool rvzLoads)
    {
        test("Threads per job: Automatic shares the processors between the parallel jobs", () =>
        {
            (int Threads, int Jobs, int Cpus, int Want)[] cases =
            [
                (0, 2, 8, 4), (0, 1, 8, 8), (0, 3, 8, 2), (0, 16, 8, 1), (0, 0, 8, 8), (0, 2, 1, 1), (0, 2, 0, 1),
                (3, 2, 8, 3), (12, 4, 8, 12),
            ];
            foreach (var c in cases)
            {
                var got = PerformanceSettings.EffectiveThreads(c.Threads, c.Jobs, c.Cpus);
                if (got != c.Want) return $"{c.Threads} threads, {c.Jobs} jobs, {c.Cpus} processors: {got}, expected {c.Want}";
            }
            var s = new PerformanceSettings { ParallelJobs = 2, ThreadsPerJob = 0 };
            var expected = Math.Max(1, Environment.ProcessorCount / Math.Min(2, Environment.ProcessorCount));
            if (s.EffectiveThreadsPerJob != expected) return $"this machine: {s.EffectiveThreadsPerJob}, expected {expected}";
            return System.Text.Json.JsonSerializer.Serialize(s).Contains("Effective") ? "the effective count was saved to settings.json" : null;
        });

        // ---- CSO / ZSO / PBP in parallel batches ----

        var iso = Path.Combine(dir("perf-cso"), "mixed.iso");
        File.WriteAllBytes(iso, MixedImage(2 << 20, 2048, seed: 7));

        foreach (var lz4 in new[] { false, true })
        {
            var kind = lz4 ? "ZSO" : "CSO";
            test($"{kind} decode: parallel batches give the same image as one block at a time", () =>
            {
                var folder = dir($"perf-{kind}-par");
                var cso = Path.Combine(folder, "mixed." + kind.ToLowerInvariant());
                Encoders.WriteCso(iso, cso, lz4);
                var one = Path.Combine(folder, "one.iso");
                var many = Path.Combine(folder, "many.iso");
                CsoDecoder.Decode(cso, one, null, CancellationToken.None, threads: 1, batchBytes: 1);
                CsoDecoder.Decode(cso, many, null, CancellationToken.None, threads: 4, batchBytes: 24 * 1024); // 12 blocks a batch, the last one short
                return SameBytes(iso, one, "one at a time") ?? SameBytes(iso, many, "parallel");
            });
        }

        test("CSO decode: a damaged block fails the same way, after the same blocks, in parallel", () =>
        {
            var folder = dir("perf-cso-damage");
            var cso = Path.Combine(folder, "mixed.cso");
            Encoders.WriteCso(iso, cso, lz4: false);
            var bytes = File.ReadAllBytes(cso);
            const int bad = 301; // mid-batch for the parallel run
            BitConverter.GetBytes(0u).CopyTo(bytes, 24 + (bad + 1) * 4); // block 301 now ends before it starts
            File.WriteAllBytes(cso, bytes);

            string Fail(int threads, int batch, string output)
            {
                try { CsoDecoder.Decode(cso, output, null, CancellationToken.None, threads, batch); return "decoded without an error"; }
                catch (InvalidDataException ex) { return $"{ex.Message} after {new FileInfo(output).Length} bytes"; }
            }
            var one = Fail(1, 1, Path.Combine(folder, "one.iso"));
            var many = Fail(4, 64 * 1024, Path.Combine(folder, "many.iso"));
            var want = $"CSO block {bad} has an invalid size; the file is damaged. after {bad * 2048L} bytes";
            return one != want ? $"one at a time: {one}" : many != want ? $"parallel: {many}" : null;
        });

        test("CSO decode: cancelling mid-run stops it", () =>
        {
            var folder = dir("perf-cso-cancel");
            var cso = Path.Combine(folder, "mixed.cso");
            Encoders.WriteCso(iso, cso, lz4: false);
            using var cts = new CancellationTokenSource();
            var progress = new InlineProgress<double>(f => { if (f > 0.25) cts.Cancel(); });
            try
            {
                CsoDecoder.Decode(cso, Path.Combine(folder, "out.iso"), progress, cts.Token, threads: 4, batchBytes: 64 * 1024);
                return "finished although cancelled";
            }
            catch (OperationCanceledException) { }
            var written = new FileInfo(Path.Combine(folder, "out.iso")).Length;
            return written < new FileInfo(iso).Length ? null : "wrote the whole image before stopping";
        });

        test("PBP decode: parallel batches give the same bin as one block at a time", () =>
        {
            var folder = dir("perf-pbp");
            var bin = Path.Combine(folder, "mixed.bin");
            const int sectors = 1000; // 62.5 blocks of 16 sectors: the last one is short
            File.WriteAllBytes(bin, MixedImage(sectors * 2352, 16 * 2352, seed: 11));
            var pbp = Path.Combine(folder, "EBOOT.PBP");
            Encoders.WritePbp(bin, pbp, "_SLUS_00001", [new(false, 0)]);
            var one = PbpExtractor.Extract(pbp, dir("perf-pbp-one"), "one", null, CancellationToken.None, threads: 1, batchBytes: 1);
            var many = PbpExtractor.Extract(pbp, dir("perf-pbp-many"), "many", null, CancellationToken.None, threads: 4, batchBytes: 5 * 16 * 2352);
            return SameBytes(bin, one[0].BinPath, "one at a time") ?? SameBytes(bin, many[0].BinPath, "parallel");
        });

        // ---- Hashes already known ----

        test("Hash cache: a lookup that misses doesn't add an entry", () =>
        {
            var file = Path.Combine(dir("perf-known"), "miss.bin");
            File.WriteAllBytes(file, MixedImage(4096, 2048, seed: 3));
            if (KnownHashes.Cached(file) is not null) return "a new file was found in the cache";
            return KnownHashes.Cached(file) is null ? null : "the miss added an entry";
        });

        test("3DS original hashes come from the hash cache, and a new file's go into it", () =>
        {
            var folder = dir("perf-3ds");
            var known = Path.Combine(folder, "Known.cci");
            File.WriteAllBytes(known, MixedImage(64 * 1024, 2048, seed: 5));
            HashedFile fake = new("Known.cci", 1, "00000000", "FROMCACHE", Md5: "FROMCACHE");
            HashCache.Default.GetOrAdd(known, () => [fake]);
            var hits = HashCache.Default.Hits;
            var got = CtrJobs.OriginalOf(known, CancellationToken.None);
            if (got is not [{ Sha1: "FROMCACHE" }]) return "the file was hashed again";
            if (HashCache.Default.Hits != hits + 1) return "no cache hit counted";

            var fresh = Path.Combine(folder, "Fresh.cci");
            File.WriteAllBytes(fresh, MixedImage(64 * 1024, 2048, seed: 6));
            var hashed = CtrJobs.OriginalOf(fresh, CancellationToken.None);
            if (KnownHashes.Cached(fresh)?[0].Sha1 != hashed[0].Sha1) return "a new file's hashes weren't cached";

            var temp = Path.Combine(folder, "Unpacked.cci");
            File.WriteAllBytes(temp, MixedImage(64 * 1024, 2048, seed: 8));
            CtrJobs.OriginalOf(temp, CancellationToken.None, cache: false);
            return KnownHashes.Cached(temp) is null ? null : "a temp file was cached";
        });

        test("Xbox: a Redump image in the hash cache isn't hashed again (with MD5 only, never a split image)", () =>
        {
            var folder = dir("perf-xbox");
            var image = Path.Combine(folder, "Game.iso");
            File.WriteAllBytes(image, new byte[8192]);
            HashCache.Default.GetOrAdd(image, () => [new HashedFile("old name.iso", 8192, "AAAAAAAA", "BBBB", Md5: "CCCC")]);
            if (XboxJobs.KnownWhole(image) is not [{ Sha1: "BBBB", Md5: "CCCC", Name: "Game.iso" }]) return "the cached hashes weren't used";

            var noMd5 = Path.Combine(folder, "Old.iso");
            File.WriteAllBytes(noMd5, new byte[8192]);
            HashCache.Default.GetOrAdd(noMd5, () => [new HashedFile("Old.iso", 8192, "AAAAAAAA", "BBBB")]);
            if (XboxJobs.KnownWhole(noMd5) is not null) return "an entry without MD5 was used";

            var part = Path.Combine(folder, "Split.1.iso");
            File.WriteAllBytes(part, new byte[8192]);
            File.WriteAllBytes(Path.Combine(folder, "Split.2.iso"), new byte[8192]);
            HashCache.Default.GetOrAdd(part, () => [new HashedFile("Split.1.iso", 8192, "AAAAAAAA", "BBBB", Md5: "CCCC")]);
            return XboxJobs.KnownWhole(part) is null ? null : "one part's entry stood for a split image";
        });

        test("RVZ verify: the source's SHA-1 comes from the hash cache, without decoding it", () =>
        {
            var folder = dir("perf-rvz-known");
            var disc = Path.Combine(folder, "Game.iso");
            File.WriteAllBytes(disc, new byte[8192]);
            HashCache.Default.GetOrAdd(disc, () => [new HashedFile("Game.iso", 8192, "AAAAAAAA", "ABCDEF")]);
            using var chdman = new Chdman(1);
            var decoded = new Lazy<WiiGameCubeKit>(() => throw new InvalidOperationException("the source was decoded"));
            var c = new JobContext(chdman, new AppSettings(), log, new NullReport(), CancellationToken.None, decoded);
            var prepared = new PreparedDisc("Game", DiscKind.Dvd, disc, "GameCube/Wii iso", IsNintendo: true);
            var (sha1, size) = RvzJobs.SourceHash(c, prepared, disc);
            if (sha1 != "ABCDEF" || size != 8192) return $"got {sha1}, {size}";

            // A disc unpacked from an archive isn't the source: it's decoded.
            try { RvzJobs.SourceHash(c, prepared, Path.Combine(folder, "Game.zip")); return "a temp disc wasn't decoded"; }
            catch (InvalidOperationException ex) when (ex.Message == "the source was decoded") { return null; }
        });

        if (!rvzLoads) return;
        test("GameCube ISO → RVZ with R11 and verify reads the source for the hash once, not twice", () =>
        {
            var folder = dir("perf-rvz");
            var disc = Path.Combine(folder, "Perf Game (USA).iso");
            NintendoDiscs.WriteGameCube(disc);
            var hits = HashCache.Default.Hits;
            var o = runJob(c => ChdJobs.Convert(c, disc), s =>
            {
                s.Output.Folder = dir("perf-rvz-out");
                s.TargetFormats.RecordOriginalHashes = true;
                s.Chd.VerifyAfterCreate = true;
            });
            if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
            // R11 hashed the ISO (a miss); the verify then found it in the cache instead of decoding it again.
            return HashCache.Default.Hits == hits + 1 ? null : $"{HashCache.Default.Hits - hits} cache hits, expected 1";
        });
    }

    /// <summary>Blocks of zeros, text and random bytes in turn: deflate/LZ4 blocks of different sizes, and stored ones.</summary>
    private static byte[] MixedImage(int length, int block, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        var text = WiiUSample.Text(block);
        for (var off = 0; off < length; off += block)
        {
            var span = data.AsSpan(off, Math.Min(block, length - off));
            switch (off / block % 3)
            {
                case 1: text.AsSpan(0, span.Length).CopyTo(span); span[0] = (byte)(off / block); break;
                case 2: rng.NextBytes(span); break;
            }
        }
        return data;
    }

    private static string? SameBytes(string expected, string actual, string what)
    {
        var a = File.ReadAllBytes(expected);
        var b = File.ReadAllBytes(actual);
        if (a.Length != b.Length) return $"{what}: {b.Length:N0} bytes, expected {a.Length:N0}";
        var at = a.AsSpan().CommonPrefixLength(b);
        return at == a.Length ? null : $"{what}: differs at byte {at:N0}";
    }
}
