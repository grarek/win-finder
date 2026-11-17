using System.Collections.Concurrent;

namespace WinFinder.Core.Shell;

/// <summary>
/// A small pool of dedicated STA threads. Shell thumbnail handlers and IFileOperation
/// require (or behave best on) single-threaded apartments, and must stay off the UI thread.
/// </summary>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue;
    private readonly List<Thread> _threads = new();

    /// <param name="lifo">Run the most recently queued work first (good for thumbnails while scrolling).</param>
    public StaWorker(string name, int threadCount = 1, bool lifo = false)
    {
        _queue = lifo
            ? new BlockingCollection<Action>(new ConcurrentStack<Action>())
            : new BlockingCollection<Action>(new ConcurrentQueue<Action>());

        for (int i = 0; i < threadCount; i++)
        {
            var t = new Thread(Loop) { IsBackground = true, Name = $"{name} #{i + 1}" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _threads.Add(t);
        }
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch { /* individual work items report their own failures */ }
        }
    }

    public Task<T> RunAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            tcs.SetCanceled(ct);
            return tcs.Task;
        }
        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task RunAsync(Action action, CancellationToken ct = default) =>
        RunAsync(() => { action(); return true; }, ct);

    public void Dispose() => _queue.CompleteAdding();
}
