using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using NoIntrovert.Core;
using NoIntrovert.Core.Themes;
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
        // The default themes until the settings are read (and in the designer and tests that don't read any).
        ApplyThemes(ThemeLibrary.BuiltIn.First(t => t.Id == ThemeLibrary.DefaultDarkId),
            ThemeLibrary.BuiltIn.First(t => t.Id == ThemeLibrary.DefaultLightId));
        // System follows the OS, which can switch while the app runs: the primary shade follows it.
        ActualThemeVariantChanged += (_, _) => ApplyColorTheme();
        ApplyColorTheme();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            var window = new MainWindow();
            var vm = new MainViewModel(settings, window);
            CatchUnhandled(vm.Log);
            BindThemes(vm.Settings.General);
            window.DataContext = vm;
            desktop.MainWindow = window;
            // The Library's match results, read once the window shows so the first selection doesn't wait for them.
            window.Opened += (_, _) => Core.Dats.MatchStore.WarmInBackground();
            if (desktop.Args is { Length: > 0 } args)
                window.Opened += async (_, _) => await vm.ApplyCommandLineAsync(args);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The last resort for errors nothing else caught: one on the UI thread
    /// (a command's file error, say) is logged and the app keeps running,
    /// rather than closing and taking a running batch with it. A task's
    /// error nobody looked at is logged too. An error on another thread
    /// still ends the app (.NET allows nothing else), but is logged first, so
    /// the session log says why.
    /// </summary>
    private static void CatchUnhandled(ILog log)
    {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            ReportUnhandled(log, e.Exception, fatal: false);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var all = e.Exception.Flatten();
            ReportUnhandled(log, all.InnerExceptions.Count == 1 ? all.InnerExceptions[0] : all, fatal: false);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ReportUnhandled(log, ex, fatal: e.IsTerminating);
        };
    }

    /// <summary>Logs an error nothing else caught: a short message for the user, and the details as a debug line (in full when the app must close).</summary>
    internal static void ReportUnhandled(ILog log, Exception ex, bool fatal)
    {
        try
        {
            if (fatal)
                log.Error($"NoIntrovert ran into a problem it can't recover from and has to close ({ex.Message.TrimEnd('.')}).{Environment.NewLine}{ex}");
            else
            {
                log.Error($"Something went wrong ({ex.Message.TrimEnd('.')}). NoIntrovert kept running; if anything looks wrong, restart it.");
                log.Debug(ex.ToString());
            }
        }
        catch (Exception) { } // the log itself failed: nothing more can be done
    }

    /// <summary>
    /// Settings › General › Theme, Dark theme and Light theme (and the theme editor's preview) apply as soon
    /// as they change.
    /// </summary>
    public void BindThemes(GeneralSettingsViewModel general)
    {
        void Apply()
        {
            ApplyThemes(general.ColorThemes.ActiveDark, general.ColorThemes.ActiveLight);
            RequestedThemeVariant = general.ColorThemes.PreviewKind is { } preview
                ? (preview == ThemeKind.Dark ? ThemeVariant.Dark : ThemeVariant.Light)
                : VariantFor(general.Theme);
            ApplyColorTheme();
        }
        Apply();
        general.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GeneralSettingsViewModel.Theme)) Apply();
        };
        general.ColorThemes.Changed += Apply;
    }

    /// <summary>The theme variant for a Theme setting: System follows the operating system.</summary>
    public static ThemeVariant VariantFor(AppTheme theme) => theme switch
    {
        AppTheme.Dark => ThemeVariant.Dark,
        AppTheme.Light => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    private ThemeDefinition? _dark, _light;
    private readonly Dictionary<ThemeVariant, HashSet<string>> _themeKeys = new();

    /// <summary>
    /// Puts each theme's colours into Application.Resources' Dark and Light dictionaries, so whichever
    /// variant is showing (System can switch while the app runs) has its own theme's colours.
    /// </summary>
    public void ApplyThemes(ThemeDefinition dark, ThemeDefinition light)
    {
        _dark = dark;
        _light = light;
        var changed = Fill(ThemeVariant.Dark, dark) | Fill(ThemeVariant.Light, light);
        // A change inside a theme dictionary isn't announced to the windows: one change at the top is,
        // and every {DynamicResource} looks again (once, however many colours changed).
        if (changed) Resources["ThemeRevision"] = ++_revision;
        ApplyColorTheme();
    }

    private int _revision;

    /// <returns>Whether any colour changed.</returns>
    private bool Fill(ThemeVariant variant, ThemeDefinition theme)
    {
        if (!Resources.ThemeDictionaries.TryGetValue(variant, out var provider) || provider is not ResourceDictionary dict) return false;
        var changed = false;
        var values = ThemeResources.Build(theme);
        // A key only the last theme set (one of its own) goes, so it doesn't linger.
        if (_themeKeys.TryGetValue(variant, out var old))
            foreach (var key in old.Where(k => !values.ContainsKey(k))) changed |= dict.Remove(key);
        foreach (var (key, value) in values)
            if (!dict.TryGetValue(key, out var current) || !SameColour(current, value))
            {
                dict[key] = value;
                changed = true;
            }
        _themeKeys[variant] = values.Keys.ToHashSet();
        return changed;
    }

    private static bool SameColour(object? a, object b) => (a, b) switch
    {
        (Color x, Color y) => x == y,
        (ISolidColorBrush x, ISolidColorBrush y) => x.Color == y.Color && x.Opacity == y.Opacity,
        _ => false,
    };

    private (string, Color, Color)? _sukiColors;

    private void ApplyColorTheme()
    {
        // SukiUI keeps its primary outside the theme dictionaries: the theme showing sets it.
        var theme = ActualThemeVariant == ThemeVariant.Light ? _light : _dark;
        if (theme is null || Styles.OfType<SukiTheme>().FirstOrDefault() is not { } suki) return;
        var key = (theme.Id, theme[ThemeRole.Primary], theme[ThemeRole.Accent]);
        if (key == _sukiColors) return;
        _sukiColors = key;
        suki.ChangeColorTheme(new SukiColorTheme(theme.Name, theme[ThemeRole.Primary], theme[ThemeRole.Accent]));
    }
}
