using System.Collections.Concurrent;
using System.Text.Json;

namespace NoIntrovert.Core.Dats;

/// <summary>
/// R11, Q1: the No-Intro/Redump hashes of what a converted file was made
/// from (name, size, CRC32, MD5, SHA-1 of each file), kept with the library
/// index in <c>originals.json</c> and keyed by the converted file's path,
/// size and date. Verify &amp; match checks a CHD, RVZ or fixed ROM against
/// these instead of decoding it.
/// </summary>
public sealed class OriginalHashes
{
    public sealed record Entry(long Size, long Ticks, string Source, IReadOnlyList<HashedFile> Files);

    private static readonly ConcurrentDictionary<string, OriginalHashes> Stores = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly ConcurrentDictionary<string, Entry> _entries;
    private readonly string _path;
    private readonly object _saveLock = new();

    private OriginalHashes(string path)
    {
        _path = path;
        Dictionary<string, Entry>? loaded = null;
        try
        {
            if (File.Exists(path)) loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        _entries = new ConcurrentDictionary<string, Entry>(loaded ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public static OriginalHashes Default => Stores.GetOrAdd(Path.Combine(AppSettings.SettingsDirectory, "originals.json"), p => new OriginalHashes(p));

    /// <summary>The originals of <paramref name="output"/>, while it's the file they were recorded for.</summary>
    public Entry? Get(string output)
    {
        if (!_entries.TryGetValue(Path.GetFullPath(output), out var e)) return null;
        var (size, ticks) = HashCache.Stamp(output);
        return e.Size == size && e.Ticks == ticks ? e : null;
    }

    /// <summary>Records what <paramref name="output"/> was made from (call once it's written and verified).</summary>
    public void Set(string output, string source, IReadOnlyList<HashedFile> files)
    {
        var (size, ticks) = HashCache.Stamp(output);
        _entries[Path.GetFullPath(output)] = new Entry(size, ticks, Path.GetFileName(source), files);
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
