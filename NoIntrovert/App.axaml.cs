using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using NoIntrovert.Core;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;
using SukiUI;
using SukiUI.Models;

namespace NoIntrovert;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // System follows the OS, which can switch while the app runs: the primary shade follows it.
        ActualThemeVariantChanged += (_, _) => ApplyColorTheme();
        ApplyColorTheme();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            ApplyTheme(settings.General.Theme);
            var window = new MainWindow();
            var vm = new MainViewModel(settings, window);
            // Settings › General › Theme applies as soon as it changes.
            vm.Settings.General.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GeneralSettingsViewModel.Theme)) ApplyTheme(vm.Settings.General.Theme);
            };
            window.DataContext = vm;
            desktop.MainWindow = window;
            if (desktop.Args is { Length: > 0 } args)
                window.Opened += async (_, _) => await vm.ApplyCommandLineAsync(args);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The theme variant for a Theme setting: System follows the operating system.</summary>
    public static ThemeVariant VariantFor(AppTheme theme) => theme switch
    {
        AppTheme.Dark => ThemeVariant.Dark,
        AppTheme.Light => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    /// <summary>The design's lilac primary and orange accent: a deeper lilac on the light theme, a lighter one on the dark.</summary>
    private static readonly SukiColorTheme LightColors = new("NoIntrovert Light", Color.Parse("#616283"), Color.Parse("#FF8C42"));
    private static readonly SukiColorTheme DarkColors = new("NoIntrovert Dark", Color.Parse("#7C7EA6"), Color.Parse("#FF8C42"));

    private void ApplyTheme(AppTheme theme)
    {
        RequestedThemeVariant = VariantFor(theme);
        ApplyColorTheme();
    }

    private void ApplyColorTheme()
    {
        if (Styles.OfType<SukiTheme>().FirstOrDefault() is { } suki)
            suki.ChangeColorTheme(ActualThemeVariant == ThemeVariant.Light ? LightColors : DarkColors);
    }
}
