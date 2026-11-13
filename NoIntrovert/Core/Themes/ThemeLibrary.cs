using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

namespace NoIntrovert.Core.Themes;

/// <summary>
/// The themes to choose from: the built-in ones (NoIntrovert/Themes, embedded) and every theme file in the
/// themes folder (%AppData%\NoIntrovert\themes), where custom and imported themes are saved. A file
/// dropped into the folder by hand shows up the next time the list is read.
/// </summary>
public sealed class ThemeLibrary
{
    public const string DefaultDarkId = "nointrovert-dark";
    public const string DefaultLightId = "nointrovert-light";
    public const string UserPrefix = "user:";

    /// <summary>Where tinted-theming's Base16 schemes can be browsed, each a file Import from URL takes.</summary>
    public const string GalleryUrl = "https://tinted-theming.github.io/tinted-gallery/";

    /// <summary>The built-in themes, in the order the lists show them (each kind's default first).</summary>
    private static readonly string[] BuiltInIds = [DefaultDarkId, "ember", "cartridge", DefaultLightId, "sunrise", "seafoam"];

    private static readonly string[] Extensions = [".axaml", ".xaml", ".yaml", ".yml"];

    private static IReadOnlyList<ThemeDefinition>? _builtIn;
    private List<ThemeDefinition> _themes = [];

    public ThemeLibrary(string folder)
    {
        Folder = folder;
        Reload();
    }

    public static string DefaultFolder => Path.Combine(AppSettings.SettingsDirectory, "themes");

    public string Folder { get; }

    /// <summary>The problems reading the folder's files met, by file name (shown under the lists).</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public IReadOnlyList<ThemeDefinition> Themes => _themes;

    /// <summary>The themes of one kind as the lists show them: NoIntrovert's, then custom and imported ones, then the Base16 schemes A to Z.</summary>
    public IEnumerable<ThemeDefinition> OfKind(ThemeKind kind) => _themes.Where(t => t.Kind == kind).OrderBy(t => t.Collection); // a stable sort


    public static IReadOnlyList<ThemeDefinition> BuiltIn => _builtIn ??= LoadBuiltIn();

