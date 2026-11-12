using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Themes;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>A theme in the Dark theme / Light theme lists: its name and a few swatches of its colours.</summary>
public sealed class ThemeOption(ThemeDefinition theme)
{
    public ThemeDefinition Theme { get; } = theme;
    public string Id => Theme.Id;
    public string Name => Theme.Name;
    public string Origin => Theme.IsBuiltIn ? "Built in" : "Custom";
    public IBrush Background { get; } = new SolidColorBrush(theme[ThemeRole.Background]);
    public IBrush Surface { get; } = new SolidColorBrush(theme[ThemeRole.Surface]);
    public IBrush Primary { get; } = new SolidColorBrush(theme[ThemeRole.Primary]);
    public IBrush Accent { get; } = new SolidColorBrush(theme[ThemeRole.Accent]);
    public IBrush Text { get; } = new SolidColorBrush(theme[ThemeRole.Text]);
    public override string ToString() => Name;
}

/// <summary>
/// Settings › General's themes: which dark and which light theme the app uses, the theme editor, and
/// importing themes (an Avalonia ResourceDictionary, or a Base16/Base24 scheme from a file or the web).
/// App listens to <see cref="Changed"/> and applies <see cref="ActiveDark"/> and <see cref="ActiveLight"/>.
/// </summary>
public sealed partial class ThemesViewModel : ViewModelBase
{
    private readonly AppSettings _s;
    private readonly IDialogs _dialogs;
    private readonly HttpMessageHandler? _http;

    public ThemesViewModel(AppSettings settings, IDialogs dialogs, ThemeLibrary? library = null, HttpMessageHandler? http = null)
    {
        _s = settings;
        _dialogs = dialogs;
        _http = http;
        Library = library ?? new ThemeLibrary(ThemeLibrary.DefaultFolder);
        Refresh();
    }

    public ThemeLibrary Library { get; }

    /// <summary>The colours changed: a theme was chosen, or the editor's preview changed.</summary>
    public event Action? Changed;

    public string GalleryUrl => ThemeLibrary.GalleryUrl;

    [ObservableProperty] private IReadOnlyList<ThemeOption> _darkThemes = [];
    [ObservableProperty] private IReadOnlyList<ThemeOption> _lightThemes = [];

    /// <summary>What the last import, save or delete did, or what went wrong.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>The address Import from web reads (a theme file or a Base16 scheme).</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ImportFromUrlCommand))] private string _importUrl = "";

    /// <summary>The theme being made or changed; null when the editor is closed.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEditing), nameof(PreviewKind))] private ThemeEditorViewModel? _editor;

    public bool IsEditing => Editor is not null;

    /// <summary>While the editor is open, the kind it edits is shown, whatever Theme says, so the preview can be seen.</summary>
    public ThemeKind? PreviewKind => Editor?.Kind;

    public ThemeOption? SelectedDark
    {
        get => DarkThemes.FirstOrDefault(o => o.Id == ActiveId(ThemeKind.Dark));
        set { if (value is not null) Choose(ThemeKind.Dark, value.Id); }
    }

    public ThemeOption? SelectedLight
    {
        get => LightThemes.FirstOrDefault(o => o.Id == ActiveId(ThemeKind.Light));
        set { if (value is not null) Choose(ThemeKind.Light, value.Id); }
    }

    public ThemeDefinition ActiveDark => Editor is { Kind: ThemeKind.Dark } e ? e.Preview : Library.Resolve(_s.General.DarkTheme, ThemeKind.Dark);
    public ThemeDefinition ActiveLight => Editor is { Kind: ThemeKind.Light } e ? e.Preview : Library.Resolve(_s.General.LightTheme, ThemeKind.Light);

    private string ActiveId(ThemeKind kind) => Library.Resolve(kind == ThemeKind.Dark ? _s.General.DarkTheme : _s.General.LightTheme, kind).Id;

    public ThemeDefinition Chosen(ThemeKind kind) => Library.Resolve(kind == ThemeKind.Dark ? _s.General.DarkTheme : _s.General.LightTheme, kind);

    /// <summary>Uses theme <paramref name="id"/> for its kind, and saves that.</summary>
    public void Choose(ThemeKind kind, string id)
    {
        var current = kind == ThemeKind.Dark ? _s.General.DarkTheme : _s.General.LightTheme;
        if (current == id) return;
        if (kind == ThemeKind.Dark) _s.General.DarkTheme = id; else _s.General.LightTheme = id;
        _s.Save();
        OnPropertyChanged(kind == ThemeKind.Dark ? nameof(SelectedDark) : nameof(SelectedLight));
        Changed?.Invoke();
    }

