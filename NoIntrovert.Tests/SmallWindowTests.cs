using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// Small windows (docs/requirements/ui-redesign.md, "Small windows"; UX review R7), in the real window:
/// under the breakpoint the side panel leaves the layout and the list takes the width; the Details tab or
/// Ctrl+I opens the same panel over the list from the right; Escape, ✕ or a click outside closes it; and
/// growing the window puts the panel back in its column.
/// </summary>
static class SmallWindowTests
{
    private static readonly string[] Pages = ["optimize", "convert", "verify", "cleanup"];

    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        SidePanelHost? Host() => f.Find<SidePanelHost>(h => h.IsEffectivelyVisible);
        double Width(Visual v) => v.Bounds.Width;
        double Left(Visual v) => v.TranslatePoint(new Point(0, 0), f.Window)!.Value.X;

        test("Small windows: at 900×600 every page's list takes the width, the panel is out of the layout and the Details tab shows", () => dispatch(() =>
        {
            foreach (var page in Pages)
            {
                f.Resize(1180, 820);
                f.Show(page);
                f.Layout();
                var host = Host();
                if (host?.Panel is not { } panel) return $"{page}: no side panel host";
                if (host.IsCollapsed || !panel.IsEffectivelyVisible || host.DetailsButton.IsVisible) return $"{page}: at 1180×820 the panel isn't a column";
                var columnWidth = Width(panel);

                f.Resize(900, 600);
                if (!host.IsCollapsed) return $"{page}: not collapsed at 900×600 (page {Width(host):F0} px)";
                if (panel.IsEffectivelyVisible) return $"{page}: the panel still shows at 900×600";
                if (!host.DetailsButton.IsEffectivelyVisible) return $"{page}: no Details tab";
                if (ToolTip.GetTip(host.DetailsButton) is not string tip || !tip.Contains("Show details for the selected file")) return $"{page}: the Details tab's tooltip is '{ToolTip.GetTip(host.DetailsButton)}'";
                var list = host.Main!;
                var free = Width(host) - Width(list);
                if (free > 60) return $"{page}: the list leaves {free:F0} px of the page's {Width(host):F0} unused";
                if (columnWidth < 300) return $"{page}: the column was {columnWidth:F0} px wide";
                var problems = LayoutTests.Check(f.Window);
                if (problems.Count > 0) return $"{page}: " + string.Join("\n        ", problems.Take(6));
                if (screenshots is not null) f.Save(Path.Combine(screenshots, $"small-{page}-closed.png"));
            }
            return null;
        }));

