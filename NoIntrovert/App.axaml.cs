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
            BindThemes(vm.Settings.General);
            window.DataContext = vm;
            desktop.MainWindow = window;
            if (desktop.Args is { Length: > 0 } args)
                window.Opened += async (_, _) => await vm.ApplyCommandLineAsync(args);
        }

        base.OnFrameworkInitializationCompleted();
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
