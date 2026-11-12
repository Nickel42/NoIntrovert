using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Library;

namespace NoIntrovert.Core.Jobs;

/// <summary>
/// The per-system jobs of target formats Phase 3: a ROM packed under its
/// canonical name (3DS .cci, R12 header fixes), an archive of several games
/// split apart (R6), a PS Vita game folder zipped for Vita3K (FR-10), and a
/// multi-disc game converted into its hidden folder with an .m3u (R7, FR-7).
/// </summary>
public static class RomJobs
{
    /// <summary>
    /// One ROM into its own archive, named <paramref name="archiveName"/>: as
    /// it is, or renamed (.3ds → .cci) or fixed (R12) inside the archive. A
    /// fixed ROM's original hashes are recorded (R11), since its bytes change.
    /// </summary>
    public static JobOutcome Pack(JobContext c, string rom, string archiveName, string outputFolder)
    {
        var s = c.Settings;
        // D2: a 3DS image that's encrypted, a .cia or a .zcci goes in as a decrypted .cci, made by lib-3ds-kit.
        if (Path.GetExtension(rom).ToLowerInvariant() is ".3ds" or ".cci" or ".cia" or ".zcci" or ".zcia"
            && Ctr.CtrFacts.Of(rom) is { NeedsConversion: true })
            return PackCtr(c, rom, archiveName, outputFolder);
        var fixedExt = RomFixes.FixedExtension(rom, s.TargetFormats.NormalizeHeaders);
        if (fixedExt is null) return ArchiveJobs.CompressOne(c, [rom], archiveName, outputFolder);

        var member = Path.GetFileNameWithoutExtension(rom) + fixedExt;
        var removeOriginal = s.Output.ReplacesOriginals;
        // The archive's name, " (2)" when taken, worked out here so the hashes below find it.
        var wanted = Path.Combine(outputFolder, archiveName + ArchiveJobs.Extension(s.Archives.Format));
        archiveName = Path.GetFileNameWithoutExtension(OutputPaths.Available(wanted, s.Output.Overwrite));
        var keep = s.Clone();
        // The original goes only once the archive of its fixed copy tests OK, below.
        if (removeOriginal) keep.Output.SaveTo = SaveTo.NextToOriginals;
        keep.Archives.TestAfter |= removeOriginal;
        var inner = c.WithSettings(keep);
        JobOutcome outcome;
        string? archive = null;

        var renamed = Path.Combine(Path.GetDirectoryName(rom)!, member);
        if (RomFixes.RenameOnly(rom) && !File.Exists(renamed) && !s.Output.LeaveSourcesAlone)
        {
            // Same bytes, new name: rename beside the original (no copy of a 4 GB 3DS image), then back.
            c.Log.Info($"  {Path.GetFileName(rom)} goes in as {member}");
            File.Move(rom, renamed);
            try { outcome = ArchiveJobs.CompressOne(inner, [renamed], archiveName, outputFolder); }
            finally { if (File.Exists(renamed) && !File.Exists(rom)) File.Move(renamed, rom); }
        }
        else
        {
            Directory.CreateDirectory(s.Performance.EffectiveTempFolder);
            using var work = new WorkDirectory(s.Performance.EffectiveTempFolder);
            var copy = Path.Combine(work.Path, member);
            c.Report.SetStage(RomFixes.RenameOnly(rom) ? "Copying" : "Fixing the header");
            if (RomFixes.RenameOnly(rom)) File.Copy(rom, copy);
            else
            {
                RomFixes.WriteFixed(rom, copy, c.Token);
                c.Log.Info($"  {Path.GetFileName(rom)} fixed to {member} (R12)");
            }
            outcome = ArchiveJobs.CompressOne(inner, [copy], archiveName, outputFolder);
            archive = Path.Combine(outputFolder, archiveName + ArchiveJobs.Extension(s.Archives.Format));
            if (outcome.Status == JobStatus.Succeeded && s.TargetFormats.RecordOriginalHashes && !RomFixes.RenameOnly(rom))
                OriginalHashes.Default.Set(archive, rom, Hasher.HashFile(rom, c.Token));
        }

        if (outcome.Status == JobStatus.Succeeded && removeOriginal)
        {
            c.Report.SetStage("Removing the original");
            FileRemover.Remove([rom], s.Output.UseRecycleBin, c.Log);
        }
        return outcome;
    }

