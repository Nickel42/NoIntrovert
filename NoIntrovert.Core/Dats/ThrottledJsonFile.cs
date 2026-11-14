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
    public ThrottledJsonFile(string path, Func<string> serialize)
    {
        _path = path;
        _serialize = serialize;
        _timer = new Timer(_ => Flush());
        All.Enqueue(this);
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
                var temp = _path + ".tmp";
                File.WriteAllText(temp, _serialize());
                File.Move(temp, _path, overwrite: true);
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
