using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Presets;

namespace NoIntrovert.ViewModels;

/// <summary>One entry of Preset settings' Target format: a format the system has, with the values it starts from.</summary>
public sealed record FormatChoice(string Format, TargetKind? Kind, bool IsEnabled, PresetValues? Standard)
{
    public string Label => IsEnabled ? Format : $"{Format} · {PresetListItem.NotAvailableYet.ToLowerInvariant()}";
    public override string ToString() => Label;
}

/// <summary>
/// The side panel's Preset settings section, shown only while the focused row
/// is on Custom: Target format first, then every setting of that format, then
/// Save as a preset. The controls are the Settings page's own (CHD, RVZ,
/// archives), bound to a scratch copy of the settings that's never saved; each
/// change is copied into the row's Custom values.
/// </summary>
public sealed partial class PresetSettingsViewModel : ViewModelBase
{
    public const string HunkWarning = "⚠ Advanced. A non-default hunk size can make CHDs larger, slower to load or unreadable in some emulators. Leave it unless you understand the risks.";
    public const string ZstandardNote = "Zstandard CHDs need a reader from 2024 or later (chdman 0.262+). Older emulators can’t open them.";
    public const string ScrubWarning = "⚠ Scrubbing removes padding, so the original Redump checksum can't be restored.";

    private readonly JobItemViewModel _row;
    private readonly DefaultPresetsViewModel _presets;
    private AppSettings _scratch = new() { IsScratch = true };
    private bool _pulling;

    public PresetSettingsViewModel(JobItemViewModel row, DefaultPresetsViewModel presets)
    {
        _row = row;
        _presets = presets;
        Formats = FormatsFor(row);
        Load(row.CustomValues ?? new PresetValues());
    }

    /// <summary>Every format the file's system has (one not available yet is listed, disabled).</summary>
    public IReadOnlyList<FormatChoice> Formats { get; }

    [ObservableProperty] public partial ChdSettingsViewModel Chd { get; private set; } = null!;
    [ObservableProperty] public partial NintendoSettingsViewModel Nintendo { get; private set; } = null!;
    [ObservableProperty] public partial ArchiveSettingsViewModel Archives { get; private set; } = null!;

    private PresetValues Values => _row.CustomValues ?? new PresetValues();

    public FormatChoice? SelectedFormat
    {
        get => Match(Formats, Values);
        set
        {
            if (value is not { IsEnabled: true, Standard: { } standard } || value == SelectedFormat) return;
            _row.CustomValues = standard.Copy();
            Load(_row.CustomValues);
        }
    }

    public bool IsChd => Values.Target == TargetKind.Chd;
    public bool IsRvz => Values.Target == TargetKind.Rvz;
    public bool IsArchive => Values.Target is TargetKind.SevenZip or TargetKind.Zip;
    public bool IsSevenZip => Values.Target == TargetKind.SevenZip;
    public bool IsExtract => Values.Target == TargetKind.DiscImage;
    public bool IsUnpack => Values.Target == TargetKind.Files;
    public bool HasNothingToSet => Values.Target is TargetKind.Iso or TargetKind.Wua or TargetKind.Cci or TargetKind.Zcci
        or TargetKind.Nsz or TargetKind.Xcz or TargetKind.Nsp or TargetKind.Xci or TargetKind.Xiso or TargetKind.XboxCci or TargetKind.Zar
        or TargetKind.Ps3Iso or TargetKind.Ps3Folder;
    public bool UsesZstandard => Values.UsesZstandard;

    /// <summary>Compression's codecs, at the right of its label: "cdzs, cdzl, cdfl", or "Default codecs".</summary>
    public string CodecsText => CodecsFor(Values);

    /// <summary><see cref="CodecsText"/>'s rule.</summary>
    public static string CodecsFor(PresetValues values) =>
        values.Codecs is { Count: > 0 } codecs ? string.Join(", ", codecs) : "Default codecs";

    /// <summary>The note at the top: "Changes apply to this file. Use “Apply to 3 selected” to share them."</summary>
    [ObservableProperty] public partial string Note { get; private set; } = NoteFor(null);

