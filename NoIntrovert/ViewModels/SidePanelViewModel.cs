using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Presets;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The Convert page's side panel (docs/requirements/ui-redesign.md): File,
/// Preset and Preset settings for the focused row. Preset edits that row only;
/// Apply to N selected copies its preset to the other selected rows.
/// </summary>
public sealed partial class SidePanelViewModel : ViewModelBase
{
    private readonly BatchTabViewModel _page;
    private readonly DefaultPresetsViewModel _presets;
    private JobItemViewModel? _watched;

    public SidePanelViewModel(BatchTabViewModel page, DefaultPresetsViewModel presets)
    {
        _page = page;
        _presets = presets;
    }

    public FileFactsViewModel File { get; } = new();
    public WorksWithViewModel WorksWith { get; } = new();

    /// <summary>The settings, for the Wii U keys block.</summary>
    public SettingsViewModel Settings => _page.Settings;

    /// <summary>"Focused: row 3 of 9 · 3 selected".</summary>
    [ObservableProperty] public partial string Header { get; private set; } = "";

    /// <summary>The row the Preset section shows: the focused one, or with none, a row of the most common preset in the list.</summary>
    [ObservableProperty] public partial JobItemViewModel? Row { get; private set; }

    /// <summary>Whether <see cref="Row"/> is the focused row (otherwise the dropdown only shows the list's most common presets).</summary>
    [ObservableProperty] public partial bool CanChoose { get; private set; }

    [ObservableProperty] public partial bool HasRow { get; private set; }

    /// <summary>"7 presets for PlayStation discs".</summary>
    [ObservableProperty] public partial string PresetCount { get; private set; } = "";

    public IReadOnlyList<PresetListItem> Options => Row?.PresetOptions ?? [];

    public PresetListItem? Selected
    {
        get => Row?.PresetChoice;
        set
        {
            if (CanChoose && Row is not null && value is not null) Row.PresetChoice = value;
        }
    }

    // ---- Apply to N selected ----

