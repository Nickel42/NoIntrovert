using System.ComponentModel;
using NoIntrovert.Core;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The Archive tab: compress files and folders into 7z or zip, or convert
/// zip/7z archives into the other format, with 7-Zip's engine.
/// </summary>
public partial class ArchiveViewModel : BatchTabViewModel
{
    public ArchiveViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs, JobQueue queue) : base(settings, log, dialogs, queue)
    {
        settings.Archives.PropertyChanged += OnSettingChanged;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArchiveSettingsViewModel.SelectedAction))
        {
            OnPropertyChanged(nameof(StartLabel));
            OnPropertyChanged(nameof(EmptyHint));
        }
    }

    private bool Converting => Settings.Archives.IsConvert;
    private bool Extracting => Settings.Archives.IsExtract;

    /// <summary>Converting and extracting both work on existing archives rather than on any file.</summary>
    private bool TakesArchives => Converting || Extracting;

    public override string Title => "Archive";
    public override string StartLabel => Extracting ? "Extract archives" : Converting ? "Convert archives" : "Create archives";
    public override string EmptyHint => Extracting
        ? "Drop .zip or .7z archives (or folders holding them) here to unpack them."
        : Converting
        ? "Drop .zip or .7z archives (or folders holding them) here to convert them to the other format."
        : "Drop files and folders here to pack them into .7z or .zip archives.\nEach folder you add stays one row; the grouping option decides how many archives it makes.";
    protected override string FilterName => TakesArchives ? "Archives" : "All files";
    /// <summary>
    /// Converting and extracting take zip and 7z files (found inside added
    /// folders); compressing takes anything, and a folder stays one row.
    /// </summary>
    protected override IReadOnlyList<FormatInfo>? InputFormats => TakesArchives ? KnownFormats.ConvertibleArchives : null;

    protected override bool BeforeStart(IReadOnlyList<JobItemViewModel> pending)
    {
        if (!ArchiveEngine.IsAvailable(out var problem))
        {
            Log.Error($"Archive: {problem}");
            return false;
        }
        if (TakesArchives && pending.Any(i => !ArchiveJobs.IsConvertible(i.Path)))
            Log.Warn($"Archive: rows that aren't .zip or .7z files are skipped when {(Extracting ? "extracting" : "converting")}.");
        if (Settings.Archives.IsCompress && Settings.Archives.IsSingle && string.IsNullOrWhiteSpace(Settings.Archives.SingleName))
        {
            Log.Error("Archive: give the combined archive a name first.");
            return false;
        }
        if (Settings.Output.RemoveArchiveOriginals)
        {
            var fate = Settings.Output.UseRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently";
            Log.Warn(Extracting
                ? $"Archive: each archive will be {fate} after it extracts cleanly."
                : $"Archive: originals will be {fate} after each archive tests OK.");
        }
        return true;
    }

    /// <summary>"Everything into one archive" makes the whole list a single job.</summary>
    protected override IReadOnlyList<IReadOnlyList<JobItemViewModel>> GroupItems(IReadOnlyList<JobItemViewModel> pending) =>
        Settings.Archives.IsCompress && Settings.Archives.IsSingle ? [pending] : base.GroupItems(pending);

    protected override JobOutcome WorkGroup(JobContext context, IReadOnlyList<JobItemViewModel> group)
    {
        var s = context.Settings;
        if (s.Archives.Action == ArchiveAction.Extract)
        {
            var item = group[0];
            return ArchiveJobs.IsConvertible(item.Path)
                ? ArchiveJobs.Extract(context, item.Path)
                : JobOutcome.Skipped("Not a zip or 7z archive");
        }

        if (s.Archives.Action == ArchiveAction.Convert)
        {
            var item = group[0];
            return ArchiveJobs.IsConvertible(item.Path)
                ? ArchiveJobs.Convert(context, item.Path)
                : JobOutcome.Skipped("Not a zip or 7z archive");
        }

        if (s.Archives.Grouping == ArchiveGrouping.Single)
        {
            var paths = group.Select(i => i.Path).ToList();
            var folder = OutputPaths.FolderFor(s, paths[0]);
            return ArchiveJobs.CompressOne(context, paths, s.Archives.SingleName.Trim(), folder);
        }

        var row = group[0];
        if (s.Archives.Grouping == ArchiveGrouping.PerEntryInFolders && row.IsFolder)
            return ArchiveJobs.CompressEntries(context, row.Path, s.Output.Folder);

        return ArchiveJobs.CompressOne(context, [row.Path], NameFor(row), OutputPaths.FolderFor(s, row.Path));
    }

    protected override JobOutcome Work(JobContext context, JobItemViewModel item) => WorkGroup(context, [item]);

    /// <summary>Archive name for a row, keeping the extension when two rows would otherwise clash.</summary>
    private string NameFor(JobItemViewModel row)
    {
        var siblings = Items.Where(i => string.Equals(i.Folder, row.Folder, StringComparison.OrdinalIgnoreCase)).Select(i => i.Path).ToList();
        var names = ArchiveJobs.UniqueNames(siblings);
        var index = siblings.FindIndex(p => p.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? names[index] : ArchiveJobs.BaseName(row.Path);
    }
}
