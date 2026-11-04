using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Views;

/// <summary>A small yes/no dialog; ShowDialog returns true for the first button.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog() : this("", "", "OK", "Cancel") { }

    public ConfirmDialog(string title, string message, string yes, string no)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (IBrush?)Application.Current?.FindResource("PanelBackground") ?? Brushes.Black;

        var yesButton = new Button { Content = yes, Classes = { "primary" }, IsDefault = true };
        var noButton = new Button { Content = no, IsCancel = true };
        yesButton.Click += (_, _) => Close(true);
        noButton.Click += (_, _) => Close(false);

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
                    Children = { noButton, yesButton },
                },
            },
        };
    }

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
            "Replace the originals", "Cancel");
        return await dialog.ShowDialog<bool>(owner);
    }
}
