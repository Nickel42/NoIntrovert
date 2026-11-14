using System.CommandLine;
using System.Text;
using NoIntrovert.Cli.Output;

namespace NoIntrovert.Cli.Cli;

/// <summary>One group of options in a command's help ("WHERE TO SAVE"), with an optional note under its title.</summary>
public sealed record HelpGroup(string Title, IReadOnlyList<Option> Options, string? Note = null, bool Advanced = false);

/// <summary>
/// A command's help page (docs/requirements/cli.md §6): one line saying what it does, a paragraph, usage,
/// examples first, then the options in groups, then where to learn more.
/// </summary>
public sealed record HelpDoc(
    string Summary,
    string Description,
    string Usage,
    IReadOnlyList<(string Command, string Explain)> Examples,
    IReadOnlyList<HelpGroup> Groups,
    string? Footer = null);

/// <summary>Writes help pages: plain text that fits the terminal (80 columns when piped), coloured only on a terminal.</summary>
public static class HelpWriter
{
    public const string DocsUrl = "https://github.com/Nickel42/NoIntrovert/blob/main/docs/requirements/cli.md";

    public static string Render(Command command, HelpDoc doc, Paint p, bool all, int width)
    {
        width = Math.Clamp(width, 60, 100);
        var sb = new StringBuilder();
        sb.AppendLine(Wrap(doc.Summary, width, ""));
        if (doc.Description.Length > 0)
        {
            sb.AppendLine();
            foreach (var para in doc.Description.Split("\n\n"))
                sb.AppendLine(Wrap(para.Replace('\n', ' '), width, ""));
        }
        sb.AppendLine();
        sb.AppendLine(p.Bold("USAGE"));
        foreach (var line in doc.Usage.Split('\n')) sb.AppendLine("  " + line);
        if (doc.Examples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(p.Bold("EXAMPLES"));
            foreach (var (cmd, explain) in doc.Examples)
            {
                sb.AppendLine("  " + p.Cyan(cmd));
                if (explain.Length > 0) sb.AppendLine(Wrap(explain, width, "      "));
            }
        }
        foreach (var group in doc.Groups.Where(g => all || !g.Advanced))
        {
            sb.AppendLine();
            sb.AppendLine(p.Bold(group.Title) + (group.Note is null ? "" : "  " + p.Dim(group.Note)));
            foreach (var option in group.Options.Where(o => all || !o.Hidden))
                AppendOption(sb, option, p, width);
        }
        var advanced = doc.Groups.Where(g => g.Advanced).ToList();
        if (!all && advanced.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Wrap($"{string.Join(" and ", advanced.Select(g => Capitalized(g.Title)))}: noi {CommandPath(command)} --help-all", width, ""));
        }
        sb.AppendLine();
        sb.AppendLine(Wrap(doc.Footer ?? $"Options can go in any order. On Windows, /? and /option work too (/q, /o:folder). More: {DocsUrl}", width, ""));
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Capitalized(string title) => title.Length == 0 ? title : title[0] + title[1..].ToLowerInvariant();

    public static string CommandPath(Command command)
    {
        var names = new List<string>();
        for (var c = command; c is not null && c is not RootCommand; c = c.Parents.OfType<Command>().FirstOrDefault())
            names.Insert(0, c.Name);
        return string.Join(" ", names);
    }

    private static void AppendOption(StringBuilder sb, Option option, Paint p, int width)
    {
        var names = option.Aliases.Concat([option.Name]).Distinct().OrderBy(a => a.StartsWith("--") ? 1 : 0).ThenBy(a => a.Length).ToList();
        var shorts = names.Where(n => !n.StartsWith("--")).ToList();
        var longs = names.Where(n => n.StartsWith("--")).ToList();
        var value = option.ValueType == typeof(bool) ? "" : $" <{option.HelpName ?? "value"}>";
        var left = (shorts.Count > 0 ? string.Join(", ", shorts) + ", " : "    ") + string.Join(", ", longs) + value;
        const int column = 28;
        var description = option.Description ?? "";
        if (left.Length + 2 > column)
        {
            sb.AppendLine("  " + p.Green(left));
            sb.AppendLine(Wrap(description, width, new string(' ', column + 2)));
        }
        else
        {
            var first = Wrap(description, width - column - 2, "").Split('\n');
            sb.AppendLine("  " + p.Green(left) + new string(' ', column - left.Length) + first[0]);
            foreach (var rest in first.Skip(1)) sb.AppendLine(new string(' ', column + 2) + rest);
        }
    }

    /// <summary>Wraps <paramref name="text"/> at word boundaries to <paramref name="width"/>, each line after <paramref name="indent"/>.</summary>
    public static string Wrap(string text, int width, string indent)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && indent.Length + line.Length + 1 + word.Length > width)
            {
                lines.Add(indent + line);
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0 || lines.Count == 0) lines.Add(indent + line);
        return string.Join("\n", lines);
    }
}
