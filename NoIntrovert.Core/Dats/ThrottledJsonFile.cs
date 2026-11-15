using System.Collections.Concurrent;

namespace NoIntrovert.Core.Dats;

/// <summary>
/// Saves one of the stores kept beside the settings (hashes.json,
/// matches.json, originals.json) without rewriting the whole file for every
/// input. A change marks the file dirty and it is written on a timer, at
/// most once every <see cref="Interval"/>, so it is never more than that
/// behind. The job queue flushes every store when a job group finishes, and
/// the process flushes them as it exits.
/// </summary>
public sealed class ThrottledJsonFile
{
    /// <summary>The longest a change waits before it is written.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentQueue<ThrottledJsonFile> All = new();
    private readonly string _path;
    private readonly Func<string> _serialize;
    private readonly Action<string>? _merge;
    private DateTime _diskStamp;
    private readonly object _stateLock = new();
    private readonly object _writeLock = new();
    private readonly Timer _timer;
    private DateTime _saved;
    private bool _dirty;
    private bool _scheduled;
    private int _writes;

    static ThrottledJsonFile()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAll();
    }

    /// <param name="path">The file to write.</param>
    /// <param name="serialize">The store's whole contents as JSON; called on whichever thread writes.</param>
    /// <param name="merge">
    /// Called, under a lock other processes respect, with the file as it is on disk when another process (the app
    /// and noi run side by side) wrote it since this one last did: the caller adds what it doesn't have yet, so a
    /// save never drops the other's entries.
    /// </param>
    public ThrottledJsonFile(string path, Func<string> serialize, Action<string>? merge = null)
    {
        _path = path;
        _serialize = serialize;
        _merge = merge;
        _diskStamp = Stamp(path);
        _timer = new Timer(_ => Flush());
        All.Enqueue(this);
    }

    private static DateTime Stamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>True while a change hasn't been written yet.</summary>
    public bool IsDirty
    {
        get { lock (_stateLock) return _dirty; }
    }

    /// <summary>How many times the file has been written (tests use it to check writes are throttled).</summary>
    public int Writes => Volatile.Read(ref _writes);

    /// <summary>Notes a change: the file is written once the interval since the last write has passed.</summary>
    public void Changed()
    {
        lock (_stateLock)
        {
            _dirty = true;
            if (_scheduled) return;
            _scheduled = true;
            var wait = _saved + Interval - DateTime.UtcNow;
            _timer.Change(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes the file now if anything changed since it was last written.</summary>
    public void Flush()
    {
        lock (_writeLock)
        {
            lock (_stateLock)
            {
                if (!_dirty) return;
                _dirty = false;
                _scheduled = false;
                _saved = DateTime.UtcNow;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using var gate = FileGate.Acquire(_path + ".lock", TimeSpan.FromSeconds(10));
                if (_merge is not null && Stamp(_path) is var onDisk && onDisk != default && onDisk != _diskStamp)
                {
                    try { _merge(File.ReadAllText(_path)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                }
                var temp = $"{_path}.{Environment.ProcessId}.tmp";
                File.WriteAllText(temp, _serialize());
                File.Move(temp, _path, overwrite: true);
                _diskStamp = Stamp(_path);
                Interlocked.Increment(ref _writes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Writes every store with unwritten changes: at the end of a job group, and on exit.</summary>
    public static void FlushAll()
    {
        foreach (var file in All) file.Flush();
    }
}

/// <summary>
/// A lock on a file that every NoIntrovert process respects (the app and noi), held while a shared file is
/// read and rewritten. Waits up to the timeout, then goes ahead without it rather than lose the save.
/// </summary>
public sealed class FileGate : IDisposable
{
    private readonly FileStream? _stream;

    private FileGate(FileStream? stream) => _stream = stream;

    public static FileGate Acquire(string lockPath, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileGate(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
            }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(25); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new FileGate(null); }
        }
    }

    public void Dispose() => _stream?.Dispose();
}
