using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Unpacks zip, 7z, rar (including multi-volume .partN.rar / .r00 sets),
/// tar and gz archives into a work folder, and joins plain split files
/// (game.bin.001, .002, ...) back into one.
/// </summary>
internal static class ArchiveExtractor
{
    public static readonly string[] Extensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz"];

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

    /// <summary>The volumes of a multi-part rar, for deleting originals.</summary>
    public static IEnumerable<string> ArchiveVolumes(string archivePath)
    {
        yield return archivePath;
        var dir = Path.GetDirectoryName(archivePath)!;
        var name = Path.GetFileName(archivePath);
        if (!name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) yield break;

        var partIndex = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        if (partIndex > 0)
        {
            var stem = name[..partIndex];
            foreach (var p in Directory.EnumerateFiles(dir, stem + ".part*.rar"))
                if (!p.Equals(archivePath, StringComparison.OrdinalIgnoreCase)) yield return p;
        }
        else
        {
            var stem = Path.Combine(dir, Path.GetFileNameWithoutExtension(name));
            for (var i = 0; i < 1000; i++)
            {
                var p = $"{stem}.r{i:00}";
                if (!File.Exists(p)) break;
                yield return p;
            }
        }
    }

    /// <summary>Skips rar volumes other than the first, which open the whole set.</summary>
    public static bool IsSecondaryVolume(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name);
        if (ext.Length == 4 && ext[1] == 'r' && char.IsDigit(ext[2]) && char.IsDigit(ext[3])) return true;
        var partIndex = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        if (partIndex > 0 && name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            var digits = name[(partIndex + 5)..^4];
            return int.TryParse(digits, out var n) && n > 1;
        }
        return ext.Length == 4 && char.IsDigit(ext[1]) && char.IsDigit(ext[2]) && char.IsDigit(ext[3]) && ext != ".001";
    }

    public static void Extract(string archivePath, string destination, IProgress<double>? progress, CancellationToken token)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var buffer = new byte[1 << 20];
        var volumes = ArchiveVolumes(archivePath).ToList();

        // 7z needs random access, and multi-volume rar sets need every volume, so
        // those open as archives. Everything else streams through a forward-only
        // reader, which also handles tar.gz.
        if (volumes.Count > 1 || Path.GetExtension(archivePath).Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = volumes.Count > 1
                ? ArchiveFactory.OpenArchive(volumes.Skip(1).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).Prepend(volumes[0]).Select(v => new FileInfo(v)).ToList())
                : ArchiveFactory.OpenArchive(archivePath);
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
