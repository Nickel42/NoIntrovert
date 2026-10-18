using System.Diagnostics;
using System.Security.Cryptography;
using LibChdman;
using NoIntrovert.Core.Cd;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Jobs;

public enum TestKind { CompareWithChdman, RoundTrip }

/// <summary>One row of the test results table (and of the CSV export).</summary>
public sealed record TestRecord(
    string Input, string Disc, string Test, bool Passed,
    TimeSpan LibTime, TimeSpan? ExeTime, long LibBytes, long? ExeBytes,
    double LibMbps, double? ExeMbps, string Notes);

/// <summary>
/// Library test bench: compare libchdman's CHDs with chdman.exe's, and
/// check that create → verify → extract gives back the original data.
/// </summary>
public static class TestJobs
{
    public static JobOutcome Run(JobContext c, string source, TestKind kind, Action<TestRecord> record)
    {
        var s = c.Settings;
        Directory.CreateDirectory(s.EffectiveTempFolder);
        c.Report.SetStage("Preparing");
        var preparer = new InputPreparer(s.EffectiveTempFolder, s.IsoMode, c.Log);
        using var prepared = preparer.Prepare(source,
            new InlineProgress<(string Stage, double Fraction)>(p => { c.Report.SetStage(p.Stage); c.Report.SetProgress(p.Fraction); }), c.Token);

        var passed = 0;
        foreach (var disc in prepared.Discs)
        {
            var r = kind == TestKind.CompareWithChdman
                ? Compare(c, source, disc, prepared.Work.Path)
                : RoundTrip(c, source, disc, prepared.Work.Path);
            record(r);
            if (r.Passed) passed++;
            (r.Passed ? (Action<string>)c.Log.Success : c.Log.Error)($"{r.Test} · {r.Disc}: {(r.Passed ? "PASS" : "FAIL")} — {r.Notes}");
        }

        var total = prepared.Discs.Count;
        return passed == total
            ? new JobOutcome(JobStatus.Succeeded, total == 1 ? "Pass" : $"{passed}/{total} pass")
            : new JobOutcome(JobStatus.Failed, $"{total - passed} of {total} failed");
    }

    private static TestRecord Compare(JobContext c, string source, PreparedDisc disc, string work)
    {
        var exe = c.Settings.ChdmanExePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new FileNotFoundException("Set the path to chdman.exe on the Library tests tab to run comparisons.");

        var libOut = Path.Combine(work, disc.OutputName + ".lib.chd");
        var exeOut = Path.Combine(work, disc.OutputName + ".exe.chd");
        var settings = c.OutputSettings();
        settings.Force = true;

        c.TrackChdman("libchdman: compressing");
        var lib = ChdService.Create(c.Chdman, disc, libOut, settings, c.Token);

        c.Report.SetStage("chdman.exe: compressing");
        c.Report.SetProgress(0.5);
        var exeRun = ChdmanExe.Create(exe, disc, exeOut, c.Settings, c.Token);
        if (exeRun.ExitCode != 0 || !File.Exists(exeOut))
        {
            var tail = exeRun.Output.Trim().Split('\n').LastOrDefault()?.Trim();
            return new TestRecord(Path.GetFileName(source), disc.OutputName, "Compare", false, lib.Elapsed, exeRun.Elapsed,
                lib.OutputBytes, null, lib.MegabytesPerSecond, null, $"chdman.exe failed (exit {exeRun.ExitCode}): {tail}");
        }

        c.Report.SetStage("Comparing outputs");
        var exeBytes = new FileInfo(exeOut).Length;
        var exeMbps = exeRun.Elapsed.TotalSeconds > 0 ? lib.InputBytes / 1048576.0 / exeRun.Elapsed.TotalSeconds : 0;
        var difference = FirstDifference(libOut, exeOut, c.Token);
        string notes;
        bool same = difference < 0;
        if (same)
        {
            notes = $"byte-identical ({ChdJobs.Size(lib.OutputBytes)}); libchdman {lib.Elapsed.TotalSeconds:F1}s vs chdman.exe {exeRun.Elapsed.TotalSeconds:F1}s";
        }
        else
        {
            // Not identical: check whether at least the stored data matches.
            var libInfo = c.Chdman.Info(libOut);
            var exeInfo = c.Chdman.Info(exeOut);
            notes = libInfo.RawSha1 == exeInfo.RawSha1
                ? $"files differ from byte {difference} but the data SHA-1 matches ({libInfo.Compression} vs {exeInfo.Compression})"
                : $"files differ from byte {difference}; data SHA-1 {libInfo.RawSha1} vs {exeInfo.RawSha1}";
        }
        TryDelete(libOut);
        TryDelete(exeOut);
        return new TestRecord(Path.GetFileName(source), disc.OutputName, "Compare", same, lib.Elapsed, exeRun.Elapsed,
            lib.OutputBytes, exeBytes, lib.MegabytesPerSecond, exeMbps, notes);
    }

