using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// The 2026-11-12 review's page fixes, checked in the real window (headless):
/// destructive questions default to the safe button, the Cleanup page shows
/// when it's busy, the Library's actions need a selection, the shortcuts
/// work, log levels show as glyphs, and inputs have names for screen readers.
/// </summary>
static class UxLayoutTests
{
    public static void Run(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch)
    {
        test("Dialogs: a destructive question has the safe button as default, with focus, and Enter picks it", () => dispatch(() =>
        {
            string? Ask(bool destructive, bool expected)
            {
                var dialog = new ConfirmDialog("Empty this run's trash?", "2 files are deleted permanently.", "Delete permanently", "Cancel", destructive);
                var answer = dialog.ShowDialog<bool>(f.Window);
                if (!f.Pump(() => dialog.IsVisible)) return "the dialog didn't open";
                var safe = destructive ? dialog.NoButton : dialog.YesButton;
                if (!safe.IsDefault || (destructive ? dialog.YesButton : dialog.NoButton).IsDefault)
                    return destructive ? "the destructive button is the default" : "a plain question's yes isn't the default";
                if (destructive && !dialog.YesButton.Classes.Contains("danger")) return "the destructive button isn't drawn as a danger button";
                if (dialog.FocusManager?.GetFocusedElement() != safe) return $"focus is on {dialog.FocusManager?.GetFocusedElement()}, not the default button";
                dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                if (!f.Pump(() => answer.IsCompleted)) { dialog.Close(false); return "Enter didn't close the dialog"; }
                return answer.Result == expected ? null : $"Enter answered {answer.Result}";
            }
            return Ask(destructive: true, expected: false) ?? Ask(destructive: false, expected: true);
        }));

        test("Dialogs: closing during a run talks about the user's files, not the library", () =>
            MainWindow.CloseMessage.Contains("lib-chd-kit") || !MainWindow.CloseMessage.Contains("kept") ? $"'{MainWindow.CloseMessage}'" : null);

        test("Cleanup: while busy the page shows progress and what it's doing, in place of the empty message", () => dispatch(() =>
        {
            f.Resize(1180, 820);
            f.Show("cleanup");
            var cleanup = f.Vm.Cleanup;
            cleanup.IsBusy = true;
            cleanup.BusyText = "Making the plan…";
            try
            {
                f.Layout();
                var status = f.Find<StackPanel>(p => p.Name == "BusyStatus");
                if (status is not { IsEffectivelyVisible: true }) return "no busy line in the run bar";
                if (f.Find<TextBlock>(t => t.Text == "Making the plan…" && t.IsEffectivelyVisible) is null) return "the busy text doesn't show";
                var hint = f.Find<TextBlock>(t => t.Text?.StartsWith("Choose games on the Library page") == true);
                if (hint is { IsEffectivelyVisible: true }) return "the empty-page message shows while planning";
                var problems = LayoutTests.Check(f.Window);
                return problems.Count == 0 ? null : string.Join("\n        ", problems.Take(6));
            }
            finally
            {
                cleanup.IsBusy = false;
                f.Layout();
            }
        }));

        test("Library: Convert, Verify and Clean up selected are off with no game selected", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("library");
            f.Layout();
            var lib = f.Vm.Library;
            var list = f.Find<ListBox>(l => l.Name == "GameList");
            if (list is null) return "no game list";
            list.SelectedItems?.Clear();
            lib.SelectedGame = null;
            f.Layout();
            var buttons = new[] { "Convert selected", "Verify selected", "Clean up selected" }
                .Select(name => f.Find<Button>(b => b.Content as string == name)).ToList();
            if (buttons.Any(b => b is null)) return "an action button is missing";
            if (buttons.FirstOrDefault(b => b!.IsEffectivelyEnabled) is { } on) return $"'{on.Content}' is on with nothing selected";
            list.SelectedItems?.Add(lib.VisibleGames[0]);
            f.Layout();
            var off = buttons.FirstOrDefault(b => !b!.IsEffectivelyEnabled);
            list.SelectedItems?.Clear();
            lib.SelectedGame = null;
            return off is null ? null : $"'{off.Content}' stays off with a game selected";
        }));

        test("Shortcuts: Ctrl+F finds and Escape clears the filter; Enter on the game list converts the selection", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("convert");
            f.Layout();
            var filter = f.Find<TextBox>(t => t.Name == "FilterBox" && t.IsEffectivelyVisible);
            if (filter is null) return "no filter box";
            f.Window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            f.Layout();
            if (!filter.IsFocused) return "Ctrl+F didn't focus Convert's filter";
            filter.Text = "zzz";
            f.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            f.Layout();
            if (!string.IsNullOrEmpty(filter.Text)) return "Escape didn't clear the filter";

            f.Show("library");
            f.Layout();
            var search = f.Find<TextBox>(t => t.Name == "SearchBox" && t.IsEffectivelyVisible);
            if (search is null) return "no Library search box";
            f.Window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            f.Layout();
            if (!search.IsFocused) return "Ctrl+F didn't focus the Library search";

            var lib = f.Vm.Library;
            var list = f.Find<ListBox>(l => l.Name == "GameList")!;
            var sent = lib.SendToConvert;
            IReadOnlyList<string>? got = null;
            lib.SendToConvert = paths => got = paths;
            try
            {
                // A game with something to convert, or Convert selected has nothing to send.
                var game = lib.VisibleGames.FirstOrDefault(g => g.Game.Items.Any(i => i.IsSource || i.FormatStatus == NoIntrovert.Core.Formats.TargetStatus.Convert));
                if (game is null) return "no game in the samples needs converting";
                list.SelectedItems?.Add(game);
                f.Layout();
                list.ScrollIntoView(game);
                f.Layout();
                var row = list.ContainerFromItem(game);
                if (row is null) return "the game's row isn't shown";
                // Raised on the row itself: here the headless keyboard didn't route Enter to the list.
                row.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = row });
                f.Layout();
            }
            finally
            {
                lib.SendToConvert = sent;
                list.SelectedItems?.Clear();
                lib.SelectedGame = null;
            }
            return got is { Count: > 0 } ? null : "Enter on the game list didn't send the game to Convert";
        }));

        test("Log: each line shows its level as a glyph, not by colour alone", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            var log = f.Vm.Log;
            var expanded = log.IsExpanded;
            var entry = new NoIntrovert.Core.LogEntry(DateTime.Now, NoIntrovert.Core.LogLevel.Warning, "layout test: a warning");
            log.IsExpanded = true;
            try
            {
                // Straight into the list: the log's drain timer doesn't tick on the headless clock.
                log.Entries.Add(entry);
                f.Layout();
                f.Find<ListBox>(l => l.Name == "LogList")?.ScrollIntoView(entry);
                f.Layout();
                var glyph = f.Find<TextBlock>(t => t.Classes.Contains("levelglyph") && t.Text == "⚠" && t.IsEffectivelyVisible);
                if (glyph is null) return "no ⚠ beside the warning";
                return AutomationProperties.GetName(glyph) == "Warning" ? null : $"the glyph's name is '{AutomationProperties.GetName(glyph)}'";
            }
            finally { log.Entries.Remove(entry); log.IsExpanded = expanded; f.Layout(); }
        }));

        test("Accessibility: inputs and icon-only buttons on Library, Convert, Verify and Organize have names", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            var problems = new List<string>();
            var checkedCount = 0;
            foreach (var page in new[] { "library", "convert", "verify", "cleanup" })
            {
                f.Show(page);
                if (page == "convert") f.FocusRow(f.Vm.Convert);
                f.Layout();
                foreach (var c in f.Window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible))
                {
                    if (c.FindAncestorOfType<ContentControl>() is null || c.FindAncestorOfType<Window>() != f.Window) continue;
                    var named = !string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)) || AutomationProperties.GetLabeledBy(c) is not null;
                    var needsName = c switch
                    {
                        TextBox or ComboBox => true,
                        CheckBox { Content: null } => true,
                        Button { Content: string s } => !s.Any(char.IsLetter),
                        _ => false,
                    };
                    if (needsName) checkedCount++;
                    if (needsName && !named) problems.Add($"{page}: {c.GetType().Name} {c.Name} in {c.FindAncestorOfType<UserControl>()?.GetType().Name}");
                }
            }
            f.Vm.Convert.FocusedItem = null;
            if (checkedCount < 10) return $"only {checkedCount} inputs found to check";
            return problems.Count == 0 ? null : string.Join("\n        ", problems.Distinct().Take(12));
        }));
    }
}
