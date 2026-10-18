using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibChdman;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

public sealed record InfoRow(string Name, string Value);

public sealed record MetadataRow(string Tag, uint Index, bool Checksummed, int Length, string Text, ChdMetadata Source)
{
    public string Flags => Checksummed ? "checksummed" : "";
    public string LengthText => $"{Length:N0} B";
}

public sealed record HunkRow(string Name, uint Hunks, string Share);

/// <summary>The CHD Info tab: header fields, metadata and (optionally) per-codec hunk counts of one CHD.</summary>
public partial class InfoViewModel(LogViewModel log, IDialogs dialogs) : ViewModelBase
{
    public string Title => "CHD Info";

    [ObservableProperty] public partial string Path { get; set; } = "";
    [ObservableProperty] public partial bool IncludeHunkStats { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Headline { get; set; } = "Open a CHD to see what's inside it.";
    [ObservableProperty] public partial MetadataRow? SelectedMetadata { get; set; }
    [ObservableProperty] public partial bool HasInfo { get; set; }

    public bool IsIdle => !IsBusy;

    public ObservableCollection<InfoRow> Properties { get; } = [];
    public ObservableCollection<MetadataRow> Metadata { get; } = [];
    public ObservableCollection<HunkRow> HunkStats { get; } = [];

    [RelayCommand]
    private async Task Open()
    {
        var file = await dialogs.PickFileAsync("Open a CHD", "CHD files", ["*.chd"]);
        if (file is not null) await LoadAsync(file);
    }

    [RelayCommand]
    private Task Reload() => Path.Length > 0 ? LoadAsync(Path) : Task.CompletedTask;

    partial void OnIncludeHunkStatsChanged(bool value)
    {
        if (Path.Length > 0 && !IsBusy) _ = LoadAsync(Path);
    }

    public async Task LoadAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        Path = path;
        Headline = $"Reading {System.IO.Path.GetFileName(path)}…";
        try
        {
            var stats = IncludeHunkStats;
            var info = await Task.Run(() =>
            {
                using var chdman = new Chdman();
                return chdman.Info(path, hunkStats: stats);
            });
            Show(path, info);
            log.Info($"Info: read {System.IO.Path.GetFileName(path)} ({ChdService.Describe(info.MediaType)}, {info.Metadata.Count} metadata item(s))");
        }
        catch (Exception ex) when (ex is ChdmanException or IOException)
        {
            Clear();
            Headline = $"Couldn't read {System.IO.Path.GetFileName(path)}: {OutputPaths.Describe(ex)}";
            log.Error($"Info: {Headline}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Clear()
    {
        Properties.Clear();
        Metadata.Clear();
        HunkStats.Clear();
        HasInfo = false;
    }

    private void Show(string path, ChdInfo info)
    {
        Clear();
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

        foreach (var m in info.Metadata)
            Metadata.Add(new MetadataRow(m.TagString, m.Index, m.Checksummed, m.Data.Length, Preview(m), m));

        var totalHunks = Math.Max(1u, info.HunkCount);
        foreach (var h in info.HunkStats)
            HunkStats.Add(new HunkRow(h.Name, h.Hunks, $"{(double)h.Hunks / totalHunks:P1}"));
        HasInfo = true;
    }

    private static string Preview(ChdMetadata m)
    {
        var printable = m.Data.Take(Math.Max(0, m.Data.Length - 1)).All(b => b is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\r' or (byte)'\t');
        if (printable) return m.Text;
        return Convert.ToHexString(m.Data.AsSpan(0, Math.Min(m.Data.Length, 48))) + (m.Data.Length > 48 ? "…" : "");
    }

    [RelayCommand]
    private async Task CopyAll()
    {
        var sb = new StringBuilder();
        foreach (var p in Properties) sb.AppendLine($"{p.Name}: {p.Value}");
        if (Metadata.Count > 0)
        {
            sb.AppendLine().AppendLine("Metadata:");
            foreach (var m in Metadata) sb.AppendLine($"  {m.Tag}[{m.Index}] {m.Flags} {m.Text}");
        }
        if (HunkStats.Count > 0)
        {
            sb.AppendLine().AppendLine("Hunks by codec:");
            foreach (var h in HunkStats) sb.AppendLine($"  {h.Name}: {h.Hunks:N0} ({h.Share})");
        }
        await dialogs.SetClipboardAsync(sb.ToString());
        log.Info("Info: copied to the clipboard");
    }

    [RelayCommand]
    private async Task SaveMetadata()
    {
        if (SelectedMetadata is not { } m) { log.Warn("Info: select a metadata item first."); return; }
        var file = await dialogs.SaveFileAsync("Save metadata", $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.{m.Tag.Trim()}{m.Index}.bin", "All files", "*.*");
        if (file is null) return;
        await File.WriteAllBytesAsync(file, m.Source.Data);
        log.Info($"Info: saved {m.Tag}[{m.Index}] to {file}");
    }
}