    private static TestRecord RoundTrip(JobContext c, string source, PreparedDisc disc, string work)
    {
        var chd = Path.Combine(work, disc.OutputName + ".chd");
        var extractDir = Path.Combine(work, "extract-" + disc.OutputName);
        Directory.CreateDirectory(extractDir);
        var settings = c.OutputSettings();
        settings.Force = true;

        var sw = Stopwatch.StartNew();
        c.TrackChdman("Compressing");
        var created = ChdService.Create(c.Chdman, disc, chd, settings, c.Token);

        c.TrackChdman("Verifying");
        var (verify, _) = ChdService.Verify(c.Chdman, chd, false, c.Token);

        var originalFiles = DataFiles(disc.InputPath);
        var format = disc.Kind switch
        {
            DiscKind.Cd when disc.InputPath.EndsWith(".gdi", StringComparison.OrdinalIgnoreCase) => ExtractFormat.Gdi,
            DiscKind.Cd when IsBareIso(disc.InputPath) => ExtractFormat.Iso,
            DiscKind.Cd => ExtractFormat.CueBin,
            DiscKind.Dvd => ExtractFormat.Iso,
            _ => ExtractFormat.Raw,
        };
        var splitBin = originalFiles.Count > 1;

        c.TrackChdman("Extracting");
        var (files, _) = ChdService.Extract(c.Chdman, chd, extractDir, format, splitBin, true, c.Log, c.Token);
        sw.Stop();

        c.Report.SetStage("Comparing data");
        var extractedFiles = DataFiles(files[0]);
        var (same, detail) = CompareConcatenated(originalFiles, extractedFiles, c.Token);
        var verified = verify.Outcome == ChdVerifyOutcome.Ok;

        TryDelete(chd);
        try { Directory.Delete(extractDir, true); } catch (IOException) { }

        var notes = $"{(verified ? "verify OK" : "verify " + verify.Outcome)}; {detail}; ratio {created.Ratio:P1}; total {sw.Elapsed.TotalSeconds:F1}s";
        return new TestRecord(Path.GetFileName(source), disc.OutputName, "Round trip", verified && same, created.Elapsed, null,
            created.OutputBytes, null, created.MegabytesPerSecond, null, notes);
    }

    /// <summary>The data files behind an input, in disc order: a cue's/gdi's tracks, or the image itself.</summary>
    private static List<string> DataFiles(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        IEnumerable<string> names = ext switch
        {
            ".cue" => DiscFiles.CueReferences(path),
            ".gdi" => DiscFiles.GdiReferences(path),
            _ => [Path.GetFileName(path)],
        };
        return names.Select(n => Path.GetFullPath(Path.Combine(dir, n))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsBareIso(string path) =>
        path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && !StartsWithSync(path);

    private static bool StartsWithSync(string path)
    {
        Span<byte> head = stackalloc byte[16];
        using var fs = File.OpenRead(path);
        return fs.Read(head) == 16 && CdSector.HasSync(head);
    }

    /// <summary>Compares two file lists as if each were one long file.</summary>
    private static (bool Same, string Detail) CompareConcatenated(List<string> a, List<string> b, CancellationToken token)
    {
        var lengthA = a.Sum(f => new FileInfo(f).Length);
        var lengthB = b.Sum(f => new FileInfo(f).Length);
        using var hashA = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var hashB = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        Feed(a, hashA, token);
        Feed(b, hashB, token);
        var sha1A = Convert.ToHexStringLower(hashA.GetHashAndReset());
        var sha1B = Convert.ToHexStringLower(hashB.GetHashAndReset());
        if (sha1A == sha1B)
            return (true, $"data identical, SHA-1 {sha1A[..12]}… over {a.Count} file(s)");
        return (false, lengthA != lengthB
            ? $"data differs: {lengthA:N0} bytes in vs {lengthB:N0} bytes out"
            : $"data differs: SHA-1 {sha1A[..12]}… vs {sha1B[..12]}…");
    }

    private static void Feed(List<string> files, IncrementalHash hash, CancellationToken token)
    {
        var buffer = new byte[1 << 20];
        foreach (var f in files)
        {
            using var fs = File.OpenRead(f);
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                token.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>Offset of the first differing byte, or -1 when the files are identical.</summary>
    internal static long FirstDifference(string a, string b, CancellationToken token)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        var ba = new byte[1 << 20];
        var bb = new byte[1 << 20];
        long offset = 0;
        while (true)
        {
            var na = fa.ReadAtLeast(ba, ba.Length, false);
            var nb = fb.ReadAtLeast(bb, bb.Length, false);
            var n = Math.Min(na, nb);
            var mismatch = ba.AsSpan(0, n).CommonPrefixLength(bb.AsSpan(0, n));
            if (mismatch < n) return offset + mismatch;
            if (na != nb) return offset + n;
            if (na == 0) return -1;
            offset += n;
            token.ThrowIfCancellationRequested();
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static string ToCsv(IEnumerable<TestRecord> records)
    {
        static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "input,disc,test,passed,lib_seconds,exe_seconds,lib_bytes,exe_bytes,lib_mb_s,exe_mb_s,notes" };
        foreach (var r in records)
            lines.Add(string.Join(",", Q(r.Input), Q(r.Disc), Q(r.Test), r.Passed ? "yes" : "no",
                r.LibTime.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                r.ExeTime?.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                r.LibBytes, r.ExeBytes?.ToString() ?? "",
                r.LibMbps.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                r.ExeMbps?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "", Q(r.Notes)));
        return string.Join(Environment.NewLine, lines);
    }
}
