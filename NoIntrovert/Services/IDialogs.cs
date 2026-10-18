namespace NoIntrovert.Services;

/// <summary>File pickers and the clipboard, implemented by the main window.</summary>
public interface IDialogs
{
    Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, IReadOnlyList<string> patterns);
    Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns);
    Task<string?> PickFolderAsync(string title);
    Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern);
    Task SetClipboardAsync(string text);
}
