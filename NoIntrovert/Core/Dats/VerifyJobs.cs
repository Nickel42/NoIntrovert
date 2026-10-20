using LibChdman;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Dats;

/// <summary>
/// The Verify &amp; match page's job (M6): checks an input's own integrity
/// (FR-9), hashes what's in it (FR-5, through the cache), looks the hashes up
/// in its system's DATs (FR-7) and remembers the result for the Library (FR-11).
/// </summary>
public static class VerifyJobs
{
    public static JobOutcome Check(JobContext c, string path) => Check(c, path, DatStore.Default, HashCache.Default, MatchStore.Default);

    public static JobOutcome Check(JobContext c, string path, DatStore dats, HashCache cache, MatchStore matches)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var set = FormatRegistry.Default.Identify(path) ?? new FileSet(path, [path], ConvertTargets.OtherFile);
        var system = TargetFormats.ById(SystemDetector.SystemOf(set));

        // FR-9: a damaged file isn't worth hashing.
        if (Integrity(c, set) is { } damage)
        {
            var bad = MatchResult.Of(MatchStatus.Damaged, damage);
            matches.Set(path, bad);
            c.Log.Error($"{name}: {damage}");
            return new JobOutcome(JobStatus.Failed, damage);
        }

        // R11: a converted file is checked against the hashes of what it was made from.
        var original = OriginalHashes.Default.Get(path);
        if (!CanHash(set) && original is null)
        {
            var skip = MatchResult.Of(MatchStatus.CantCheck, "Can't be checked yet");
            matches.Set(path, skip);
            return new JobOutcome(JobStatus.Skipped, skip.Text);
        }

        IReadOnlyList<HashedFile> files;
        if (original is not null)
            files = original.Files;
        else
        {
            c.Report.SetStage("Hashing");
            try { files = cache.GetOrAdd(path, () => Hash(c, set)); }
            finally { cache.Flush(); }
        }

