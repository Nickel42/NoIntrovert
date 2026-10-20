using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>One system in the Target formats grid: its recommended target and your choice (FR-14).</summary>
public sealed partial class SystemTargetRow : ObservableObject
{
    private readonly TargetFormatSettingsViewModel _owner;
    private bool _loading;

    public SystemTargetRow(TargetFormatSettingsViewModel owner, SystemInfo system, string current)
    {
        _owner = owner;
        System = system;
        Options = TargetOverrides.Options(system);
        _loading = true;
        Choice = Options.FirstOrDefault(o => o.Id == current) ?? Options[0];
        _loading = false;
    }

    public SystemInfo System { get; }
    public string Name => System.Name;
    public string Recommended => $"Recommended: {System.Target.Label} ({System.Target.Container.ToString().ToLowerInvariant().Replace("sevenzip", "7z")})";
    public IReadOnlyList<TargetOption> Options { get; }

    /// <summary>Text the filter box searches: the name, id and file extensions.</summary>
    public string SearchText => $"{System.Name} {System.Id} {string.Join(" ", System.Extensions)}";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsOverridden))]
    public partial TargetOption Choice { get; set; }

    public bool IsOverridden => Choice.Id != TargetOverrides.Recommended;

    partial void OnChoiceChanged(TargetOption value)
    {
        if (_loading) return;
        // Alternatives the app can't make yet are shown, greyed out, but can't be chosen.
        if (!value.Producible) { Dispatch(() => Choice = Options[0]); return; }
        _owner.SetSystem(System.Id, value.Id);
    }

    [RelayCommand]
    private void Reset() => Choice = Options[0];

    internal void Load(string id)
    {
        _loading = true;
        Choice = Options.FirstOrDefault(o => o.Id == id) ?? Options[0];
        _loading = false;
    }

    private static void Dispatch(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a);
}

/// <summary>A file-type rule in the list: which system (or every one), which extension, which target.</summary>
public sealed record ExtensionRuleRow(TargetOverride Override)
{
    public string Text => $"{TargetOverrides.Describe(Override)} → {Label}";

    private string Label =>
        TargetOverrides.Options(Override.System == TargetOverrides.AllSystems ? null : TargetFormats.ById(Override.System))
            .FirstOrDefault(o => o.Id == Override.Target)?.Label ?? Override.Target;
}

/// <summary>
/// Settings → Target formats (FR-14): the systems grid, file-type rules, the
/// rules' options, and Reset all / Export / Import. Changes apply to new rows
/// on the Convert page and to the Library after a rescan.
/// </summary>
public sealed partial class TargetFormatSettingsViewModel : SettingsSectionViewModel
{
    private readonly IDialogs _dialogs;
    private readonly List<SystemTargetRow> _all;

    public TargetFormatSettingsViewModel(AppSettings settings, IDialogs dialogs) : base(settings)
    {
        _dialogs = dialogs;
        _all = TargetFormats.All.Select(s => new SystemTargetRow(this, s, CurrentFor(s.Id))).ToList();
        Systems = new ObservableCollection<SystemTargetRow>(_all);
        Rules = new ObservableCollection<ExtensionRuleRow>(ExtensionRules());
        RuleSystems = [new("*", "All systems"), .. TargetFormats.All.Select(s => new Choice<string>(s.Id, s.Name))];
        NewRuleSystem = RuleSystems[0];
    }

    public override string Title => "Target formats";

    public ObservableCollection<SystemTargetRow> Systems { get; }
    public ObservableCollection<ExtensionRuleRow> Rules { get; }

