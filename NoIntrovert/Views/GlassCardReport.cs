using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Diagnostics;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using SukiUI.Controls;

namespace NoIntrovert.Views;

/// <summary>
/// What every SukiUI GlassCard in the open windows looks like, for the session log when one throws
/// "Could not find control 'RootPanel'" inside a layout pass: where each sits, whether its template is applied and
/// has the part, and what set its IsAnimated (the card returns before the lookup when it's false). The card that
/// threw is the one whose template isn't applied yet.
/// </summary>
internal static class GlassCardReport
{
    /// <summary>Whether <paramref name="ex"/> is that error.</summary>
    public static bool IsRootPanelError(Exception ex) =>
        ex is KeyNotFoundException && ex.Message.Contains("RootPanel") && (ex.StackTrace?.Contains("GlassCard") ?? false);

    /// <param name="windows">The windows to look in; by default the app's open windows.</param>
    public static string Describe(IEnumerable<Window>? windows = null)
    {
        var text = new StringBuilder();
        windows ??= Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.ToList() : [];
        var number = 0;
        foreach (var window in windows)
        {
            var cards = window.GetVisualDescendants().OfType<GlassCard>().ToList();
            text.AppendLine($"Window '{window.Title}': {cards.Count} GlassCards; not yet templated listed first");
            foreach (var card in cards.OrderBy(c => c.GetVisualChildren().Any()))
            {
                number++;
                var children = card.GetVisualDescendants().OfType<Control>().ToList();
                var templated = card.GetVisualChildren().Any();
                text.AppendLine($"#{number} templated={templated} RootPanel={children.Any(c => c.Name == "RootPanel")} " +
                                $"measured={card.IsMeasureValid} visible={card.IsEffectivelyVisible} classes='{string.Join(",", card.Classes)}' " +
                                $"template={card.Template?.GetType().Name ?? "none"} theme={(card.Theme is null ? "none" : "set")}");
                text.AppendLine($"    IsAnimated={card.IsAnimated} from {Source(card, GlassCard.IsAnimatedProperty)}; Template from {Source(card, Avalonia.Controls.Primitives.TemplatedControl.TemplateProperty)}");
                text.AppendLine($"    templated parent: {Name(card.TemplatedParent as StyledElement)}; logical parent: {Name(card.GetLogicalParent() as StyledElement)}");
                text.AppendLine($"    in: {string.Join(" < ", card.GetVisualAncestors().Take(14).Select(a => Name(a as StyledElement)))}");
            }
        }
        return text.ToString();
    }

    private static string Source(AvaloniaObject o, AvaloniaProperty property)
    {
        try
        {
            var d = o.GetDiagnostic(property);
            return $"{d.Priority}{(string.IsNullOrEmpty(d.Diagnostic) ? "" : $" ({d.Diagnostic})")}";
        }
        catch (Exception ex) { return $"? ({ex.GetType().Name})"; }
    }

    private static string Name(StyledElement? e)
    {
        if (e is null) return "-";
        var name = e.GetType().Name;
        if (!string.IsNullOrEmpty(e.Name)) name += "#" + e.Name;
        var classes = e.Classes.Where(c => !c.StartsWith(':')).ToList();
        if (classes.Count > 0) name += "." + string.Join(".", classes);
        return name;
    }
}
