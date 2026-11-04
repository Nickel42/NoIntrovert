using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NoIntrovert.Core.Emulators;

namespace NoIntrovert.ViewModels;

/// <summary>One emulator in "Works with": its name, ✓/✕/? state and the tooltip detail.</summary>
public sealed record WorksWithItem(string Name, SupportState State, string Detail);

/// <summary>
/// The side panel's "Works with" list: every RetroArch core for the system,
/// then the most common standalone emulators, each marked for the current
/// output. Recomputed whenever <see cref="SystemId"/>, <see cref="OutputExtension"/>
/// or <see cref="Codecs"/> changes; <see cref="Set"/> changes all three at once.
/// </summary>
public partial class WorksWithViewModel : ViewModelBase
{
    private readonly Func<EmulatorCatalog> catalog;
    private bool updating;

    /// <summary>Uses <see cref="EmulatorCatalog.Current"/>.</summary>
    public WorksWithViewModel() : this(() => EmulatorCatalog.Current) { }

    /// <summary>Uses the catalog <paramref name="catalog"/> returns (tests).</summary>
    public WorksWithViewModel(Func<EmulatorCatalog> catalog)
    {
        this.catalog = catalog;
        Refresh();
    }

    /// <summary>A NoIntrovert system id ("playstation") or an ES-DE one ("psx").</summary>
    [ObservableProperty] public partial string? SystemId { get; set; }

    /// <summary>The output's extension, with or without the dot (".chd").</summary>
    [ObservableProperty] public partial string? OutputExtension { get; set; }

    /// <summary>The CHD codecs the preset uses; empty for chdman's defaults.</summary>
    [ObservableProperty] public partial IReadOnlyCollection<string>? Codecs { get; set; }

    [ObservableProperty] public partial bool HasCores { get; private set; }
    [ObservableProperty] public partial bool HasStandalone { get; private set; }
    [ObservableProperty] public partial bool HasAny { get; private set; }

    /// <summary>The footer, e.g. "From libretro core info and ES-DE · 4 Nov 2026".</summary>
    [ObservableProperty] public partial string Source { get; private set; } = "";

    public ObservableCollection<WorksWithItem> Cores { get; } = [];
    public ObservableCollection<WorksWithItem> Standalone { get; } = [];

    /// <summary>Changes the system, output and codecs together, recomputing once.</summary>
    public void Set(string? systemId, string? outputExtension, IReadOnlyCollection<string>? codecs)
    {
        updating = true;
        try
        {
            SystemId = systemId;
            OutputExtension = outputExtension;
            Codecs = codecs;
        }
        finally
        {
            updating = false;
        }
        Refresh();
    }

    partial void OnSystemIdChanged(string? value) => RefreshUnlessUpdating();
    partial void OnOutputExtensionChanged(string? value) => RefreshUnlessUpdating();
    partial void OnCodecsChanged(IReadOnlyCollection<string>? value) => RefreshUnlessUpdating();

    private void RefreshUnlessUpdating()
    {
        if (!updating) Refresh();
    }

    /// <summary>Recomputes the list (also after the emulator data was downloaded again).</summary>
    public void Refresh()
    {
        IReadOnlyList<EmulatorSupportEntry> entries;
        try
        {
            var data = catalog();
            Source = data.SourceText;
            entries = EmulatorSupport.For(data, SystemId, OutputExtension, Codecs);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            // Unreadable data shows an empty list rather than breaking the panel.
            entries = [];
        }
        Fill(Cores, entries.Where(e => e.Group == EmulatorGroup.RetroArchCore));
        Fill(Standalone, entries.Where(e => e.Group == EmulatorGroup.Standalone));
        HasCores = Cores.Count > 0;
        HasStandalone = Standalone.Count > 0;
        HasAny = HasCores || HasStandalone;
    }

    private static void Fill(ObservableCollection<WorksWithItem> list, IEnumerable<EmulatorSupportEntry> entries)
    {
        list.Clear();
        foreach (var e in entries) list.Add(new WorksWithItem(e.Name, e.State, e.Detail));
    }
}
