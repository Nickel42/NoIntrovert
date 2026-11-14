using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

/// <summary>Help › Keyboard shortcuts: the keys and what each does, opened with F1 or Ctrl+/ (⌘/ on macOS).</summary>
public sealed class ShortcutsDialog : Window
{
    public ShortcutsDialog()
    {
        Title = "Keyboard shortcuts";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Application.Current?.FindResource("PanelBackground") as IBrush ?? Brushes.Black;

        var sheet = PageShortcuts.Sheet(OperatingSystem.IsMacOS());
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 18, RowSpacing = 8 };
        for (var i = 0; i < sheet.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var keys = new TextBlock { Text = sheet[i].Keys, FontWeight = FontWeight.SemiBold };
            var what = new TextBlock { Text = sheet[i].What, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(what, i);
            Grid.SetColumn(what, 1);
            grid.Children.Add(keys);
            grid.Children.Add(what);
        }

        CloseButton = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        CloseButton.Click += (_, _) => Close();
        Opened += (_, _) => CloseButton.Focus();
        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = Title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                grid,
                CloseButton,
            },
        };
    }

    public Button CloseButton { get; }
}
