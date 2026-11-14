using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NoIntrovert.Core;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// The Settings page's section list (UX review R6): it names every card in the page's order, choosing
/// one scrolls its card to the top, the card at the top is the one highlighted, Up/Down and Enter work,
/// and below a 1000 px window it's a Jump to… drop-down. Appearance has the themes and Row density;
/// General keeps Start on.
/// </summary>
internal static class SettingsNavTests
{
    public static void Run(Action<string, Func<string?>> test)
    {
        test("Settings: the section list shows from a 1000 px window, a Jump to… drop-down below it", () =>
            SettingsView.ShowsSectionList(1000) && SettingsView.ShowsSectionList(1180) && !SettingsView.ShowsSectionList(999) && !SettingsView.ShowsSectionList(900)
                ? null : "the section list's width is off");
        test("Settings: Set up keys asks for the Keys section; any card can be asked for by its name", () =>
        {
            var vm = new SettingsViewModel(new AppSettings { IsScratch = true }, new NoDialogs());
            vm.ShowKeys();
            if (vm.PendingSection != "Keys" || !vm.ShowKeysPending) return $"ShowKeys asked for {vm.PendingSection}";
            vm.ShowSection("Log");
            if (vm.ShowKeysPending) return "asking for Log still says Keys";
            return SettingsView.AllSections.Any(s => s.Title == SettingsViewModel.KeysSection) ? null : "no section is called Keys";
        });
    }

    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        SettingsView Page()
        {
            f.Show("settings");
            f.Layout();
            return f.Find<SettingsView>(v => v.IsEffectivelyVisible)!;
        }
        static ListBox List(SettingsView page) => page.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "SectionList");
        static ScrollViewer Scroll(SettingsView page) => page.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "PageScroll");
        static Expander Advanced(SettingsView page) => page.GetVisualDescendants().OfType<Expander>().First(e => e.Name == "AdvancedSection");

        // Runs the UI (and its render timer) until done() or 3 seconds.
        bool Settle(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                f.Layout();
                if (done()) return true;
                Thread.Sleep(15);
            }
            return false;
        }

        // Null when the card's top is in the top half of the page, or the page is scrolled to its end with the card showing.
        static string? InView(SettingsView page, SettingsSection section)
        {
            var card = page.CardOf(section)!;
            var scroll = Scroll(page);
            if (!card.IsEffectivelyVisible) return $"{section.Title}'s card isn't showing";
            if (card.TranslatePoint(default, scroll) is not { } top) return $"{section.Title}'s card isn't in the page";
            var atEnd = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1;
            if (top.Y >= -1 && (top.Y <= scroll.Viewport.Height / 2 || atEnd && top.Y < scroll.Viewport.Height - 20)) return null;
            return $"{section.Title}'s card top is at {top.Y:F0} of a {scroll.Viewport.Height:F0} px page (offset {scroll.Offset.Y:F0} of {scroll.Extent.Height - scroll.Viewport.Height:F0})";
        }

        test("Settings: the section list names every card, in the page's order (Advanced's cards under it), and is in the tab order", () => dispatch(() =>
        {
            f.Resize(1180, 820);
            var page = Page();
            var list = List(page);
            if (!list.IsEffectivelyVisible) return "no section list at 1180 px";
            if (!KeyboardNavigation.GetIsTabStop(list) || list.ContainerFromIndex(0) is not { Focusable: true }) return "the section list isn't in the tab order";
            var advanced = Advanced(page);
            advanced.IsExpanded = true;
            f.Layout();

            try
            {
                // The cards in the page's order (one column, so the visual tree's order).
                var cards = page.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsEffectivelyVisible && (c is Border b && b.Classes.Contains("card") || c == advanced))
                    .Select(c => c.Name ?? "(unnamed card)").ToList();
                var listed = page.Sections.Select(s => s.CardName).ToList();
                if (!cards.SequenceEqual(listed))
                    return $"the page has {string.Join(", ", cards)}; the list has {string.Join(", ", listed)}";
                var titles = list.Items.OfType<SettingsSection>().Select(s => s.Title).ToList();
                string[] expected = ["General", "Appearance", "Output", "Performance", "Log", "Default presets", "Library", "Keys", "Advanced",
                    "Power user", "Archives", "Files and folders", "Game databases", "Target formats", "About"];
                if (!titles.SequenceEqual(expected)) return "the list reads " + string.Join(", ", titles);
                if (page.Sections.Where(s => s.IsSub).Any(s => page.CardOf(s)!.FindAncestorOfType<Expander>() != advanced)) return "a sub-section isn't in Advanced";
                // Power-user mode shows Diagnostics, and its section.
                f.Vm.Settings.General.PowerUserMode = true;
                f.Layout();
                var withDiagnostics = page.Sections.Select(s => s.Title).ToList();
                f.Vm.Settings.General.PowerUserMode = false;
                f.Layout();
                if (!withDiagnostics.Contains("Diagnostics") || withDiagnostics.IndexOf("Diagnostics") != withDiagnostics.IndexOf("About") - 1)
                    return "Power-user mode didn't list Diagnostics before About: " + string.Join(", ", withDiagnostics);
                return page.Sections.Any(s => s.Title == "Diagnostics") ? "Diagnostics stays listed without Power-user mode" : null;
            }
            finally
            {
                advanced.IsExpanded = false;
                f.Layout();
            }
        }));

        test("Settings: choosing a section scrolls its card into view and keeps it highlighted; scrolling highlights the card at the top", () => dispatch(() =>
        {
            f.Resize(1180, 820);
            var page = Page();
            var list = List(page);
            var scroll = Scroll(page);
            var advanced = Advanced(page);
            try
            {
                foreach (var section in page.Sections.ToList())
                {
                    list.SelectedItem = section;
                    Settle(() => InView(page, section) is null);
                    if (InView(page, section) is { } problem) return problem;
                    if (!Equals(list.SelectedItem, section)) return $"after choosing {section.Title}, {list.SelectedItem} is highlighted";
                    if (section.IsSub && !advanced.IsExpanded) return $"choosing {section.Title} didn't open Advanced";
                    if (screenshots is not null && section.Title is "Keys" or "Target formats")
                        f.Save(Path.Combine(screenshots, $"settings-section-{section.CardName}.png"));
                }
                advanced.IsExpanded = false;
                f.Layout();
                // Scroll-spy: back at the top General is highlighted; scrolled to Output's top, Output is.
                scroll.Offset = default;
                f.Layout();
                if ((list.SelectedItem as SettingsSection)?.Title != "General") return $"at the top {list.SelectedItem} is highlighted";
                var output = page.Sections.First(s => s.Title == "Output");
                var top = page.CardOf(output)!.TranslatePoint(default, (Visual)scroll.Content!)!.Value.Y;
                scroll.Offset = new Vector(0, top + 4);
                f.Layout();
                if ((list.SelectedItem as SettingsSection)?.Title != "Output") return $"with Output at the top, {list.SelectedItem} is highlighted";
                scroll.ScrollToEnd();
                f.Layout();
                return (list.SelectedItem as SettingsSection)?.Title == "About" ? null : $"at the end {list.SelectedItem} is highlighted";
            }
            finally
            {
                advanced.IsExpanded = false;
                scroll.Offset = default;
                f.Layout();
            }
        }));

        test("Settings: in the section list Down moves to the next card and Enter goes into it", () => dispatch(() =>
        {
            f.Resize(1180, 820);
            var page = Page();
            var list = List(page);
            var scroll = Scroll(page);
            // A key on the focused row of the list, as the keyboard sends it.
            void Press(Key key)
            {
                var target = f.Window.FocusManager?.GetFocusedElement() as Interactive ?? list;
                target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });
                f.Layout();
            }
            try
            {
                list.SelectedItem = page.Sections.First(s => s.Title == "Log");
                f.Layout();
                (list.ContainerFromItem(list.SelectedItem!) as Control)?.Focus(NavigationMethod.Tab);
                f.Layout();
                Press(Key.Down);
                if (list.SelectedItem is not SettingsSection { Title: "Default presets" } presets) return $"Down from Log chose {list.SelectedItem} (focus on {(f.Window.FocusManager?.GetFocusedElement() as Control)?.DataContext} in {(f.Window.FocusManager?.GetFocusedElement() as Control)?.FindAncestorOfType<ListBox>()?.Name})";
                if (InView(page, presets) is { } problem) return problem;
                Press(Key.Down);
                Press(Key.Enter);
                var library = page.Sections.First(s => s.Title == "Library");
                var focused = f.Window.FocusManager?.GetFocusedElement() as Visual;
                if (focused is null || !page.CardOf(library)!.IsVisualAncestorOf(focused)) return $"Enter on Library focused {focused}, not a control in Library";
                return InView(page, library);
            }
            finally
            {
                scroll.Offset = default;
                f.Layout();
            }
        }));

        test("Settings: Row density, the themes and More themes… are in Appearance; General keeps Start on", () => dispatch(() =>
        {
            f.Resize(1180, 820);
            var page = Page();
            var appearance = page.FindControl<Border>("AppearanceCard")!;
            var general = page.FindControl<Border>("GeneralCard")!;
            bool In(Border card, Func<Control, bool> match) => card.GetVisualDescendants().OfType<Control>().Any(match);
            if (!In(appearance, c => c is RadioButton { Content: "Compact" }) || !In(appearance, c => c is RadioButton { Content: "Comfortable" }))
                return "Row density isn't in Appearance";
            if (In(general, c => c is RadioButton { Content: "Compact" })) return "Row density is still in General";
            foreach (var name in new[] { "DarkThemePicker", "LightThemePicker", "LightBrightnessSlider", "MoreThemes" })
                if (!In(appearance, c => c.Name == name)) return $"{name} isn't in Appearance";
            if (In(general, c => c is RadioButton { Content: "Dark" })) return "the theme mode is still in General";
            if (!In(general, c => c is ComboBox && Avalonia.Automation.AutomationProperties.GetName(c) == "Start on")) return "Start on isn't in General";
            var more = page.FindControl<Expander>("MoreThemes")!;
            if (more.IsExpanded) return "More themes… starts open";
            // Row density still works from its new place.
            var comfortable = appearance.GetVisualDescendants().OfType<RadioButton>().First(r => r.Content as string == "Comfortable");
            comfortable.IsChecked = true;
            var switched = f.Vm.Settings.General.ListDensity == ListDensity.Comfortable;
            f.Vm.Settings.General.IsCompactRows = true;
            if (!switched) return "Comfortable in Appearance didn't change the row density";
            if (page.FindControl<Image>("AboutLogo") is not { Bounds.Width: > 60 and <= 130 } logo) return "the About card has no illustration of a modest size";
            return null;
        }));

        test("Settings: at 950 px the section list is a Jump to… drop-down that scrolls to the card", () => dispatch(() =>
        {
            f.Resize(950, 820);
            var page = Page();
            var list = List(page);
            var jump = page.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "JumpList");
            var scroll = Scroll(page);
            try
            {
                if (list.IsEffectivelyVisible) return "the section list still shows at 950 px";
                if (!jump.IsEffectivelyVisible) return "no Jump to… drop-down at 950 px";
                if (jump.PlaceholderText != "Jump to…") return $"the drop-down says \"{jump.PlaceholderText}\"";
                var keys = page.Sections.First(s => s.Title == "Keys");
                jump.SelectedItem = keys;
                f.Layout();
                if (InView(page, keys) is { } problem) return problem;
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "settings-950-jump.png"));
                var problems = LayoutTests.Check(f.Window);
                return problems.Count > 0 ? string.Join("\n        ", problems.Take(6)) : null;
            }
            finally
            {
                scroll.Offset = default;
                f.Resize(1180, 820);
            }
        }));
    }
}
