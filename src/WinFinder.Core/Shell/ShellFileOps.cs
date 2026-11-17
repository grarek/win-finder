using System.Runtime.InteropServices;
using WinFinder.Core.Navigation;

namespace WinFinder.Core.Shell;

public sealed record FileOpResult(bool Aborted, IReadOnlyList<string> ResultPaths)
{
    public static readonly FileOpResult Nothing = new(false, Array.Empty<string>());
}

/// <summary>
/// All mutating file operations go through the shell's IFileOperation so the user gets
/// Explorer's native progress, conflict, elevation and Recycle Bin behaviour. Each
/// operation records an <see cref="UndoEntry"/> so Ctrl+Z can reverse it.
/// </summary>
public sealed class ShellFileOps : IDisposable
{
    private readonly StaWorker _worker = new("FileOps");

    public ShellFileOps(UndoStack? undo = null) => Undo = undo ?? new UndoStack();

    public UndoStack Undo { get; }

    /// <summary>Owner window for progress/conflict dialogs.</summary>
    public IntPtr OwnerWindow { get; set; }

    /// <summary>Raised after items move or are renamed (old path, new path). Used to keep tags attached.</summary>
    public event Action<IReadOnlyList<(string OldPath, string NewPath)>>? ItemsRelocated;

    /// <summary>Raised after any operation completes, with the folders that changed.</summary>
    public event Action? OperationCompleted;

