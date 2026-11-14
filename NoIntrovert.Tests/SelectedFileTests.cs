using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// SELECTED FILE on every page that lists files: Optimize, Convert, Verify and Organize show the
/// selected file's system, format, size and match result the same way (FileFacts), following the selection alike.
/// </summary>
internal static class SelectedFileTests
{
    public static void Run(Action<string, Func<string?>> test)
    {
        test("Selected file: follows the focused file, lists the systems when the selection spans several, and is empty with neither", () =>
        {
            var facts = new FileFactsViewModel();
            // Paths in this system's form: on Linux a backslash isn't a separator, so C:\games\x.nes would be all file name.
            string Game(string name) => Path.Combine(Path.GetTempPath(), "games", name);
            facts.Follow(Game("Tetris (USA).nes"), "nes-famicom", ["nes-famicom"], _ => null);
            if (facts.Mode != FileFactsMode.File || facts.SystemId != "nes-famicom" || facts.FileName != "Tetris (USA).nes") return $"one file: {facts.Mode} {facts.SystemId} {facts.FileName}";
            facts.Follow(Game("Tetris (USA).nes"), "nes-famicom", ["nes-famicom", "game-boy"], _ => null);
            if (facts.Mode != FileFactsMode.Systems || facts.SystemLines.Count != 2) return $"two systems: {facts.Mode}";
            facts.Follow(null, null, [], _ => null);
            if (facts.Mode != FileFactsMode.Empty) return "nothing selected: " + facts.Mode;
            // A match result read later is shown only while its file is.
            facts.Follow(Game("a.nes"), "nes-famicom", ["nes-famicom"], _ => null);
            facts.ShowMatch(Game("b.nes"), Core.Dats.MatchResult.Of(Core.Dats.MatchStatus.Verified, ""));
            if (facts.IsVerified) return "another file's match result was shown";
            facts.ShowMatch(Game("a.nes"), Core.Dats.MatchResult.Of(Core.Dats.MatchStatus.Verified, ""));
            return facts.IsVerified ? null : "the file's own match result wasn't shown";
        });

        test("Selected file: the header reads as Convert's", () =>
        {
            var texts = new[] { SelectedFileViewModel.HeaderFor(3, 40, 2), SelectedFileViewModel.HeaderFor(null, 40, 2), SelectedFileViewModel.HeaderFor(null, 40, 0) };
            return texts.SequenceEqual(["Focused: row 3 of 40 · 2 selected", "2 selected", ""]) ? null : string.Join(" | ", texts);
        });
    }

    /// <summary>In the real window (headless): each page shows SELECTED FILE for its selection, fitting at every size.</summary>
    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        // The FileFacts in a page's side panel, shown (not one of another page's).
        FileFacts? FactsOn<TPage>() where TPage : Visual =>
            f.Find<FileFacts>(c => c.IsEffectivelyVisible && c.FindAncestorOfType<TPage>() is not null);

        string? Fits(string name)
        {
            foreach (var (width, height) in LayoutTests.Sizes)
            {
                f.Resize(width, height);
                f.Layout();
                var problems = LayoutTests.Check(f.Window);
                if (problems.Count > 0) return $"{name} {width}×{height}: " + string.Join("\n        ", problems.Take(6));
                if (width == 1366 && screenshots is not null) f.Save(Path.Combine(screenshots, $"selected-file-{name}.png"));
            }
            return null;
        }

        test("Selected file: Verify shows the focused file as Convert does", () => dispatch(() =>
        {
            var verify = f.Vm.Verify;
            f.Show("verify");
            verify.FocusedItem = verify.Items.FirstOrDefault(i => i.Path.EndsWith(".nes", StringComparison.OrdinalIgnoreCase));
            f.Layout();
            try
            {
                if (verify.FocusedItem is null) return "Verify has no .nes row";
                if (verify.SelectedFile.Facts is not { Mode: FileFactsMode.File, SystemId: "nes-famicom" } facts) return $"shows {verify.SelectedFile.Facts.Mode} {verify.SelectedFile.Facts.SystemId}";
                if (FactsOn<VerifyView>() is not { DataContext: FileFactsViewModel shown } || shown != facts) return "no SELECTED FILE on the Verify page";
                if (!verify.SelectedFile.Header.StartsWith($"Focused: row {verify.Items.IndexOf(verify.FocusedItem) + 1} of ")) return "header: " + verify.SelectedFile.Header;
                return Fits("verify");
            }
            finally { verify.FocusedItem = null; }
        }));

        // Optimize and Organize list files as Verify does: SELECTED FILE follows the focused row.
        string? FocusedOn<TPage>(PlanTabViewModel tab, string id, string name) where TPage : Visual
        {
            f.Show(id);
            f.Layout();
            tab.FocusedItem = tab.Items.FirstOrDefault(i => i.Path.EndsWith(".nes", StringComparison.OrdinalIgnoreCase));
            f.Layout();
            try
            {
                if (tab.FocusedItem is null) return $"{name} has no .nes row";
                if (tab.SelectedFile.Facts is not { Mode: FileFactsMode.File, SystemId: "nes-famicom" } facts) return $"shows {tab.SelectedFile.Facts.Mode} {tab.SelectedFile.Facts.SystemId}";
                if (facts.Path != tab.FocusedItem.Path) return $"shows {facts.Path}";
                if (FactsOn<TPage>() is not { DataContext: FileFactsViewModel shown } || shown != facts) return $"no SELECTED FILE on the {name} page";
                return Fits(name.ToLowerInvariant());
            }
            finally { tab.FocusedItem = null; }
        }

        test("Selected file: Optimize shows the focused file as Convert does", () => dispatch(() => FocusedOn<OptimizeView>(f.Vm.Optimize, "optimize", "Optimize")));
        test("Selected file: Organize shows the focused file as Convert does", () => dispatch(() => FocusedOn<CleanupView>(f.Vm.Cleanup, "cleanup", "Organize")));

        test("Selected file: Convert keeps its own side panel (the shared one stays idle there)", () => dispatch(() =>
        {
            var convert = f.Vm.Convert;
            convert.FocusedItem = convert.Items.FirstOrDefault();
            try
            {
                if (convert.Panel.File.Mode != FileFactsMode.File) return "Convert's side panel doesn't show the file";
                return convert.SelectedFile.Facts.Mode == FileFactsMode.Empty ? null : "the shared SELECTED FILE followed Convert's rows too";
            }
            finally { convert.FocusedItem = null; }
        }));
    }
}
