using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using NoIntrovert.Controls;

// GlowBorder demo: cards and buttons in both modes. Pass "light" for the light theme.
class App : Application
{
    public override void Initialize() { Styles.Add(new FluentTheme()); }

    public override void OnFrameworkInitializationCompleted()
    {
        var dark = !Environment.GetCommandLineArgs().Contains("light");
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var panel = Brush.Parse(dark ? "#1E2128" : "#FFFFFF");
        var border = Brush.Parse(dark ? "#2C3039" : "#DDE0E5");

        Control Card(string title, string sub) => new Border
        {
            Background = panel, BorderBrush = border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = sub, Opacity = 0.7 },
                },
            },
        };

        var glows = new List<GlowBorder>
        {
            new() { Child = Card("Preparing your report", "Gathering the latest sales data.") },
            new() { Mode = GlowBorderMode.Pulse, Child = Card("Report ready", "Your sales summary is ready to view.") },
            new() { GlowSize = 10, Child = new Button { Content = "Travel button" } },
            new() { Mode = GlowBorderMode.Pulse, GlowSize = 12, Child = new Button { Content = "Pulse button" } },
            new()
            {
                Mode = GlowBorderMode.Pulse, Palette = GlowBorderPalette.Cool, GlowSize = 12,
                Child = new Button { Content = "Start", Background = Brush.Parse("#E0703A"), Foreground = Brushes.White, Padding = new Thickness(18, 7) },
            },
        };

        // Turning the effect off and on shows the fade.
        var active = new CheckBox { Content = "Active", IsChecked = true };
        active.IsCheckedChanged += (_, _) => glows.ForEach(g => g.IsActive = active.IsChecked == true);

        var stack = new StackPanel { Margin = new Thickness(24), Spacing = 18, Width = 380 };
        stack.Children.Add(glows[0]);
        stack.Children.Add(glows[1]);
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { glows[2], glows[3], glows[4] } });
        stack.Children.Add(active);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window
            {
                Title = "GlowBorder demo", Width = 430, Height = 360,
                Background = Brush.Parse(dark ? "#16181D" : "#F3F4F6"), Content = stack,
            };
        base.OnFrameworkInitializationCompleted();
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args) => AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}