        var result = Matcher.Match(path, files, dats.For(system, c.Settings.Dats.OwnFolder));
        matches.Set(path, result);
        var details = string.Join("\n", files.Select(f => $"{f.Name}{(f.Headerless ? " (without header)" : "")}: {f.Size:N0} bytes, CRC32 {f.Crc}{(f.Sha1 is null ? "" : ", SHA-1 " + f.Sha1)}"));
        if (original is not null) details = $"Checked with the hashes of {original.Source}, which it was made from\n{details}";
        if (result.Dat is not null) details = $"{result.Dat}: {result.Game}\n{details}";
        switch (result.Status)
        {
            case MatchStatus.Verified:
                c.Log.Success($"{name}: {result.Text}");
                return new JobOutcome(JobStatus.Succeeded, result.Text, Details: details);
            case MatchStatus.NoDatabase:
                c.Log.Warn($"{name}: {result.Text} ({system?.Name ?? TargetFormats.UnknownName})");
                return new JobOutcome(JobStatus.Skipped, result.Text, Details: details);
            default:
                c.Log.Warn($"{name}: {result.Text}");
                return new JobOutcome(JobStatus.Warning, result.Text, Details: details);
        }
    }

    /// <summary>
    /// The hashes of <paramref name="path"/> as a conversion's source (R11),
    /// through the hash cache; null for an input that isn't hashed.
    /// </summary>
    public static IReadOnlyList<HashedFile>? HashesOf(JobContext c, string path)
    {
        var set = FormatRegistry.Default.Identify(path);
        if (set is null || !CanHash(set) || set.Format == KnownFormats.Chd) return null;
        try { return HashCache.Default.GetOrAdd(path, () => Hash(c, set)); }
        finally { HashCache.Default.Flush(); }
    }

    /// <summary>What's wrong with the file itself, or null: CHDs are verified, 7z and zip tested.</summary>
    private static string? Integrity(JobContext c, FileSet set)
    {
        if (set.Format == KnownFormats.Chd)
        {
            c.TrackChdman(c.Settings.Chd.FixHeaderSha1 ? "Verifying and fixing" : "Verifying");
            ChdVerifyResult result;
            try { (result, _) = ChdService.Verify(c.Chdman, set.Primary, c.Settings.Chd.FixHeaderSha1, c.Token); }
            catch (ChdmanException ex) { return $"Damaged: {ex.Message}"; } // a hunk that won't even decompress
            return result.Outcome is ChdVerifyOutcome.Ok or ChdVerifyOutcome.Fixed or ChdVerifyOutcome.NotApplicable
                ? null
                : result.Outcome == ChdVerifyOutcome.RawMismatch ? "Damaged: the CHD's data doesn't match its checksum" : "Damaged: checksum mismatch";
        }
        if (ArchiveJobs.IsConvertible(set.Primary))
        {
            c.Report.SetStage("Testing");
            try
            {
                using var engine = new ArchiveEngine();
                engine.Test(set.Primary, c.Report.SetProgress, c.Token);
            }
            catch (ArchiveException ex) { return $"Damaged: {ex.Message}"; }
        }
        return null;
    }

    /// <summary>The inputs FR-5 hashes; Wii U, CSO/ZSO and the like aren't.</summary>
    private static bool CanHash(FileSet set)
    {
        var f = set.Format;
        if (KnownFormats.WiiUInputs.Contains(f) || set.Holds(KnownFormats.WiiUBuildable)) return false;
        if (f == ConvertTargets.OtherFile || f == ConvertTargets.PackedFolder || Directory.Exists(set.Primary)) return false;
        if (KnownFormats.Archives.Contains(f)) return ArchiveJobs.IsConvertible(set.Primary);
        var ext = Path.GetExtension(set.Primary).ToLowerInvariant();
        if (ext is ".cso" or ".zso" or ".jso" or ".dax" or ".isz" or ".pbp" or ".ecm" or ".wbfs" or ".ciso" or ".tgc" or ".nrg" or ".mds" or ".toc") return false;
        return true;
    }

    private static List<HashedFile> Hash(JobContext c, FileSet set)
    {
        var path = set.Primary;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // zip/7z: the CRC32s the archive already holds, without unpacking.
        if (KnownFormats.Archives.Contains(set.Format))
            return (ArchivePeek.Members(path) ?? [])
                .Where(m => m.Crc is not null)
                .Select(m => new HashedFile(Path.GetFileName(m.Name), m.Size, m.Crc!, null))
                .ToList();

        // GameCube/Wii: the decoded disc, from any container libdolphinrvz reads.
        if (ext is ".rvz" or ".wia" or ".gcz" or ".gcm" || (ext == ".iso" && NintendoDisc.IsPlainDisc(path)))
        {
            c.TrackRvz("Hashing the disc");
            var h = c.Rvz.Hash(path, c.Token);
            return [new HashedFile(Path.GetFileName(path), (long)h.Size, h.Crc32.ToUpperInvariant(), h.Sha1.ToUpperInvariant())];
        }

        // CHD: its tracks, as they'd be extracted (one bin per track, or an iso).
        if (set.Format == KnownFormats.Chd)
        {
            Directory.CreateDirectory(c.Settings.Performance.EffectiveTempFolder);
            using var work = new WorkDirectory(c.Settings.Performance.EffectiveTempFolder);
            c.TrackChdman("Extracting to hash");
            var (written, _) = ChdService.Extract(c.Chdman, path, work.Path, ExtractFormat.Auto, splitBin: true, overwrite: true, c.Log, c.Token);
            return written.Where(f => !IsDescriptor(f)).SelectMany(f => Hasher.HashFile(f, c.Token)).ToList();
        }

        // cue/bin, gdi, ccd/img: each track or image file; anything else: the file itself.
        return set.Members.Where(File.Exists).Where(f => !IsDescriptor(f)).SelectMany(f => Hasher.HashFile(f, c.Token)).ToList();
    }

    private static bool IsDescriptor(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".cue" or ".gdi" or ".ccd" or ".toc" or ".mds" or ".sub";
}
