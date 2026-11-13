using System.Buffers;

namespace NoIntrovert.Core;

/// <summary>A file copy that reports its progress and stops when cancelled, for copies big enough to watch.</summary>
internal static class FileCopy
{
    /// <summary>
    /// Copies one file in 4 MiB chunks, so a multi-GB copy reports its progress and stops as soon as it's
    /// cancelled. A copy that doesn't finish is deleted. The copy keeps the source's modification date, as
    /// File.Copy does; an existing <paramref name="to"/> is replaced.
    /// </summary>
    /// <param name="copied">Told the bytes of each chunk as it lands.</param>
    public static void Copy(string from, string to, CancellationToken token, Action<long>? copied = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4 << 20);
        try
        {
            using (var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            using (var target = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1))
            {
                target.SetLength(source.Length);
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                    copied?.Invoke(read);
                }
                target.SetLength(target.Position);
            }
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
        }
        catch
        {
            try { File.Delete(to); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