        test("Small windows: the Details tab opens the same panel over the list, focus moves into it, Escape closes it back to the tab", () => dispatch(() =>
        {
            f.Resize(1180, 820);
            f.Show("convert");
            f.FocusRow(f.Vm.Convert);
            f.Layout();
            var host = Host()!;
            var panel = host.Panel!;
            f.Resize(900, 600);
            host.DetailsButton.Focus();
            host.DetailsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            f.Layout();
            try
            {
                if (!host.IsOpen) return "the Details tab didn't open the panel";
                if (host.Panel != panel || !panel.IsEffectivelyVisible) return "the panel that shows isn't the page's panel";
                var flyout = panel.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("sidepanelflyout"));
                if (flyout is null) return "the panel isn't in the overlay";
                if (Math.Abs(Width(flyout) - SidePanelHost.FlyoutWidth) > 1) return $"the overlay is {Width(flyout):F0} px wide";
                var right = Left(flyout) + Width(flyout);
                var hostRight = Left(host) + Width(host);
                if (Math.Abs(right - hostRight) > 1) return $"the overlay ends at {right:F0}, not the page's right edge {hostRight:F0}";
                if (Width(host.Main!) < Width(host) - 60) return "opening the overlay narrowed the list";
                var focused = f.Window.FocusManager?.GetFocusedElement() as Visual;
                if (focused is null || !flyout.IsVisualAncestorOf(focused)) return $"focus is on {focused?.GetType().Name ?? "nothing"}, not in the panel";
                if (!host.CloseButton.IsEffectivelyVisible) return "no ✕ on the overlay";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "small-convert-open.png"));

                f.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                f.Layout();
                if (host.IsOpen || panel.IsEffectivelyVisible) return "Escape didn't close the overlay";
                if (f.Window.FocusManager?.GetFocusedElement() != host.DetailsButton) return "closing didn't give focus back to the Details tab";
                return null;
            }
            finally { host.IsOpen = false; f.Layout(); }
        }));

        test("Small windows: Ctrl+I toggles the overlay, ✕ and a click outside close it", () => dispatch(() =>
        {
            f.Resize(900, 600);
            f.Show("cleanup");
            f.FocusRow(f.Vm.Cleanup);
            f.Layout();
            var host = Host()!;
            try
            {
                f.Window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.Control);
                f.Layout();
                if (!host.IsOpen) return "Ctrl+I didn't open the overlay";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "small-cleanup-open.png"));
                f.Window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.Control);
                f.Layout();
                if (host.IsOpen) return "a second Ctrl+I didn't close it";

                host.IsOpen = true;
                f.Layout();
                host.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                f.Layout();
                if (host.IsOpen) return "✕ didn't close it";

                host.IsOpen = true;
                f.Layout();
                // A click beside the overlay lands on the scrim: it covers the list, above it, up to the overlay.
                // (Headless hit testing reads a stale frame here, so the press is raised on the scrim itself.)
                var scrim = host.Scrim;
                if (!scrim.IsEffectivelyVisible || scrim.ZIndex <= host.Main!.ZIndex) return "no scrim above the list";
                if (Left(scrim) > Left(host.Main!) + 1 || Width(scrim) < Width(host.Main!) - 1) return "the scrim doesn't cover the list";
                var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                var at = new Point(Left(host) + 40, 200);
                scrim.RaiseEvent(new PointerPressedEventArgs(scrim, pointer, f.Window, at, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
                f.Layout();
                if (host.IsOpen) return "a click on the list beside the overlay didn't close it";

                f.Resize(1180, 820);
                f.Window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.Control);
                f.Layout();
                return host.IsOpen ? "Ctrl+I opened an overlay while the panel is a column" : null;
            }
            finally { host.IsOpen = false; f.Layout(); }
        }));

        test("Small windows: growing past the breakpoint closes the overlay and puts the panel back in its column", () => dispatch(() =>
        {
            f.Resize(900, 600);
            f.Show("convert");
            f.Layout();
            var host = Host()!;
            host.IsOpen = true;
            f.Layout();
            if (!host.IsOpen) return "didn't open";
            f.Resize(1180, 820);
            var panel = host.Panel!;
            if (host.IsOpen || host.IsCollapsed) return "the overlay is still open at 1180×820";
            if (!panel.IsEffectivelyVisible || host.DetailsButton.IsVisible) return "the panel isn't back in its column";
            if (Width(panel) < 280) return $"the column is only {Width(panel):F0} px wide";
            var gap = Left(panel) - (Left(host.Main!) + Width(host.Main!));
            if (gap < 6) return $"the panel overlaps the list ({gap:F0} px gap)";
            var problems = LayoutTests.Check(f.Window);
            return problems.Count == 0 ? null : string.Join("\n        ", problems.Take(6));
        }));

        test("Small windows: the panel collapses once, as the window narrows, whatever the rail does (page width, not window width)", () => dispatch(() =>
        {
            f.Show("convert");
            var collapsedAt = new List<int>();
            for (var width = 1400; width >= 900; width -= 4)
            {
                f.Resize(width, 820);
                if (Host()!.IsCollapsed) collapsedAt.Add(width);
            }
            f.Resize(1180, 820);
            if (collapsedAt.Count == 0) return "never collapsed";
            var widest = collapsedAt.Max();
            var gaps = Enumerable.Range(0, (widest - 900) / 4 + 1).Select(i => 900 + i * 4).Except(collapsedAt).ToList();
            if (gaps.Count > 0) return $"collapsed at {widest} but not at {string.Join(", ", gaps.Take(5))}: it flips";
            // The rail turns to icons under 1100 px; the panel holds on until the page itself is small.
            return widest is >= 1100 or < 940 ? $"collapses below a {widest + 4} px window" : null;
        }));
    }
}
