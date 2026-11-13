using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace NoIntrovert.Core.Dats;

/// <summary>
/// One hashed file of an input: its name, size, CRC32 and SHA-1 (upper-case
/// hex; SHA-1 is null for archive members, whose CRC32 comes from the
/// archive's headers). <see cref="Headerless"/> marks the same ROM hashed
/// without its header. MD5 is kept for the original hashes of converted files (R11).
/// </summary>
public sealed record HashedFile(string Name, long Size, string Crc, string? Sha1, bool Headerless = false, string? Md5 = null);

/// <summary>Size, CRC32 and SHA-1 in one pass, plus the headerless form of headered ROMs (FR-5).</summary>
public static class Hasher
{
    /// <summary>The file, and for a headered ROM the file without its header.</summary>
    public static List<HashedFile> HashFile(string path, CancellationToken token = default)
    {
        var name = Path.GetFileName(path);
        var list = new List<HashedFile> { Hash(path, 0, name, token) };
        var skip = HeaderSize(path);
        if (skip > 0) list.Add(Hash(path, skip, name, token) with { Headerless = true });
        return list;
    }

    private static HashedFile Hash(string path, long skip, string name, CancellationToken token)
    {
        // Azahar's .zcci/.zcia (Z3DS) is hashed as the file it holds, which is what its DAT entry describes.
        using var stream = OpenContents(path);
        stream.Position = skip;
        var crc = new System.IO.Hashing.Crc32();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[1 << 20];
        long size = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            crc.Append(buffer.AsSpan(0, read));
            sha.AppendData(buffer, 0, read);
            md5.AppendData(buffer, 0, read);
            size += read;
        }
        return new HashedFile(name, size, crc.GetCurrentHashAsUInt32().ToString("X8"), Convert.ToHexString(sha.GetHashAndReset()),
            Md5: Convert.ToHexString(md5.GetHashAndReset()));
    }

    /// <summary>The bytes a file stands for: itself, or for a Z3DS file (.zcci, .zcia…) the file it holds, decompressed as it's read.</summary>
    private static Stream OpenContents(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".zcci" or ".zcia" or ".zcxi" or ".z3dsx")
        {
            try { if (Lib3dsKit.CtrFile.IsCompressed(path)) return Lib3dsKit.CtrFile.OpenRead(path); }
            catch (Lib3dsKit.CtrFormatException) { } // damaged: hashed as it is, so it simply won't match
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
    }

    /// <summary>
    /// How many bytes of header a ROM carries that some DATs leave out: iNES
    /// and fwNES (16), Lynx (64), Atari 7800 (128), and a SNES copier header
    /// (512, when the size is 512 more than a multiple of 1 KiB). 0 for none.
    /// </summary>
    public static int HeaderSize(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        Span<byte> head = stackalloc byte[16];
        long length;
        try
        {
            using var s = File.OpenRead(path);
            length = s.Length;
            if (s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        return ext switch
        {
            ".nes" when head[..4].SequenceEqual("NES\x1A"u8) => 16,
            ".fds" when head[..4].SequenceEqual("FDS\x1A"u8) => 16,
            ".lnx" when head[..4].SequenceEqual("LYNX"u8) => 64,
            ".a78" when head[1..10].SequenceEqual("ATARI7800"u8) => 128,
            ".sfc" or ".smc" or ".swc" or ".fig" when length % 1024 == 512 => 512,
            _ => 0,
        };
    }
}

/// <summary>
/// Hashes already worked out (FR-6), kept in <c>hashes.json</c> beside the
/// settings and keyed by path, size and modification time, so an unchanged
/// input is never hashed twice.
/// </summary>
public sealed class HashCache
{
    public sealed record Entry(long Size, long Ticks, IReadOnlyList<HashedFile> Files);

    private static readonly ConcurrentDictionary<string, HashCache> Caches = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly ConcurrentDictionary<string, Entry> _entries;
    private readonly ThrottledJsonFile _file;

    private HashCache(string path)
    {
        Dictionary<string, Entry>? loaded = null;
        try
        {
            if (File.Exists(path)) loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        _entries = new ConcurrentDictionary<string, Entry>(loaded ?? [], StringComparer.OrdinalIgnoreCase);
        _file = new ThrottledJsonFile(path, () => JsonSerializer.Serialize(_entries, JsonOptions));
    }

    /// <summary>The cache under the current settings folder.</summary>
    public static HashCache Default => Caches.GetOrAdd(Path.Combine(AppSettings.SettingsDirectory, "hashes.json"), p => new HashCache(p));

    /// <summary>True while a hash worked out hasn't been written to hashes.json yet.</summary>
    public bool IsDirty => _file.IsDirty;

    /// <summary>How many times hashes.json has been written.</summary>
    public int Writes => _file.Writes;

    /// <summary>How many lookups found what they needed (tests use it to check nothing was hashed again).</summary>
    public int Hits;

    /// <summary>The cached hashes of <paramref name="path"/> while it's unchanged, or null; never hashes.</summary>
    public IReadOnlyList<HashedFile>? TryGet(string path)
    {
        if (!_entries.TryGetValue(path, out var e)) return null;
        var (size, ticks) = Stamp(path);
        if (e.Size != size || e.Ticks != ticks) return null;
        Interlocked.Increment(ref Hits);
        return e.Files;
    }

    /// <summary>The input's hashes, from the cache while <paramref name="path"/> is unchanged, else from <paramref name="hash"/>.</summary>
    public IReadOnlyList<HashedFile> GetOrAdd(string path, Func<IReadOnlyList<HashedFile>> hash)
    {
        var (size, ticks) = Stamp(path);
        if (_entries.TryGetValue(path, out var e) && e.Size == size && e.Ticks == ticks)
        {
            Interlocked.Increment(ref Hits);
            return e.Files;
        }
        var files = hash();
        _entries[path] = new Entry(size, ticks, files);
        _file.Changed();
        return files;
    }

    /// <summary>Total size and newest change of a file, or of a folder's files.</summary>
    public static (long Size, long Ticks) Stamp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var f = new FileInfo(path);
                return (f.Length, f.LastWriteTimeUtc.Ticks);
            }
            if (Directory.Exists(path))
            {
                var files = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                return (files.Sum(f => f.Length), files.Count == 0 ? 0 : files.Max(f => f.LastWriteTimeUtc.Ticks));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (-1, 0);
    }

    /// <summary>Writes the cache now if it changed. While hashing it's written at most every two seconds; a job group flushes it at the end.</summary>
    public void Flush() => _file.Flush();
}
