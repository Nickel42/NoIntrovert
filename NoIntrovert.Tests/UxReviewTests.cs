using NoIntrovert.Core;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Library;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>
/// Fixes from the 2026-11-12 review of the Library, Optimize and Convert
/// pages: destructive actions ask first (and say where files go), the
/// Cleanup page shows when it's busy and does one thing at a time, and
/// Start needs something to run.
/// </summary>
internal static class UxReviewTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir)
    {
        test("Cleanup: Empty trash asks first, says whether files go to the Recycle Bin, and a no keeps them", () =>
            WithSettingsFolder(dir("ux-empty-trash-settings"), () =>
            {
                var (cleanup, settings, dialogs) = NewCleanup();
                var trash = dir("ux-empty-trash");
                File.WriteAllBytes(Path.Combine(trash, "Game.bin"), new byte[2048]);
                File.WriteAllBytes(Path.Combine(trash, "Game.cue"), new byte[10]);
                var row = new CleanupRunRow(new CleanupRun { Steps = 1, TrashFolders = [trash] });

                settings.Output.UseRecycleBin = false;
                dialogs.Answer = false;
                cleanup.EmptyTrashCommand.ExecuteAsync(row).GetAwaiter().GetResult();
                if (dialogs.Asked.Count != 1) return $"asked {dialogs.Asked.Count} times";
                var (title, message, yes, destructive) = dialogs.Asked[0];
                if (!destructive) return "the question isn't marked destructive, so Enter would empty the trash";
                if (!message.Contains("2 files") || !message.Contains("deleted permanently")) return $"the question doesn't say what's deleted and how: '{message}'";
                if (yes != "Delete permanently") return $"the button says '{yes}'";
                if (!File.Exists(Path.Combine(trash, "Game.bin"))) return "a no still emptied the trash";
                if (!cleanup.EmptyTrashTip.Contains("deleted permanently")) return $"the tooltip says '{cleanup.EmptyTrashTip}' with the Recycle Bin off";

                var tipChanged = false;
                cleanup.PropertyChanged += (_, e) => tipChanged |= e.PropertyName == nameof(CleanupViewModel.EmptyTrashTip);
                settings.Output.UseRecycleBin = true;
                if (!tipChanged) return "turning the Recycle Bin on didn't update the tooltip";
                if (FileRemover.CanUseBin && !cleanup.EmptyTrashTip.Contains(FileRemover.BinName)) return $"the tooltip says '{cleanup.EmptyTrashTip}' with the Recycle Bin on";
                cleanup.EmptyTrashCommand.ExecuteAsync(row).GetAwaiter().GetResult();
                if (FileRemover.CanUseBin && !dialogs.Asked[^1].Message.Contains($"moved to the {FileRemover.BinName}")) return $"with the Recycle Bin on it says '{dialogs.Asked[^1].Message}'";

                // Yes, with the Recycle Bin off (a test folder: deleted for good).
                settings.Output.UseRecycleBin = false;
                dialogs.Answer = true;
                cleanup.EmptyTrashCommand.ExecuteAsync(row).GetAwaiter().GetResult();
                return File.Exists(Path.Combine(trash, "Game.bin")) ? "a yes didn't empty the trash" : null;
            }));

        test("Cleanup: Undo asks first, and a no changes nothing", () =>
            WithSettingsFolder(dir("ux-undo-settings"), () =>
            {
                var (cleanup, _, dialogs) = NewCleanup();
                var run = new CleanupRun { Steps = 1 };
                dialogs.Answer = false;
                cleanup.UndoCommand.ExecuteAsync(new CleanupRunRow(run)).GetAwaiter().GetResult();
                if (dialogs.Asked.Count != 1) return $"asked {dialogs.Asked.Count} times";
                return run.Undone ? "a no undid the run" : null;
            }));

        test("Cleanup: while busy it says so, and Whole Library, Refresh plan, Undo and Empty trash wait", () =>
            WithSettingsFolder(dir("ux-busy-settings"), () =>
            {
                var (cleanup, _, dialogs) = NewCleanup();
                var row = new CleanupRunRow(new CleanupRun { Steps = 1 });
                if (!cleanup.ShowsEmptyHint) return "the empty-page message doesn't show on a new page";
                cleanup.IsBusy = true;
                cleanup.BusyText = "Running 3 steps for 2 games…";
                try
                {
                    if (cleanup.ShowsEmptyHint) return "the empty-page message shows while busy";
                    if (cleanup.WholeLibraryCommand.CanExecute(null)) return "Whole Library can start a second plan while busy";
                    if (cleanup.MakePlanCommand.CanExecute(null)) return "Refresh plan can run while busy";
                    if (cleanup.UndoCommand.CanExecute(row) || cleanup.EmptyTrashCommand.CanExecute(row)) return "Undo or Empty trash can run while busy";
                    // Called directly (as Clean up selected does), a plan waits instead of clearing the busy state.
                    cleanup.UseGames(null);
                    if (!cleanup.IsBusy) return "a plan asked for while busy turned the busy state off";
                    cleanup.EmptyTrashCommand.ExecuteAsync(row).GetAwaiter().GetResult();
                    if (dialogs.Asked.Count != 0) return "Empty trash asked while busy";
                }
                finally { cleanup.IsBusy = false; }
                return cleanup.WholeLibraryCommand.CanExecute(null) ? null : "Whole Library stayed off after the work was done";
            }));

        test("Cleanup: the header's box ticks every step that can run, or none, and is half-ticked for some", () =>
            WithSettingsFolder(dir("ux-tick-all-settings"), () =>
            {
                var (cleanup, _, _) = NewCleanup();
                CleanupStep Step(CleanupKind kind) => new() { Kind = kind, GameKey = kind.ToString(), Game = kind.ToString(), Root = "C:/lib", Sources = ["C:/lib/a"], Reason = "" };
                foreach (var kind in new[] { CleanupKind.Rename, CleanupKind.Move, CleanupKind.Clash })
                    cleanup.Steps.Add(new CleanupStepRow(Step(kind), () => { }));
                cleanup.AllEnabled = false;
                if (cleanup.Steps.Any(s => s.CanRun && s.Enabled) || cleanup.AllEnabled != false) return "unticking all left a step ticked";
                cleanup.Steps[0].Enabled = true;
                if (cleanup.AllEnabled is not null) return $"one of two ticked reads {cleanup.AllEnabled}, not half";
                cleanup.AllEnabled = true;
                if (cleanup.Steps.Any(s => s.CanRun && !s.Enabled) || cleanup.AllEnabled != true) return "ticking all left a step unticked";
                return cleanup.Steps.Single(s => !s.CanRun).Step.Kind == CleanupKind.Clash ? null : "a clash counts as a step that can run";
            }));

        test("Convert: Clear all asks first when a row failed or the list is long, and a no keeps the rows", () =>
            WithSettingsFolder(dir("ux-clear-all-settings"), () =>
            {
                var dialogs = new AskingDialogs();
                var settings = new AppSettings();
                var vm = new SettingsViewModel(settings, dialogs);
                var convert = new ConvertViewModel(vm, new LogViewModel(dialogs, settings), dialogs, new JobQueue(() => 1));
                convert.Items.Add(new JobItemViewModel(Path.Combine(dir("ux-clear-all"), "A.cue")));
                convert.Items.Add(new JobItemViewModel(Path.Combine(dir("ux-clear-all"), "B.cue")));
                convert.ClearAllCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                if (dialogs.Asked.Count != 0) return "a short list with no failures asked first";
                if (convert.Items.Count != 0) return "Clear all didn't clear a short list";

                for (var i = 0; i < 3; i++) convert.Items.Add(new JobItemViewModel(Path.Combine(dir("ux-clear-all"), $"{i}.cue")));
                convert.Items[1].Status = JobStatus.Failed;
                dialogs.Answer = false;
                convert.ClearAllCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                if (dialogs.Asked.Count != 1) return "a list with a failed row didn't ask";
                if (!dialogs.Asked[0].Destructive || !dialogs.Asked[0].Message.Contains("1 row failed")) return $"the question: '{dialogs.Asked[0].Message}'";
                if (convert.Items.Count != 3) return "a no cleared the list";
                dialogs.Answer = true;
                convert.ClearAllCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                return convert.Items.Count == 0 ? null : "a yes didn't clear the list";
            }));

        test("Convert: Start is off with nothing in the list", () =>
            WithSettingsFolder(dir("ux-start-settings"), () =>
            {
                var dialogs = new AskingDialogs();
                var settings = new AppSettings();
                var vm = new SettingsViewModel(settings, dialogs);
                var convert = new ConvertViewModel(vm, new LogViewModel(dialogs, settings), dialogs, new JobQueue(() => 1));
                if (convert.StartCommand.CanExecute(null)) return "Start is on with an empty list";
                convert.Items.Add(new JobItemViewModel(Path.Combine(dir("ux-start"), "A.cue")));
                return convert.StartCommand.CanExecute(null) ? null : "Start stays off with a row in the list";
            }));

        test("Dialogs: without a window to ask in, a question is answered no", () =>
            new NoDialogs() is NoIntrovert.Services.IDialogs d && !d.ConfirmAsync("t", "m", "Delete", "Cancel", destructive: true).GetAwaiter().GetResult()
                ? null : "a test double without ConfirmAsync said yes");
    }

    private static (CleanupViewModel Cleanup, SettingsViewModel Settings, AskingDialogs Dialogs) NewCleanup()
    {
        var dialogs = new AskingDialogs();
        var appSettings = new AppSettings();
        var settings = new SettingsViewModel(appSettings, dialogs);
        var log = new LogViewModel(dialogs, appSettings);
        var cleanup = new CleanupViewModel(settings, log, new JobQueue(() => 1), () => new LibraryIndex(), dialogs);
        return (cleanup, settings, dialogs);
    }

    private static string? WithSettingsFolder(string folder, Func<string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }

    /// <summary>Records each question and gives <see cref="Answer"/>.</summary>
    internal sealed class AskingDialogs : NoIntrovert.Services.IDialogs
    {
        public bool Answer { get; set; }
        public List<(string Title, string Message, string Yes, bool Destructive)> Asked { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string yes, string no, bool destructive = false)
        {
            Asked.Add((title, message, yes, destructive));
            return Task.FromResult(Answer);
        }

        public Task OpenFolderAsync(string folder) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
        public Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
        public Task<string?> PickFolderAsync(string title) => throw new NotSupportedException();
        public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern) => throw new NotSupportedException();
        public Task SetClipboardAsync(string text) => throw new NotSupportedException();
    }
}
