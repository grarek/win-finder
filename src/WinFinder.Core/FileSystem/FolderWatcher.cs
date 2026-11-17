namespace WinFinder.Core.FileSystem;

public enum ChangeKind { Created, Deleted, Changed, Renamed }

public sealed record FolderChange(ChangeKind Kind, string FullPath, string? OldFullPath = null);

/// <summary>
/// Debounced wrapper around <see cref="FileSystemWatcher"/>. Events are coalesced for
/// <c>debounceMs</c> and delivered as one batch on a thread-pool thread. When the
/// buffer overflows (or a burst is too large) <see cref="Overflowed"/> asks for a full reload.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private const int MaxBatch = 1500;

    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _timer;
    private readonly int _debounceMs;
    private readonly object _gate = new();
    private List<FolderChange> _pending = new();
    private bool _overflow;
    private bool _disposed;

    public event Action<IReadOnlyList<FolderChange>>? Changed;
    public event Action? Overflowed;

    public string Path { get; }

    public FolderWatcher(string path, int debounceMs = 150)
    {
        Path = path;
        _debounceMs = debounceMs;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
            };
            _watcher.Created += (_, e) => Enqueue(new FolderChange(ChangeKind.Created, e.FullPath));
            _watcher.Deleted += (_, e) => Enqueue(new FolderChange(ChangeKind.Deleted, e.FullPath));
            _watcher.Changed += (_, e) => Enqueue(new FolderChange(ChangeKind.Changed, e.FullPath));
            _watcher.Renamed += (_, e) => Enqueue(new FolderChange(ChangeKind.Renamed, e.FullPath, e.OldFullPath));
            _watcher.Error += (_, _) => { lock (_gate) _overflow = true; Arm(); };
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Some locations (certain network shares, virtual folders) can't be watched.
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public bool IsWatching => _watcher is not null;

    private void Enqueue(FolderChange change)
    {
        lock (_gate)
        {
            if (_overflow) return;
            if (_pending.Count >= MaxBatch) { _overflow = true; _pending.Clear(); }
            else _pending.Add(change);
        }
        Arm();
    }

    private void Arm()
    {
        if (_disposed) return;
        try { _timer.Change(_debounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Flush()
    {
        List<FolderChange> batch;
        bool overflow;
        lock (_gate)
        {
            batch = _pending;
            overflow = _overflow;
            _pending = new List<FolderChange>();
            _overflow = false;
        }
        if (_disposed) return;
        if (overflow) Overflowed?.Invoke();
        else if (batch.Count > 0) Changed?.Invoke(Coalesce(batch));
    }

    /// <summary>Drops redundant events: repeated "Changed" for the same path, and create+delete pairs.</summary>
    public static IReadOnlyList<FolderChange> Coalesce(IReadOnlyList<FolderChange> changes)
    {
        var result = new List<FolderChange>(changes.Count);
        var seenChanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes)
        {
            if (c.Kind == ChangeKind.Changed && !seenChanged.Add(c.FullPath))
                continue;
            if (c.Kind == ChangeKind.Deleted)
            {
                int created = result.FindLastIndex(r => r.Kind == ChangeKind.Created &&
                    string.Equals(r.FullPath, c.FullPath, StringComparison.OrdinalIgnoreCase));
                if (created >= 0)
                {
                    result.RemoveAt(created);
                    result.RemoveAll(r => r.Kind == ChangeKind.Changed &&
                        string.Equals(r.FullPath, c.FullPath, StringComparison.OrdinalIgnoreCase));
                    continue;
                }
            }
            result.Add(c);
        }
        return result;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        _timer.Dispose();
    }
}