    [ObservableProperty] public partial bool ShowsApply { get; private set; }
    [ObservableProperty] public partial string ApplyLabel { get; private set; } = "";
    [ObservableProperty] public partial string ApplyHint { get; private set; } = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyToSelectedCommand))]
    public partial bool CanApply { get; private set; }

    /// <summary>Copies the focused row's preset (and its Custom values) to every other selected row that can use it.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyToSelected()
    {
        if (_page.FocusedItem is not { Preset: { } preset } focused) return;
        foreach (var other in Others(focused))
        {
            if (other.OptionFor(preset.Id) is not { } option) continue;
            if (preset.IsCustom)
            {
                if (focused.CustomValues is not { } values || !other.Offers(values.Target)) continue;
                other.PresetChoice = option;
                other.CustomValues = values.Copy();
            }
            else other.PresetChoice = option;
        }
        RefreshApply();
    }

    // ---- Preset settings (Custom only) ----

    [ObservableProperty] public partial PresetSettingsViewModel? Custom { get; private set; }

    [ObservableProperty] public partial bool ShowsWiiUKeys { get; private set; }

    /// <summary>A selected (or the shown) row is a 3DS file that can't be converted without the user's keys.</summary>
    [ObservableProperty] public partial bool ShowsThreeDsKeys { get; private set; }

    /// <summary>A selected (or the shown) row is a Switch file: every Switch conversion needs prod.keys.</summary>
    [ObservableProperty] public partial bool ShowsSwitchKeys { get; private set; }

    /// <summary>Which 3DS files need keys, read once per file (it opens the file's headers).</summary>
    private readonly Dictionary<string, bool> _threeDsNeedsKeys = new(Core.PathNames.Comparer);

    private bool ThreeDsNeedsKeys(JobItemViewModel row)
    {
        if (row.SystemId != KeysLink.ThreeDsSystem) return false;
        if (!_threeDsNeedsKeys.TryGetValue(row.Path, out var needs))
            _threeDsNeedsKeys[row.Path] = needs = KeysLink.ThreeDsFileNeedsKeys(row.Path);
        return needs;
    }

    private void RefreshKeys(JobItemViewModel? row)
    {
        ShowsWiiUKeys = row is not null && (row.SystemId == "wii-u" || row.Target?.Kind == TargetKind.Wua);
        var rows = _page.Selected.Append(row).OfType<JobItemViewModel>().Distinct().ToList();
        ShowsThreeDsKeys = rows.Any(ThreeDsNeedsKeys);
        ShowsSwitchKeys = rows.Any(r => r.SystemId == KeysLink.SwitchSystem);
    }

    /// <summary>Follows the page's focus and selection; called whenever either changes.</summary>
    public void Refresh()
    {
        var focused = _page.FocusedItem;
        var selected = _page.Selected;
        var items = _page.Items;

        Header = focused is not null
            ? $"Focused: row {items.IndexOf(focused) + 1} of {items.Count} · {selected.Count} selected"
            : selected.Count > 0 ? $"{selected.Count} selected" : "";

        // File: the focused file, or one line per system when the selection spans several.
        if (selected.Select(r => r.SystemId ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            File.ShowSystems(selected.Select(r => r.SystemId));
        else if (focused is not null)
        {
            if (focused.Path != File.Path || File.Mode != FileFactsMode.File)
                File.ShowFile(focused.Path, focused.SystemId, MatchStore.Default.Get(focused.Path));
        }
        else File.Clear();

        // Preset: the focused row, or the list's most common preset, read-only.
        var row = focused ?? items.Where(i => i.HasPresets)
            .GroupBy(i => (i.SystemId, i.Preset?.Id))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.First();
        Watch(row);
        Row = row;
        HasRow = row is not null && row.HasPresets;
        CanChoose = focused is not null && focused.HasPresets;
        var count = row?.PresetOptions.Count(i => i.IsPreset && !i.Preset!.IsLeave) ?? 0;
        PresetCount = row is null ? "" : PresetCountText(count, row.SystemId);
        RefreshPreset();
    }

    /// <summary>"6 presets for PlayStation discs", "4 presets for Game Boy cartridges", "3 presets for this file".</summary>
    public static string PresetCountText(int count, string? systemId)
    {
        var what = count == 1 ? "1 preset" : $"{count} presets";
        if (systemId is null) return $"{what} for this file";
        var facts = Core.Systems.SystemFacts.ForOrFallback(systemId);
        return $"{what} for {facts.Short}{MediaNoun(facts.Media)}";
    }

    /// <summary>" discs" for CD, DVD, GD-ROM, UMD and Blu-ray systems, " cartridges" for cartridges, otherwise nothing.</summary>
    private static string MediaNoun(string? media) => media switch
    {
        null => "",
        _ when media.Contains("Cartridge", StringComparison.OrdinalIgnoreCase) => " cartridges",
        _ when media.Contains("disc", StringComparison.OrdinalIgnoreCase) && !media.Contains("Floppy", StringComparison.OrdinalIgnoreCase)
               || media is "CD-ROM" or "DVD" or "Mini DVD" or "GD-ROM" or "UMD" or "Blu-ray" => " discs",
        _ => "",
    };

    private void Watch(JobItemViewModel? row)
    {
        if (_watched == row) return;
        if (_watched is not null) _watched.PropertyChanged -= OnRowChanged;
        _watched = row;
        if (row is not null) row.PropertyChanged += OnRowChanged;
        Custom = null;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JobItemViewModel.PresetChoice) or nameof(JobItemViewModel.PresetOptions))
        {
            if (e.PropertyName == nameof(JobItemViewModel.PresetOptions)) OnPropertyChanged(nameof(Options));
            Custom = null; // a new choice: Preset settings start again from the row's values
            RefreshPreset();
        }
        else if (e.PropertyName == nameof(JobItemViewModel.CustomValues))
        {
            RefreshWorksWith();
            RefreshApply();
        }
    }

    private void RefreshPreset()
    {
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(Selected));
        var row = Row;
        if (row is { IsCustom: true } && CanChoose)
            Custom ??= new PresetSettingsViewModel(row, _presets);
        else Custom = null;
        RefreshKeys(row);
        RefreshWorksWith();
        RefreshApply();
    }

    private void RefreshWorksWith()
    {
        var row = Row;
        if (row is null)
        {
            WorksWith.Set(null, null, null);
            return;
        }
        var values = row.Values;
        var extension = row.Preset is { IsLeave: true } || values is null
            ? (row.IsFolder ? null : System.IO.Path.GetExtension(row.Path))
            : row.IsCustom ? values.OutputExtension(row.SystemId) : row.Preset!.OutputExtension(row.SystemId);
        WorksWith.Set(row.SystemId, extension, values?.Codecs ?? []);
    }

    private IEnumerable<JobItemViewModel> Others(JobItemViewModel focused) => _page.Selected.Where(r => r != focused);

    private void RefreshApply()
    {
        var focused = _page.FocusedItem;
        var others = focused is null ? [] : Others(focused).ToList();
        ShowsApply = focused?.Preset is not null && others.Count > 0;
        if (!ShowsApply)
        {
            CanApply = false;
            ApplyLabel = ApplyHint = "";
            Custom?.SetApplyLabel(null);
            return;
        }
        var preset = focused!.Preset!;
        bool Uses(JobItemViewModel r) => r.Preset?.Id == preset.Id && (!preset.IsCustom || Equals(r.CustomValues, focused.CustomValues));
        var different = others.Count(r => !Uses(r));
        ApplyLabel = $"Apply to {others.Count + 1} selected";
        ApplyHint = different == 0
            ? (others.Count == 1 ? "The other selected row already uses this preset." : $"The other {others.Count} selected rows already use this preset.")
            : others.Count == 1 ? "The other selected row uses a different preset."
            : different == others.Count ? $"The other {others.Count} selected rows use a different preset."
            : $"{different} of the other {others.Count} selected rows use a different preset.";
        CanApply = different > 0;
        Custom?.SetApplyLabel(ApplyLabel);
    }
}
