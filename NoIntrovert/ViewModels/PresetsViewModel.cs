using NoIntrovert.Core;
using NoIntrovert.Core.Presets;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The preset chosen for each action (issue #33). One picker per action,
/// shared by the Convert, Verify and Settings pages, so a choice made on one
/// shows on the others.
/// </summary>
public sealed class PresetsViewModel : SettingsSectionViewModel
{
    public PresetsViewModel(AppSettings settings, SettingsViewModel all) : base(settings)
    {
        Chd = new(this, all, PresetAction.Chd);
        Rvz = new(this, all, PresetAction.Rvz);
        Wua = new(this, all, PresetAction.Wua);
        Archive = new(this, all, PresetAction.Archive);
        Extract = new(this, all, PresetAction.Extract);
        Unpack = new(this, all, PresetAction.Unpack);
        Verify = new(this, all, PresetAction.Verify);
    }

    public override string Title => "Presets";

    public ActionPresetViewModel Chd { get; }
    public ActionPresetViewModel Rvz { get; }
    public ActionPresetViewModel Wua { get; }
    public ActionPresetViewModel Archive { get; }
    public ActionPresetViewModel Extract { get; }
    public ActionPresetViewModel Unpack { get; }
    public ActionPresetViewModel Verify { get; }

    /// <summary>Every action, in the order the Settings page lists them.</summary>
    public IReadOnlyList<ActionPresetViewModel> All => [Chd, Rvz, Wua, Archive, Extract, Unpack, Verify];

    internal ActionPreset Selected(PresetAction action) => ActionPresets.Selected(S, action);

    internal bool Choose(ActionPreset preset) => Change(s => ActionPresets.Select(s, preset));
}

/// <summary>One action's presets. Its parameters are shown only while Custom is chosen.</summary>
public sealed class ActionPresetViewModel(PresetsViewModel owner, SettingsViewModel settings, PresetAction action) : ViewModelBase
{
    public PresetAction Action { get; } = action;

    public string Title { get; } = ActionPresets.Title(action).ToUpperInvariant();

    public IReadOnlyList<ActionPreset> Presets { get; } = ActionPresets.For(action);

    public ActionPreset Selected
    {
        get => owner.Selected(Action);
        set
        {
            if (value is null || !owner.Choose(value)) return;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(Summary));
        }
    }

    public bool IsCustom => Selected.IsCustom;
    public string Summary => Selected.IsPreferred ? $"Recommended: {Selected.Summary}" : Selected.Summary;

    /// <summary>The settings Custom edits.</summary>
    public SettingsViewModel Settings { get; } = settings;

    // Which parameters the Custom panel shows.
    public bool IsChd => Action == PresetAction.Chd;
    public bool IsRvz => Action == PresetAction.Rvz;
    public bool IsArchive => Action == PresetAction.Archive;
    public bool IsExtract => Action == PresetAction.Extract;
    public bool IsUnpack => Action == PresetAction.Unpack;
    public bool IsVerify => Action == PresetAction.Verify;
}
