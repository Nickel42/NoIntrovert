using Lib3dsKit;
using Lib3dsKit.IO;
using Lib3dsKit.Keys;
using Lib3dsKit.Operations;
using Lib3dsKit.Verification;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Ctr;

/// <summary>
/// What a Nintendo 3DS file is, read from its headers (D2): its type (for a Z3DS file, the type it holds),
/// whether it's compressed or encrypted, and for a CIA whether it's a game (only games become a .cci).
/// </summary>
public sealed record CtrFacts(CtrFileType Type, bool Compressed, bool Encrypted, bool IsGame)
{
    /// <summary>Whether lib-3ds-kit has to make a .cci of it first: encrypted, a CIA, or compressed.</summary>
    public bool NeedsConversion => Type is CtrFileType.Cci or CtrFileType.Cia && (Encrypted || Compressed || Type == CtrFileType.Cia);

    /// <summary>
    /// The facts of <paramref name="path"/>; null when lib-3ds-kit doesn't read it as a 3DS file. A plain
    /// cartridge image is checked by its NCCH flags alone, so a scan of a large library stays quick.
    /// </summary>
    public static CtrFacts? Of(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (!CtrFile.IsCompressed(path) && RomFixes.Is3dsEncrypted(path) is { } encrypted)
                return new CtrFacts(CtrFileType.Cci, false, encrypted, true);
            var info = CtrInspector.Inspect(path);
            if (info.Type == CtrFileType.Unknown) return null;
            var high = (uint)(info.TitleId >> 32);
            var game = info.Type != CtrFileType.Cia || high is 0x00040000 or 0x00040002;
            return new CtrFacts(info.Type, info.Compressed, info.Encrypted, game);
        }
        catch (Exception ex) when (ex is CtrFormatException or MissingKeyException or IOException or UnauthorizedAccessException
                                       or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Nintendo 3DS conversions through lib-3ds-kit (D2): an encrypted image, a .cia or a .zcci/.zcia becomes
/// a decrypted .cci, packed into its 7z (through <see cref="RomJobs.Pack"/>), bare, or compressed as
/// Azahar's .zcci. The keys are the user's own (Settings › Keys), never bundled.
/// </summary>
public static class CtrJobs
{
    public const string NoKeysHelp =
        "Put your boot9.bin and aes_keys.txt (dumped from your own 3DS) in Azahar's sysdata folder, or choose their folder in Settings › Keys.";

    /// <summary>The self-test's stand-in keys (lib-3ds-kit's fake ones, which the usual search rightly refuses).</summary>
    internal static Func<CtrKeyStore>? KeysForTests;

    /// <summary>The user's 3DS keys: <paramref name="folder"/> first, then Azahar's and Citra's sysdata, ~/.3ds and keys/ beside the app.</summary>
    public static CtrKeyStore LoadKeys(string folder, out IReadOnlyList<string> problems) =>
        KeyLocator.LoadDefault(out problems, string.IsNullOrWhiteSpace(folder) ? [] : [folder]);

    /// <summary>"boot9.bin, aes_keys.txt and seeddb.bin found (12 seeds): ready for games and eShop titles", for Settings.</summary>
    public static string Describe(CtrKeyStore keys)
    {
        var s = keys.GetStatus();
        if (s.LoadedFrom.Count == 0) return "No 3DS keys found yet. Encrypted games, .cia files and eShop titles need them.";
        var where = string.Join(", ", s.LoadedFrom.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase));
        return s.IsRetailReady
            ? $"Found {where}: ready for encrypted games and .cia files{(s.SeedCount > 0 ? $" ({s.SeedCount} eShop seed(s))" : "")}."
            : $"Found {where}, but some keys are missing (boot9.bin and aes_keys.txt are both needed for most games).";
    }

    /// <summary>
    /// A bare decrypted .cci (or, with <paramref name="compress"/>, a .zcci) of <paramref name="source"/> in
    /// <paramref name="outputFolder"/> (the Save to folder or the source's own when null). An archive is unpacked first.
    /// </summary>
    public static JobOutcome ToCci(JobContext c, string source, bool compress, string? outputFolder = null)
    {
        var s = c.Settings;
        var folder = outputFolder ?? OutputPaths.FolderFor(s, source);
        Directory.CreateDirectory(s.Performance.EffectiveTempFolder);
        using var work = new WorkDirectory(s.Performance.EffectiveTempFolder);
        var input = source;
        if (ArchiveExtractor.IsArchive(source))
        {
            c.Report.SetStage("Unpacking");
            ArchiveExtractor.Extract(source, work.Path, new InlineProgress<double>(f => c.Report.SetProgress(f * 0.3)), c.Token);
            input = Directory.EnumerateFiles(work.Path, "*", SearchOption.AllDirectories)
                        .FirstOrDefault(f => CtrFacts.Of(f) is { Type: CtrFileType.Cci or CtrFileType.Cia, IsGame: true })
                    ?? throw new InvalidDataException("no 3DS game inside");
        }
        var facts = CtrFacts.Of(input);
        if (facts is not { Type: CtrFileType.Cci or CtrFileType.Cia }) return JobOutcome.Failed("not a 3DS game image or .cia");
        if (!facts.IsGame) return JobOutcome.Skipped("an update or DLC .cia; only games become a .cci");

        var name = Path.GetFileNameWithoutExtension(source);
        var output = OutputPaths.Available(Path.Combine(folder, name + (compress ? ".zcci" : ".cci")), s.Output.Overwrite);
        // R11's hashes of a file not hashed yet: read beside the conversion, while the file is in the OS cache.
        var cacheIt = PathNames.Same(input, source);
        using var original = RecordsOriginal(s, facts, compress) && OriginalHashes.Default.Get(input) is null
                                 && !(cacheIt && KnownHashes.Cached(input) is not null)
            ? BackgroundHash.Start(input, token => OriginalOf(input, token, cacheIt), c.Token)
            : null;
        var result = Convert(c, input, facts, output, compress);
        if (result.Outcome is { } failed) return failed;

        RecordOriginal(c, source, input, output, facts, compress, original);
        if (s.Output.ReplacesOriginals)
        {
            c.Report.SetStage("Removing the original");
            FileRemover.Remove([source], s.Output.UseRecycleBin, c.Log);
        }
        var saved = SizeOf(source) - SizeOf(output);
        c.Log.Success($"{Path.GetFileName(source)} → {Path.GetFileName(output)}{(saved > 0 ? $" ({ChdJobs.Size(saved)} smaller)" : "")}");
        return new JobOutcome(result.Warnings.Count > 0 ? JobStatus.Warning : JobStatus.Succeeded,
            Path.GetFileName(output), Details: result.Warnings.Count > 0 ? string.Join("\n", result.Warnings) : null);
    }

    /// <summary>
    /// The hashes the original had (R11), for a file whose bytes the conversion changed: its own record, else its
    /// contents, through the hash cache (a file Verify &amp; match already hashed isn't read again, and a new one is
    /// remembered). <paramref name="cache"/> is false for a temp file unpacked from an archive.
    /// </summary>
    public static IReadOnlyList<HashedFile> OriginalOf(string source, CancellationToken token, bool cache = true) =>
        OriginalHashes.Default.Get(source)?.Files
        ?? (cache ? HashCache.Default.GetOrAdd(source, () => Hasher.HashFile(source, token)) : Hasher.HashFile(source, token));

    private sealed record Converted(JobOutcome? Outcome, IReadOnlyList<string> Warnings);

    private static Converted Convert(JobContext c, string input, CtrFacts facts, string output, bool compress)
    {
        var s = c.Settings;
        var keys = KeysForTests?.Invoke() ?? LoadKeys(s.Keys.ThreeDsKeysFolder, out _);
        var converter = new CtrConverter(keys);
        var options = new CtrConvertOptions
        {
            Decrypt = true,
            Trim = true, // only a .cia is rebuilt; an image's own trim is kept (Decrypt below)
            Overwrite = s.Output.Overwrite,
            ComputeOutputHashes = false,
            Compress = compress,
        };
        var progress = new InlineProgress<OperationProgress>(p =>
        {
            c.Report.SetStage(p.Stage);
            c.Report.SetProgress(p.Fraction);
        });
        CtrOperationResult result;
        try
        {
            c.Report.SetStage(facts.Type == CtrFileType.Cia ? "Converting the .cia" : facts.Encrypted ? "Decrypting" : compress ? "Compressing" : "Decompressing");
            // A cartridge image keeps its layout (trimmed or not), so a decrypted dump still matches its DAT entry.
            result = facts.Type == CtrFileType.Cia
                ? converter.Convert(input, output, CtrOutputFormat.Cci, options, progress, c.Token)
                : converter.Decrypt(input, output, options, progress, c.Token);
        }
        catch (MissingKeyException ex)
        {
            return new Converted(JobOutcome.Failed($"3DS key missing ({ex.KeyName}). {NoKeysHelp}"), []);
        }
        catch (CtrFormatException ex)
        {
            return new Converted(JobOutcome.Failed(ex.Message), []);
        }
        var warnings = result.Warnings.Where(w => !w.StartsWith("Input was already decrypted", StringComparison.Ordinal)).ToList();

        // Checked like other conversions: the hashes stored inside the new file (Settings › "Check each file after converting").
        if (s.Chd.VerifyAfterCreate || s.Output.ReplacesOriginals)
        {
            c.Report.SetStage("Checking");
            var report = new CtrVerifier(keys).Verify(output, new CtrVerifyOptions(), progress, c.Token);
            if (report.Verdict == CtrVerdict.Bad)
            {
                FileRemover.Remove([output], useBin: false, c.Log);
                return new Converted(JobOutcome.Failed("the new file failed its own hash checks; it was removed"), warnings);
            }
        }
        return new Converted(null, warnings);
    }

    /// <summary>R11: a decrypted or compressed file's bytes differ from its dump, so the dump's hashes are kept for Verify &amp; match.</summary>
    /// <param name="input">The game file itself: the source, or what was unpacked from it when it's an archive.</param>
    /// <param name="beside">The hashes already being worked out beside the conversion, when they were.</param>
    private static void RecordOriginal(JobContext c, string source, string input, string output, CtrFacts facts, bool compress,
        BackgroundHash<IReadOnlyList<HashedFile>>? beside)
    {
        if (!RecordsOriginal(c.Settings, facts, compress)) return;
        try { OriginalHashes.Default.Set(output, source, beside?.Result() ?? OriginalOf(input, c.Token, cache: PathNames.Same(input, source))); }
        catch (IOException ex) { c.Log.Warn($"  couldn't record {Path.GetFileName(source)}'s original hashes: {ex.Message}"); }
    }

    private static bool RecordsOriginal(AppSettings s, CtrFacts facts, bool compress) =>
        s.TargetFormats.RecordOriginalHashes && (facts.NeedsConversion || compress);

    private static long SizeOf(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }
}
