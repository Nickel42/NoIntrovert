// In the root namespace so Core's log lines and job outcomes can count with it too.
namespace NoIntrovert;

/// <summary>Small helpers for the words the app shows.</summary>
internal static class Words
{
    /// <summary>"1 error", "0 errors", "2 errors", "1,234 files"; <paramref name="plural"/> for nouns that don't just add an s.</summary>
    public static string Count(int n, string noun, string? plural = null) => n == 1 ? $"1 {noun}" : $"{n:N0} {plural ?? noun + "s"}";

    /// <summary>"Not checked yet" → "not checked yet", for a sentence that goes after a colon.</summary>
    public static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
