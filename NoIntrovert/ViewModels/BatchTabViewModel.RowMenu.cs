using NoIntrovert.Core;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The job list's row menu (right-click, Shift+F10 or the Menu key): Open file location,
/// Copy path, Verify (on the pages that aren't Verify), Preset ▸ (Convert) and Remove from list.
/// Every item acts on the selected rows; what changes the list waits for the run to finish,
/// as the toolbar does.
/// </summary>
public abstract partial class BatchTabViewModel
{
    /// <summary>Runs Verify &amp; match on these files on the Verify page (the main view model sets it on the pages that aren't Verify).</summary>
    public Func<IReadOnlyList<string>, Task>? VerifyFilesRequested { get; set; }

    /// <summary>Whether the Verify page can take a run now (the main view model sets it with <see cref="VerifyFilesRequested"/>).</summary>
    public Func<bool>? VerifyFilesAvailable { get; set; }

    /// <summary>The menu has Verify: every page but Verify (and the Library tests).</summary>
    public bool OffersVerify => VerifyFilesRequested is not null;

    /// <summary>Open file location and Copy path: always, with a row selected.</summary>
    public bool CanRevealSelection => SelectedCount > 0;

    /// <summary>Verify: with rows selected, while neither this page nor Verify is running.</summary>
    public bool CanVerifySelection => OffersVerify && SelectedCount > 0 && IsIdle && !IsScanning && (VerifyFilesAvailable?.Invoke() ?? true);

    /// <summary>Remove from list: as the toolbar's Remove, not during a run.</summary>
    public bool CanRemoveSelection => SelectedCount > 0 && IsIdle;

    /// <summary>The menu has Preset ▸: Convert, on a row with presets.</summary>
    public bool OffersPresets(JobItemViewModel? row) => ShowsTargets && row is { HasPresets: true };

    /// <summary>Preset ▸ can change the selected rows: not during a run.</summary>
    public bool CanChoosePreset => SelectedCount > 0 && IsIdle;

    /// <summary>The selected rows' paths, one per line, for the clipboard.</summary>
    public string SelectedPathsText => string.Join(Environment.NewLine, Selected.Select(i => i.Path));

    public Task RevealSelectedAsync() => SelectedCount == 0 ? Task.CompletedTask : Dialogs.RevealAsync(Selected.Select(i => i.Path).ToList());

    public Task CopySelectedPathsAsync() => SelectedCount == 0 ? Task.CompletedTask : Dialogs.SetClipboardAsync(SelectedPathsText);

    /// <summary>Verifies the selected rows' files on the Verify page; this page stays shown.</summary>
    public async Task VerifySelectedAsync()
    {
        if (!CanVerifySelection || VerifyFilesRequested is not { } verify) return;
        var paths = Selected.Select(i => i.Path).ToList();
        Log.Info($"{Title}: verifying {Words.Count(paths.Count, "file")} on the Verify page");
        await verify(paths);
    }

    /// <summary>
    /// Puts every selected row that can use it on the preset <paramref name="presetId"/>
    /// (a row that can't is left as it was). Returns how many rows changed.
    /// </summary>
    public int ApplyPresetToSelected(string presetId)
    {
        if (!CanChoosePreset) return 0;
        var changed = 0;
        foreach (var row in Selected)
        {
            if (row.Preset?.Id == presetId || row.OptionFor(presetId) is not { } option) continue;
            row.PresetChoice = option;
            changed++;
        }
        return changed;
    }
}