    /// <summary>
    /// D2: a 3DS game that lib-3ds-kit has to convert first (encrypted, a .cia, a .zcci or .zcia) is
    /// decrypted into a scratch .cci, which goes into the archive. The archive's bytes differ from the
    /// dump's, so the dump's hashes are recorded (R11); the original goes only once the archive tests OK.
    /// </summary>
    private static JobOutcome PackCtr(JobContext c, string rom, string archiveName, string outputFolder)
    {
        var s = c.Settings;
        var removeOriginal = s.Output.ReplacesOriginals;
        var wanted = Path.Combine(outputFolder, archiveName + ArchiveJobs.Extension(s.Archives.Format));
        archiveName = Path.GetFileNameWithoutExtension(OutputPaths.Available(wanted, s.Output.Overwrite));
        var keep = s.Clone();
        if (removeOriginal) keep.Output.SaveTo = SaveTo.NextToOriginals;
        keep.Archives.TestAfter |= removeOriginal;

        Directory.CreateDirectory(s.Performance.EffectiveTempFolder);
        using var work = new WorkDirectory(s.Performance.EffectiveTempFolder);
        var cci = Ctr.CtrJobs.DecryptedCopy(c, rom, work.Path, out var failure);
        if (cci is null) return failure!;
        c.Log.Info($"  {Path.GetFileName(rom)} goes in as {Path.GetFileName(cci)}, decrypted");
        var outcome = ArchiveJobs.CompressOne(c.WithSettings(keep), [cci], archiveName, outputFolder);
        if (outcome.Status != JobStatus.Succeeded) return outcome;

        var archive = Path.Combine(outputFolder, archiveName + ArchiveJobs.Extension(s.Archives.Format));
        if (s.TargetFormats.RecordOriginalHashes) OriginalHashes.Default.Set(archive, rom, Ctr.CtrJobs.OriginalOf(rom, c.Token));
        if (removeOriginal)
        {
            c.Report.SetStage("Removing the original");
            FileRemover.Remove([rom], s.Output.UseRecycleBin, c.Log);
        }
        return outcome;
    }

