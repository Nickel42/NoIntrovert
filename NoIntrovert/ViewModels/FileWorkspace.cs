using NoIntrovert.Core;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The files and folders loaded into the app, shared by Optimize, Convert, Verify and Organize:
/// what's added on one page shows on the others when they're opened (each keeps the inputs it can
/// use), and what's removed or cleared on one goes from all of them.
/// </summary>
public sealed class FileWorkspace
{
    private readonly List<string> _paths = [];
    private readonly HashSet<string> _removed = new(PathNames.Comparer);

    /// <summary>The files and folders added, in the order they were added.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>The folders among <see cref="Paths"/>: a file's library folder is the innermost one it's in.</summary>
    public IReadOnlyList<string> Folders => _paths.Where(Directory.Exists).ToList();

    /// <summary>Raised after paths were added (the page that's showing takes them at once; the others when opened).</summary>
    public event Action? Added;

    /// <summary>Raised with the rows' paths removed from the list (they go from every page).</summary>
    public event Action<IReadOnlyCollection<string>>? Removed;

    /// <summary>Raised when the list was emptied.</summary>
    public event Action? Cleared;

    /// <summary>Raised with old → new paths after Optimize or Organize moved, renamed or converted files.</summary>
    public event Action<IReadOnlyDictionary<string, string>>? Moved;

    /// <summary>Adds files and folders; a file removed earlier and added again comes back.</summary>
    public void Add(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var raw in paths)
        {
            var path = Path.TrimEndingDirectorySeparator(raw);
            _removed.Remove(path);
            if (_paths.Contains(path, PathNames.Comparer)) continue;
            _paths.Add(path);
            added = true;
        }
        if (added) Added?.Invoke();
    }

    /// <summary>Whether a row's path was removed from the list (a folder that holds it doesn't bring it back).</summary>
    public bool IsRemoved(string path) => _removed.Contains(Path.TrimEndingDirectorySeparator(path));

    /// <summary>Removes rows (by path) from every page.</summary>
    public void Remove(IReadOnlyCollection<string> rows)
    {
        if (rows.Count == 0) return;
        foreach (var row in rows)
        {
            var path = Path.TrimEndingDirectorySeparator(row);
            _removed.Add(path);
            _paths.RemoveAll(p => PathNames.Comparer.Equals(p, path));
        }
        Removed?.Invoke(rows);
    }

    /// <summary>Empties the list on every page.</summary>
    public void Clear()
    {
        _paths.Clear();
        _removed.Clear();
        Cleared?.Invoke();
    }

    /// <summary>Files that were moved, renamed or converted: each page's row follows its file to the new path.</summary>
    public void Move(IReadOnlyDictionary<string, string> moves)
    {
        if (moves.Count == 0) return;
        foreach (var (from, to) in moves)
        {
            var i = _paths.FindIndex(p => PathNames.Comparer.Equals(p, from));
            if (i >= 0) _paths[i] = to;
            _removed.Remove(to);
        }
        Moved?.Invoke(moves);
    }
}
