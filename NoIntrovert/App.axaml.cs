using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using NoIntrovert.Core;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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

    private void ApplyTheme(AppTheme theme) => RequestedThemeVariant = VariantFor(theme);
}
