using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Unpacks zip, 7z, tar and gz archives into a work folder, and joins plain
/// split files (game.bin.001, .002, ...) back into one. RAR is refused.
/// </summary>
internal static class ArchiveExtractor
{
    public static readonly string[] Extensions = [".zip", ".7z", ".tar", ".gz", ".tgz"];

    public static bool IsArchive(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>True for the first part of a split file: name.ext.001.</summary>
    public static bool IsFirstSplitPart(string path) =>
        Path.GetExtension(path).Equals(".001", StringComparison.Ordinal);

    /// <summary>All parts of a split file, in order, starting from the .001.</summary>
    public static List<string> SplitParts(string firstPart)
    {
        var stem = firstPart[..^4];
        var parts = new List<string>();
        for (var i = 1; i < 1000; i++)
        {
            var p = $"{stem}.{i:000}";
            if (!File.Exists(p)) break;
            parts.Add(p);
        }
        return parts;
    }

    /// <summary>
    /// RAR archives (and their .rNN / .partN.rar volumes) aren't supported: 7-Zip's
    /// RAR decoder is under the unRAR license, which NoIntrovert's GPL doesn't allow.
    /// </summary>
    public static bool IsRar(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".rar", StringComparison.OrdinalIgnoreCase)
            || (ext.Length == 4 && char.ToLowerInvariant(ext[1]) == 'r' && char.IsDigit(ext[2]) && char.IsDigit(ext[3]));
    }

    public const string RarNotSupported =
        "RAR archives aren't supported (the RAR decoder's license isn't compatible with NoIntrovert's GPL). Repack it as 7z or zip.";

    /// <summary>By content, so a RAR renamed to .zip is refused too: "Rar!\x1A\x07".</summary>
    private static bool HasRarSignature(string path)
    {
        Span<byte> head = stackalloc byte[6];
        using var f = File.OpenRead(path);
        return f.Read(head) == 6 && head.SequenceEqual("Rar!\x1A\x07"u8);
    }

    /// <summary>Later parts of a split file (.002, .003, ...), which the .001 brings in.</summary>
    public static bool IsSecondaryVolume(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length == 4 && char.IsDigit(ext[1]) && char.IsDigit(ext[2]) && char.IsDigit(ext[3]) && ext != ".001";
    }

    public static void Extract(string archivePath, string destination, IProgress<double>? progress, CancellationToken token)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var buffer = new byte[1 << 20];
        if (IsRar(archivePath) || HasRarSignature(archivePath)) throw new NotSupportedException(RarNotSupported);

        // 7z needs random access, so it opens as an archive. Everything else streams
        // through a forward-only reader, which also handles tar.gz.
        if (Path.GetExtension(archivePath).Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var total = Math.Max(1, archive.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size));
            long done = 0;
            void Count(int n) { done += n; progress?.Report((double)done / total); }

            if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
            {
                // One pass in archive order, so solid blocks are decompressed once.
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    if (reader.Entry.IsDirectory) continue;
                    using var src = reader.OpenEntryStream();
                    WriteEntry(reader.Entry, src, root, destination, buffer, Count, token);
                }
            }
            else
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    using var src = entry.OpenEntryStream();
                    WriteEntry(entry, src, root, destination, buffer, Count, token);
                }
            }
        }
        else
        {
            using var file = File.OpenRead(archivePath);
            using var reader = ReaderFactory.OpenReader(file, new ReaderOptions { LeaveStreamOpen = true });
            void Count(int _) => progress?.Report((double)file.Position / Math.Max(1, file.Length));
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                using var src = reader.OpenEntryStream();
                WriteEntry(reader.Entry, src, root, destination, buffer, Count, token);
            }
        }
        progress?.Report(1);
    }

    private static void WriteEntry(IEntry entry, Stream src, string root, string destination, byte[] buffer,
        Action<int> count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (entry.IsEncrypted)
            throw new NotSupportedException($"'{entry.Key}' in this archive is password protected.");

        // Refuse entries that would land outside the work folder ("../" tricks).
        var target = Path.GetFullPath(Path.Combine(destination, entry.Key ?? "unnamed"));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Archive entry '{entry.Key}' points outside the extraction folder.");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var dst = File.Create(target);
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            dst.Write(buffer, 0, n);
            count(n);
            token.ThrowIfCancellationRequested();
        }
    }

    public static string JoinSplitParts(string firstPart, string destinationDirectory, IProgress<double>? progress, CancellationToken token)
    {
        var parts = SplitParts(firstPart);
        var target = Path.Combine(destinationDirectory, Path.GetFileName(firstPart[..^4]));
        var total = Math.Max(1, parts.Sum(p => new FileInfo(p).Length));
        long done = 0;
        var buffer = new byte[1 << 20];
        using var dst = File.Create(target);
        foreach (var part in parts)
        {
            using var src = File.OpenRead(part);
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dst.Write(buffer, 0, n);
                done += n;
                progress?.Report((double)done / total);
                token.ThrowIfCancellationRequested();
            }
        }
        return target;
    }
}
