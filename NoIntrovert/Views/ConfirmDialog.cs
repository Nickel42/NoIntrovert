using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

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
}
