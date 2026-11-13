namespace NoIntrovert.Services;

/// <summary>File pickers, the clipboard and yes/no questions, implemented by the main window.</summary>
public interface IDialogs
{
    Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, IReadOnlyList<string> patterns);
    Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns);
    Task<string?> PickFolderAsync(string title);
    Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern);
    Task SetClipboardAsync(string text);

    /// <summary>Opens the system file manager at <paramref name="folder"/>.</summary>
    Task OpenFolderAsync(string folder);

    /// <summary>
    /// Asks a yes/no question; true when the user picks <paramref name="yes"/>.
    /// With <paramref name="destructive"/>, <paramref name="no"/> is the default
    /// button (Enter) and has focus, and <paramref name="yes"/> looks like a danger button.
    /// Without a window to ask in, the answer is no, so nothing is lost.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string yes, string no, bool destructive = false) => Task.FromResult(false);
}
