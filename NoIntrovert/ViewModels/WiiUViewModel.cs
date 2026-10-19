using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.WiiU;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The Wii U tab: turns extracted title folders and existing .wua files into
/// one .wua per game (base game + newest update + DLC), the format Cemu reads.
/// </summary>
public partial class WiiUViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs)
    : BatchTabViewModel(settings, log, dialogs)
{
    public override string Title => "Wii U";
    public override string StartLabel => "Build .wua files";
    public override string EmptyHint =>
        "Drop Wii U games here: extracted title folders (code, content, meta), NUS folders (title.tmd, title.tik, .app),\n" +
        "WUD/WUX disc images or .wua files, or a folder that holds them.\n" +
        "Everything that belongs to the same game (base, update, DLC) goes into one .wua.";
    protected override string FilterName => "Wii U archives";
    protected override IReadOnlyList<string> Patterns { get; } = ["*.wua", "*.wud", "*.wux", "*.wuhb"];

    protected override bool Accepts(string path) =>
        WiiUTitles.IsSource(path) || WiiUTitles.UnsupportedReason(path) is not null;

    protected override IEnumerable<string> ItemsForFolder(string folder) =>
        WiiUTitles.FindSources(folder, Settings.ScanSubfolders);

    // Filled by GroupItems on the UI thread before a run; read by the workers.
    private readonly Dictionary<string, ulong> _baseIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _problems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One job per game: every row whose titles share a base title ID.</summary>
    protected override IReadOnlyList<IReadOnlyList<JobItemViewModel>> GroupItems(IReadOnlyList<JobItemViewModel> pending)
    {
        _baseIds.Clear();
        _problems.Clear();
        var keys = WiiUKeys.Empty;
        if (pending.Any(i => WiiUEncrypted.IsNusFolder(i.Path) || WiiUEncrypted.IsDiscImage(i.Path)))
        {
            keys = WiiUKeys.Load(Settings.Snapshot().WiiUKeysPath);
            Log.Info($"Wii U: {keys.Describe()}");
            foreach (var problem in keys.Problems) Log.Warn($"Wii U: {problem}");
        }
        var groups = new List<IReadOnlyList<JobItemViewModel>>();
        var byGame = new Dictionary<ulong, List<JobItemViewModel>>();
        foreach (var item in pending)
        {
            if (WiiUTitles.UnsupportedReason(item.Path) is { } reason)
            {
                _problems[item.Path] = reason;
                groups.Add([item]);
                continue;
            }
            try
            {
                var titles = WiiUTitles.Read(item.Path, keys);
                foreach (var baseId in titles.Select(t => t.BaseId).Distinct().Skip(1))
                    Log.Warn($"Wii U: {item.Name} holds more than one game; only the first ({titles[0].BaseId:X16}) is built from it.");
                var id = titles[0].BaseId;
                if (!byGame.TryGetValue(id, out var list)) byGame[id] = list = [];
                list.Add(item);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                           or KeyNotFoundException or NotSupportedException)
            {
                _problems[item.Path] = "!" + ex.Message;
                groups.Add([item]);
            }
        }
        foreach (var (id, rows) in byGame)
        {
            _baseIds[rows[0].Path] = id;
            groups.Add(rows);
        }
        Log.Info($"Wii U: {byGame.Count} game(s) to build from {pending.Count} item(s)");
        return groups;
    }

    protected override JobOutcome WorkGroup(JobContext context, IReadOnlyList<JobItemViewModel> group)
    {
        var lead = group[0].Path;
        if (_problems.TryGetValue(lead, out var problem))
            return problem.StartsWith('!') ? JobOutcome.Failed(problem[1..]) : JobOutcome.Skipped(problem);
        return WuaJobs.Build(context, _baseIds[lead], group.Select(i => i.Path).ToList());
    }

    protected override JobOutcome Work(JobContext context, JobItemViewModel item) => WorkGroup(context, [item]);

    protected override bool BeforeStart(IReadOnlyList<JobItemViewModel> pending)
    {
        if (Settings.WiiUDeleteOriginals)
        {
            var fate = Settings.UseRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently";
            Log.Warn($"Wii U: the folders and .wua files that go into each new .wua will be {fate} once it verifies.");
        }
        return true;
    }
}
