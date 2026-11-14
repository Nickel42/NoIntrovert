using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// The job list's toolbar, CHANGES column and empty state (UX review R4, R5, R8), in the real window:
/// Add ▾ · Remove · ⋯ and the filter on one line; CHANGES leading with where each file ends up;
/// an empty list as a drop zone with its buttons, lit while files are dragged over the window.
/// </summary>
static class JobListTests
{
    /// <summary>The visible items of the shown page's Add ▾ menu, by header (the menu is opened and closed to read them).</summary>
    public static List<string> AddMenu(LayoutTests.Fixture f) => Menu(f, "AddMenuButton");

    /// <summary>The visible items of the menu on the shown page's toolbar button <paramref name="button"/>.</summary>
    public static List<string> Menu(LayoutTests.Fixture f, string button) => MenuItems(f, button).Select(i => i.Header as string ?? "").ToList();

    private static List<MenuItem> MenuItems(LayoutTests.Fixture f, string button)
    {
        if (f.Find<Button>(b => b.Name == button && b.IsEffectivelyVisible) is not { Flyout: MenuFlyout menu } owner) return [];
        // The headless window can't open a popup: the items get the page as an open menu would give it them.
        var items = menu.Items.OfType<MenuItem>().ToList();
        foreach (var item in items) item.DataContext = owner.DataContext;
        f.Layout();
        return items.Where(i => i.IsVisible).ToList();
    }

    /// <summary>A theme brush's colour as <paramref name="control"/> sees it (the brushes are per theme variant).</summary>
    private static Color? Brush(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var value) && value is ISolidColorBrush brush ? brush.Color : null;

    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        Rect In(Control c) => new(c.TranslatePoint(new Point(0, 0), f.Window)!.Value, c.Bounds.Size);

        test("Toolbar: Add ▾, Remove, ⋯ and the filter sit on one line on every page and at every size (Convert at 1180 px too)", () => dispatch(() =>
        {
            foreach (var (width, height) in LayoutTests.Sizes)
                foreach (var page in new[] { "optimize", "convert", "verify", "cleanup" })
                {
                    f.Resize(width, height);
                    f.Show(page);
                    f.Layout();
                    var controls = new Control?[]
                    {
                        f.Find<Button>(b => b.Name == "AddButton" && b.IsEffectivelyVisible),
                        f.Find<Button>(b => b.Name == "AddMenuButton" && b.IsEffectivelyVisible),
                        f.Find<Button>(b => b.Name == "RemoveButton" && b.IsEffectivelyVisible),
                        f.Find<Button>(b => b.Name == "MoreButton" && b.IsEffectivelyVisible),
                        f.Find<TextBox>(t => t.Name == "FilterBox" && t.IsEffectivelyVisible),
                    };
                    if (controls.Any(c => c is null)) return $"{page} {width}×{height}: the toolbar is missing a control";
                    var rects = controls.Select(c => In(c!)).ToList();
                    var middle = rects.Select(r => r.Center.Y).ToList();
                    if (middle.Max() - middle.Min() > 2) return $"{page} {width}×{height}: the toolbar is on more than one line ({string.Join(", ", middle.Select(m => m.ToString("F0")))})";
                    for (var i = 1; i < rects.Count; i++)
                        if (rects[i].Left < rects[i - 1].Right - 2) return $"{page} {width}×{height}: {controls[i]!.Name} overlaps {controls[i - 1]!.Name}";
                    if (page == "convert" && width == 1180 && screenshots is not null) f.Save(System.IO.Path.Combine(screenshots, "joblist-toolbar-convert-1180.png"));
                    var problems = LayoutTests.Check(f.Window);
                    if (problems.Count > 0) return $"{page} {width}×{height}: " + string.Join("\n        ", problems.Take(6));
                }
            return null;
        }));

