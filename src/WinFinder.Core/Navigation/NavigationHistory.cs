namespace WinFinder.Core.Navigation;

/// <summary>Per-tab back/forward history.</summary>
public sealed class NavigationHistory<T> where T : class
{
    private readonly Stack<T> _back = new();
    private readonly Stack<T> _forward = new();
    private readonly int _limit;

    public NavigationHistory(T initial, int limit = 100)
    {
        Current = initial;
        _limit = limit;
    }

    public T Current { get; private set; }
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public IEnumerable<T> BackItems => _back;
    public IEnumerable<T> ForwardItems => _forward;

    public void Navigate(T next)
    {
        if (ReferenceEquals(next, Current)) return;
        _back.Push(Current);
        Trim();
        _forward.Clear();
        Current = next;
    }

    /// <summary>Changes the current entry without adding history (e.g. while typing a search).</summary>
    public void ReplaceCurrent(T value) => Current = value;

    public T? GoBack()
    {
        if (_back.Count == 0) return null;
        _forward.Push(Current);
        Current = _back.Pop();
        return Current;
    }

    public T? GoForward()
    {
        if (_forward.Count == 0) return null;
        _back.Push(Current);
        Current = _forward.Pop();
        return Current;
    }

    private void Trim()
    {
        if (_back.Count <= _limit) return;
        var keep = _back.Take(_limit).Reverse().ToList();
        _back.Clear();
        foreach (var item in keep) _back.Push(item);
    }
}
