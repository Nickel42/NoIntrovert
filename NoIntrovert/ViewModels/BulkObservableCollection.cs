using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;

namespace NoIntrovert.ViewModels;

/// <summary>
/// An ObservableCollection that can add or remove thousands of items with
/// one change notification instead of one per item, so a 5,000-row list
/// isn't redrawn and recounted 5,000 times.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Below this many, items are added one by one (cheap, and keeps the selection).</summary>
    private const int ResetThreshold = 16;

    public void AddRange(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();
        if (list.Count < ResetThreshold)
        {
            foreach (var item in list) Add(item);
            return;
        }
        CheckReentrancy();
        foreach (var item in list) Items.Add(item);
        RaiseReset();
    }

    /// <summary>Replaces the whole contents with one notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        RaiseReset();
    }

    /// <summary>Removes every item <paramref name="match"/> picks; returns how many went.</summary>
    public int RemoveWhere(Func<T, bool> match)
    {
        var doomed = this.Where(match).ToList();
        if (doomed.Count < ResetThreshold)
        {
            foreach (var item in doomed) Remove(item);
            return doomed.Count;
        }
        CheckReentrancy();
        var keep = this.Where(i => !match(i)).ToList();
        Items.Clear();
        foreach (var item in keep) Items.Add(item);
        RaiseReset();
        return doomed.Count;
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// Runs an action once, soon, on the UI thread, however many times it's
/// requested before then: many rows changing at once cost one refresh.
/// </summary>
public sealed class Coalesced(Action action)
{
    private bool _pending;

    public void Request()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            action();
        }, DispatcherPriority.Background);
    }
}
