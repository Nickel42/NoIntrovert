using LibPsVitaKit.Licenses;

namespace NoIntrovert.Core.Vita;

/// <summary>
/// Where a package's zRIF comes from: a ".zrif" file next to it (same name), then the list chosen in
/// Settings › Keys. Any text works for either, NoPayStation's TSV included: every zRIF-looking word is
/// decoded, and the content ID inside the license says which package it's for, so columns don't matter.
/// </summary>
public static class ZrifLibrary
{
    private static readonly char[] Separators = ['\t', ',', ';', ' ', '"', '\''];
    private static readonly object Gate = new();
    private static (string Path, DateTime Written, long Length, Dictionary<string, string> Zrifs)? _cached;

    /// <summary>The zRIF for <paramref name="contentId"/>, or null. <paramref name="problem"/> says why a list couldn't be read.</summary>
    public static string? Find(string pkgPath, string contentId, string listPath, out string? problem)
    {
        problem = null;
        var sidecar = Path.ChangeExtension(pkgPath, ".zrif");
        if (File.Exists(sidecar))
        {
            try
            {
                if (Parse(File.ReadAllText(sidecar)).TryGetValue(contentId, out var beside)) return beside;
                problem = $"{Path.GetFileName(sidecar)} holds no zRIF for {contentId}.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = $"{Path.GetFileName(sidecar)}: {ex.Message}"; }
        }
        if (string.IsNullOrWhiteSpace(listPath)) return null;
        var list = Load(listPath, out var listProblem);
        problem ??= listProblem;
        return list?.GetValueOrDefault(contentId);
    }

    /// <summary>The zRIFs in a list file, by content ID (read again only when the file changes). Null when it can't be read.</summary>
    public static IReadOnlyDictionary<string, string>? Load(string listPath, out string? problem)
    {
        problem = null;
        try
        {
            var info = new FileInfo(listPath);
            if (!info.Exists)
            {
                problem = $"The zRIF list {listPath} doesn't exist.";
                return null;
            }
            lock (Gate)
            {
                if (_cached is { } c && PathNames.Same(c.Path, listPath) && c.Written == info.LastWriteTimeUtc && c.Length == info.Length)
                    return c.Zrifs;
            }
            var zrifs = Parse(File.ReadAllText(listPath));
            lock (Gate) _cached = (listPath, info.LastWriteTimeUtc, info.Length, zrifs);
            return zrifs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"The zRIF list {listPath} can't be read: {ex.Message}";
            return null;
        }
    }

    /// <summary>Every valid zRIF in <paramref name="text"/>, by the content ID it licenses.</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var zrifs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
            foreach (var word in line.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // A Vita zRIF is about 60–120 base64 characters and starts with the zlib header's "KO5i"; skip the rest quickly.
                if (word.Length < 40 || !word.StartsWith("KO5i", StringComparison.Ordinal)) continue;
                if (Zrif.TryDecode(word, out var rif)) zrifs.TryAdd(Zrif.ContentIdOf(rif), word);
            }
        return zrifs;
    }

    /// <summary>What Settings › Keys says about the list.</summary>
    public static string Describe(string listPath)
    {
        if (string.IsNullOrWhiteSpace(listPath))
            return "No zRIF list chosen. PS Vita games and DLC need a zRIF: put it in a .zrif file next to each .pkg, or choose a list.";
        var list = Load(listPath, out var problem);
        if (list is null) return problem ?? $"{listPath} can't be read.";
        return list.Count == 0 ? $"{listPath} holds no zRIFs." : $"{list.Count:N0} zRIF(s) in {listPath}. A .zrif file next to a .pkg is used first.";
    }
}
