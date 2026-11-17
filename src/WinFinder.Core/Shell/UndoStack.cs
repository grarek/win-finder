namespace WinFinder.Core.Shell;

public enum UndoKind { Copy, Move, Rename, Recycle, NewItem }

/// <param name="Original">Path before the operation (the source).</param>
/// <param name="Result">Path (or shell parsing name, for recycled items) after the operation.</param>
public sealed record UndoItem(string Original, string Result);

public sealed record UndoEntry(UndoKind Kind, IReadOnlyList<UndoItem> Items)
{
    public string Description => Kind switch
    {
        UndoKind.Copy => Items.Count == 1 ? "Undo Copy" : $"Undo Copy of {Items.Count} Items",
        UndoKind.Move => Items.Count == 1 ? "Undo Move" : $"Undo Move of {Items.Count} Items",
        UndoKind.Rename => "Undo Rename",
        UndoKind.Recycle => Items.Count == 1 ? "Undo Move to Recycle Bin" : $"Undo Move of {Items.Count} Items to Recycle Bin",
        UndoKind.NewItem => "Undo New Folder",
        _ => "Undo",
    };
}

/// <summary>Bounded LIFO of undoable file operations (thread-safe).</summary>
public sealed class UndoStack
{
    private readonly LinkedList<UndoEntry> _entries = new();
    private readonly int _limit;
    private readonly object _gate = new();

    public UndoStack(int limit = 50) => _limit = limit;

    public int Count { get { lock (_gate) return _entries.Count; } }

    public UndoEntry? Peek() { lock (_gate) return _entries.First?.Value; }

    public void Push(UndoEntry entry)
    {
        if (entry.Items.Count == 0) return;
        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > _limit) _entries.RemoveLast();
        }
    }

    public UndoEntry? Pop()
    {
        lock (_gate)
        {
            var first = _entries.First;
            if (first is null) return null;
            _entries.RemoveFirst();
            return first.Value;
        }
    }
}
