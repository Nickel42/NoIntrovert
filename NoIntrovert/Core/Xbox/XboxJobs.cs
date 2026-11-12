using LibXisoKit;
using LibXisoKit.IO;
using LibXisoKit.Shrinking;
using LibXisoKit.Xdvdfs;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Xbox;

/// <summary>
/// Original Xbox discs, all done by lib-xiso-kit: a Redump image becomes an XISO (the game partition only, which
/// xemu reads) or a Cerbios CCI (split below the FATX 4 GiB limit), and the result is re-read and checked. The
/// conservative mode is used: files keep their sectors, nothing is removed, and no executable is patched.
/// NoIntrovert only picks the output's name and folder, keeps the Redump hashes the library reports (R11),
/// and removes the original afterwards when asked (Replace the originals).
/// </summary>
public static class XboxJobs
{
    /// <summary>The options a target uses: trim only, no XBE patch, verified, no sidecar (the original hashes live in the library index).</summary>
    public static ShrinkOptions OptionsFor(TargetKind kind, AppSettings s, bool hashSource) => new()
    {
        Mode = ShrinkMode.TrimOnly,
        Format = kind == TargetKind.XboxCci ? OutputFormat.Cci : OutputFormat.Iso,
        SplitSize = kind == TargetKind.XboxCci ? XdvdfsConstants.FatxSplitSize : null,
        MediaPatch = false,
        Scrub = true,
        Verify = true,
        WriteSidecar = false,
        HashSource = hashSource,
        Overwrite = s.Output.Overwrite,
    };

    public static JobOutcome Convert(JobContext c, string source, TargetKind kind, string? outputFolder = null)
    {
        var s = c.Settings;
        var folder = outputFolder ?? OutputPaths.FolderFor(s, source);
        var ext = kind == TargetKind.XboxCci ? ".cci" : ".iso";
        var name = Path.GetFileNameWithoutExtension(source);
        var wanted = Path.Combine(folder, name + ext);
        // An XISO beside its Redump image has the same name: with Replace the originals it takes the original's
        // place once it's checked; otherwise it gets " (2)" like any taken name.
        var swap = PathNames.Same(wanted, source) && s.Output.ReplacesOriginals;
        var output = swap ? Path.Combine(folder, $"{name}.noi-xiso{ext}") : OutputPaths.Available(wanted, s.Output.Overwrite);
        if (PathNames.Same(output, source)) return JobOutcome.Skipped("already that format");

        // The Redump image's own hashes, for Verify & match (R11): an XISO's were kept when it was made.
        var redump = !DiscProbe.IsXiso(source);
        var keepHashes = s.TargetFormats.RecordOriginalHashes;
        var progress = new InlineProgress<OperationProgress>(p =>
        {
            c.Report.SetStage(p.Stage.ToString());
            if (p.Fraction is { } f) c.Report.SetProgress(f);
        });
        ShrinkResult result;
        try
        {
            result = XisoShrinker.ShrinkAsync(source, output, OptionsFor(kind, s, keepHashes && redump), null, progress, c.Token)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or ArgumentException)
        {
            return JobOutcome.Failed(ex.Message);
        }
        if (result.Verification is { Success: false } v)
            return JobOutcome.Failed("the new image failed its check: " + string.Join("; ", v.Problems));

        var first = result.OutputFiles[0];
        var recorded = OriginalHashes.Default.Get(source)?.Files; // read before the original may go
        if (swap)
        {
            c.Report.SetStage("Replacing the original");
            FileRemover.Remove([source], s.Output.UseRecycleBin, c.Log);
            File.Move(first, wanted);
            first = wanted;
        }
        if (keepHashes)
        {
            var original = recorded;
            if (original is null && result.SourceHashes is { } h)
                original = [new HashedFile(Path.GetFileName(source), h.Size, h.Crc32.ToUpperInvariant(), h.Sha1.ToUpperInvariant(), Md5: h.Md5.ToUpperInvariant())];
            if (original is not null) OriginalHashes.Default.Set(first, source, original);
        }
        if (s.Output.ReplacesOriginals && !swap && File.Exists(source))
        {
            c.Report.SetStage("Removing the original");
            FileRemover.Remove([source], s.Output.UseRecycleBin, c.Log);
        }

        var names = swap ? Path.GetFileName(first) : string.Join(", ", result.OutputFiles.Select(Path.GetFileName));
        var saved = result.SavedBytes > 0 ? $" ({ChdJobs.Size(result.SavedBytes)} smaller)" : "";
        c.Log.Success($"{Path.GetFileName(source)} → {names}{saved}");
        return new JobOutcome(JobStatus.Succeeded, names);
    }
}
