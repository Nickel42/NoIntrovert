using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LibChdKit;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

public sealed record InfoRow(string Name, string Value);

/// <summary>
/// The header fields of one CHD, read with lib-chd-kit, for the Library's
/// game details. (The CHD info page that also showed metadata and hunk
/// counts was removed in the redesign.)
/// </summary>
public partial class InfoViewModel : ViewModelBase
{
    private readonly LogViewModel _log;

    /// <param name="log">Where read errors go.</param>
    /// <param name="dialogs">Not used since the page went; kept so callers don't change.</param>
    public InfoViewModel(LogViewModel log, IDialogs dialogs)
    {
        _log = log;
        _ = dialogs;
    }

    [ObservableProperty] public partial string Path { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Headline { get; set; } = "";
    [ObservableProperty] public partial bool HasInfo { get; set; }

    public ObservableCollection<InfoRow> Properties { get; } = [];

    public async Task LoadAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        Path = path;
        Headline = $"Reading {System.IO.Path.GetFileName(path)}…";
        try
        {
            var info = await Task.Run(() =>
            {
                using var chdman = new Chdman();
                return chdman.Info(path, hunkStats: false);
            });
            Show(path, info);
            _log.Info($"Info: read {System.IO.Path.GetFileName(path)} ({ChdService.Describe(info.MediaType)}, {info.Metadata.Count} metadata item(s))");
        }
        catch (Exception ex) when (ex is ChdmanException or IOException)
        {
            Properties.Clear();
            HasInfo = false;
            Headline = $"Couldn't read {System.IO.Path.GetFileName(path)}: {OutputPaths.Describe(ex)}";
            _log.Error($"Info: {Headline}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(string path, ChdInfo info)
    {
        Properties.Clear();
        Headline = $"{System.IO.Path.GetFileName(path)} — {ChdService.Describe(info.MediaType)}, CHD v{info.Version}";
        void Add(string n, string v) => Properties.Add(new InfoRow(n, v));
        Add("File", path);
        Add("Media", ChdService.Describe(info.MediaType));
        Add("CHD version", info.Version.ToString());
        Add("File size", $"{ChdJobs.Size((long)info.FileBytes)} ({info.FileBytes:N0} bytes)");
        Add("Logical size", $"{ChdJobs.Size((long)info.LogicalBytes)} ({info.LogicalBytes:N0} bytes)");
        Add("Ratio", info.LogicalBytes > 0 ? $"{(double)info.FileBytes / info.LogicalBytes:P1}" : "—");
        Add("Compression", info.IsCompressed ? info.Compression : "none");
        Add("Hunk size", $"{info.HunkBytes:N0} bytes");
        Add("Hunks", info.HunkCount.ToString("N0"));
        Add("Unit size", $"{info.UnitBytes:N0} bytes");
        Add("Units", info.UnitCount.ToString("N0"));
        Add("SHA-1", info.Sha1);
        Add("Data SHA-1", info.RawSha1);
        if (!string.IsNullOrEmpty(info.ParentSha1) && info.ParentSha1.Trim('0').Length > 0)
            Add("Parent SHA-1", info.ParentSha1);
        HasInfo = true;
    }
}
