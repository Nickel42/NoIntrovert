using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Presets;

namespace NoIntrovert.ViewModels;

/// <summary>
/// Settings › Default presets: the format preset each system's rows start on,
/// and the presets saved from Custom (Manage… renames and deletes them). The
/// Convert page saves presets through here too, and rebuilds its rows' lists
/// when <see cref="Changed"/> fires.
/// </summary>
public sealed partial class DefaultPresetsViewModel : SettingsSectionViewModel
{
    public DefaultPresetsViewModel(AppSettings settings) : base(settings) => Rebuild();

    public override string Title => "Default presets";

    /// <summary>Raised after a default or a saved preset changes.</summary>
    public event Action? Changed;

    /// <summary>The live preset choices (read on the UI thread).</summary>
    public PresetSettings Current => S.Presets;

    /// <summary>One dropdown per system that has a choice, most common first.</summary>
    [ObservableProperty] public partial IReadOnlyList<SystemDefaultViewModel> Systems { get; private set; } = [];

    public ObservableCollection<SavedPresetItemViewModel> Saved { get; } = [];

    public bool HasSaved => Saved.Count > 0;

    /// <summary>"No saved presets yet." or "2 saved presets".</summary>
    public string SavedSummary => Saved.Count switch
    {
        0 => "None yet. Save one from Custom on the Convert page.",
        1 => "1 saved preset",
        var n => $"{n} saved presets",
    };

    /// <summary>Whether the saved presets are shown for renaming and deleting.</summary>
    [ObservableProperty] public partial bool IsManaging { get; set; }

    [RelayCommand]
    private void Manage() => IsManaging = !IsManaging;

    /// <summary>Makes <paramref name="preset"/> the default for <paramref name="systemId"/>; the recommended one isn't written down.</summary>
    public void SetDefault(string systemId, FormatPreset preset)
    {
        var recommended = FormatPresets.Recommended(systemId);
        if (!Change(s =>
            {
                if (preset.Id == recommended?.Id) s.Presets.DefaultBySystem.Remove(systemId);
                else s.Presets.DefaultBySystem[systemId] = preset.Id;
            })) return;
        Changed?.Invoke();
    }

    /// <summary>
    /// Saves <paramref name="values"/> as <paramref name="name"/> for
    /// <paramref name="systemId"/> ("" for any system), replacing a saved preset
    /// of that name for the same system. Returns the saved preset.
    /// </summary>
    public SavedPreset Save(string name, string? systemId, PresetValues values)
    {
        name = name.Trim();
        var system = systemId ?? "";
        var saved = S.Presets.Saved.FirstOrDefault(p => p.System == system && p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        Change(s =>
        {
            if (saved is null)
            {
                saved = new SavedPreset { Name = name, System = system };
                s.Presets.Saved.Add(saved);
            }
            saved.Name = name;
            saved.Values = values.Copy();
        });
        Rebuild();
        Changed?.Invoke();
        return saved!;
    }

    /// <summary>
    /// After emulator data is updated: forgets the cached "N of M" counts and
    /// rebuilds the lists, so open rows and the side panel count again.
    /// </summary>
    public void EmulatorDataChanged()
    {
        FormatPresets.ForgetCounts();
        RebuildSystems();
        Changed?.Invoke();
    }

    internal void Rename(SavedPreset preset, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || !Change(_ => preset.Name = name)) return;
        RebuildSystems();
        Changed?.Invoke();
    }

    internal void Delete(SavedPreset preset)
    {
        var id = FormatPreset.SavedPrefix + preset.Id;
        Change(s =>
        {
            s.Presets.Saved.Remove(preset);
            foreach (var key in s.Presets.DefaultBySystem.Where(p => p.Value == id).Select(p => p.Key).ToList())
                s.Presets.DefaultBySystem.Remove(key);
        });
        Rebuild();
        Changed?.Invoke();
    }

    private void Rebuild()
    {
        Saved.Clear();
        foreach (var p in S.Presets.Saved) Saved.Add(new SavedPresetItemViewModel(this, p));
        OnPropertyChanged(nameof(HasSaved));
        OnPropertyChanged(nameof(SavedSummary));
        RebuildSystems();
    }

    private void RebuildSystems() =>
        Systems = FormatPresets.DraftedSystems
            .Select(id => new SystemDefaultViewModel(this, id))
            .Where(s => s.Options.Count > 1)
            .ToList();
}

/// <summary>One system's default preset in Settings.</summary>
public sealed class SystemDefaultViewModel(DefaultPresetsViewModel owner, string systemId) : ViewModelBase
{
    public string SystemId { get; } = systemId;

    public string Name { get; } = Core.Systems.SystemFacts.For(systemId)?.Name ?? TargetFormats.NameOf(systemId);

    /// <summary>Every preset the app can make for the system, then the user's saved ones.</summary>
    public IReadOnlyList<FormatPreset> Options { get; } =
        FormatPresets.ForSystem(systemId).Where(p => p.Producible).Concat(FormatPresets.Saved(owner.Current, systemId)).ToList();

    public FormatPreset? Selected
    {
        get
        {
            var chosen = FormatPresets.Default(SystemId, owner.Current);
            return Options.FirstOrDefault(o => o.Id == chosen?.Id) ?? Options.FirstOrDefault();
        }
        set
        {
            if (value is null) return;
            owner.SetDefault(SystemId, value);
            OnPropertyChanged();
        }
    }
}

/// <summary>A saved preset in Manage…: its name (edited in place) and Delete.</summary>
public sealed partial class SavedPresetItemViewModel(DefaultPresetsViewModel owner, SavedPreset preset) : ViewModelBase
{
    public string Name
    {
        get => preset.Name;
        set => owner.Rename(preset, value);
    }

    /// <summary>"CHD · Sony PlayStation", or "CHD · any system".</summary>
    public string Detail => $"{FormatPresets.FormatOf(preset.Values.Target)} · " +
                            (preset.System.Length == 0 ? "any system" : Core.Systems.SystemFacts.For(preset.System)?.Name ?? TargetFormats.NameOf(preset.System));

    [RelayCommand]
    private void Delete() => owner.Delete(preset);
}
