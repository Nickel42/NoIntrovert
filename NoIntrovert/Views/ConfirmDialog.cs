using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NoIntrovert.Core.Jobs;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

/// <summary>
/// A small yes/no dialog; ShowDialog returns true for the first button.
/// A destructive one makes the safe button (no) the default, with focus,
/// so Enter never deletes anything, and draws the yes button as a danger button.
/// </summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog() : this("", "", "OK", "Cancel") { }

    public ConfirmDialog(string title, string message, string yes, string no, bool destructive = false, string? option = null)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Application.Current?.FindResource("PanelBackground") as IBrush ?? Brushes.Black;
        IsDestructive = destructive;

        YesButton = new Button { Content = yes, IsDefault = !destructive };
        YesButton.Classes.Add(destructive ? "danger" : "primary");
        NoButton = new Button { Content = no, IsCancel = true, IsDefault = destructive };
        YesButton.Click += (_, _) => Close(true);
        NoButton.Click += (_, _) => Close(false);
        // The default button starts with focus, so Space and Enter both pick it.
        Opened += (_, _) => (destructive ? NoButton : YesButton).Focus();

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            },
        };
        if (option is not null)
        {
            Option = new CheckBox { Content = option };
            body.Children.Add(Option);
        }
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { NoButton, YesButton },
        });
        Content = body;
    }

    public bool IsDestructive { get; }
    public Button YesButton { get; }
    public Button NoButton { get; }

    /// <summary>The checkbox under the message ("Don't ask again…"), when the dialog has one.</summary>
    public CheckBox? Option { get; }

    /// <summary>What happens to an original once its new file verifies: "moved to the Recycle Bin" or "deleted permanently".</summary>
    private static string Fate(bool useRecycleBin) =>
        useRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently";

    /// <summary>
    /// The one-time "Replace the originals?" question Settings › Output asks
    /// the first time Replace the originals is made the default.
    /// False when the user cancels or <paramref name="anchor"/> isn't in a window.
    /// </summary>
    public static async Task<bool> ConfirmReplaceOriginalsAsync(Visual anchor, bool useRecycleBin)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner) return false;
        var dialog = new ConfirmDialog("Make Replace the originals the default?",
            $"Each original is {Fate(useRecycleBin)} once its new file verifies. If verification fails, the original stays and the row shows why. " +
            "Each run that replaces originals still asks before it starts.",
            "Replace the originals", "Cancel", destructive: true);
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>The run bar's own words for a count of originals: "1 original", "29 originals".</summary>
    private static string Originals(int count) => count == 1 ? "1 original" : $"{count:N0} originals";

    /// <summary>
    /// "Replace 29 originals?", asked as a run with Replace the originals starts.
    /// Cancel is the default. Not confirmed when the user cancels or
    /// <paramref name="anchor"/> isn't in a window.
    /// </summary>
    public static async Task<ReplaceAnswer> ConfirmReplaceRunAsync(Visual anchor, int count, bool useRecycleBin)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner) return new(false);
        var dialog = new ConfirmDialog($"Replace {Originals(count)}?",
            $"Each original is removed only after its new file verifies, and is then {Fate(useRecycleBin)}. " +
            "If verification fails, the original stays and the row shows why.",
            $"Replace {Originals(count)}", "Cancel", destructive: true,
            option: "Don't ask again until NoIntrovert restarts");
        var yes = await dialog.ShowDialog<bool>(owner);
        return new(yes, yes && dialog.Option?.IsChecked == true);
    }
}
