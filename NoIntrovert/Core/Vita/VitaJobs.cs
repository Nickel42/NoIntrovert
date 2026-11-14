using LibPsVitaKit;
using LibPsVitaKit.Keys;
using LibPsVitaKit.Operations;
using LibPsVitaKit.Pkg;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Vita;

/// <summary>
/// PS Vita .pkg files to the zip Vita3K installs (File › Install .zip). lib-psvita-kit decrypts and unpacks the
/// package as a NoNpDrm folder, with the license from its zRIF; NoIntrovert zips that folder, names it, writes
/// the launcher file for a game (FR-10) and removes the .pkg afterwards when asked (Replace the originals).
/// Games and DLC need a zRIF (<see cref="ZrifLibrary"/>); updates don't.
/// </summary>
public static class VitaJobs
{
    public const string NoZrifHelp =
        "Put the zRIF in a .zrif file next to the .pkg, or choose a list of zRIFs (such as NoPayStation's TSV) in Settings › Keys.";

    /// <summary>The self-test's stand-in package keys (its synthetic packages use made-up ones).</summary>
    internal static Func<PkgKeys>? KeysForTests;

    /// <summary>A PS Vita game, DLC, update or PSM package (not a PS3 or PSP one), from its header.</summary>
    public static bool IsVitaPackage(string path) => KindOf(path) is not PkgContentKind.Unknown and not PkgContentKind.Psp and not PkgContentKind.Psx;

    /// <summary>What a .pkg holds, from its header and metadata (no keys needed); Unknown when it can't be read.</summary>
    public static PkgContentKind KindOf(string path)
    {
        try { return File.Exists(path) ? PkgFile.Open(path).Kind : PkgContentKind.Unknown; }
        catch (Exception ex) when (ex is VitaFormatException or IOException or UnauthorizedAccessException) { return PkgContentKind.Unknown; }
    }

    /// <summary>
    /// <paramref name="pkgPath"/> as a zip in <paramref name="outputFolder"/>: "Title (PCSE00000).zip" for a game (and
    /// its launcher file), "… [DLC].zip" or "… [Update v01.05].zip". The package is unpacked into the temp folder first.
    /// </summary>
    public static JobOutcome FromPackage(JobContext c, string pkgPath, string outputFolder)
    {
        var s = c.Settings;
        PkgFile pkg;
        try { pkg = PkgFile.Open(pkgPath); }
        catch (Exception ex) when (ex is VitaFormatException or IOException or UnauthorizedAccessException) { return JobOutcome.Failed(ex.Message); }
        if (pkg.Kind is not (PkgContentKind.VitaApp or PkgContentKind.VitaDlc or PkgContentKind.VitaPatch))
            return JobOutcome.Skipped(pkg.Kind == PkgContentKind.Psm ? "a PlayStation Mobile package" : "not a PS Vita game, DLC or update");
        var titleId = pkg.TitleId ?? pkg.Sfo?.TitleId;
        if (titleId is null) return JobOutcome.Failed($"The package's content ID '{pkg.ContentId}' isn't valid.");

        string? zrif = null;
        if (pkg.Kind != PkgContentKind.VitaPatch)
        {
            zrif = ZrifLibrary.Find(pkgPath, pkg.ContentId, s.Keys.VitaZrifPath, out var problem);
            if (problem is not null) c.Log.Warn($"  {problem}");
            if (zrif is null) return JobOutcome.Failed($"No zRIF for {pkg.ContentId}. {NoZrifHelp}");
        }

        var name = ZipName(pkg, titleId);
        Directory.CreateDirectory(s.Performance.EffectiveTempFolder);
        using var work = new WorkDirectory(s.Performance.EffectiveTempFolder);
        VitaOperationResult unpacked;
        try
        {
            c.Report.SetStage("Unpacking");
            var keys = KeysForTests?.Invoke() ?? PkgKeys.BuiltIn();
            var progress = new InlineProgress<OperationProgress>(p => c.Report.SetProgress(p.Fraction * 0.5));
            unpacked = new VitaConverter(keys).ExtractPkgAsync(pkgPath, zrif, work.Path, null, progress, c.Token).GetAwaiter().GetResult();
        }
        catch (MissingLicenseException ex) { return JobOutcome.Failed($"{ex.Message} {NoZrifHelp}"); }
        catch (Exception ex) when (ex is VitaFormatException or MissingKeyException or IOException or UnauthorizedAccessException)
        {
            return JobOutcome.Failed(ex.Message);
        }
        foreach (var w in unpacked.Warnings) c.Log.Warn($"  {Path.GetFileName(pkgPath)}: {w}");

        // Zip the folder (Vita3K finds sce_sys/param.sfo inside and installs by its category). The .pkg, not the
        // temporary folder, is the original: the zip is tested whenever the .pkg is to be removed.
        var zip = s.Clone();
        zip.Archives.Format = ArchiveFormat.Zip;
        zip.Archives.TestAfter = s.Archives.TestAfter || s.Output.ReplacesOriginals;
        if (zip.Output.ReplacesOriginals) zip.Output.SaveTo = SaveTo.NextToOriginals;
        var sub = new ArchiveJobs.SubReport(c.Report, 0.5, 0.5, "");
        var outcome = ArchiveJobs.CompressOne(new JobContext(c.Chdman, zip, c.Log, sub, c.Token, c.RvzSource), [unpacked.OutputPath], name, outputFolder);
        if (outcome.Status is not (JobStatus.Succeeded or JobStatus.Warning)) return outcome;

        if (pkg.Kind == PkgContentKind.VitaApp) WriteLauncher(c, outputFolder, name, titleId);
        if (s.Output.ReplacesOriginals)
        {
            c.Report.SetStage("Removing the original");
            FileRemover.Remove([pkgPath], s.Output.UseRecycleBin, c.Log);
        }
        return outcome;
    }

    /// <summary>"Title (PCSE00000)", "Title (PCSE00000) [DLC]" or "Title (PCSE00000) [Update v01.05]".</summary>
    public static string ZipName(PkgFile pkg, string titleId)
    {
        var name = VitaGame.ZipName(pkg.Title ?? titleId, titleId);
        return pkg.Kind switch
        {
            PkgContentKind.VitaDlc => $"{name} [DLC]",
            PkgContentKind.VitaPatch => $"{name} [Update v{pkg.Sfo?.AppVersion ?? "?"}]",
            _ => name,
        };
    }

    /// <summary>FR-10: with the launcher option on, a small file named like the zip, holding the title ID.</summary>
    public static void WriteLauncher(JobContext c, string outputFolder, string name, string titleId)
    {
        var s = c.Settings;
        if (!s.TargetFormats.WriteVitaLauncher) return;
        var ext = "." + s.TargetFormats.VitaLauncherExtension.Trim().TrimStart('.');
        File.WriteAllText(Path.Combine(outputFolder, name + ext), titleId);
        c.Log.Info($"  wrote the launcher file {name}{ext}");
    }
}
