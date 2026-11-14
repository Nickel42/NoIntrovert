using Avalonia.Controls;
using Avalonia.VisualTree;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// Row density (docs/requirements/ui-redesign.md, "Row density"), in the real window:
/// Compact rows are one line, Comfortable keeps the two-line layout, the list stays
/// virtualised, and a row off its recommended preset is marked.
/// </summary>
static class DensityTests
{
    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        List<ListBoxItem> Rows() => f.Window.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(i => i.IsEffectivelyVisible && i.DataContext is JobItemViewModel && i.FindAncestorOfType<JobListPanel>() is { IsEffectivelyVisible: true })
            .ToList();

        // A second line (folder, note under the preset, stage, speed) showing this text.
        bool Shows(string text, JobItemViewModel row) => f.Window.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.IsEffectivelyVisible && t.Text == text && t.DataContext == row && t.Classes.Contains("secondline"));

        test("Row density: Compact rows are one line (â‰¤ 36 px), Comfortable keeps the folder and the note under the preset; the list is virtualised", () => dispatch(() =>
        {
            var general = f.Vm.Settings.General;
            f.Resize(1180, 820);
            f.Show("convert");
            var tab = f.Vm.Convert;
            if (!f.Pump(() => tab.Items.Count > 0 && !tab.IsScanning)) return "no rows on Convert";
            try
            {
                if (!general.IsCompactRows) return "Compact isn't the default";
                f.Layout();
                var list = f.Find<ListBox>(l => l.Name == "List" && l.IsEffectivelyVisible)!;
                if (list.ItemsPanelRoot is not VirtualizingStackPanel) return $"the list's panel is {list.ItemsPanelRoot?.GetType().Name}, not virtualising";
                var rows = Rows();
                if (rows.Count == 0) return "no rows shown";
                if (rows.FirstOrDefault(r => r.Bounds.Height > 36) is { } tall) return $"a Compact row is {tall.Bounds.Height:F0} px tall";
                var row = (JobItemViewModel)rows[0].DataContext!;
                if (Shows(row.Folder, row)) return "Compact still shows the folder under the name";
                if (row.PresetNote.Length > 0 && Shows(row.PresetNote, row)) return $"Compact still shows \"{row.PresetNote}\" under the preset";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "density-compact.png"));
                var compact = LayoutTests.Check(f.Window);
                if (compact.Count > 0) return "Compact: " + string.Join("\n        ", compact.Take(6));

                general.IsComfortableRows = true;
                f.Layout();
                rows = Rows();
                if (rows.FirstOrDefault(r => r.Bounds.Height < 50) is { } short_) return $"a Comfortable row is only {short_.Bounds.Height:F0} px tall";
                row = (JobItemViewModel)rows[0].DataContext!;
                if (!Shows(row.Folder, row)) return "Comfortable doesn't show the folder";
                if (row.PresetNote.Length > 0 && !Shows(row.PresetNote, row)) return "Comfortable doesn't show the note under the preset";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "density-comfortable.png"));
                var comfortable = LayoutTests.Check(f.Window);
                return comfortable.Count == 0 ? null : "Comfortable: " + string.Join("\n        ", comfortable.Take(6));
            }
            finally
            {
                general.IsCompactRows = true;
                f.Layout();
            }
        }));

        test("Row density: a row off its system's recommended (â˜…) preset shows an accent dot", () => dispatch(() =>
        {
            f.Resize(1180, 820);
            f.Show("convert");
            var tab = f.Vm.Convert;
            if (!f.Pump(() => tab.Items.Count > 0 && !tab.IsScanning)) return "no rows on Convert";
            var row = tab.Items.FirstOrDefault(i => i.HasPresets && i.RecommendedPreset is not null && i.Preset?.Id == i.RecommendedPreset.Id
                && i.PresetOptions.Any(o => o.IsPreset && o.IsEnabled && !o.Preset!.IsCustom && o.Preset.Id != i.RecommendedPreset.Id));
            if (row is null) return "no row on its recommended preset with another to choose";
            var before = row.PresetChoice;
            try
            {
                var list = f.Find<ListBox>(l => l.Name == "List" && l.IsEffectivelyVisible)!;
                list.ScrollIntoView(row);
                f.Layout();
                Avalonia.Controls.Shapes.Ellipse? Dot() => f.Window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().FirstOrDefault(e => e.Classes.Contains("notrecommended") && e.DataContext == row);
                if (Dot() is { IsEffectivelyVisible: true }) return "the recommended preset is marked";
                row.PresetChoice = row.PresetOptions.First(o => o.IsPreset && o.IsEnabled && !o.Preset!.IsCustom && o.Preset.Id != row.RecommendedPreset!.Id);
                f.Layout();
                if (Dot() is not { IsEffectivelyVisible: true }) return $"{row.PresetLabel} isn't marked";
                return row.PresetTip.Contains("Not the recommended preset") ? null : $"the tooltip: \"{row.PresetTip}\"";
            }
            finally
            {
                row.PresetChoice = before;
                f.Layout();
            }
        }));
    }
}
