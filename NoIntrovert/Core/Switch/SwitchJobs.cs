using LibNxKit;
using LibNxKit.Keys;
using LibNxKit.Ncz;
using LibNxKit.Packages;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Switch;

/// <summary>
/// Switch conversions (NSP, NSZ, XCI, XCZ), all done by lib-nx-kit: it reads the package, compresses or
/// decompresses it and checks every content file's hash against its metadata as it writes. NoIntrovert
/// only picks the output's name and folder, and removes the original afterwards when asked (Replace the
/// originals). The keys are the user's own prod.keys, found by lib-nx-kit's <see cref="KeyLocator"/>.
/// </summary>
public static class SwitchJobs
{
    public const string NoKeysHelp =
        "Switch files need your own prod.keys (dumped from your Switch with Lockpick_RCM). Put it where Ryujinx or a yuzu-family emulator keeps it, or choose it in Settings › Keys.";

    /// <summary>The self-test's stand-in keys (lib-nx-kit's fake ones, in its tests/fixtures).</summary>
    internal static Func<KeySet>? KeysForTests;

    /// <summary>The user's keys: <paramref name="prodKeysPath"/> (a file or its folder) first, then where Switch emulators keep them. Null when none were found.</summary>
    public static KeySet? LoadKeys(string prodKeysPath, out string? foundAt)
    {
        foundAt = null;
        try { return KeyLocator.LoadDefault(out foundAt, string.IsNullOrWhiteSpace(prodKeysPath) ? [] : [prodKeysPath]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"prod.keys in C:\…\Ryujinx\system (with title.keys)", for Settings.</summary>
    public static string Describe(string prodKeysPath)
    {
        var keys = LoadKeys(prodKeysPath, out var path);
        if (keys is null || path is null)
            return !string.IsNullOrWhiteSpace(prodKeysPath) && !File.Exists(prodKeysPath) && !Directory.Exists(prodKeysPath)
                ? $"{prodKeysPath} doesn't exist, and no prod.keys was found in Ryujinx's or the yuzu family's folders."
                : "No prod.keys found in Ryujinx's or the yuzu family's folders. Switch conversions need it.";
        var folder = Path.GetDirectoryName(path)!;
        var title = File.Exists(Path.Combine(folder, KeyLocator.TitleKeysFileName)) ? " (with title.keys)" : "";
        return keys.HasHeaderKey ? $"Using prod.keys in {folder}{title}." : $"{path} has no header_key, so it can't open Switch files.";
    }

    /// <summary>
    /// What a target makes from <paramref name="source"/>: "uncompressed" is an .nsp, or an .xci for a cartridge image.
    /// </summary>
    public static OutputFormat FormatFor(TargetKind kind, string source) => kind switch
    {
        TargetKind.Nsz => OutputFormat.Nsz,
        TargetKind.Xcz => OutputFormat.Xcz,
        TargetKind.Xci => OutputFormat.Xci,
        _ => IsGameCard(source) ? OutputFormat.Xci : OutputFormat.Nsp,
    };

    public static bool IsGameCard(string path) => Path.GetExtension(path).ToLowerInvariant() is ".xci" or ".xcz";

    /// <summary>
    /// <paramref name="source"/> as <paramref name="kind"/> in the Save to folder (or its own): one file named after it,
    /// or one per game when it holds several. The original is removed (Replace the originals) only when every title
    /// in it was written, checked by lib-nx-kit as it went.
    /// </summary>
    public static JobOutcome Convert(JobContext c, string source, TargetKind kind, string? outputFolder = null)
    {
        var s = c.Settings;
        var format = FormatFor(kind, source);
        var keys = KeysForTests?.Invoke() ?? LoadKeys(s.Keys.SwitchKeysPath, out _);
        if (keys is null) return JobOutcome.Failed(NoKeysHelp);
        var folder = outputFolder ?? OutputPaths.FolderFor(s, source);
        var before = SizeOf(source);
        var written = new List<string>();
        var warnings = new List<string>();
        try
        {
            c.Report.SetStage("Reading");
            using var package = SwitchPackage.Open(source, keys);
            var plans = MergePlanner.Plan([package], out var skipped);
            warnings.AddRange(skipped);
            if (plans.Count == 0) return JobOutcome.Failed(skipped.Count > 0 ? skipped[0] : "no complete title inside");

            var writer = new PackageWriter(keys);
            var options = new WriteOptions
            {
                Format = format,
                Verify = true,
                Ncz = new NczOptions { MaxDegreeOfParallelism = s.Performance.ThreadsPerJob > 0 ? s.Performance.ThreadsPerJob : Environment.ProcessorCount },
            };
            var name = Path.GetFileNameWithoutExtension(source);
            for (var i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                var stem = plans.Count == 1 ? name : Path.GetFileNameWithoutExtension(SwitchConverter.DefaultFileName(plan, format));
                var output = OutputPaths.Available(Path.Combine(folder, stem + format.Extension()), s.Output.Overwrite);
                if (PathNames.Same(output, source)) return JobOutcome.Skipped($"already {format.Extension()}");
                var share = 1.0 / plans.Count;
                var start = i * share;
                c.Report.SetStage(format.IsCompressed() ? "Compressing" : "Writing");
                var progress = new InlineProgress<WriteProgress>(p => c.Report.SetProgress(start + p.Fraction * share));
                var result = writer.WriteAsync(plan, output, options, progress, c.Token).GetAwaiter().GetResult();
                written.Add(result.OutputPath);
                warnings.AddRange(plan.Warnings);
                warnings.AddRange(result.Warnings);
            }

            // Only what was written may replace the original: a title left out (or an older update dropped) keeps it.
            var everything = plans.Sum(p => p.Titles.Count()) == package.Titles.Count && skipped.Count == 0;
            foreach (var w in warnings) c.Log.Warn($"  {Path.GetFileName(source)}: {w}");
            if (s.Output.ReplacesOriginals)
            {
                if (everything)
                {
                    c.Report.SetStage("Removing the original");
                    package.Dispose();
                    FileRemover.Remove([source], s.Output.UseRecycleBin, c.Log);
                }
                else c.Log.Warn($"  {Path.GetFileName(source)} was kept: not every title in it went into the new file.");
            }
        }
        catch (MissingKeyException ex)
        {
            return JobOutcome.Failed($"Switch key missing ({ex.KeyName}). {NoKeysHelp}");
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return JobOutcome.Failed(ex.Message);
        }

        var after = written.Sum(SizeOf);
        var saved = before > 0 && after > 0 && after < before ? $" ({ChdJobs.Size(before - after)} smaller)" : "";
        c.Log.Success($"{Path.GetFileName(source)} → {string.Join(", ", written.Select(Path.GetFileName))}{saved}");
        return new JobOutcome(warnings.Count > 0 ? JobStatus.Warning : JobStatus.Succeeded,
            string.Join(", ", written.Select(Path.GetFileName)), Details: warnings.Count > 0 ? string.Join("\n", warnings) : null);
    }

    private static long SizeOf(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }
}
