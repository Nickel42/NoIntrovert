using System.Diagnostics;
using NoIntrovert.Core;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Tests;

/// <summary>
/// The optional speed-ups from the 2026-11-12 review: CSO/PBP blocks inflated by one reused zlib inflater per thread,
/// a 3DS ROM packed after a chunked copy, source hashes worked out beside the conversion, fewer automatic threads per
/// job on a spinning hard drive, and the Library's match results read in the background at startup.
/// </summary>
internal static class OptCoreTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir, ILog log, JobPerformanceTests.RunJobFn runJob, bool rvzLoads)
    {
        // ---- CSO and PBP decoding, many threads ----

        var iso = Path.Combine(dir("opt-inflate"), "mixed.iso");
        File.WriteAllBytes(iso, Mixed(3 << 20, 2048, seed: 21));

        test("CSO decode: 4 threads give back the original image", () =>
        {
            var folder = dir("opt-cso");
            var cso = Path.Combine(folder, "mixed.cso");
            Encoders.WriteCso(iso, cso, lz4: false);
            var decoded = Path.Combine(folder, "decoded.iso");
            CsoDecoder.Decode(cso, decoded, null, CancellationToken.None, threads: 4, batchBytes: 64 * 1024);
            return Same(iso, decoded, "decoded");
        });

        test("PBP decode: 4 threads give back the original bin", () =>
        {
            var folder = dir("opt-pbp");
            var bin = Path.Combine(folder, "mixed.bin");
            File.WriteAllBytes(bin, Mixed(700 * 2352, 16 * 2352, seed: 22));
            var pbp = Path.Combine(folder, "EBOOT.PBP");
            Encoders.WritePbp(bin, pbp, "_SLUS_00002", [new(false, 0)]);
            var decoded = PbpExtractor.Extract(pbp, dir("opt-pbp-decoded"), "decoded", null, CancellationToken.None, threads: 4);
            return Same(bin, decoded[0].BinPath, "decoded");
        });

        test("CSO decode: a damaged deflate block fails, with everything before it written", () =>
        {
            var folder = dir("opt-cso-damage");
            var cso = Path.Combine(folder, "mixed.cso");
            Encoders.WriteCso(iso, cso, lz4: false);
            var bytes = File.ReadAllBytes(cso);
            // The first compressed block after block 200 gets an invalid block type (BTYPE 11).
            var damaged = -1;
            for (var i = 200; i < 1000 && damaged < 0; i++)
            {
                var entry = BitConverter.ToUInt32(bytes, 24 + i * 4);
                var next = BitConverter.ToUInt32(bytes, 24 + (i + 1) * 4) & 0x7FFFFFFF;
                if ((entry & 0x80000000) == 0 && next - entry < 2048) { bytes[entry] = 0xFF; damaged = i; }
            }
            if (damaged < 0) return "no compressed block to damage";
            File.WriteAllBytes(cso, bytes);
            string Fail(string output)
            {
                try { CsoDecoder.Decode(cso, output, null, CancellationToken.None, threads: 4, batchBytes: 32 * 1024); return "decoded without an error"; }
                catch (InvalidDataException ex) { return $"{ex.Message} after {new FileInfo(output).Length} bytes"; }
            }
            var failed = Fail(Path.Combine(folder, "damaged.iso"));
            return failed.EndsWith($" after {damaged * 2048L} bytes", StringComparison.Ordinal) ? null : failed;
        });

        // ---- The chunked copy ----

        test("Chunked copy: reports every byte, keeps the date, and removes a cancelled copy", () =>
        {
            var folder = dir("opt-copy");
            var from = Path.Combine(folder, "big.bin");
            File.WriteAllBytes(from, Mixed(9 << 20, 4096, seed: 25));
            File.SetLastWriteTimeUtc(from, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            var to = Path.Combine(folder, "copy.bin");
            long reported = 0;
            FileCopy.Copy(from, to, CancellationToken.None, n => reported += n);
            if (Same(from, to, "copy") is { } different) return different;
            if (reported != new FileInfo(from).Length) return $"reported {reported} bytes";
            if (File.GetLastWriteTimeUtc(to) != File.GetLastWriteTimeUtc(from)) return "the date wasn't kept";
            using var cts = new CancellationTokenSource();
            var cancelled = Path.Combine(folder, "cancelled.bin");
            try { FileCopy.Copy(from, cancelled, cts.Token, _ => cts.Cancel()); return "finished although cancelled"; }
            catch (OperationCanceledException) { }
            return File.Exists(cancelled) ? "the cancelled copy was left behind" : null;
        });

        test("Pack: a ROM renamed inside its archive is copied in chunks, with progress, when its folder is left alone", () =>
        {
            var folder = dir("opt-pack");
            var rom = Path.Combine(folder, "Game (USA).smc");
            File.WriteAllBytes(rom, Mixed(6 << 20, 4096, seed: 26)); // no copier header: a rename only
            var copying = new StageReport("Copying");
            using var chdman = new LibChdKit.Chdman(1);
            var s = new AppSettings();
            s.Output.LeaveSourcesAlone = true;
            s.TargetFormats.NormalizeHeaders = true;
            s.Output.SaveTo = SaveTo.Folder;
            s.Output.Folder = dir("opt-pack-out");
            s.Performance.TempFolder = dir("opt-pack-temp");
            var c = new JobContext(chdman, s, log, copying, CancellationToken.None);
            var o = RomJobs.Pack(c, rom, "Game (USA)", s.Output.Folder);
            if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
            if (copying.Values is not [_, _, ..] || copying.Values[^1] < 0.999) return $"copy progress: {string.Join(", ", copying.Values)}";
            var back = dir("opt-pack-back");
            using (var engine = new SevenZipEngine()) engine.Extract(Path.Combine(s.Output.Folder, "Game (USA).7z"), back);
            if (!File.Exists(rom)) return "the ROM was moved";
            return Same(rom, Path.Combine(back, "Game (USA).sfc"), "packed ROM");
        });

        // ---- Spinning drives ----

        test("Threads per job: Automatic stops at 4 on a spinning drive; a chosen number is kept", () =>
        {
            (int Threads, int Jobs, int Cpus, bool Spinning, int Want)[] cases =
                [(0, 1, 16, true, 4), (0, 1, 16, false, 16), (0, 2, 16, true, 4), (0, 8, 16, true, 2), (0, 1, 2, true, 2), (6, 1, 16, true, 6)];
            foreach (var x in cases)
                if (PerformanceSettings.EffectiveThreads(x.Threads, x.Jobs, x.Cpus, x.Spinning) is var got && got != x.Want)
                    return $"{x}: {got}";

            var hdd = dir("opt-hdd");
            Drives.ForTests = p => p.StartsWith(hdd, StringComparison.OrdinalIgnoreCase);
            try
            {
                var s = new AppSettings();
                s.Performance.ParallelJobs = 1;
                var auto = s.Performance.EffectiveThreadsPerJob;
                var onHdd = JobQueue.SettingsFor(s, [Path.Combine(hdd, "game.iso")]).Performance.EffectiveThreadsPerJob;
                if (onHdd != Math.Min(auto, PerformanceSettings.SpinningDriveThreads)) return $"source on a hard drive: {onHdd} threads";
                if (!ReferenceEquals(JobQueue.SettingsFor(s, [Path.Combine(dir("opt-ssd"), "game.iso")]), s)) return "an SSD job's settings were copied";
                s.Output.SaveTo = SaveTo.Folder;
                s.Output.Folder = hdd;
                if (JobQueue.SettingsFor(s, [Path.Combine(dir("opt-ssd"), "game.iso")]).Performance.EffectiveThreadsPerJob != onHdd) return "Save to a hard drive wasn't capped";
                s.Performance.ThreadsPerJob = 7;
                return JobQueue.SettingsFor(s, [Path.Combine(hdd, "game.iso")]).Performance.EffectiveThreadsPerJob == 7 ? null : "a chosen number was capped";
            }
            finally { Drives.ForTests = null; }
        });

        test("Spinning drive check: asks each drive once, and a share or no path isn't spinning", () =>
        {
            var here = dir("opt-drive");
            var first = Drives.IsSpinning(here);
            var asked = Drives.Queries;
            if (Drives.IsSpinning(Path.Combine(here, "other.iso")) != first || Drives.Queries != asked) return "the drive was asked again";
            if (Drives.IsSpinning(null) || Drives.IsSpinning(@"\\server\share\game.iso") && OperatingSystem.IsWindows()) return "a share or no path counted as spinning";
            var drive = Path.GetPathRoot(Path.GetFullPath(here));
            return Note($"{drive} is {(first ? "a spinning drive" : "not a spinning drive (or couldn't tell)")}");
        });

        // ---- Hashing beside the conversion ----

        test("Hash beside: the same hashes as hashing after, cancelled with the job, and not on a spinning drive", () =>
        {
            var file = Path.Combine(dir("opt-beside"), "game.bin");
            File.WriteAllBytes(file, Mixed(5 << 20, 4096, seed: 27));
            using (var beside = BackgroundHash.Start(file, t => Hasher.HashFile(file, t), CancellationToken.None))
            {
                if (beside is null) return Drives.IsSpinning(file) ? Note("the scratch folder is on a spinning drive") : "didn't start";
                if (!BackgroundHash.IsRunning(file)) return "not registered while it runs";
                var got = beside.Result();
                var want = Hasher.HashFile(file);
                if (got[0] != want[0]) return $"{got[0].Sha1}, after: {want[0].Sha1}";
            }
            if (BackgroundHash.IsRunning(file)) return "still registered after it was disposed";

            using var cts = new CancellationTokenSource();
            using (var slow = BackgroundHash.Start(file, t => { cts.Cancel(); t.WaitHandle.WaitOne(); t.ThrowIfCancellationRequested(); return 1; }, cts.Token))
            {
                try { slow!.Result(); return "a cancelled job's hash finished"; }
                catch (OperationCanceledException) { }
            }

            Drives.ForTests = _ => true;
            try { return BackgroundHash.Start(file, _ => 1, CancellationToken.None) is null ? null : "started on a spinning drive"; }
            finally { Drives.ForTests = null; }
        });

        if (rvzLoads)
            test("GameCube ISO → RVZ: the source is hashed beside the compression, with the same R11 hashes and verify", () =>
            {
                var folder = dir("opt-rvz");
                string? Convert(string name, bool spinning, out IReadOnlyList<HashedFile>? kept, out int beside)
                {
                    kept = null;
                    var disc = Path.Combine(folder, name);
                    NintendoDiscs.WriteGameCube(disc);
                    var before = BackgroundHash.Count;
                    Drives.ForTests = spinning ? _ => true : null;
                    try
                    {
                        var o = runJob(c => ChdJobs.Convert(c, disc), s =>
                        {
                            s.Output.Folder = dir("opt-rvz-out");
                            s.TargetFormats.RecordOriginalHashes = true;
                            s.Chd.VerifyAfterCreate = true;
                        });
                        beside = BackgroundHash.Count - before;
                        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
                    }
                    finally { Drives.ForTests = null; }
                    kept = OriginalHashes.Default.Get(Path.Combine(dir("opt-rvz-out"), Path.ChangeExtension(name, ".rvz")))?.Files;
                    return kept is null ? "no original hashes were kept" : null;
                }
                if (Convert("Beside (USA).iso", spinning: false, out var a, out var startedA) is { } p1) return p1;
                if (Convert("After (USA).iso", spinning: true, out var b, out var startedB) is { } p2) return p2;
                if (startedA != 1) return $"{startedA} hashes ran beside the compression, expected 1";
                if (startedB != 0) return "a hash ran beside the compression on a spinning drive";
                // The two discs are the same bytes: the same hashes either way.
                return a![0] with { Name = "" } == b![0] with { Name = "" } ? null : $"beside: {a[0]}, after: {b[0]}";
            });

        // ---- The Library's match results ----

        test("Library: the match results are read in the background, once", () =>
        {
            var warm = MatchStore.WarmInBackground();
            if (!warm.Wait(TimeSpan.FromSeconds(30))) return "the warm-up didn't finish";
            return ReferenceEquals(MatchStore.Default, MatchStore.Default) ? null : "the store was read again";
        });
    }

    /// <summary>The progress reported while the job is at one stage.</summary>
    private sealed class StageReport(string stage) : IJobReport
    {
        private string _stage = "";
        public List<double> Values { get; } = [];
        public void SetStage(string s) => _stage = s;
        public void SetProgress(double fraction) { if (_stage == stage) Values.Add(fraction); }
    }

    /// <summary>Blocks of zeros, text and random bytes in turn: deflate blocks of different sizes, and stored ones.</summary>
    private static byte[] Mixed(int length, int block, int seed)
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

    private static string? Same(string expected, string actual, string what)
    {
        var a = File.ReadAllBytes(expected);
        var b = File.ReadAllBytes(actual);
        if (a.Length != b.Length) return $"{what}: {b.Length:N0} bytes, expected {a.Length:N0}";
        var at = a.AsSpan().CommonPrefixLength(b);
        return at == a.Length ? null : $"{what}: differs at byte {at:N0}";
    }

    private static string Note(string text) => "\u0001" + text;
}