    /// <summary>Sets <see cref="Note"/> from the panel's Apply button label (null when no other row is selected).</summary>
    public void SetApplyLabel(string? applyLabel) => Note = NoteFor(applyLabel);

    public static string NoteFor(string? applyLabel) => applyLabel is { Length: > 0 }
        ? $"Changes apply to this file. Use “{applyLabel}” to share them."
        : "Changes apply to this file. Select other rows to share them.";

    /// <summary>"Reset to CHD · Standard".</summary>
    public string ResetLabel => _row.CustomBase is { } p ? $"Reset to {p.Label}" : "";
    public bool CanReset => _row.CustomBase is not null;

    [RelayCommand]
    private void Reset()
    {
        if (_row.CustomBase is { } p && _row.OptionFor(p.Id) is { } option) _row.PresetChoice = option;
    }

    // ---- Save as a preset ----

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    public partial string SaveName { get; set; } = "";

    private bool CanSavePreset => SaveName.Trim().Length > 0;

    /// <summary>Saves the row's Custom values for its system and puts the row on the saved preset.</summary>
    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        var saved = _presets.Save(SaveName, _row.SystemId, Values);
        SaveName = "";
        if (_row.OptionFor(FormatPreset.SavedPrefix + saved.Id) is { } option) _row.PresetChoice = option;
    }

    /// <summary>The formats of a row's dropdown, each once, with the first usable preset's values.</summary>
    public static IReadOnlyList<FormatChoice> FormatsFor(JobItemViewModel row)
    {
        var list = new List<FormatChoice>();
        foreach (var item in row.PresetOptions.Where(i => i.IsPreset && i.Preset!.Kind == PresetKind.Standard))
        {
            var p = item.Preset!;
            var at = list.FindIndex(f => f.Format == p.Format);
            var choice = new FormatChoice(p.Format, p.Target, item.IsEnabled, item.IsEnabled ? p.Values : null);
            if (at < 0) list.Add(choice);
            else if (!list[at].IsEnabled && item.IsEnabled) list[at] = choice;
        }
        return list;
    }

    /// <summary>The format <paramref name="values"/> make: the same target and, for a disc image, the same layout.</summary>
    public static FormatChoice? Match(IReadOnlyList<FormatChoice> formats, PresetValues values) =>
        formats.FirstOrDefault(f => f.Standard is { } s && s.Target == values.Target
                                    && (values.Target != TargetKind.DiscImage || s.ExtractFormat == values.ExtractFormat))
        ?? formats.FirstOrDefault(f => f.Kind == values.Target);

    /// <summary>Points the controls at a scratch copy holding <paramref name="values"/>.</summary>
    private void Load(PresetValues values)
    {
        _scratch = new AppSettings { IsScratch = true };
        values.ApplyTo(_scratch);
        if (Chd is not null) Chd.PropertyChanged -= OnSectionChanged;
        if (Nintendo is not null) Nintendo.PropertyChanged -= OnSectionChanged;
        if (Archives is not null) Archives.PropertyChanged -= OnSectionChanged;
        Chd = new ChdSettingsViewModel(_scratch);
        Nintendo = new NintendoSettingsViewModel(_scratch);
        Archives = new ArchiveSettingsViewModel(_scratch);
        Chd.PropertyChanged += OnSectionChanged;
        Nintendo.PropertyChanged += OnSectionChanged;
        Archives.PropertyChanged += OnSectionChanged;
        RaiseAll();
    }

    private void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Some controls announce the change before writing it, so read it once they're done.
        if (_pulling) return;
        _pulling = true;
        Dispatcher.UIThread.Post(Pull);
    }

    /// <summary>Copies the controls' values into the row's Custom values.</summary>
    internal void Pull()
    {
        _pulling = false;
        var target = Values.Target;
        _row.CustomValues = PresetValues.From(_scratch, target);
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in new[] { nameof(SelectedFormat), nameof(IsChd), nameof(IsRvz), nameof(IsArchive), nameof(IsSevenZip), nameof(IsExtract),
                     nameof(IsUnpack), nameof(HasNothingToSet), nameof(UsesZstandard), nameof(CodecsText), nameof(ResetLabel), nameof(CanReset) })
            OnPropertyChanged(name);
    }
}
