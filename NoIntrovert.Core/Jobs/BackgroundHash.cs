using System.Collections.Concurrent;

namespace NoIntrovert.Core.Jobs;

/// <summary>
/// A source's hashes worked out on another thread while the conversion reads the same file, so the second read
/// mostly comes from the OS cache instead of the disk afterwards. The hashes are the same as hashing afterwards;
/// cancelling the job cancels it, and disposing it stops it and waits, so the file is closed before it's removed.
/// </summary>
internal sealed class BackgroundHash<T> : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task<T> _task;
    private readonly CancellationToken _job;
    private readonly string _path;

    internal BackgroundHash(string path, Func<CancellationToken, T> hash, CancellationToken job)
    {
        _path = Path.GetFullPath(path);
        _job = job;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(job);
        var token = _cts.Token;
        _task = Task.Factory.StartNew(() => hash(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        BackgroundHash.Running[_path] = _task;
    }

    /// <summary>The hashes, once they're done; throws what hashing threw (OperationCanceledException when the job was cancelled).</summary>
    public T Result()
    {
        try
        {
            return _task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_job.IsCancellationRequested)
        {
            throw new OperationCanceledException(_job);
        }
    }

    /// <summary>Stops a hash still running (the conversion failed or was cancelled) and waits for it to let go of the file.</summary>
    public void Dispose()
    {
        if (!_task.IsCompleted) _cts.Cancel();
        try { _task.Wait(); }
        catch (AggregateException) { } // its error, if any, was the caller's to read through Result()
        BackgroundHash.Running.TryRemove(new KeyValuePair<string, Task>(_path, _task));
        _cts.Dispose();
    }
}

/// <summary>Starts a <see cref="BackgroundHash{T}"/>, and lets other steps of the job wait for one.</summary>
internal static class BackgroundHash
{
    /// <summary>The hashes running now, by file.</summary>
    internal static readonly ConcurrentDictionary<string, Task> Running = new(PathNames.Comparer);

    /// <summary>
    /// Starts hashing <paramref name="path"/> now; null when it should wait for the conversion instead: the file is on
    /// a spinning hard drive, where two reads at once would make the head jump between them.
    /// </summary>
    public static BackgroundHash<T>? Start<T>(string path, Func<CancellationToken, T> hash, CancellationToken job)
    {
        if (Drives.IsSpinning(path)) return null;
        Interlocked.Increment(ref Count);
        return new BackgroundHash<T>(path, hash, job);
    }

    /// <summary>Whether a hash of <paramref name="path"/> is running beside a conversion.</summary>
    public static bool IsRunning(string path) => Running.ContainsKey(Path.GetFullPath(path));

    /// <summary>Waits for a hash of <paramref name="path"/> running beside the conversion, if there is one (its errors are its owner's).</summary>
    public static void WaitFor(string path, CancellationToken token)
    {
        if (!Running.TryGetValue(Path.GetFullPath(path), out var task)) return;
        try { task.Wait(token); }
        catch (AggregateException) { }
    }

    /// <summary>How many hashes have run beside a conversion (tests check one did).</summary>
    internal static int Count;
}
