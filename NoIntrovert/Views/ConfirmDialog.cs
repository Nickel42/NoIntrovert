using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Views;

/// <summary>
/// A small yes/no dialog; ShowDialog returns true for the first button.
/// A destructive one makes the safe button (no) the default, with focus,
/// so Enter never deletes anything, and draws the yes button as a danger button.
/// </summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog() : this("", "", "OK", "Cancel") { }

    public ConfirmDialog(string title, string message, string yes, string no, bool destructive = false)
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

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { NoButton, YesButton },
                },
            },
        };
    }

    public bool IsDestructive { get; }
    public Button YesButton { get; }
    public Button NoButton { get; }

    /// <summary>
    /// The one-time "Replace the originals?" question, asked by the run bar
    /// and by Settings › Output the first time Replace the originals is chosen.
    /// False when the user cancels or <paramref name="anchor"/> isn't in a window.
    /// </summary>
    public static async Task<bool> ConfirmReplaceOriginalsAsync(Visual anchor, bool useRecycleBin)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner) return false;
        var fate = useRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently";
        var dialog = new ConfirmDialog("Replace the originals?",
            $"Each original is {fate} once its new file verifies. If verification fails, the original stays and the row shows why. " +
            "You're asked only this once.",
            "Replace the originals", "Cancel", destructive: true);
        return await dialog.ShowDialog<bool>(owner);
    }
}