    /// <summary>Reads the themes folder again (a file added by hand shows up).</summary>
    [RelayCommand]
    public void Refresh()
    {
        Library.Reload();
        DarkThemes = Library.OfKind(ThemeKind.Dark).Select(t => new ThemeOption(t)).ToList();
        LightThemes = Library.OfKind(ThemeKind.Light).Select(t => new ThemeOption(t)).ToList();
        OnPropertyChanged(nameof(SelectedDark));
        OnPropertyChanged(nameof(SelectedLight));
        if (Library.Problems.Count > 0) Status = "Couldn't read " + string.Join("; ", Library.Problems);
        Changed?.Invoke();
    }

    private static ThemeKind KindOf(string? kind) => kind == "Light" ? ThemeKind.Light : ThemeKind.Dark;

    /// <summary>
    /// Opens the editor on the chosen theme of that kind: a custom theme is changed in place; a built-in
    /// one is copied ("My Ember"), so the built-in stays as it is.
    /// </summary>
    [RelayCommand]
    public void Customize(string? kind)
    {
        var theme = Chosen(KindOf(kind));
        Editor = new ThemeEditorViewModel(this, theme, editsOwn: !theme.IsBuiltIn);
        Status = "";
        Changed?.Invoke();
    }

    internal void PreviewChanged() => Changed?.Invoke();

    /// <summary>Saves the editor's theme in the themes folder and uses it for its kind.</summary>
    internal void SaveEditor(ThemeEditorViewModel editor)
    {
        var name = editor.Name.Trim();
        if (name.Length == 0) { Status = "Give the theme a name first."; return; }
        // The same name as another theme of this kind would be hard to tell apart in the list.
        if (Library.OfKind(editor.Kind).Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && t.Id != editor.OwnId))
        {
            Status = $"There's already a {editor.Kind.ToString().ToLowerInvariant()} theme called {name}. Choose another name.";
            return;
        }
        try
        {
            var saved = Library.Save(editor.Build(name));
            Editor = null;
            Refresh();
            Choose(saved.Kind, saved.Id);
            Status = $"Saved {saved.Name} in the themes folder.";
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Couldn't save the theme: " + ex.Message;
        }
    }

    internal void CloseEditor()
    {
        Editor = null;
        Status = "";
        Changed?.Invoke();
    }

    /// <summary>Removes the theme being edited from the themes folder; its kind goes back to the default theme if it was chosen.</summary>
    internal void DeleteEditor(ThemeEditorViewModel editor)
    {
        if (Library.Find(editor.OwnId) is not { IsBuiltIn: false } theme) return;
        try
        {
            Library.Delete(theme);
            Editor = null;
            if (ActiveId(theme.Kind) != theme.Id || Library.Find(theme.Id) is null)
                Choose(theme.Kind, theme.Kind == ThemeKind.Dark ? ThemeLibrary.DefaultDarkId : ThemeLibrary.DefaultLightId);
            Refresh();
            Status = $"Deleted {theme.Name}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Couldn't delete the theme: " + ex.Message;
        }
    }

    /// <summary>Imports theme files (.axaml, or Base16/Base24 .yaml) into the themes folder and uses the first of each kind.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        var files = await _dialogs.PickFilesAsync("Import themes", "Themes (Avalonia .axaml, Base16 .yaml)", ["*.axaml", "*.xaml", "*.yaml", "*.yml"]);
        if (files.Count == 0) return;
        var imported = new List<ThemeDefinition>();
        var problems = new List<string>();
        foreach (var file in files)
        {
            try { imported.AddRange(Library.Import(file)); }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        Imported(imported, problems);
    }

    /// <summary>Downloads a theme file or Base16 scheme (a GitHub page's address works too) and imports it.</summary>
    [RelayCommand(CanExecute = nameof(CanImportFromUrl))]
    private async Task ImportFromUrlAsync()
    {
        Status = "Downloading…";
        try
        {
            var imported = await Library.ImportUrlAsync(ImportUrl, _http);
            ImportUrl = "";
            Imported(imported, []);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            Status = "Couldn't import it: " + (ex is TaskCanceledException ? "the server didn't answer in time." : ex.Message);
        }
    }

    private bool CanImportFromUrl() => !string.IsNullOrWhiteSpace(ImportUrl);

    private void Imported(IReadOnlyList<ThemeDefinition> imported, IReadOnlyList<string> problems)
    {
        Refresh();
        foreach (var kind in new[] { ThemeKind.Dark, ThemeKind.Light })
            if (imported.FirstOrDefault(t => t.Kind == kind) is { } first) Choose(kind, first.Id);
        var parts = new List<string>();
        if (imported.Count > 0)
            parts.Add("Imported " + string.Join(", ", imported.Select(t => $"{t.Name} ({t.Kind.ToString().ToLowerInvariant()})")) + ".");
        if (problems.Count > 0) parts.Add("Couldn't import " + string.Join("; ", problems));
        Status = string.Join(" ", parts);
    }

    /// <summary>Saves the chosen theme of that kind as an .axaml file, to share or to use in another Avalonia app.</summary>
    [RelayCommand]
    private async Task ExportAsync(string? kind)
    {
        var theme = Chosen(KindOf(kind));
        var path = await _dialogs.SaveFileAsync("Export theme", ThemeFile.Slug(theme.Name) + ".axaml", "Avalonia theme", "*.axaml");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, ThemeFile.Write(theme));
            Status = $"Exported {theme.Name} to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Couldn't export the theme: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        Directory.CreateDirectory(Library.Folder);
        await _dialogs.OpenFolderAsync(Library.Folder);
    }
}