    private static IReadOnlyList<ThemeDefinition> LoadBuiltIn()
    {
        var asm = Assembly.GetExecutingAssembly();
        var list = new List<ThemeDefinition>();
        foreach (var id in BuiltInIds)
        {
            using var stream = asm.GetManifestResourceStream($"NoIntrovert.Themes.{id}.axaml")
                ?? throw new InvalidOperationException($"The built-in theme {id} isn't embedded.");
            using var reader = new StreamReader(stream);
            list.AddRange(ThemeFile.Parse(reader.ReadToEnd(), id, builtIn: true));
        }
        // Then tinted-theming's Base16 schemes, A to Z by name: "base16-" and the scheme's file name.
        const string prefix = "NoIntrovert.Themes.Base16.";
        var schemes = new List<ThemeDefinition>();
        foreach (var resource in asm.GetManifestResourceNames().Where(r => r.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var file = resource[prefix.Length..];
            using var stream = asm.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            schemes.Add(Base16.Parse(reader.ReadToEnd(), Base16Prefix + Path.GetFileNameWithoutExtension(file),
                Path.GetFileNameWithoutExtension(file), builtIn: true));
        }
        list.AddRange(schemes.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>The start of a built-in Base16 scheme's id ("base16-gruvbox-dark-hard").</summary>
    public const string Base16Prefix = "base16-";

    /// <summary>Reads the folder again.</summary>
    public void Reload()
    {
        var themes = new List<ThemeDefinition>(BuiltIn);
        var problems = new List<string>();
        if (Directory.Exists(Folder))
        {
            foreach (var file in Directory.EnumerateFiles(Folder).Where(IsThemeFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (new FileInfo(file).Length > ThemeFile.MaxBytes) throw new FormatException("The file is too big to be a theme.");
                    themes.AddRange(ThemeFile.Parse(File.ReadAllText(file), UserPrefix + Path.GetFileNameWithoutExtension(file),
                        Path.GetFileNameWithoutExtension(file), path: file));
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        _themes = themes;
        Problems = problems;
    }

    private static bool IsThemeFile(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public ThemeDefinition? Find(string? id) => id is null ? null : _themes.FirstOrDefault(t => t.Id == id);

    /// <summary>The theme <paramref name="id"/> names if it's there and of that kind, otherwise the kind's default.</summary>
    public ThemeDefinition Resolve(string? id, ThemeKind kind) =>
        Find(id) is { } t && t.Kind == kind ? t : Find(kind == ThemeKind.Dark ? DefaultDarkId : DefaultLightId)!;

    /// <summary>
    /// Saves <paramref name="theme"/> in the folder as .axaml: over its own file when it's a theme from the
    /// folder, otherwise in a new file named after it. Returns the theme as the folder now has it.
    /// </summary>
    public ThemeDefinition Save(ThemeDefinition theme)
    {
        Directory.CreateDirectory(Folder);
        var path = theme is { IsBuiltIn: false, FilePath: { } own } && Path.GetExtension(own).Equals(".axaml", StringComparison.OrdinalIgnoreCase)
            && File.Exists(own) ? own : FreePath(ThemeFile.Slug(theme.Name));
        File.WriteAllText(path, ThemeFile.Write(theme), new UTF8Encoding(false));
        // An imported .yaml that was edited is now this .axaml; the scheme it came from goes.
        if (theme is { IsBuiltIn: false, FilePath: { } old } && old != path && File.Exists(old) && Path.GetDirectoryName(old) == Folder)
            File.Delete(old);
        Reload();
        return Find(UserPrefix + Path.GetFileNameWithoutExtension(path))!;
    }

    private string FreePath(string slug)
    {
        for (var i = 1; ; i++)
        {
            var stem = i == 1 ? slug : $"{slug}-{i}";
            if (!Extensions.Any(e => File.Exists(Path.Combine(Folder, stem + e)))) return Path.Combine(Folder, stem + ".axaml");
        }
    }

    /// <summary>Removes a theme of the folder (built-in themes can't be).</summary>
    public void Delete(ThemeDefinition theme)
    {
        if (theme.IsBuiltIn || theme.FilePath is null) return;
        if (File.Exists(theme.FilePath)) File.Delete(theme.FilePath);
        Reload();
    }

    /// <summary>Reads a theme file (or Base16 scheme) and saves each theme in it into the folder.</summary>
    /// <exception cref="FormatException">It isn't a theme.</exception>
    public IReadOnlyList<ThemeDefinition> Import(string path)
    {
        if (new FileInfo(path).Length > ThemeFile.MaxBytes) throw new FormatException("The file is too big to be a theme.");
        return ImportText(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Saves each theme in <paramref name="text"/> into the folder, as .axaml.</summary>
    public IReadOnlyList<ThemeDefinition> ImportText(string text, string fallbackName)
    {
        var parsed = ThemeFile.Parse(text, "import", fallbackName);
        return parsed.Select(t => Save(Copy(t, t.Name, null))).ToList();
    }

    /// <summary>
    /// Downloads a theme file or Base16 scheme and imports it. A GitHub page's address ("…/blob/…") is
    /// read from raw.githubusercontent.com.
    /// </summary>
    public async Task<IReadOnlyList<ThemeDefinition>> ImportUrlAsync(string url, HttpMessageHandler? handler = null, CancellationToken token = default)
    {
        var uri = RawUri(url);
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NoIntrovert", "1.0"));
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ThemeFile.MaxBytes) throw new FormatException("The file is too big to be a theme.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > ThemeFile.MaxBytes) throw new FormatException("The file is too big to be a theme.");
        }
        var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        return ImportText(Encoding.UTF8.GetString(buffer.ToArray()), name.Length > 0 ? name : "Downloaded theme");
    }

    /// <summary>An http(s) address, with GitHub's file pages turned into their raw file.</summary>
    /// <exception cref="FormatException">It isn't an http(s) address.</exception>
    public static Uri RawUri(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new FormatException("Enter a web address starting with https://");
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            // github.com/owner/repo/blob/ref/path → raw.githubusercontent.com/owner/repo/ref/path
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length >= 5 && parts[2] is "blob" or "raw")
                return new Uri($"https://raw.githubusercontent.com/{parts[0]}/{parts[1]}/{string.Join('/', parts.Skip(3))}");
        }
        return uri;
    }

    /// <summary>A copy of <paramref name="theme"/> as a theme of the folder: renamed, with its file (or none, for a new one).</summary>
    public static ThemeDefinition Copy(ThemeDefinition theme, string name, string? path,
        IReadOnlyDictionary<ThemeRole, Avalonia.Media.Color>? roles = null) => new()
    {
        Id = path is null ? "new" : UserPrefix + Path.GetFileNameWithoutExtension(path),
        Name = name, Kind = theme.Kind, Author = theme.Author, IsBuiltIn = false, FilePath = path,
        Roles = roles ?? theme.Roles,
        Overrides = theme.Overrides,
    };
}