        test("Toolbar: Add ▾ adds files; its menu has Add files…, Add folder… and, on Convert only, Add folder as one archive…; ⋯ has Clear finished and Clear all", () => dispatch(() =>
        {
            f.Resize(1180, 820);
            foreach (var page in new[] { "optimize", "convert", "verify", "cleanup" })
            {
                f.Show(page);
                f.Layout();
                var tab = (BatchTabViewModel)f.Vm.SelectedPage.Content;
                if (f.Find<Button>(b => b.Name == "AddButton" && b.IsEffectivelyVisible) is not { } add) return $"{page}: no Add button";
                if (add.Command != tab.AddFilesCommand) return $"{page}: Add doesn't add files";
                if (Avalonia.Automation.AutomationProperties.GetName(add) != "Add files") return $"{page}: Add's accessible name is \"{Avalonia.Automation.AutomationProperties.GetName(add)}\"";
                if (!(ToolTip.GetTip(add) as string ?? "").Contains("Ctrl+O")) return $"{page}: Add's tooltip doesn't name Ctrl+O";
                var want = page == "convert" ? new[] { "Add files…", "Add folder…", "Add folder as one archive…" } : ["Add files…", "Add folder…"];
                var items = MenuItems(f, "AddMenuButton");
                var names = items.Select(i => i.Header as string).ToList();
                if (!names.SequenceEqual(want)) return $"{page}: the Add ▾ menu has {string.Join(", ", names)}";
                if (items[0].Command != tab.AddFilesCommand || items[1].Command != tab.AddFolderCommand)
                    return $"{page}: the Add ▾ menu's items don't add files and a folder ({items[0].Command?.GetType().Name}, {items[1].Command?.GetType().Name})";
                if (items[0].InputGesture?.ToString() != "Ctrl+O") return $"{page}: Add files… doesn't show Ctrl+O";
                var more = MenuItems(f, "MoreButton");
                if (!more.Select(i => i.Header as string).SequenceEqual(["Clear finished", "Clear all"])) return $"{page}: ⋯ has {string.Join(", ", more.Select(i => i.Header))}";
                if (more[0].Command != tab.ClearFinishedCommand || more[1].Command != tab.ClearAllCommand) return $"{page}: ⋯'s items don't clear";
            }
            return null;
        }));

