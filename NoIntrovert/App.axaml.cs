using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            var window = new MainWindow();
            var vm = new MainViewModel(settings, window);
            window.DataContext = vm;
            desktop.MainWindow = window;
            if (desktop.Args is { Length: > 0 } args)
                window.Opened += async (_, _) => await vm.ApplyCommandLineAsync(args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