/// <summary>The theme editor: a name, and a colour for each role, previewed in the app as they change.</summary>
public sealed partial class ThemeEditorViewModel : ViewModelBase
{
    private readonly ThemesViewModel _owner;
    private readonly ThemeDefinition _base;

    public ThemeEditorViewModel(ThemesViewModel owner, ThemeDefinition theme, bool editsOwn)
    {
        _owner = owner;
        _base = theme;
        EditsOwn = editsOwn;
        _name = editsOwn ? theme.Name : "My " + theme.Name;
        Rows = new(ThemeRoles.All.Select(r => new ThemeColorRow(r, theme[r], Preview_Changed)));
        Preview = Build(_name);
    }

    public ThemeKind Kind => _base.Kind;

    /// <summary>The theme's id when it's a custom one being changed (Save writes over its file).</summary>
    public string? OwnId => EditsOwn ? _base.Id : null;

    public bool EditsOwn { get; }

    public string Title => (EditsOwn ? "Edit " : "New ") + Kind.ToString().ToLowerInvariant() + " theme";

    public string BasedOn => EditsOwn ? $"Changing {_base.Name}." : $"Starting from {_base.Name}, which stays as it is.";

    [ObservableProperty] private string _name;

    public ObservableCollection<ThemeColorRow> Rows { get; }

    /// <summary>The theme as it stands, which the app shows while the editor is open.</summary>
    public ThemeDefinition Preview { get; private set; }

    private void Preview_Changed()
    {
        Preview = Build(Name);
        _owner.PreviewChanged();
    }

    /// <summary>
    /// The theme with the editor's colours. The keys a theme file set directly are left out: they'd keep
    /// a colour from following the role it was made from (a built-in's tuned shades follow the new roles).
    /// </summary>
    public ThemeDefinition Build(string name) => new()
    {
        Id = EditsOwn ? _base.Id : "new",
        Name = name, Kind = Kind, Author = EditsOwn ? _base.Author : "", IsBuiltIn = false,
        FilePath = EditsOwn ? _base.FilePath : null,
        Roles = Rows.ToDictionary(r => r.Role, r => r.Color),
    };

    [RelayCommand] private void Save() => _owner.SaveEditor(this);
    [RelayCommand] private void Cancel() => _owner.CloseEditor();
    [RelayCommand] private void Delete() => _owner.DeleteEditor(this);

    /// <summary>Puts every colour back as the theme had it.</summary>
    [RelayCommand]
    private void Reset()
    {
        foreach (var row in Rows) row.Color = _base[row.Role];
    }
}

/// <summary>One role in the editor: its name and colour.</summary>
public sealed partial class ThemeColorRow(ThemeRole role, Color color, Action changed) : ViewModelBase
{
    public ThemeRole Role { get; } = role;
    public string Label => ThemeRoles.Label(Role);

    private Color _color = color;

    public Color Color
    {
        get => _color;
        set
        {
            // Opaque only: a see-through background would show whatever is behind the window.
            value = Color.FromRgb(value.R, value.G, value.B);
            if (!SetProperty(ref _color, value)) return;
            OnPropertyChanged(nameof(Hex));
            changed();
        }
    }

    /// <summary>The colour as #RRGGBB, which can be typed or pasted.</summary>
    public string Hex
    {
        get => ThemeRoles.Hex(Color);
        set
        {
            var text = value.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            if (Color.TryParse(text, out var c)) Color = c;
        }
    }
}
