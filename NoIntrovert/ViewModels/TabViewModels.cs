using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// Verify &amp; match (M6): checks each input's own integrity, then looks its
/// hashes up in the No-Intro and Redump databases and says what it should be called.
/// </summary>
public partial class VerifyViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs, JobQueue queue)
    : BatchTabViewModel(settings, log, dialogs, queue)
{
    public override string Title => "Verify & match";
    public override string StartLabel => "Verify";
    public override string EmptyHint =>
        "Drop games here: ROMs, archives, disc images, CHDs, GameCube/Wii discs.\n" +
        "Each is checked for damage, then looked up in the No-Intro and Redump databases, which say whether it's a good dump and what it should be called.";
    protected override string FilterName => "Games";
    protected override IReadOnlyList<FormatInfo>? InputFormats => null;
    protected override bool Accepts(string path) => FormatRegistry.Default.Identify(path) is not null;
    protected override Core.Presets.PresetAction? RunPreset => Core.Presets.PresetAction.Verify;

    /// <summary>A folder brings in every input the registry finds in it.</summary>
    protected override IEnumerable<string> ItemsForFolder(string folder) =>
        FormatRegistry.Default.Scan(folder, Settings.Output.ScanSubfolders).Select(s => s.Primary).ToList();
    protected override JobOutcome Work(JobContext context, JobItemViewModel item) => Core.Dats.VerifyJobs.Check(context, item.Path);
}

public partial class TestsViewModel : BatchTabViewModel
{
    public TestsViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs, JobQueue queue) : base(settings, log, dialogs, queue)
    {
        SelectedTest = TestChoices[1];
    }

    public override string Title => "Library tests";
    public override string StartLabel => "Run tests";
    public override string EmptyHint =>
        "Drop disc images here (any format the Convert tab takes).\n" +
        "Round trip: create → verify → extract, then compare with the original data.\n" +
        "Compare: create with lib-chd-kit and a chdman executable, then compare the CHDs byte for byte.";
    protected override string FilterName => "Disc images and archives";
    protected override IReadOnlyList<FormatInfo> InputFormats => KnownFormats.DiscInputs;
    protected override bool Takes(FileSet set) => KnownFormats.TakesDisc(set);

    public static IReadOnlyList<Choice<TestKind>> TestChoices { get; } =
    [
        new(TestKind.CompareWithChdman, "Compare with chdman (byte-identical output, speed)"),
        new(TestKind.RoundTrip, "Round trip (create → verify → extract → compare data)"),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsChdman))]
    public partial Choice<TestKind> SelectedTest { get; set; }

    public bool NeedsChdman => SelectedTest.Value == TestKind.CompareWithChdman;

    [ObservableProperty] public partial string ChdmanVersion { get; set; } = "";

    public ObservableCollection<TestRecord> Records { get; } = [];

    [ObservableProperty] public partial string RecordSummary { get; set; } = "";

    protected override bool BeforeStart(IReadOnlyList<JobItemViewModel> pending)
    {
        if (NeedsChdman && (string.IsNullOrWhiteSpace(Settings.Diagnostics.ChdmanExePath) || !File.Exists(Settings.Diagnostics.ChdmanExePath)))
        {
            Log.Error("Library tests: choose a chdman executable first (needed for the comparison test).");
            return false;
        }
        if (NeedsChdman)
        {
            ChdmanVersion = ChdmanExe.Version(Settings.Diagnostics.ChdmanExePath);
            Log.Info($"Comparing lib-chd-kit {LibChdKit.Chdman.Version} with {(ChdmanVersion.Length > 0 ? ChdmanVersion : Settings.Diagnostics.ChdmanExePath)}");
        }
        return true;
    }

    protected override JobOutcome Work(JobContext context, JobItemViewModel item) =>
        TestJobs.Run(context, item.Path, SelectedTest.Value, r => Dispatcher.UIThread.Post(() =>
        {
            Records.Add(r);
            var passed = Records.Count(x => x.Passed);
            RecordSummary = $"{passed} of {Records.Count} passed";
        }));

    [RelayCommand]
    private void ClearResults()
    {
        Records.Clear();
        RecordSummary = "";
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (Records.Count == 0) { Log.Warn("No test results to export yet."); return; }
        var path = await Dialogs.SaveFileAsync("Export test results", $"lib-chd-kit-tests-{DateTime.Now:yyyyMMdd-HHmm}.csv", "CSV file", "*.csv");
        if (path is null) return;
        await File.WriteAllTextAsync(path, TestJobs.ToCsv(Records));
        Log.Info($"Test results saved to {path}");
    }
}
