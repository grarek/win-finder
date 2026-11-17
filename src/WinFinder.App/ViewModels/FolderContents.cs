using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using WinFinder.App.Services;
using WinFinder.Core.FileSystem;
using WinFinder.Core.Navigation;
using WinFinder.Core.Search;
using WinFinder.Core.Shell;
using WinFinder.Core.Tags;

namespace WinFinder.App.ViewModels;

public static class ItemSorting
{
    public static IComparer<FileEntry> Comparer(SortKey key, bool descending) =>
        FileSorter.CreateComparer(key, descending, AppServices.FoldersFirst, ShellInfo.GetKind);

    public static IComparer<FileItemViewModel> ItemComparer(SortKey key, bool descending)
    {
        var c = Comparer(key, descending);
        return Comparer<FileItemViewModel>.Create((a, b) => c.Compare(a.Entry, b.Entry));
    }

    public static Dictionary<int, TagDefinition> TagDefinitions() =>
        AppServices.TagStore.GetTags().ToDictionary(t => t.Id);
}

/// <summary>
/// The sorted, live contents of one location: a folder (watched for changes), "This PC",
/// a tag collection or search results. Loads off the UI thread and publishes snapshots.
/// </summary>
public sealed partial class FolderContents : ObservableObject, IDisposable
{
    private readonly Dictionary<string, FileItemViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _depth;
    private CancellationTokenSource? _cts;
    private FolderWatcher? _watcher;
    private bool _disposed;

    public FolderContents(Location location, SortKey sortKey, bool descending, int depth = 0)
    {
        Location = location;
        SortKey = sortKey;
        Descending = descending;
        _depth = depth;
        AppServices.TagsChanged += OnTagsChanged;
        AppServices.ShowHiddenChanged += OnShowHiddenChanged;
        AppServices.FoldersFirstChanged += OnFoldersFirstChanged;
        AppServices.FileOperationCompleted += OnFileOperationCompleted;
    }

    public Location Location { get; }
    public string? FolderPath => Location.IsFolder ? Location.Path : null;
    public SortKey SortKey { get; private set; }
    public bool Descending { get; private set; }
    public bool IsWatching => _watcher?.IsWatching == true;

    [ObservableProperty]
    private ObservableCollection<FileItemViewModel> _items = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;
    public bool ShowEmptyHint => !IsLoading && Error is null && Items.Count == 0;

    /// <summary>Raised once a load (or reload) has finished.</summary>
    public event Action? Loaded;

    public FileItemViewModel? Find(string path) => _byPath.GetValueOrDefault(path);

    public async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        _watcher?.Dispose();
        _watcher = null;