        test("CHANGES: Optimize and Organize lead with where each file ends up; steps are chips (under it in Comfortable, in the tooltip in Compact)", () => dispatch(() =>
        {
            var general = f.Vm.Settings.General;
            f.Resize(1180, 820);
            try
            {
                foreach (var (page, tab) in new (string, PlanTabViewModel)[] { ("optimize", f.Vm.Optimize), ("cleanup", f.Vm.Cleanup) })
                {
                    f.Show(page);
                    tab.Replan();
                    if (!f.Pump(() => tab.Items.Count > 0 && !tab.IsScanning && tab.PlanMade.IsCompleted && !tab.IsPlanning && tab.Items.All(i => i.ChangesTarget.Length > 0)))
                        return $"{page}: the rows never got their changes ({tab.Items.Count(i => i.ChangesTarget.Length == 0)} of {tab.Items.Count} without, e.g. {tab.Items.FirstOrDefault(i => i.ChangesTarget.Length == 0)?.Name}; planning {tab.IsPlanning}, \"{tab.PlanSummary}\")";
                    foreach (var compact in new[] { true, false })
                    {
                        if (compact) general.IsCompactRows = true;
                        else general.IsComfortableRows = true;
                        f.Layout();
                        var density = compact ? "compact" : "comfortable";
                        foreach (var row in tab.VisibleItems.Take(6))
                        {
                            if (row.ChangesTarget.StartsWith("Verify") || row.ChangesTarget.Contains(" → ")) return $"{page}: {row.Name} leads with its steps: \"{row.ChangesTarget}\"";
                            var cell = f.Find<TextBlock>(t => t.Classes.Contains("changestarget") && t.DataContext == row && t.IsEffectivelyVisible);
                            if (cell is null) continue; // not in view
                            if (cell.Text != row.ChangesTarget) return $"{page}: {row.Name}'s cell reads \"{cell.Text}\"";
                            if (row.IsChangesPath && cell.TextTrimming != TextTrimming.LeadingCharacterEllipsis) return $"{page}: {row.Name}'s path is trimmed at its end";
                            if (row.IsChangesMuted && Brush(cell, "SubtleText") is { } subtle && (cell.Foreground as ISolidColorBrush)?.Color != subtle)
                                return $"{page}: {row.Name} is left alone but not muted";
                            var chips = f.Window.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(i => i.Classes.Contains("stepchips") && i.DataContext == row);
                            var shown = chips?.IsEffectivelyVisible ?? false;
                            if (compact && shown) return $"{page}: Compact shows {row.Name}'s steps under its changes";
                            if (!compact && row.HasChangesSteps && !shown) return $"{page}: Comfortable doesn't show {row.Name}'s steps";
                        }
                        if (compact)
                        {
                            var tall = f.Window.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible && i.DataContext is JobItemViewModel && tab.Items.Contains((JobItemViewModel)i.DataContext!))
                                .FirstOrDefault(i => i.Bounds.Height > 36);
                            if (tall is not null) return $"{page}: a Compact row is {tall.Bounds.Height:F0} px tall";
                        }
                        if (screenshots is not null) f.Save(System.IO.Path.Combine(screenshots, $"joblist-changes-{page}-{density}.png"));
                        var problems = LayoutTests.Check(f.Window);
                        if (problems.Count > 0) return $"{page} ({density}): " + string.Join("\n        ", problems.Take(6));
                    }
                    general.IsCompactRows = true;
                }
                // A clash: the warning colour and glyph. A planned path: trimmed at its start, so the file name shows.
                var clashRow = f.Vm.Cleanup.Items[0];
                var pathRow = f.Vm.Cleanup.Items[2];
                var was = (clashRow.ChangesTarget, clashRow.ChangesKind, clashRow.ChangesSteps, clashRow.ChangesDetails);
                var pathWas = (pathRow.ChangesTarget, pathRow.ChangesKind, pathRow.ChangesSteps, pathRow.ChangesDetails);
                try
                {
                    f.Show("cleanup");
                    clashRow.SetChanges(new RowChanges("Clash: Tetris (USA).nes already exists", ChangesKind.Clash, []), "Tetris (USA).nes already exists: nothing is overwritten");
                    pathRow.SetChanges(new RowChanges("roms/nes/Castlevania III - Dracula's Curse (USA).nes", ChangesKind.Planned, ["Rename", "Move"]), "Moved into roms/nes");
                    f.Find<ListBox>(l => l.Name == "List" && l.IsEffectivelyVisible)?.ScrollIntoView(clashRow);
                    f.Layout();
                    var cell = f.Find<TextBlock>(t => t.Classes.Contains("changestarget") && t.DataContext == clashRow && t.IsEffectivelyVisible);
                    if (cell is null || !cell.Classes.Contains("warning")) return "a clash isn't drawn as a warning";
                    if (Brush(cell, "WarningText") is not { } warning || (cell.Foreground as ISolidColorBrush)?.Color != warning) return "a clash isn't in the warning colour";
                    if (f.Find<Avalonia.Controls.Shapes.Path>(p => p.Classes.Contains("clashglyph") && p.DataContext == clashRow && p.IsEffectivelyVisible) is null) return "a clash has no warning glyph";
                    var path = f.Find<TextBlock>(t => t.Classes.Contains("changestarget") && t.DataContext == pathRow && t.IsEffectivelyVisible);
                    if (path is null || path.TextTrimming != TextTrimming.LeadingCharacterEllipsis) return "a planned path isn't trimmed at its start";
                    if (path.Classes.Contains("muted") || path.Classes.Contains("warning")) return "a planned path is drawn muted or as a warning";
                    if (screenshots is not null) f.Save(System.IO.Path.Combine(screenshots, "joblist-changes-clash.png"));
                }
                finally
                {
                    clashRow.SetChanges(new RowChanges(was.ChangesTarget, was.ChangesKind, was.ChangesSteps), was.ChangesDetails);
                    pathRow.SetChanges(new RowChanges(pathWas.ChangesTarget, pathWas.ChangesKind, pathWas.ChangesSteps), pathWas.ChangesDetails);
                }
                return null;
            }
            finally
            {
                general.IsCompactRows = true;
                f.Layout();
            }
        }));

        test("Empty list: a drop zone with Add files…, Add folder… and What can I add?, lit while files are dragged over; gone once there are rows", () => dispatch(() =>
        {
            // Library tests: a page whose list starts empty (it doesn't share the other pages' files).
            var tab = f.Vm.Tests;
            f.Resize(1180, 820);
            f.Show("tests");
            f.Layout();
            if (tab.Items.Count > 0) return "the Library tests page isn't empty";
            try
            {
                if (f.Find<Panel>(p => p.Name == "DropZone" && p.IsEffectivelyVisible) is not { } zone) return "no drop zone on an empty list";
                if (f.Find<TextBlock>(t => t.Name == "DropHintText" && t.IsEffectivelyVisible)?.Text != tab.DropHint) return "the drop zone doesn't say what to drop";
                if (f.Find<Button>(b => b.Name == "DropAddFiles" && b.IsEffectivelyVisible) is not { } files || files.Command != tab.AddFilesCommand) return "no Add files… in the drop zone";
                if (f.Find<Button>(b => b.Name == "DropAddFolder" && b.IsEffectivelyVisible) is not { } folder || folder.Command != tab.AddFolderCommand) return "no Add folder… in the drop zone";
                if (f.Find<TextBlock>(t => t.Name == "AddHelpText")?.IsEffectivelyVisible == true) return "the long hint shows before What can I add? is clicked";
                if (f.Find<TextBlock>(t => t.Text == "FILE" && t.IsEffectivelyVisible) is not null) return "the column headings show over an empty list";
                var zoneRect = In(zone);
                var list = f.Find<ListBox>(l => l.Name == "List" && l.IsEffectivelyVisible)!;
                if (zoneRect.Height < In(list).Height * 0.8) return $"the drop zone is {zoneRect.Height:F0} px high in a {In(list).Height:F0} px list";
                if (screenshots is not null) f.Save(System.IO.Path.Combine(screenshots, "joblist-empty.png"));
                tab.ToggleAddHelpCommand.Execute(null);
                f.Layout();
                if (f.Find<TextBlock>(t => t.Name == "AddHelpText")?.IsEffectivelyVisible != true) return "What can I add? doesn't show the long hint";
                var problems = LayoutTests.Check(f.Window);
                if (problems.Count > 0) return "with the hint open: " + string.Join("\n        ", problems.Take(6));
                var panel = zone.FindAncestorOfType<JobListPanel>()!;
                var frame = zone.GetVisualDescendants().OfType<Rectangle>().First(r => r.Classes.Contains("dropframe"));
                var resting = (frame.Stroke as ISolidColorBrush)?.Color;
                panel.SetDragOver(true);
                f.Layout();
                if (!tab.IsDragOver) return "dragging files over the window didn't reach the page";
                var lit = (frame.Stroke as ISolidColorBrush)?.Color;
                if (Brush(frame, "AccentBrush") is not { } accent || lit != accent || lit == resting)
                    return $"the drop zone's frame is {lit} while files are dragged over (at rest {resting}), not the accent {Brush(frame, "AccentBrush")}";
                if (screenshots is not null) f.Save(System.IO.Path.Combine(screenshots, "joblist-empty-dragover.png"));
                panel.SetDragOver(false);
                tab.Items.Add(new JobItemViewModel(System.IO.Path.Combine(f.LibraryFolder, "NES", "Tetris (USA).nes")));
                f.Layout();
                if (zone.IsEffectivelyVisible) return "the drop zone still shows with a row in the list";
                tab.Items.Clear();
                f.Layout();
                return zone.IsEffectivelyVisible ? null : "the drop zone didn't come back when the list emptied";
            }
            finally
            {
                tab.ShowsAddHelp = false;
                tab.IsDragOver = false;
                tab.Items.Clear();
                f.Show("convert");
                f.Layout();
            }
        }));
    }
}
