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
    // Every GlassCard seen getting a template, wherever it lives (popups such as tooltips and drop-downs are separate
    // windows the main window's tree doesn't reach), with what happened to its template and IsAnimated.
    private static readonly List<WeakReference<GlassCard>> Seen = [];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GlassCard, List<string>> History = new();

    /// <summary>Remembers <paramref name="card"/> and a line of its history (the newest few are kept).</summary>
    public static void Track(GlassCard card, string what)
    {
        var history = History.GetValue(card, c =>
        {
            lock (Seen)
            {
                Seen.RemoveAll(w => !w.TryGetTarget(out _));
                Seen.Add(new WeakReference<GlassCard>(c));
            }
            return [];
        });
        history.Add($"{DateTime.Now:HH:mm:ss.fff} {what}");
        if (history.Count > 8) history.RemoveAt(0);
    }

    // The card whose template is being built right now (its parts get it as their templated parent before its
    // OnApplyTemplate runs, so on the error this is the card that threw), and what it was like at that moment.
    private static WeakReference<GlassCard>? _templating;
    private static string _templatingState = "";
    private static readonly List<string> AnimatedWhileTemplating = [];
    private static bool _watching;

    /// <summary>Starts noting each GlassCard as its template is built (see <see cref="DescribeMoment"/>).</summary>
    public static void WatchTemplating()
    {
        if (_watching) return;
        _watching = true;
        StyledElement.TemplatedParentProperty.Changed.AddClassHandler<StyledElement>((part, e) =>
        {
            if (e.NewValue is not GlassCard card) return;
            if (_templating is not null && _templating.TryGetTarget(out var last) && ReferenceEquals(last, card)) return;
            _templating = new WeakReference<GlassCard>(card);
            _templatingState = $"{DateTime.Now:HH:mm:ss.fff} first part {part.GetType().Name}{(string.IsNullOrEmpty(part.Name) ? "" : "#" + part.Name)}; " +
                               $"IsAnimated={card.IsAnimated} from {Source(card, GlassCard.IsAnimatedProperty)}; Template from {Source(card, Avalonia.Controls.Primitives.TemplatedControl.TemplateProperty)}";
            if (card.IsAnimated && AnimatedWhileTemplating.Count < 20)
                AnimatedWhileTemplating.Add($"{_templatingState}; in {Owner(card)}");
        });
    }

    private static string Owner(GlassCard card) =>
        $"templated parent {Name(card.TemplatedParent as StyledElement)}, parent {Name(card.Parent)}, {string.Join(" < ", card.GetVisualAncestors().Take(8).Select(a => Name(a as StyledElement)))}";

    /// <summary>The card whose template was being built last, as it was then and as it is now, and the first cards seen animated while templated.</summary>
    public static string DescribeMoment()
    {
        var text = new StringBuilder();
        if (_templating is not null && _templating.TryGetTarget(out var card))
        {
            text.AppendLine($"Card being templated last: {_templatingState}");
            text.AppendLine($"    now: IsAnimated={card.IsAnimated} from {Source(card, GlassCard.IsAnimatedProperty)}; classes='{string.Join(",", card.Classes)}'; " +
                            $"template={card.Template?.GetType().Name ?? "none"}; attached={TopLevel.GetTopLevel(card) is not null}");
            text.AppendLine($"    parts: {string.Join(", ", card.GetVisualDescendants().OfType<Control>().Where(c => !string.IsNullOrEmpty(c.Name)).Select(c => c.Name).Take(12))}");
            text.AppendLine($"    name scope has RootPanel: {(Avalonia.Controls.NameScope.GetNameScope(card.GetVisualChildren().FirstOrDefault() as StyledElement ?? card) is { } scope ? scope.Find("RootPanel") is not null : "no scope")}");
            text.AppendLine($"    owner: {Owner(card)}");
            text.AppendLine($"    logical: {string.Join(" < ", card.GetLogicalAncestors().Take(10).Select(a => Name(a as StyledElement)))}");
            if (History.TryGetValue(card, out var history)) text.AppendLine($"    history: {string.Join(" | ", history)}");
        }
        else text.AppendLine("Card being templated last: none recorded");
        text.AppendLine($"Cards seen animated while being templated ({AnimatedWhileTemplating.Count}{(AnimatedWhileTemplating.Count == 20 ? ", the first 20" : "")}):");
        foreach (var line in AnimatedWhileTemplating) text.AppendLine("    " + line);
        return text.ToString();
    }

    /// <summary>
    /// The tracked cards shown in another top level (a popup: a tooltip or a drop-down, which the windows' trees don't
    /// reach) and every tracked card whose applied template has no RootPanel, wherever it is: the one that threw has
    /// its template but not the part. Cards on pages and in rows not on screen (detached) are left out otherwise.
    /// </summary>
    public static string DescribeOthers(IEnumerable<Window> windows)
    {
        var inWindows = windows.SelectMany(w => w.GetVisualDescendants().OfType<GlassCard>()).ToHashSet();
        List<GlassCard> cards;
        lock (Seen) cards = Seen.Select(w => w.TryGetTarget(out var c) ? c : null).OfType<GlassCard>().ToList();
        var text = new StringBuilder();
        static bool MissingPart(GlassCard c) => c.GetVisualChildren().Any() && !c.GetVisualDescendants().OfType<Control>().Any(d => d.Name == "RootPanel");
        var picked = cards.Where(c => (!inWindows.Contains(c) && TopLevel.GetTopLevel(c) is not null) || MissingPart(c)).ToList();
        text.AppendLine($"Tracked GlassCards: {cards.Count}; in a popup or templated without RootPanel: {picked.Count}");
        var number = 0;
        foreach (var card in picked)
        {
            number++;
            var root = TopLevel.GetTopLevel(card) as Visual ?? card.GetVisualAncestors().LastOrDefault();
            text.AppendLine($"*{number} root={root?.GetType().Name ?? "none (detached)"} inWindow={inWindows.Contains(card)} templated={card.GetVisualChildren().Any()} " +
                            $"RootPanel={card.GetVisualDescendants().OfType<Control>().Any(c => c.Name == "RootPanel")} IsAnimated={card.IsAnimated} from {Source(card, GlassCard.IsAnimatedProperty)} " +
                            $"classes='{string.Join(",", card.Classes)}'");
            text.AppendLine($"    template parts: {string.Join(", ", card.GetVisualDescendants().OfType<Control>().Where(c => !string.IsNullOrEmpty(c.Name)).Select(c => c.Name).Take(10))}");
            text.AppendLine($"    templated parent: {Name(card.TemplatedParent as StyledElement)}; logical parent: {Name(card.GetLogicalParent() as StyledElement)}; logical root: {Name(card.GetLogicalAncestors().LastOrDefault() as StyledElement)}");
            text.AppendLine($"    in: {string.Join(" < ", card.GetVisualAncestors().Take(16).Select(a => Name(a as StyledElement)))}");
            if (History.TryGetValue(card, out var history)) text.AppendLine($"    history: {string.Join(" | ", history)}");
        }
        return text.ToString();
    }

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
        text.Append(DescribeMoment());
        text.Append(DescribeOthers(windows));
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