    public Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder) =>
        RunAsync(UndoKind.Copy, sources, destinationFolder, record: true);

    public Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder)
    {
        var effective = sources.Where(s => !string.Equals(PathUtil.Parent(s), PathUtil.Normalize(destinationFolder), StringComparison.OrdinalIgnoreCase))
                               .Where(s => !PathUtil.IsSameOrUnder(destinationFolder, s)) // can't move a folder into itself
                               .ToList();
        if (effective.Count == 0) return Task.FromResult(FileOpResult.Nothing);
        return RunAsync(UndoKind.Move, effective, destinationFolder, record: true);
    }

    public Task<FileOpResult> RecycleAsync(IReadOnlyList<string> paths) =>
        RunAsync(UndoKind.Recycle, paths, null, record: true);

    public Task<FileOpResult> DeletePermanentlyAsync(IReadOnlyList<string> paths) =>
        _worker.RunAsync(() =>
        {
            // No FOF_ALLOWUNDO: the shell asks for confirmation and deletes for good.
            var outcome = Execute(Native.FOFX_SHOWELEVATIONPROMPT, (op, _) =>
            {
                foreach (var p in paths) op.DeleteItem(Native.CreateItem(p), null);
            });
            OperationCompleted?.Invoke();
            return new FileOpResult(outcome.Aborted, Array.Empty<string>());
        });

    public Task<FileOpResult> RenameAsync(string path, string newName) =>
        RunAsync(UndoKind.Rename, [path], null, record: true, newName: newName);

    public Task<FileOpResult> NewFolderAsync(string parentFolder, string name) =>
        RunAsync(UndoKind.NewItem, Array.Empty<string>(), parentFolder, record: true, newName: name);

    /// <summary>Reverses the most recent operation. Returns the entry that was undone, or null.</summary>
    public async Task<UndoEntry?> UndoLastAsync()
    {
        var entry = Undo.Pop();
        if (entry is null) return null;

        await _worker.RunAsync(() =>
        {
            var relocated = new List<(string, string)>();
            switch (entry.Kind)
            {
                case UndoKind.Copy:
                case UndoKind.NewItem:
                    Execute(Native.FOF_ALLOWUNDO | Native.FOFX_RECYCLEONDELETE | Native.FOF_NOCONFIRMATION, (op, _) =>
                    {
                        foreach (var item in entry.Items)
                            if (TryCreate(item.Result) is { } si) op.DeleteItem(si, null);
                    });
                    break;

                case UndoKind.Move:
                case UndoKind.Recycle:
                    Execute(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR, (op, _) =>
                    {
                        foreach (var item in entry.Items)
                        {
                            var parent = PathUtil.Parent(item.Original);
                            if (parent is null) continue;
                            var source = TryCreate(item.Result);
                            if (source is null) continue;
                            op.MoveItem(source, Native.CreateItem(parent), Path.GetFileName(item.Original), null);
                            relocated.Add((item.Result, item.Original));
                        }
                    });
                    break;

                case UndoKind.Rename:
                    Execute(Native.FOF_ALLOWUNDO, (op, _) =>
                    {
                        foreach (var item in entry.Items)
                        {
                            if (TryCreate(item.Result) is not { } si) continue;
                            op.RenameItem(si, Path.GetFileName(item.Original), null);
                            relocated.Add((item.Result, item.Original));
                        }
                    });
                    break;
            }
            if (relocated.Count > 0 && entry.Kind != UndoKind.Recycle) ItemsRelocated?.Invoke(relocated);
            OperationCompleted?.Invoke();
        });
        return entry;
    }

    private Task<FileOpResult> RunAsync(UndoKind kind, IReadOnlyList<string> sources, string? destination, bool record, string? newName = null) =>
        _worker.RunAsync(() =>
        {
            uint flags = Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR | Native.FOFX_SHOWELEVATIONPROMPT;
            if (kind == UndoKind.Recycle)
                flags |= Native.FOFX_RECYCLEONDELETE | Native.FOF_NOCONFIRMATION;
            if (kind == UndoKind.Copy && destination is not null &&
                sources.Any(s => string.Equals(PathUtil.Parent(s), PathUtil.Normalize(destination), StringComparison.OrdinalIgnoreCase)))
                flags |= Native.FOF_RENAMEONCOLLISION; // duplicate in place -> "name - Copy"

            var outcome = Execute(flags, (op, _) =>
            {
                IShellItem? dest = destination is null ? null : Native.CreateItem(destination);
                switch (kind)
                {
                    case UndoKind.Copy:
                        foreach (var s in sources) op.CopyItem(Native.CreateItem(s), dest!, null, null);
                        break;
                    case UndoKind.Move:
                        foreach (var s in sources) op.MoveItem(Native.CreateItem(s), dest!, null, null);
                        break;
                    case UndoKind.Recycle:
                        foreach (var s in sources) op.DeleteItem(Native.CreateItem(s), null);
                        break;
                    case UndoKind.Rename:
                        op.RenameItem(Native.CreateItem(sources[0]), newName!, null);
                        break;
                    case UndoKind.NewItem:
                        op.NewItem(dest!, Native.FILE_ATTRIBUTE_DIRECTORY, newName!, null, null);
                        break;
                }
            });

            var items = outcome.Records.Where(r => r.Kind == kind).Select(r => new UndoItem(r.Source, r.Result)).ToList();
            if (record) Undo.Push(new UndoEntry(kind, items));

            if (kind is UndoKind.Move or UndoKind.Rename && items.Count > 0)
                ItemsRelocated?.Invoke(items.Select(i => (i.Original, i.Result)).ToList());
            OperationCompleted?.Invoke();

            var resultPaths = kind == UndoKind.Recycle
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : items.Select(i => i.Result).ToList();
            return new FileOpResult(outcome.Aborted, resultPaths);
        });

    private static IShellItem? TryCreate(string parsingName)
    {
        try { return Native.CreateItem(parsingName); }
        catch { return null; }
    }

    private sealed record Outcome(bool Aborted, List<SinkRecord> Records);

    private Outcome Execute(uint flags, Action<IFileOperation, ProgressSink> queue)
    {
        var op = (IFileOperation)new FileOperationClass();
        var sink = new ProgressSink();
        uint cookie = 0;
        try
        {
            op.SetOperationFlags(flags);
            if (OwnerWindow != IntPtr.Zero) op.SetOwnerWindow(OwnerWindow);
            cookie = op.Advise(sink);
            queue(op, sink);
            int hr = op.PerformOperations();
            bool aborted = hr < 0;
            try { aborted |= op.GetAnyOperationsAborted(); } catch { }
            return new Outcome(aborted, sink.Records);
        }
        catch (COMException)
        {
            return new Outcome(true, sink.Records);
        }
        finally
        {
            if (cookie != 0) { try { op.Unadvise(cookie); } catch { } }
            Marshal.ReleaseComObject(op);
        }
    }

    public void Dispose() => _worker.Dispose();

    internal sealed record SinkRecord(UndoKind Kind, string Source, string Result);

    /// <summary>Collects what actually happened (including auto-renamed copies and Recycle Bin item names).</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class ProgressSink : IFileOperationProgressSink
    {
        public List<SinkRecord> Records { get; } = new();

        private static string PathOf(IShellItem? item) =>
            item is null ? "" : Native.GetName(item, SIGDN.FILESYSPATH) ?? Native.GetName(item, SIGDN.DESKTOPABSOLUTEPARSING) ?? "";

        private void Add(UndoKind kind, IShellItem source, int hr, IShellItem? created, bool preferParsingName = false)
        {
            if (hr < 0 || created is null) return;
            string result = preferParsingName
                ? Native.GetName(created, SIGDN.DESKTOPABSOLUTEPARSING) ?? PathOf(created)
                : PathOf(created);
            if (result.Length > 0) Records.Add(new SinkRecord(kind, PathOf(source), result));
        }

        public void StartOperations() { }
        public void FinishOperations(int hrResult) { }
        public void PreRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName) { }
        public void PostRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName, int hrRename, IShellItem? psiNewlyCreated) =>
            Add(UndoKind.Rename, psiItem, hrRename, psiNewlyCreated);
        public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) { }
        public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated) =>
            Add(UndoKind.Move, psiItem, hrMove, psiNewlyCreated);
        public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) { }
        public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated) =>
            Add(UndoKind.Copy, psiItem, hrCopy, psiNewlyCreated);
        public void PreDeleteItem(uint dwFlags, IShellItem psiItem) { }
        public void PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated) =>
            Add(UndoKind.Recycle, psiItem, hrDelete, psiNewlyCreated, preferParsingName: true);
        public void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName) { }
        public void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName, string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem)
        {
            if (hrNew >= 0 && psiNewItem is not null)
                Records.Add(new SinkRecord(UndoKind.NewItem, "", PathOf(psiNewItem)));
        }
        public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar) { }
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }
}
