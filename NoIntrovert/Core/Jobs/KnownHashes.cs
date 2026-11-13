using NoIntrovert.Core.Dats;

namespace NoIntrovert.Core.Jobs;

/// <summary>
/// Hashes a conversion already knows, so it doesn't read a multi-GB source again: the hash cache's entry for
/// an unchanged file (from Verify &amp; match, or R11 earlier in the same job).
/// </summary>
internal static class KnownHashes
{
    /// <summary>The hash cache's hashes of <paramref name="path"/> while it's unchanged; null when it has none. Never hashes.</summary>
    public static IReadOnlyList<HashedFile>? Cached(string path)
    {
        // GetOrAdd stores nothing when its hash function throws, so a miss leaves the cache as it was.
        try { return HashCache.Default.GetOrAdd(path, () => throw new Miss()); }
        catch (Miss) { return null; }
    }

    /// <summary>The one whole-file hash of a single-file input (no headerless twin), with its SHA-1; else null.</summary>
    public static HashedFile? Single(IReadOnlyList<HashedFile>? files) =>
        files is [{ Headerless: false, Sha1: not null } only] ? only : null;

    private sealed class Miss : Exception;
}
