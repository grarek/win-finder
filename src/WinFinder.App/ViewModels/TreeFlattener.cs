using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinFinder.App.Services;
using WinFinder.Core.FileSystem;

namespace WinFinder.App.ViewModels;

/// <summary>
/// Finder's list view lets folders expand inline. This flattens the root items plus any
/// expanded subtrees into one list for a virtualized ListView. Root-level changes are applied
/// incrementally; expanded subtrees are snapshots that refresh when re-expanded.
/// </summary>
public sealed partial class TreeFlattener : ObservableObject
{
    private FolderContents? _source;
    private ObservableCollection<FileItemViewModel>? _sourceItems;

    [ObservableProperty]
    private ObservableCollection<FileItemViewModel> _flatItems = new();

    public void Attach(FolderContents? source)
    {
        if (_source is not null) _source.PropertyChanged -= OnSourcePropertyChanged;
        HookItems(null);
        _source = source;
        if (_source is not null)
        {
            _source.PropertyChanged += OnSourcePropertyChanged;
            HookItems(_source.Items);
        }
        Rebuild();
    }

    private void HookItems(ObservableCollection<FileItemViewModel>? items)
    {
        if (_sourceItems is not null) _sourceItems.CollectionChanged -= OnSourceItemsChanged;
        _sourceItems = items;
        if (_sourceItems is not null) _sourceItems.CollectionChanged += OnSourceItemsChanged;
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FolderContents.Items) || _source is null) return;
        HookItems(_source.Items);
        Rebuild();
    }

    private void Rebuild()
    {
        var list = new List<FileItemViewModel>();
        if (_source is not null)
            foreach (var root in _source.Items)
                AddWithChildren(list, root);
        FlatItems = new ObservableCollection<FileItemViewModel>(list);
    }

    private static void AddWithChildren(List<FileItemViewModel> list, FileItemViewModel item)
    {
        list.Add(item);
        if (item.IsExpanded && item.Children is not null)
            foreach (var child in item.Children)
                AddWithChildren(list, child);
    }

    private void OnSourceItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    var item = (FileItemViewModel)e.NewItems[i]!;
                    int rootIndex = e.NewStartingIndex + i;
                    int flatIndex = FlatItems.Count;
                    if (rootIndex + 1 < _source!.Items.Count)
                    {
                        int next = FlatItems.IndexOf(_source.Items[rootIndex + 1]);
                        if (next >= 0) flatIndex = next;
                    }
                    var subtree = new List<FileItemViewModel>();
                    AddWithChildren(subtree, item);
                    foreach (var s in subtree) FlatItems.Insert(flatIndex++, s);
                }
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (FileItemViewModel item in e.OldItems)
                    RemoveSubtree(item, includeSelf: true);
                break;
            default:
                Rebuild();
                break;
        }
    }

    private void RemoveSubtree(FileItemViewModel item, bool includeSelf)
    {
        int index = FlatItems.IndexOf(item);
        if (index < 0) return;
        int end = index + 1;
        while (end < FlatItems.Count && FlatItems[end].Depth > item.Depth) end++;
        for (int i = end - 1; i > index; i--) FlatItems.RemoveAt(i);
        if (includeSelf) FlatItems.RemoveAt(index);
    }

    public Task ToggleAsync(FileItemViewModel item)
    {
        if (!item.IsExpanded) return ExpandAsync(item);
        Collapse(item);
        return Task.CompletedTask;
    }

    public async Task ExpandAsync(FileItemViewModel item)
    {
        if (!item.CanExpand || item.IsExpanded || _source is null) return;
        var comparer = ItemSorting.Comparer(_source.SortKey, _source.Descending);
        bool hidden = AppServices.ShowHidden;
        List<FileEntry> entries;
        try
        {
            entries = await Task.Run(() =>
            {
                var list = DirectoryLister.List(item.Path, hidden);
                list.Sort(comparer);
                return list;
            });
        }
        catch
        {
            entries = new List<FileEntry>();
        }

        int index = FlatItems.IndexOf(item);
        if (index < 0 || item.IsExpanded) return;

        var defs = ItemSorting.TagDefinitions();
        var tagMap = AppServices.TagStore.GetTagIdsInFolder(item.Path);
        item.Children = entries.Select(e =>
        {
            var vm = new FileItemViewModel(e, item.Depth + 1);
            vm.SetTags(tagMap.TryGetValue(e.FullPath, out var ids) ? ids : Array.Empty<int>(), defs);
            return vm;
        }).ToList();
        item.IsExpanded = true;

        if (item.Children.Count > 400)
        {
            Rebuild();
            return;
        }
        int insertAt = index + 1;
        foreach (var child in item.Children) FlatItems.Insert(insertAt++, child);
    }

    public void Collapse(FileItemViewModel item)
    {
        if (!item.IsExpanded) return;
        RemoveSubtree(item, includeSelf: false);
        item.IsExpanded = false;
        item.Children = null;
    }

    /// <summary>The row's parent row in the flattened list (for Left-arrow navigation).</summary>
    public FileItemViewModel? ParentOf(FileItemViewModel item)
    {
        if (item.Depth == 0) return null;
        for (int i = FlatItems.IndexOf(item) - 1; i >= 0; i--)
            if (FlatItems[i].Depth < item.Depth) return FlatItems[i];
        return null;
    }
}