        IsLoading = true;
        Error = null;
        var all = new List<FileEntry>();
        var clock = Stopwatch.StartNew();
        long lastPublish = 0;
        try
        {
            await foreach (var batch in Source(ct))
            {
                all.AddRange(batch);
                if (clock.ElapsedMilliseconds - lastPublish > 400)
                {
                    Publish(all);
                    lastPublish = clock.ElapsedMilliseconds;
                }
            }
            if (ct.IsCancellationRequested || _disposed) return;
            Publish(all);
            StartWatching();
        }
        catch (OperationCanceledException) { return; }
        catch (UnauthorizedAccessException) { Fail("You don't have permission to see the contents of this folder."); }
        catch (DirectoryNotFoundException) { Fail("This folder can't be found. It may have been moved or deleted."); }
        catch (IOException ex) { Fail(ex.Message); }
        catch (Exception ex) { Fail(ex.Message); }
        finally
        {
            if (!ct.IsCancellationRequested && !_disposed)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(ShowEmptyHint));
                Loaded?.Invoke();
            }
        }
    }

    private void Fail(string message)
    {
        Error = message;
        Items = new ObservableCollection<FileItemViewModel>();
        _byPath.Clear();
    }

    private IAsyncEnumerable<IReadOnlyList<FileEntry>> Source(CancellationToken ct)
    {
        bool hidden = AppServices.ShowHidden;
        return Location.Kind switch
        {
            LocationKind.Folder => DirectoryLister.ListAsync(Location.Path, hidden, ct: ct),
            LocationKind.Computer => Single(() => Drives.List()
                .Select(d => FileEntry.FromDrive(d.Root, d.Label)).ToList()),
            LocationKind.Tag => Single(() => AppServices.TagStore.GetPathsWithTag(Location.TagId)
                .Select(FileEntry.TryFromPath).OfType<FileEntry>()
                .Where(e => hidden || !e.IsHidden).ToList()),
            LocationKind.Search => SearchService.SearchAsync(Location.Query,
                Location.SearchEverywhere ? null : Location.Path, hidden, ct),
            _ => Single(() => new List<FileEntry>()),
        };

        static async IAsyncEnumerable<IReadOnlyList<FileEntry>> Single(Func<List<FileEntry>> producer)
        {
            yield return await Task.Run(producer);
        }
    }

    private IComparer<FileEntry> CurrentComparer() =>
        Location.Kind == LocationKind.Computer
            ? Comparer<FileEntry>.Create((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase))
            : ItemSorting.Comparer(SortKey, Descending);

    /// <summary>Replaces the collection with a sorted snapshot, reusing existing row view models.</summary>
    private void Publish(List<FileEntry> entries)
    {
        var sorted = new List<FileEntry>(entries);
        sorted.Sort(CurrentComparer());

        var old = new Dictionary<string, FileItemViewModel>(_byPath, StringComparer.OrdinalIgnoreCase);
        _byPath.Clear();
        var vms = new List<FileItemViewModel>(sorted.Count);
        foreach (var e in sorted)
        {
            if (_byPath.ContainsKey(e.FullPath)) continue; // search can report duplicates
            if (old.TryGetValue(e.FullPath, out var vm)) vm.Entry = e;
            else vm = new FileItemViewModel(e, _depth) { ShowWhere = Location.Kind is LocationKind.Search or LocationKind.Tag };
            _byPath[e.FullPath] = vm;
            vms.Add(vm);
        }
        Items = new ObservableCollection<FileItemViewModel>(vms);
        OnPropertyChanged(nameof(ShowEmptyHint));
        ApplyTags();
    }

    public void SetSort(SortKey key, bool descending)
    {
        SortKey = key;
        Descending = descending;
        Resort();
    }

    private void Resort()
    {
        var list = Items.ToList();
        var cmp = CurrentComparer();
        list.Sort((a, b) => cmp.Compare(a.Entry, b.Entry));
        Items = new ObservableCollection<FileItemViewModel>(list);
    }

    // ----- live updates ---------------------------------------------------------------

    private void StartWatching()
    {
        if (!Location.IsFolder) return;
        _watcher = new FolderWatcher(Location.Path);
        _watcher.Changed += changes => AppServices.Dispatcher.TryEnqueue(() => Apply(changes));
        _watcher.Overflowed += () => AppServices.Dispatcher.TryEnqueue(() => _ = LoadAsync());
    }

    private void Apply(IReadOnlyList<FolderChange> changes)
    {
        if (_disposed) return;
        foreach (var c in changes)
        {
            switch (c.Kind)
            {
                case ChangeKind.Created:
                    EnsureItem(c.FullPath);
                    break;
                case ChangeKind.Deleted:
                    Remove(c.FullPath);
                    break;
                case ChangeKind.Renamed:
                    if (c.OldFullPath is not null && Find(c.OldFullPath) is { } renamed && FileEntry.TryFromPath(c.FullPath) is { } newEntry)
                    {
                        Items.Remove(renamed);
                        _byPath.Remove(c.OldFullPath);
                        renamed.Entry = newEntry;
                        if (IsVisible(newEntry)) InsertSorted(renamed);
                    }
                    else
                    {
                        if (c.OldFullPath is not null) Remove(c.OldFullPath);
                        EnsureItem(c.FullPath);
                    }
                    break;
                case ChangeKind.Changed:
                    if (Find(c.FullPath) is { } vm)
                    {
                        var e = FileEntry.TryFromPath(c.FullPath);
                        if (e is null) { Remove(c.FullPath); break; }
                        vm.Entry = e;
                        if (SortKey is SortKey.Modified or SortKey.Size)
                        {
                            Items.Remove(vm);
                            _byPath.Remove(vm.Path);
                            InsertSorted(vm);
                        }
                    }
                    break;
            }
        }
        OnPropertyChanged(nameof(ShowEmptyHint));
    }

    /// <summary>Adds the item at <paramref name="path"/> if it belongs here and isn't listed yet.</summary>
    public FileItemViewModel? EnsureItem(string path)
    {
        if (Find(path) is { } existing) return existing;
        if (!Location.IsFolder || !string.Equals(PathUtil.Parent(path), Location.Path, StringComparison.OrdinalIgnoreCase))
            return null;
        var entry = FileEntry.TryFromPath(path);
        if (entry is null || !IsVisible(entry)) return null;
        var vm = new FileItemViewModel(entry, _depth);
        InsertSorted(vm);
        var defs = ItemSorting.TagDefinitions();
        vm.SetTags(AppServices.TagStore.GetTagIds(path), defs);
        OnPropertyChanged(nameof(ShowEmptyHint));
        return vm;
    }

    public void Remove(string path)
    {
        if (Find(path) is not { } vm) return;
        Items.Remove(vm);
        _byPath.Remove(path);
        OnPropertyChanged(nameof(ShowEmptyHint));
    }

    private void InsertSorted(FileItemViewModel vm)
    {
        var cmp = CurrentComparer();
        int lo = 0, hi = Items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (cmp.Compare(Items[mid].Entry, vm.Entry) <= 0) lo = mid + 1;
            else hi = mid;
        }
        Items.Insert(lo, vm);
        _byPath[vm.Path] = vm;
    }

    private static bool IsVisible(FileEntry e)
    {
        if (AppServices.ShowHidden) return !DirectoryLister.IsProtectedOsFile(e.Attributes);
        return !e.IsHidden && !e.IsSystem;
    }

    // ----- tags ------------------------------------------------------------------------

    private void ApplyTags()
    {
        if (Items.Count == 0) return;
        var defs = ItemSorting.TagDefinitions();
        if (Location.IsFolder)
        {
            var map = AppServices.TagStore.GetTagIdsInFolder(Location.Path);
            foreach (var vm in Items)
                vm.SetTags(map.TryGetValue(vm.Path, out var ids) ? ids : Array.Empty<int>(), defs);
        }
        else if (Location.Kind != LocationKind.Computer)
        {
            foreach (var vm in Items)
                vm.SetTags(AppServices.TagStore.GetTagIds(vm.Path), defs);
        }
    }

    private void OnTagsChanged()
    {
        if (_disposed) return;
        if (Location.Kind == LocationKind.Tag) _ = LoadAsync();
        else ApplyTags();
    }

    private void OnShowHiddenChanged()
    {
        if (!_disposed) _ = LoadAsync();
    }

    private void OnFoldersFirstChanged()
    {
        if (!_disposed) Resort();
    }

    private void OnFileOperationCompleted()
    {
        // Folders we can't watch (some network shares) still refresh after our own operations.
        if (!_disposed && Location.IsFolder && !IsWatching && !IsLoading) _ = LoadAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        _cts?.Cancel();
        _watcher?.Dispose();
        AppServices.TagsChanged -= OnTagsChanged;
        AppServices.ShowHiddenChanged -= OnShowHiddenChanged;
        AppServices.FoldersFirstChanged -= OnFoldersFirstChanged;
        AppServices.FileOperationCompleted -= OnFileOperationCompleted;
    }
}
