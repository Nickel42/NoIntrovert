using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// SELECTED FILE on every page that lists files: Optimize (the Library), Convert, Verify and Organize show the
/// selected file's system, format, size and match result the same way (FileFacts), following the selection alike.
/// </summary>
internal static class SelectedFileTests
{
    public static void Run(Action<string, Func<string?>> test)
    {
        test("Selected file: follows the focused file, lists the systems when the selection spans several, and is empty with neither", () =>
        {
            var facts = new FileFactsViewModel();
            facts.Follow(@"C:\games\Tetris (USA).nes", "nes-famicom", ["nes-famicom"], _ => null);
            if (facts.Mode != FileFactsMode.File || facts.SystemId != "nes-famicom" || facts.FileName != "Tetris (USA).nes") return $"one file: {facts.Mode} {facts.SystemId} {facts.FileName}";
            facts.Follow(@"C:\games\Tetris (USA).nes", "nes-famicom", ["nes-famicom", "game-boy"], _ => null);
            if (facts.Mode != FileFactsMode.Systems || facts.SystemLines.Count != 2) return $"two systems: {facts.Mode}";
            facts.Follow(null, null, [], _ => null);
            if (facts.Mode != FileFactsMode.Empty) return "nothing selected: " + facts.Mode;
            // A match result read later is shown only while its file is.
            facts.Follow(@"C:\a.nes", "nes-famicom", ["nes-famicom"], _ => null);
            facts.ShowMatch(@"C:\b.nes", Core.Dats.MatchResult.Of(Core.Dats.MatchStatus.Verified, ""));
            if (facts.IsVerified) return "another file's match result was shown";
            facts.ShowMatch(@"C:\a.nes", Core.Dats.MatchResult.Of(Core.Dats.MatchStatus.Verified, ""));
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

        test("Selected file: Optimize shows the selected game's file, with its match result once the details are read", () => dispatch(() =>
        {
            var library = f.Vm.Library;
            f.Show("library");
            f.Layout();
            if (FactsOn<LibraryView>() is not null) return "SELECTED FILE shows with no game selected";
            var game = library.VisibleGames.FirstOrDefault();
            if (game is null) return "the Library has no games";
            library.SelectedGame = game;
            f.Pump(() => library.DetailsLoaded.IsCompleted);
            f.Layout();
            try
            {
                var facts = library.SelectedFile.Facts;
                if (facts.Mode != FileFactsMode.File || facts.SystemId != game.Game.System || facts.Path != game.Game.Items[0].Path)
                    return $"shows {facts.Mode} {facts.SystemId} {facts.Path}";
                if (FactsOn<LibraryView>() is null) return "no SELECTED FILE on the Optimize page";
                return Fits("optimize");
            }
            finally { library.SelectedGame = null; }
        }));

        test("Selected file: Organize shows the picked step's file", () => dispatch(() =>
        {
            var cleanup = f.Vm.Cleanup;
            f.Show("cleanup");
            var nes = Directory.GetFiles(Path.Combine(f.LibraryFolder, "NES"))[0];
            cleanup.ShowPlan(new CleanupPlan([new CleanupStep
            {
                Kind = CleanupKind.Move, GameKey = "g", Game = Path.GetFileNameWithoutExtension(nes), SystemId = "nes-famicom",
                Root = f.LibraryFolder, Sources = [nes], Reason = "",
            }]));
            f.Layout();
            try
            {
                if (cleanup.SelectedFile.Facts.Mode != FileFactsMode.Empty) return "a step shows before one is picked";
                cleanup.SelectedStep = cleanup.Steps[0];
                f.Layout();
                var facts = cleanup.SelectedFile.Facts;
                if (facts.Mode != FileFactsMode.File || facts.SystemId != "nes-famicom" || facts.Path != nes) return $"shows {facts.Mode} {facts.SystemId} {facts.Path}";
                if (FactsOn<CleanupView>() is null) return "no SELECTED FILE on the Organize page";
                return Fits("organize");
            }
            finally
            {
                cleanup.SelectedStep = null;
                cleanup.ShowPlan(new CleanupPlan([]));
            }
        }));

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
