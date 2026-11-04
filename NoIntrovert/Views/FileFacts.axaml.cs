using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Views;

/// <summary>
/// The side panel's File section; set its DataContext to a <see cref="FileFactsViewModel"/>.
/// Picks the console picture for the screen's scale (128 px at 1×, 256 px above)
/// and shows the badge's explanation when the pointer rests on it.
/// </summary>
public partial class FileFacts : UserControl
{
    private static readonly Dictionary<string, Bitmap?> Pictures = new(StringComparer.OrdinalIgnoreCase);
    private FileFactsViewModel? _model;

    /// <summary>Section heads are capitals: "FORMATS FOR PLAYSTATION".</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter Upper =
        new Avalonia.Data.Converters.FuncValueConverter<string?, string>(s => (s ?? "").ToUpperInvariant());

    public FileFacts()
    {
        InitializeComponent();
        VerifiedBadge.PointerEntered += (_, _) => VerifiedBadge.Flyout?.ShowAt(VerifiedBadge);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as FileFactsViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
        UpdatePicture();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdatePicture();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileFactsViewModel.IconName)) UpdatePicture();
    }

    private void UpdatePicture()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var picture = _model is { IconName.Length: > 0 } m ? PictureFor(m.IconName, scale) : null;
        TileImage.Source = picture;
        TileImage.IsVisible = picture is not null;
        TileFallback.IsVisible = picture is null;
    }

    /// <summary>
    /// The bundled picture Assets/consoles/icons/&lt;size&gt;/&lt;name&gt;.png, the
    /// sharpest size for the scale that exists, or null when there's none.
    /// </summary>
    private static Bitmap? PictureFor(string name, double scale)
    {
        string[] sizes = scale > 1.0 ? ["256", "128"] : ["128", "256"];
        foreach (var size in sizes)
        {
            var key = $"{size}/{name}";
            if (!Pictures.TryGetValue(key, out var bitmap))
            {
                var uri = new Uri($"avares://NoIntrovert/Assets/consoles/icons/{size}/{name}.png");
                try { bitmap = AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null; }
                catch (Exception ex) when (ex is IOException or ArgumentException) { bitmap = null; }
                Pictures[key] = bitmap;
            }
            if (bitmap is not null) return bitmap;
        }
        return null;
    }
}
