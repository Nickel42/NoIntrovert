namespace NoIntrovert.ViewModels;

/// <summary>Small helpers for the words the app shows.</summary>
internal static class Words
{
    /// <summary>"1 error", "0 errors", "2 errors"; <paramref name="plural"/> for nouns that don't just add an s.</summary>
    public static string Count(int n, string noun, string? plural = null) => n == 1 ? $"1 {noun}" : $"{n} {plural ?? noun + "s"}";
}
