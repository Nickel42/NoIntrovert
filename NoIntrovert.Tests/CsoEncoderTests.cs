using System.Security.Cryptography;
using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Tests;

/// <summary>
/// CSO and ZSO output: the encoder (7-Zip's deflate through lib-7z-zip-kit, LZ4 HC), files that need an index
/// shift, blocks that don't compress, and the Convert targets and job for PSP and PS2 discs.
/// </summary>
internal static class CsoEncoderTests
{
    private static string Sha1(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(f));
    }

    /// <summary>Decodes <paramref name="cso"/> and returns null when it matches <paramref name="iso"/>, else what differs.</summary>
    private static string? RoundTrips(string cso, string iso, string dir)
    {
        var back = Path.Combine(dir, Path.GetFileName(cso) + ".iso");
        CsoDecoder.Decode(cso, back, null, CancellationToken.None);
        return Sha1(back) == Sha1(iso) ? null : $"{Path.GetFileName(cso)} decodes to a different image";
    }

    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir, ILog log,
        JobPerformanceTests.RunJobFn runJob, string isoSlice)
    {
        test("CSO/ZSO: an ISO compresses and decodes back to the same image, smaller than with .NET's deflate", () =>
        {
            var d = dir("cso-encode");
            var cso = Path.Combine(d, "slice.cso");
            var zso = Path.Combine(d, "slice.zso");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            CsoEncoder.Encode(isoSlice, cso, lz4: false, null, CancellationToken.None, sourceHash: hash);
            if (Convert.ToHexString(hash.GetHashAndReset()) != Sha1(isoSlice)) return "the source hash fed during encoding is wrong";
            CsoEncoder.Encode(isoSlice, zso, lz4: true, null, CancellationToken.None);
            if ((RoundTrips(cso, isoSlice, d) ?? RoundTrips(zso, isoSlice, d)) is { } bad) return bad;

            var header = File.ReadAllBytes(cso)[..24];
            if (System.Text.Encoding.ASCII.GetString(header, 0, 4) != "CISO" || header[20] != 1 || BitConverter.ToInt32(header, 16) != 2048)
                return "the CSO header isn't v1 with 2048-byte blocks";
            if (System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(zso), 0, 4) != "ZISO") return "the ZSO has the wrong magic";

            var iso = new FileInfo(isoSlice).Length;
            var cs = new FileInfo(cso).Length;
            if (cs >= iso) return $"the CSO isn't smaller ({cs} of {iso})";
            if (OperatingSystem.IsWindows() && !CsoEncoder.UsesSevenZipDeflate)
                return "7-Zip's deflate isn't available on Windows; rebuild lib-7z-zip-kit's native engine";
            var dotnet = Path.Combine(d, "dotnet.cso");
            Encoders.WriteCso(isoSlice, dotnet, lz4: false);
            var dn = new FileInfo(dotnet).Length;
            if (CsoEncoder.UsesSevenZipDeflate && cs > dn) return $"7-Zip's deflate made a bigger CSO ({cs}) than .NET's ({dn})";
            return Note($"CSO {cs * 100.0 / iso:F1}% (.NET deflate {dn * 100.0 / iso:F1}%), ZSO {new FileInfo(zso).Length * 100.0 / iso:F1}%"
                        + (CsoEncoder.UsesSevenZipDeflate ? " · 7-Zip deflate" : " · .NET deflate"));
        });

        test("CSO/ZSO: an index shift aligns every block, and both decode back (padding after LZ4 blocks is ignored)", () =>
        {
            var d = dir("cso-shift");
            foreach (var lz4 in new[] { false, true })
            {
                var path = Path.Combine(d, lz4 ? "shift.zso" : "shift.cso");
                CsoEncoder.Encode(isoSlice, path, lz4, null, CancellationToken.None, minimumShift: 4);
                var bytes = File.ReadAllBytes(path);
                if (bytes[21] != 4) return $"{Path.GetFileName(path)}: index shift {bytes[21]}, expected 4";
                var blocks = (int)((new FileInfo(isoSlice).Length + 2047) / 2048);
                for (var i = 0; i <= blocks; i++)
                {
                    var entry = BitConverter.ToUInt32(bytes, 24 + i * 4) & 0x7FFFFFFF;
                    if ((long)entry << 4 > bytes.Length) return $"{Path.GetFileName(path)}: block {i} points past the end";
                }
                if (RoundTrips(path, isoSlice, d) is { } bad) return bad;
            }
            // Offsets past 2 GiB need a shift; the shift is the smallest that reaches the end.
            if (CsoEncoder.IndexShift(32L << 20, 16384) != 0) return "a small image got a shift";
            var big = 5L << 30;
            var shift = CsoEncoder.IndexShift(big, (int)(big / 2048));
            return shift is >= 1 and <= 2 ? null : $"a 5 GiB image got shift {shift}";
        });

        test("CSO/ZSO: blocks that don't compress are stored as they are and flagged", () =>
        {
            var d = dir("cso-random");
            var noise = Path.Combine(d, "noise.iso");
            var data = new byte[1 << 20];
            new Random(42).NextBytes(data);
            data.AsSpan(0, 4096).Clear(); // two blocks that do compress
            File.WriteAllBytes(noise, data);
            foreach (var lz4 in new[] { false, true })
            {
                var path = Path.Combine(d, lz4 ? "noise.zso" : "noise.cso");
                CsoEncoder.Encode(noise, path, lz4, null, CancellationToken.None);
                var bytes = File.ReadAllBytes(path);
                if ((BitConverter.ToUInt32(bytes, 24) & 0x80000000) != 0) return $"{Path.GetFileName(path)}: a zero block was stored";
                if ((BitConverter.ToUInt32(bytes, 24 + 4 * 10) & 0x80000000) == 0) return $"{Path.GetFileName(path)}: a random block wasn't stored";
                if (RoundTrips(path, noise, d) is { } bad) return bad;
            }
            return null;
        });

        test("CSO/ZSO targets: a PSP disc offers CSO, a PS2 disc ZSO and CSO, and an override makes them the default", () =>
        {
            var set = FormatRegistry.Default.Identify(isoSlice);
            if (set is null) return "the ISO wasn't identified";
            var psp = TargetResolver.Resolve(set, TargetFormats.ById("playstation-portable"), new AppSettings());
            if (!psp.Alternatives.Contains(TargetChoice.Cso) || psp.Alternatives.Contains(TargetChoice.Zso))
                return $"PSP alternatives: {string.Join(", ", psp.Alternatives)}";
            var ps2 = TargetResolver.Resolve(set, TargetFormats.ById("playstation-2"), new AppSettings());
            if (!ps2.Alternatives.Contains(TargetChoice.Cso) || !ps2.Alternatives.Contains(TargetChoice.Zso))
                return $"PS2 alternatives: {string.Join(", ", ps2.Alternatives)}";
            var s = new AppSettings();
            s.TargetFormats.Overrides.Add(new TargetOverride("playstation-2", null, "zso"));
            var chosen = TargetResolver.Resolve(set, TargetFormats.ById("playstation-2"), s);
            if (chosen.Choice != TargetChoice.Zso || chosen.Status != TargetStatus.Convert) return $"zso override: {chosen.Text}";
            var presets = Core.Presets.FormatPresets.ForSystem("playstation-2").Where(p => p.Producible).Select(p => p.Values.Target);
            return presets.Contains(TargetKind.Zso) ? null : "the PS2 ZSO preset isn't producible";
        });

        test("CSO/ZSO job: an ISO becomes a verified .cso, and that .cso a verified .zso", () =>
        {
            var outDir = dir("cso-job");
            var plan = ConvertPlanner.Plan([new PlanInput(isoSlice, TargetKind.Cso, new FileInfo(isoSlice).Length)], new AppSettings(), log);
            var r = runJob(plan.Jobs[0].Work, st => st.Output.Folder = outDir);
            var cso = Path.Combine(outDir, "slice.cso");
            if (r.Status != JobStatus.Succeeded || !File.Exists(cso)) return $"to CSO: {r.Status}: {r.Summary}";
            if (RoundTrips(cso, isoSlice, dir("cso-job-check")) is { } bad) return bad;

            var zsoDir = dir("cso-job-zso");
            plan = ConvertPlanner.Plan([new PlanInput(cso, TargetKind.Zso, new FileInfo(cso).Length)], new AppSettings(), log);
            r = runJob(plan.Jobs[0].Work, st => st.Output.Folder = zsoDir);
            var zso = Path.Combine(zsoDir, "slice.zso");
            if (r.Status != JobStatus.Succeeded || !File.Exists(zso)) return $"CSO to ZSO: {r.Status}: {r.Summary}";
            return RoundTrips(zso, isoSlice, dir("cso-job-check2"));
        });
    }

    /// <summary>A pass with a note (the harness shows it after the name).</summary>
    private static string Note(string text) => "\u0001" + text;
}
