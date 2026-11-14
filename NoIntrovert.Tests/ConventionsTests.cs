using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using NoIntrovert.Core.Presets;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// Windows conventions (review Q10): the row menu on the job list, the page shortcuts and their
/// tooltips, Windows 11 caption buttons and the Keyboard shortcuts sheet. Nothing here reveals a
/// file: that would open a real Explorer window.
/// </summary>
internal static class ConventionsTests
{
    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch)
    {
        test("Shortcuts: Ctrl+1…4 and Ctrl+, map to the pages (Cmd too), and the rail's tooltips say so", () =>
        {
            (Key Key, KeyModifiers Mods, string? Page)[] cases =
            [
                (Key.D1, KeyModifiers.Control, "optimize"), (Key.D2, KeyModifiers.Control, "convert"),
                (Key.NumPad3, KeyModifiers.Control, "verify"), (Key.D4, KeyModifiers.Meta, "cleanup"),
                (Key.OemComma, KeyModifiers.Control, "settings"), (Key.D2, KeyModifiers.None, null),
                (Key.D2, KeyModifiers.Control | KeyModifiers.Shift, null), (Key.D5, KeyModifiers.Control, null),
            ];
            foreach (var (key, mods, page) in cases)
                if (PageShortcuts.PageFor(key, mods) != page) return $"{mods}+{key} opens {PageShortcuts.PageFor(key, mods) ?? "nothing"}, not {page ?? "nothing"}";
            if (PageShortcuts.LabelFor("convert", macOS: false) != "Ctrl+2" || PageShortcuts.LabelFor("settings", macOS: true) != "⌘,")
                return "the shortcut labels are wrong";
            var convert = f.Vm.Pages.First(p => p.Id == "convert");
            var tests = f.Vm.Pages.First(p => p.Id == "tests");
            if (convert.ToolTipText != (OperatingSystem.IsMacOS() ? "Convert (⌘2)" : "Convert (Ctrl+2)")) return $"Convert's tooltip is \"{convert.ToolTipText}\"";
            return tests.ToolTipText == "Library tests" ? null : $"Library tests' tooltip is \"{tests.ToolTipText}\"";
        });

        test("Shortcuts: Ctrl+2, Ctrl+3 and Ctrl+, switch pages in the window, even from the filter box", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("optimize");
            f.Layout();
            f.Window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Control);
            f.Layout();
            if (f.Vm.SelectedPage.Id != "convert") return $"Ctrl+2 showed {f.Vm.SelectedPage.Id}";
            var filter = f.Find<TextBox>(t => t.Name == "FilterBox" && t.IsEffectivelyVisible);
            filter?.Focus();
            f.Window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Control);
            f.Layout();
            if (f.Vm.SelectedPage.Id != "verify") return $"Ctrl+3 from the filter box showed {f.Vm.SelectedPage.Id}";
            f.Window.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
            f.Layout();
            var id = f.Vm.SelectedPage.Id;
            f.Show("convert");
            return id == "settings" ? null : $"Ctrl+, showed {id}";
        }));

        test("Shortcuts: F1 opens the Keyboard shortcuts sheet, which lists the page keys", () => dispatch(() =>
        {
            f.Show("convert");
            f.Layout();
            f.Window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);
            if (!f.Pump(() => f.Window.OpenShortcuts is { IsVisible: true })) return "F1 didn't open the sheet";
            var sheet = f.Window.OpenShortcuts!;
            var texts = sheet.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            sheet.Close();
            f.Pump(() => f.Window.OpenShortcuts is null);
            var key = OperatingSystem.IsMacOS() ? "⌘2" : "Ctrl+2";
            return texts.Contains(key) && texts.Contains("Convert") ? null : $"the sheet doesn't list {key} Convert";
        }));

        test("Caption buttons: on Windows 46×32 rectangles, transparent at rest, flush with the top-right corner", () => dispatch(() =>
        {
            if (!MainWindow.UsesWindowsCaption) return f.Window.Classes.Contains("wincaption") ? "Windows caption styles off Windows" : null;
            f.Resize(1366, 768);
            if (!f.Window.Classes.Contains("wincaption")) return "the window has no .wincaption class";
            var buttons = f.Window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("WindowControlsButton") && b.IsEffectivelyVisible).ToList();
            var close = buttons.FirstOrDefault(b => b.Name == "PART_CloseButton");
            if (close is null || buttons.Count < 4) return $"found {buttons.Count} caption buttons";
            foreach (var b in buttons)
            {
                if (Math.Abs(b.Bounds.Width - 46) > 0.5 || Math.Abs(b.Bounds.Height - 32) > 0.5) return $"{b.Name ?? "theme"} is {b.Bounds.Width}×{b.Bounds.Height}";
                if (b.Background is ISolidColorBrush { Color.A: > 0 }) return $"{b.Name ?? "theme"} isn't transparent at rest";
            }
            var corner = close.TranslatePoint(new Avalonia.Point(close.Bounds.Width, 0), f.Window);
            return corner is { } p && Math.Abs(p.X - f.Window.ClientSize.Width) < 1 && Math.Abs(p.Y) < 1 ? null : $"Close's top-right corner is at {corner}";
        }));

        test("Row menu: Open file location, Copy path, Verify, Preset ▸ and Remove, disabled as the toolbar is during a run", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("convert");
            f.Layout();
            var tab = f.Vm.Convert;
            var list = VisibleList(f);
            var row = tab.Items.First(i => i.HasPresets);
            list.SelectedItems!.Clear();
            list.SelectedItems.Add(row);
            f.Layout();
            var items = Named(JobListPanel.RowMenuItems(tab, row));
            foreach (var name in new[] { "reveal", "copypath", "verify", "presets", "remove" })
                if (!items.TryGetValue(name, out var item) || !item.IsEnabled) return $"Convert's menu has no enabled {name}";
            if ((string?)items["verify"].Header != "Verify this file") return $"Verify reads \"{items["verify"].Header}\"";
            if (tab.SelectedPathsText != row.Path) return $"Copy path copies \"{tab.SelectedPathsText}\"";
            var presetCount = Named(JobListPanel.RowMenuItems(tab, row))["presets"].Items.OfType<MenuItem>().Count(m => m.ToggleType == MenuItemToggleType.Radio);
            if (presetCount != row.PresetOptions.Count(o => o.IsPreset)) return $"Preset ▸ lists {presetCount} of {row.PresetOptions.Count(o => o.IsPreset)} presets";

            tab.IsRunning = true;
            try
            {
                items = Named(JobListPanel.RowMenuItems(tab, row));
                if (items["verify"].IsEnabled || items["remove"].IsEnabled || items["presets"].IsEnabled) return "Verify, Remove or Preset is enabled during a run";
                if (!items["reveal"].IsEnabled || !items["copypath"].IsEnabled) return "Open file location or Copy path is disabled during a run";
            }
            finally { tab.IsRunning = false; }

            // Verify's own page has no Verify item, and no presets.
            var verify = f.Vm.Verify;
            f.Show("verify");
            f.Layout();
            var vlist = VisibleList(f);
            var vrow = verify.Items[0];
            vlist.SelectedItems!.Clear();
            vlist.SelectedItems.Add(vrow);
            var vitems = Named(JobListPanel.RowMenuItems(verify, vrow));
            vlist.SelectedItems.Clear();
            list.SelectedItems.Clear();
            f.Show("convert");
            return vitems.ContainsKey("verify") || vitems.ContainsKey("presets") ? "Verify's menu offers Verify or Preset" : null;
        }));

        test("Row menu: right-click keeps a multi-selection, selects only an unselected row, and Preset ▸ sets every selected row", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("convert");
            f.Layout();
            var tab = f.Vm.Convert;
            var panel = f.Find<JobListPanel>(p => p.IsEffectivelyVisible)!;
            var list = VisibleList(f);
            var nes = tab.Items.Where(i => i.HasPresets && i.Path.EndsWith(".nes", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            if (nes.Count < 3) return "fewer than 3 NES rows";
            var before = nes.Select(r => r.PresetChoice).ToList();
            try
            {
                list.SelectedItems!.Clear();
                list.SelectedItems.Add(nes[0]);
                list.SelectedItems.Add(nes[1]);
                f.Layout();
                list.ScrollIntoView(nes[1]);
                f.Layout();
                if (list.ContainerFromItem(nes[1]) is not ListBoxItem selected) return "no container for a selected row";
                panel.SelectForMenu(tab, selected, nes[1]);
                f.Layout();
                if (tab.SelectedCount != 2 || tab.FocusedItem != nes[1]) return $"right-click on a selected row left {tab.SelectedCount} selected";
                var verifyHeader = Named(JobListPanel.RowMenuItems(tab, nes[1]))["verify"].Header as string;
                if (verifyHeader != "Verify 2 files") return $"Verify reads \"{verifyHeader}\" for two rows";

                // Preset ▸ Leave as is: both selected rows change, the third doesn't.
                var changed = tab.ApplyPresetToSelected(FormatPreset.LeaveId);
                if (changed != 2 || nes[0].Preset?.IsLeave != true || nes[1].Preset?.IsLeave != true || nes[2].Preset?.IsLeave == true)
                    return $"Preset ▸ Leave as is changed {changed} rows";

                list.ScrollIntoView(nes[2]);
                f.Layout();
                if (list.ContainerFromItem(nes[2]) is not ListBoxItem other) return "no container for an unselected row";
                panel.SelectForMenu(tab, other, nes[2]);
                f.Layout();
                return tab.SelectedCount == 1 && tab.Selected[0] == nes[2] && tab.FocusedItem == nes[2]
                    ? null : $"right-click on an unselected row left {tab.SelectedCount} selected";
            }
            finally
            {
                for (var i = 0; i < nes.Count; i++) if (before[i] is { } choice) nes[i].PresetChoice = choice;
                list.SelectedItems?.Clear();
                tab.FocusedItem = null;
                f.Layout();
            }
        }));
    }

    private static ListBox VisibleList(LayoutTests.Fixture f) =>
        f.Window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "List" && l.IsEffectivelyVisible);

    private static Dictionary<string, MenuItem> Named(IEnumerable<Control> items) =>
        items.OfType<MenuItem>().Where(m => m.Name is not null).ToDictionary(m => m.Name!);
}