    /// <summary>
    /// R6: an archive holding several games becomes one archive per game,
    /// each named after its ROM. Other files in it (a readme) aren't kept.
    /// </summary>
    public static JobOutcome Split(JobContext c, string archive, string outputFolder)
    {
        var s = c.Settings;
        Directory.CreateDirectory(s.Performance.EffectiveTempFolder);
        using var work = new WorkDirectory(s.Performance.EffectiveTempFolder);
        var contents = Path.Combine(work.Path, "contents");
        c.Report.SetStage("Extracting");
        using (var engine = new SevenZipEngine()) engine.Extract(archive, contents, f => c.Report.SetProgress(f * 0.3), c.Token);

        var roms = Directory.EnumerateFiles(contents, "*", SearchOption.AllDirectories)
            .Where(f => KnownFormats.Cartridges.Any(k => k.HasExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (roms.Count == 0) return JobOutcome.Skipped("No game files inside");
        var keep = s.Clone();
        if (keep.Output.ReplacesOriginals)
        {
            keep.Output.SaveTo = SaveTo.NextToOriginals; // the ROMs are temporary copies; the archive goes below,
            keep.Archives.TestAfter = true;              // once every new archive tests OK
        }
        int done = 0, failed = 0;
        for (var i = 0; i < roms.Count; i++)
        {
            var sub = new ArchiveJobs.SubReport(c.Report, 0.3 + 0.7 * i / roms.Count, 0.7 / roms.Count, $"{i + 1}/{roms.Count}: ");
            var o = Pack(new JobContext(c.Chdman, keep, c.Log, sub, c.Token, c.RvzSource), roms[i], Path.GetFileNameWithoutExtension(roms[i]), outputFolder);
            if (o.Status is JobStatus.Succeeded or JobStatus.Warning) done++; else failed++;
        }
        c.Log.Info($"{Path.GetFileName(archive)}: split into {done} archive(s)");
        if (failed == 0 && s.Output.ReplacesOriginals) FileRemover.Remove([archive], s.Output.UseRecycleBin, c.Log);
        return new JobOutcome(failed > 0 ? JobStatus.Failed : JobStatus.Succeeded, $"{done} game(s)" + (failed > 0 ? $", {failed} failed" : ""));
    }

    /// <summary>
    /// FR-10: a NoNpDrm game folder → "Title (PCSE00000).zip", which Vita3K
    /// installs with File → Install .zip; with the launcher option on, a small
    /// file of the same name holding the title ID goes beside it.
    /// </summary>
    public static JobOutcome PackVita(JobContext c, string folder, string outputFolder)
    {
        var s = c.Settings;
        var (title, id) = VitaGame.Read(folder);
        var name = VitaGame.ZipName(title, id);
        var zip = s.Clone();
        zip.Archives.Format = ArchiveFormat.Zip;
        var outcome = ArchiveJobs.CompressOne(c.WithSettings(zip), [folder], name, outputFolder);
        if (outcome.Status == JobStatus.Succeeded && s.TargetFormats.WriteVitaLauncher)
        {
            var ext = "." + s.TargetFormats.VitaLauncherExtension.Trim().TrimStart('.');
            File.WriteAllText(Path.Combine(outputFolder, name + ext), id);
            c.Log.Info($"  wrote the launcher file {name}{ext}");
        }
        return outcome;
    }

    /// <summary>
    /// FR-7: each disc of a multi-disc game is converted into
    /// <c>&lt;folder&gt;/&lt;MultiDiscFolder&gt;/</c>, then <c>&lt;Game&gt;.m3u</c> in the
    /// folder lists them by relative path, in disc order. An existing .m3u of
    /// that name is rewritten, not duplicated.
    /// </summary>
    public static JobOutcome MultiDisc(JobContext c, IReadOnlyList<string> discs, string game, string extension)
    {
        var s = c.Settings;
        var folder = OutputPaths.FolderFor(s, discs[0]);
        var hiddenName = string.IsNullOrWhiteSpace(s.TargetFormats.MultiDiscFolder) ? "hidden" : s.TargetFormats.MultiDiscFolder.Trim();
        var hidden = Path.Combine(folder, hiddenName);
        Directory.CreateDirectory(hidden);
        var lines = new List<string>();
        var failed = 0;
        for (var i = 0; i < discs.Count; i++)
        {
            var sub = new ArchiveJobs.SubReport(c.Report, (double)i / discs.Count, 1.0 / discs.Count, $"Disc {i + 1}/{discs.Count}: ");
            var made = Path.GetFileNameWithoutExtension(discs[i]) + extension;
            // A disc already in the hidden folder (an earlier run) is listed as it is, not converted again as " (2)".
            if (File.Exists(Path.Combine(hidden, made)) && !s.Output.Overwrite)
            {
                c.Log.Info($"  {hiddenName}/{made} is already there; kept");
                lines.Add($"{hiddenName}/{made}");
                continue;
            }
            var o = ChdJobs.Convert(new JobContext(c.Chdman, s, c.Log, sub, c.Token, c.RvzSource), discs[i], hidden);
            if (o.Status is not (JobStatus.Succeeded or JobStatus.Warning or JobStatus.Skipped)) failed++;
            if (File.Exists(Path.Combine(hidden, made))) lines.Add($"{hiddenName}/{made}");
        }
        if (lines.Count > 0)
        {
            File.WriteAllText(Path.Combine(folder, game + ".m3u"), string.Join("\n", lines) + "\n");
            c.Log.Success($"{game}.m3u lists {lines.Count} disc(s) in {hiddenName}/");
        }
        return new JobOutcome(failed > 0 ? JobStatus.Failed : JobStatus.Succeeded,
            $"{lines.Count} disc(s) → {hiddenName}/, {game}.m3u" + (failed > 0 ? $"; {failed} failed" : ""));
    }

    /// <summary>The discs of one game among <paramref name="paths"/>: same folder and title, numbered by their (Disc N) tags.</summary>
    public static List<(string Game, List<(int Disc, int Row)> Discs)> MultiDiscGroups(IReadOnlyList<string> paths, IEnumerable<int> rows) =>
        rows.Select(i => (Row: i, Folder: Path.GetDirectoryName(paths[i]) ?? "", Title: LibraryScanner.TitleOf(paths[i])))
            .Where(r => r.Title.Disc.Length > 0)
            .GroupBy(r => (r.Folder.ToLowerInvariant(), r.Title.Title.ToLowerInvariant()))
            .Select(g => (g.First().Title.Title, g.Select(r => (Disc: int.Parse(r.Title.Disc[5..]), r.Row)).OrderBy(d => d.Disc).ToList()))
            .Where(g => g.Item2.Select(d => d.Disc).Distinct().Count() >= 2)
            .ToList();
}
