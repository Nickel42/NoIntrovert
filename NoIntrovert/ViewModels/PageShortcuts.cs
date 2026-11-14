using Avalonia.Input;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The keys that switch pages: Ctrl+1 Optimize, Ctrl+2 Convert, Ctrl+3 Verify,
/// Ctrl+4 Organize, Ctrl+, Settings (Cmd on macOS; Ctrl works there too, as the
/// job list's shortcuts do). None of them is a key a text box uses, so they work
/// while one has focus.
/// </summary>
public static class PageShortcuts
{
    /// <summary>Each page with a shortcut, and the key after Ctrl/Cmd as the tooltip shows it.</summary>
    public static IReadOnlyList<(string PageId, string Key)> All { get; } =
    [
        ("optimize", "1"), ("convert", "2"), ("verify", "3"), ("cleanup", "4"), ("settings", ","),
    ];

    /// <summary>The page <paramref name="key"/> with <paramref name="modifiers"/> opens; null when it isn't a page shortcut.</summary>
    public static string? PageFor(Key key, KeyModifiers modifiers)
    {
        if (modifiers is not (KeyModifiers.Control or KeyModifiers.Meta)) return null;
        var name = key switch
        {
            Key.D1 or Key.NumPad1 => "1",
            Key.D2 or Key.NumPad2 => "2",
            Key.D3 or Key.NumPad3 => "3",
            Key.D4 or Key.NumPad4 => "4",
            Key.OemComma => ",",
            _ => null,
        };
        return name is null ? null : All.FirstOrDefault(s => s.Key == name).PageId;
    }

    /// <summary>"Ctrl+2" (⌘2 on macOS) for a page with a shortcut, otherwise "".</summary>
    public static string LabelFor(string pageId, bool macOS) =>
        All.FirstOrDefault(s => s.PageId == pageId).Key is { } key ? (macOS ? "⌘" : "Ctrl+") + key : "";

    public static string LabelFor(string pageId) => LabelFor(pageId, OperatingSystem.IsMacOS());

    /// <summary>The Keyboard shortcuts sheet (F1 or Ctrl+/): every shortcut the window and the job list answer, with what it does.</summary>
    public static IReadOnlyList<(string Keys, string What)> Sheet(bool macOS)
    {
        var mod = macOS ? "⌘" : "Ctrl+";
        return
        [
            ($"{mod}1", "Optimize"),
            ($"{mod}2", "Convert"),
            ($"{mod}3", "Verify"),
            ($"{mod}4", "Organize"),
            ($"{mod},", "Settings"),
            ($"{mod}O", "Add files to the list"),
            ($"{mod}F", "Filter the rows"),
            ("Escape", "Clear the filter, or stop looking through a folder"),
            ($"{mod}A", "Select every row shown"),
            ($"{mod}Enter", "Start"),
            ("Delete", "Remove the selected rows from the list"),
            (macOS ? "Control-click" : "Shift+F10 or the Menu key", "The row's menu (as a right-click)"),
            (macOS ? "F1 or ⌘/" : "F1 or Ctrl+/", "This list"),
        ];
    }

    /// <summary>Whether <paramref name="key"/> opens the Keyboard shortcuts sheet: F1, or Ctrl+/ (⌘/).</summary>
    public static bool OpensSheet(Key key, KeyModifiers modifiers) =>
        (key == Key.F1 && modifiers == KeyModifiers.None)
        || (key is Key.OemQuestion or Key.Divide && modifiers is KeyModifiers.Control or KeyModifiers.Meta);
}
