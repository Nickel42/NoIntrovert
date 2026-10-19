using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

public partial class ConvertViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs)
    : BatchTabViewModel(settings, log, dialogs)
{
    public override string Title => "Convert";
    public override string StartLabel => "Convert to CHD";
    public override string EmptyHint =>
        "Drop disc images or folders here, or use Add files / Add folder.\n" +
        "cue/bin · gdi · toc · nrg · iso · ccd/img · mds/mdf · cso · zso · isz · pbp · ecm · zip · 7z · .001 split files";
    protected override string FilterName => "Disc images and archives";
    protected override IReadOnlyList<string> Patterns { get; } =
        DiscFiles.Descriptors.Concat(DiscFiles.Images).Concat(ArchiveExtractor.Extensions).Append(".001").Select(e => "*" + e).ToList();

    protected override bool Accepts(string path) => DiscFiles.IsSupported(path);
    protected override IEnumerable<string> SelectFromFolder(IEnumerable<string> files) => DiscFiles.SelectPrimaries(files);
    protected override JobOutcome Work(JobContext context, JobItemViewModel item) => ChdJobs.Convert(context, item.Path);

    protected override bool BeforeStart(IReadOnlyList<JobItemViewModel> pending)
    {
        if (Settings.DeleteOriginals)
            Log.Warn($"Originals will be {(Settings.UseRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently")} after each CHD verifies.");
        return true;
    }
}

public partial class VerifyViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs)
    : BatchTabViewModel(settings, log, dialogs)
{
    public override string Title => "Verify";
    public override string StartLabel => "Verify CHDs";
    public override string EmptyHint => "Drop CHD files or folders here to check their data against the stored SHA-1.";
    protected override string FilterName => "CHD files";
    protected override IReadOnlyList<string> Patterns { get; } = ["*.chd"];
    protected override bool Accepts(string path) => path.EndsWith(".chd", StringComparison.OrdinalIgnoreCase);
    protected override JobOutcome Work(JobContext context, JobItemViewModel item) => ChdJobs.Verify(context, item.Path);
}

public partial class ExtractViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs)
    : BatchTabViewModel(settings, log, dialogs)
{
    public override string Title => "Extract";
    public override string StartLabel => "Extract CHDs";
    public override string EmptyHint => "Drop CHD files or folders here to turn them back into cue/bin, gdi, iso or img.";
    protected override string FilterName => "CHD files";
    protected override IReadOnlyList<string> Patterns { get; } = ["*.chd"];
    protected override bool Accepts(string path) => path.EndsWith(".chd", StringComparison.OrdinalIgnoreCase);
    protected override JobOutcome Work(JobContext context, JobItemViewModel item) => ChdJobs.Extract(context, item.Path);
}

public partial class TestsViewModel : BatchTabViewModel
{
    public TestsViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs) : base(settings, log, dialogs)
    {
        SelectedTest = TestChoices[1];
    }

    public override string Title => "Library tests";
    public override string StartLabel => "Run tests";
    public override string EmptyHint =>
        "Drop disc images here (any format the Convert tab takes).\n" +
        "Round trip: create → verify → extract, then compare with the original data.\n" +
        "Compare: create with libchdman and a chdman executable, then compare the CHDs byte for byte.";
    protected override string FilterName => "Disc images and archives";
    protected override IReadOnlyList<string> Patterns { get; } =
        DiscFiles.Descriptors.Concat(DiscFiles.Images).Concat(ArchiveExtractor.Extensions).Append(".001").Select(e => "*" + e).ToList();
    protected override bool Accepts(string path) => DiscFiles.IsSupported(path);
    protected override IEnumerable<string> SelectFromFolder(IEnumerable<string> files) => DiscFiles.SelectPrimaries(files);

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
        if (NeedsChdman && (string.IsNullOrWhiteSpace(Settings.ChdmanExePath) || !File.Exists(Settings.ChdmanExePath)))
        {
            Log.Error("Library tests: choose a chdman executable first (needed for the comparison test).");
            return false;
        }
        if (NeedsChdman)
        {
            ChdmanVersion = ChdmanExe.Version(Settings.ChdmanExePath);
            Log.Info($"Comparing libchdman {LibChdman.Chdman.Version} with {(ChdmanVersion.Length > 0 ? ChdmanVersion : Settings.ChdmanExePath)}");
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
        var path = await Dialogs.SaveFileAsync("Export test results", $"libchdman-tests-{DateTime.Now:yyyyMMdd-HHmm}.csv", "CSV file", "*.csv");
        if (path is null) return;
        await File.WriteAllTextAsync(path, TestJobs.ToCsv(Records));
        Log.Info($"Test results saved to {path}");
    }
}