    [ObservableProperty] public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Systems.Clear();
        foreach (var row in _all.Where(r => words.All(w => r.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase))))
            Systems.Add(row);
    }

    public string OverrideSummary => S.TargetFormats.Overrides.Count switch
    {
        0 => "Every system uses its recommended target.",
        1 => "1 change from the recommended targets.",
        var n => $"{n} changes from the recommended targets.",
    };

    // ---- Systems and file-type rules ----

    private string CurrentFor(string system) =>
        S.TargetFormats.Overrides.FirstOrDefault(o => o.System == system && o.Extension is null)?.Target ?? TargetOverrides.Recommended;

    private IEnumerable<ExtensionRuleRow> ExtensionRules() =>
        S.TargetFormats.Overrides.Where(o => o.Extension is not null).Select(o => new ExtensionRuleRow(o));

    /// <summary>Sets (or, for "recommended", removes) the whole-system override.</summary>
    internal void SetSystem(string system, string target)
    {
        Change(s =>
        {
            s.TargetFormats.Overrides.RemoveAll(o => o.System == system && o.Extension is null);
            if (target != TargetOverrides.Recommended) s.TargetFormats.Overrides.Add(new TargetOverride(system, null, target));
        });
        OnPropertyChanged(nameof(OverrideSummary));
    }

    public IReadOnlyList<Choice<string>> RuleSystems { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(RuleTargets))]
    public partial Choice<string> NewRuleSystem { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AddRuleCommand))]
    public partial string NewRuleExtension { get; set; } = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AddRuleCommand))]
    public partial TargetOption? NewRuleTarget { get; set; }

    /// <summary>The targets a new rule can name: the chosen system's, or those that fit every system.</summary>
    public IReadOnlyList<TargetOption> RuleTargets =>
        TargetOverrides.Options(NewRuleSystem.Value == TargetOverrides.AllSystems ? null : TargetFormats.ById(NewRuleSystem.Value))
            .Where(o => o.Producible && o.Id != TargetOverrides.Recommended).ToList();

    partial void OnNewRuleSystemChanged(Choice<string> value) => NewRuleTarget = RuleTargets.FirstOrDefault();

    private bool CanAddRule => NewRuleTarget is not null && NewRuleExtension.Trim().TrimStart('.').Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddRule))]
    private void AddRule()
    {
        var ext = "." + NewRuleExtension.Trim().TrimStart('.').ToLowerInvariant();
        var rule = new TargetOverride(NewRuleSystem.Value, ext, NewRuleTarget!.Id);
        Change(s =>
        {
            s.TargetFormats.Overrides.RemoveAll(o => o.System == rule.System && o.Extension == ext);
            s.TargetFormats.Overrides.Add(rule);
        });
        ReloadRules();
        NewRuleExtension = "";
    }

    [RelayCommand]
    private void RemoveRule(ExtensionRuleRow row)
    {
        Change(s => s.TargetFormats.Overrides.Remove(row.Override));
        ReloadRules();
    }

    [RelayCommand]
    private void ResetAll()
    {
        Change(s => s.TargetFormats.Overrides.Clear());
        Reload();
    }

    private void ReloadRules()
    {
        Rules.Clear();
        foreach (var r in ExtensionRules()) Rules.Add(r);
        OnPropertyChanged(nameof(OverrideSummary));
    }

    private void Reload()
    {
        foreach (var row in _all) row.Load(CurrentFor(row.System.Id));
        ReloadRules();
    }

    // ---- Export and import (JSON in the TargetOverride shape) ----

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [ObservableProperty] public partial string TransferNote { get; set; } = "";

    [RelayCommand]
    private async Task Export()
    {
        var file = await _dialogs.SaveFileAsync("Export target overrides", "target-overrides.json", "JSON", "*.json");
        if (file is null) return;
        File.WriteAllText(file, JsonSerializer.Serialize(S.TargetFormats.Overrides, JsonOptions));
        TransferNote = $"Exported {S.TargetFormats.Overrides.Count} override(s).";
    }

    [RelayCommand]
    private async Task Import()
    {
        var file = await _dialogs.PickFileAsync("Import target overrides", "JSON", ["*.json"]);
        if (file is null) return;
        try { TransferNote = ImportFrom(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is IOException or JsonException) { TransferNote = $"Couldn't import: {ex.Message}"; }
    }

    /// <summary>Replaces the overrides with those in <paramref name="json"/>; ones the registry doesn't know are left out.</summary>
    public string ImportFrom(string json)
    {
        var imported = JsonSerializer.Deserialize<List<TargetOverride>>(json, JsonOptions) ?? [];
        var valid = imported.Where(TargetOverrides.IsValid).ToList();
        Change(s => s.TargetFormats.Overrides = valid);
        Reload();
        var skipped = imported.Count - valid.Count;
        return $"Imported {valid.Count} override(s)" + (skipped > 0 ? $"; {skipped} named a system or target this version doesn't know and were left out." : ".");
    }

    // ---- The rules' options ----

    public string MultiDiscFolder { get => S.TargetFormats.MultiDiscFolder; set => Set(value, (s, v) => s.TargetFormats.MultiDiscFolder = v); }
    public bool NormalizeHeaders { get => S.TargetFormats.NormalizeHeaders; set => Set(value, (s, v) => s.TargetFormats.NormalizeHeaders = v); }
    public bool RecordOriginalHashes { get => S.TargetFormats.RecordOriginalHashes; set => Set(value, (s, v) => s.TargetFormats.RecordOriginalHashes = v); }
    public bool WriteVitaLauncher { get => S.TargetFormats.WriteVitaLauncher; set => Set(value, (s, v) => s.TargetFormats.WriteVitaLauncher = v); }
    public string VitaLauncherExtension { get => S.TargetFormats.VitaLauncherExtension; set => Set(value, (s, v) => s.TargetFormats.VitaLauncherExtension = v); }
    public decimal DsBareAboveMb { get => S.TargetFormats.DsBareAboveMb; set => Set((int)value, (s, v) => s.TargetFormats.DsBareAboveMb = v); }
}
