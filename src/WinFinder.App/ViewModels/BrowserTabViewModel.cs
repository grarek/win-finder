using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinFinder.App.Services;
using WinFinder.Core.FileSystem;
using WinFinder.Core.Navigation;
using WinFinder.Core.Settings;
using WinFinder.Core.Shell;

namespace WinFinder.App.ViewModels;

public enum SegmentKind { Computer, Folder, File, Tag, Search }

public sealed record PathSegment(string Name, string Path, SegmentKind Kind)
{
    public override string ToString() => Name;
}

/// <summary>
/// One tab: its location, back/forward history, view mode, sort, selection and contents.
/// Every tab is independent, and its state stays in memory so switching tabs is instant.
/// </summary>
public sealed partial class BrowserTabViewModel : ObservableObject, IDisposable
{
    private readonly NavigationHistory<Location> _history;
    private ObservableCollection<FileItemViewModel>? _countedItems;
    private readonly Dictionary<string, long> _freeSpace = new(StringComparer.OrdinalIgnoreCase);

    public BrowserTabViewModel(Location location, ViewMode? view = null)
    {
        _history = new NavigationHistory<Location>(location);
        _location = location;
        _viewMode = view ?? AppServices.Settings.Current.DefaultView;
        Columns.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ColumnBrowserViewModel.ActiveColumn) && Columns.ActiveColumn is { } active)
            {
                TrackCount(active.Contents);
                OnPropertyChanged(nameof(ActiveContents));
            }
            if (e.PropertyName is nameof(ColumnBrowserViewModel.ActiveFolder) or nameof(ColumnBrowserViewModel.SelectedPath))
            {
                UpdatePathSegments();
                UpdateStatus();
                OnPropertyChanged(nameof(CurrentFolder));
            }
        };
        Columns.CurrentFolderChanged += OnColumnFolderChanged;
    }

    /// <summary>
    /// Column view: selecting a folder makes it the current folder (title, path bar, history)
    /// without rebuilding the columns, so its parents stay on the left — like Finder.
    /// Moving between siblings in one column replaces the history entry instead of adding one,
    /// so arrowing down a long column doesn't flood Back.
    /// </summary>
    private void OnColumnFolderChanged(string folder)
    {
        if (!IsColumnView || Columns.Root is not { } root) return;
        var next = Location.Folder(folder) with { ColumnRoot = root };
        if (next.SameAs(Location)) return;

        bool sibling = string.Equals(PathUtil.Parent(next.Path), PathUtil.Parent(Location.Path), StringComparison.OrdinalIgnoreCase);
        if (sibling) _history.ReplaceCurrent(next);
        else _history.Navigate(next);

        Location = next;
        Title = ComputeTitle();
        OnPropertyChanged(nameof(TitleTooltip));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        UpdatePathSegments();
        UpdateStatus();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public TreeFlattener Tree { get; } = new();
    public ColumnBrowserViewModel Columns { get; } = new();
    public ObservableCollection<PathSegment> PathSegments { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearch), nameof(ShowWhereColumn), nameof(EffectiveView), nameof(IsListView),
        nameof(IsIconView), nameof(IsColumnView), nameof(CurrentFolder), nameof(SearchScopeName))]
    private Location _location;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveView), nameof(IsListView), nameof(IsIconView), nameof(IsColumnView), nameof(ViewModeIndex))]
    private ViewMode _viewMode;

    [ObservableProperty]
    private FolderContents? _contents;

    [ObservableProperty]
    private IReadOnlyList<FileItemViewModel> _selectedItems = Array.Empty<FileItemViewModel>();

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private SortKey _sortKey = SortKey.Name;

    [ObservableProperty]
    private bool _sortDescending;

    public ViewMode EffectiveView => ViewMode == ViewMode.Columns && !Location.IsFolder ? ViewMode.List : ViewMode;
    public bool IsListView => EffectiveView == ViewMode.List;
    public bool IsIconView => EffectiveView == ViewMode.Icons;
    public bool IsColumnView => EffectiveView == ViewMode.Columns;
    public int ViewModeIndex => (int)ViewMode;

    public bool IsSearch => Location.Kind == LocationKind.Search;
    public bool ShowWhereColumn => Location.Kind is LocationKind.Search or LocationKind.Tag;
    public string SearchScopeName => string.IsNullOrEmpty(Location.Path) ? "This PC" : $"“{FolderDisplayName(Location.Path)}”";

    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;
    public bool CanGoUp => CurrentFolder is { } f && PathUtil.Parent(f) is not null || Location.IsFolder;

    /// <summary>The real folder commands act on (null for "This PC", tags and searches).</summary>
    public string? CurrentFolder => !Location.IsFolder ? null
        : IsColumnView ? Columns.ActiveFolder ?? Location.Path
        : Location.Path;

    /// <summary>Contents shown by the active view (the focused column in column view).</summary>
    public FolderContents? ActiveContents => IsColumnView ? Columns.ActiveColumn?.Contents : Contents;

    public string TitleTooltip => Location.Kind switch
    {
        LocationKind.Folder => Location.Path,
        LocationKind.Search => $"Search results for “{Location.Query}”",
        _ => Title,
    };

    // ----- view -> VM requests ------------------------------------------------------------

    /// <summary>Ask the view to select paths (and optionally start renaming the first).</summary>
    public event Action<IReadOnlyList<string>, bool>? SelectRequested;
    public event Action<int>? MoveSelectionRequested;
    public event Action? FocusRequested;

    public void RequestSelect(IReadOnlyList<string> paths, bool beginRename = false) => SelectRequested?.Invoke(paths, beginRename);
    public void RequestMoveSelection(int delta) => MoveSelectionRequested?.Invoke(delta);
    public void RequestFocus() => FocusRequested?.Invoke();

    // ----- navigation ---------------------------------------------------------------------

    public void Start() => Load(null);

    /// <summary>First load, selecting these items once the folder is listed (e.g. "Show in folder").</summary>
    public void Start(IReadOnlyList<string>? select) => LoadSelecting(select);

    public void Navigate(Location location, string? selectPath = null)
    {
        if (location.SameAs(Location) && location.Kind != LocationKind.Search)
        {
            if (selectPath is not null) RequestSelect([selectPath]);
            return;
        }
        _history.Navigate(location);
        Load(selectPath);
    }

    /// <summary>
    /// Where a path-bar click should go. In column view the folder keeps its parents to the left:
    /// the current leftmost column stays if the folder is inside it; otherwise the columns start
    /// at the folder's drive, so its whole ancestry is shown.
    /// </summary>
    public Location PathBarLocation(string folder)
    {
        var target = Location.Folder(folder);
        if (!IsColumnView) return target;
        string? root = Columns.Root is { } current && PathUtil.IsSameOrUnder(target.Path, current)
            ? current
            : Path.GetPathRoot(target.Path);
        return target with { ColumnRoot = root };
    }

    public void GoBack()
    {
        var from = Location;
        if (_history.GoBack() is null) return;
        // Column view restores the recorded columns instead (revealing the child would re-open it).
        Load(IsColumnView ? null : ChildToReveal(from));
    }

    public void GoForward()
    {
        if (_history.GoForward() is null) return;
        Load(null);
    }

    public void GoUp()
    {
        string? folder = IsColumnView ? Location.Path : CurrentFolder;
        if (folder is null)
        {
            if (Location.Kind != LocationKind.Computer) Navigate(Location.Computer);
            return;
        }
        var parent = PathUtil.Parent(folder);
        if (parent is null) Navigate(Location.Computer, folder);
        else if (IsColumnView)
            // Keep the parents on the left; going above the leftmost column starts there instead.
            Navigate(Location.Folder(parent) with { ColumnRoot = Location.ColumnRoot ?? Columns.Root });
        else Navigate(Location.Folder(parent), folder);
    }

    /// <summary>When going back to a parent, select the folder we came from.</summary>
    private string? ChildToReveal(Location from)
    {
        if (!from.IsFolder || !Location.IsFolder) return null;
        return string.Equals(PathUtil.Parent(from.Path), Location.Path, StringComparison.OrdinalIgnoreCase) ? from.Path : null;
    }

    public void Refresh()
    {
        string? keep = SelectedItems.FirstOrDefault()?.Path;
        Load(keep);
    }

    private void Load(string? selectPath) => LoadSelecting(selectPath is null ? null : [selectPath]);

    private void LoadSelecting(IReadOnlyList<string>? select)
    {
        select = select is { Count: > 0 } ? select : null;
        string? selectPath = select?[0];
        Location = _history.Current;

        // A location recorded inside column view stays in column view (a folder's own saved
        // list/icon preference shouldn't yank the window out of the columns on Back/Forward).
        bool withinColumns = ViewMode == ViewMode.Columns && Location.ColumnRoot is not null;
        if (Location.IsFolder && !withinColumns && AppServices.Settings.GetFolderState(Location.Path) is { } state)
        {
            ViewMode = state.View;
            SortKey = state.Sort;
            SortDescending = state.Descending;
        }

        Title = ComputeTitle();
        OnPropertyChanged(nameof(TitleTooltip));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        SelectedItems = Array.Empty<FileItemViewModel>();

        Contents?.Dispose();
        Contents = null;
        Tree.Attach(null);
        Columns.Clear();

        if (IsColumnView)
        {
            // Leftmost column is the recorded root (parents stay visible); the current folder is
            // revealed selected in its parent's column, which opens its own column to the right.
            // A file to select (e.g. from Go to Folder) is revealed instead; a folder isn't,
            // because selecting it would make it the current folder.
            string root = Location.EffectiveColumnRoot;
            string? reveal = selectPath is not null && File.Exists(selectPath) ? selectPath
                : string.Equals(root, Location.Path, StringComparison.OrdinalIgnoreCase) ? null
                : Location.Path;
            Columns.SetRoot(root, reveal, SortKey, SortDescending);
            if (reveal is null && Columns.FolderColumns.LastOrDefault() is { } last)
            {
                void OnLoaded() { last.Contents.Loaded -= OnLoaded; StartupTrace.Mark($"column listed: {last.Folder}"); RequestFocus(); }
                last.Contents.Loaded += OnLoaded;
            }
        }
        else
        {
            var contents = new FolderContents(Location, SortKey, SortDescending);
            Contents = contents;
            Tree.Attach(contents);
            TrackCount(contents);
            contents.Loaded += () =>
            {
                if (!ReferenceEquals(Contents, contents)) return;
                StartupTrace.Mark($"folder listed: {Location.Path}");
                UpdateStatus();
                if (select is not null) RequestSelect(select);
                else RequestFocus();
            };
            _ = contents.LoadAsync();
        }

        UpdatePathSegments();
        UpdateStatus();
        OnPropertyChanged(nameof(ActiveContents));
    }

    private void TrackCount(FolderContents contents)
    {
        contents.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderContents.Items)) HookCount(contents.Items);
        };
        HookCount(contents.Items);
    }

    private void HookCount(ObservableCollection<FileItemViewModel> items)
    {
        if (_countedItems is not null) _countedItems.CollectionChanged -= OnCountedItemsChanged;
        _countedItems = items;
        _countedItems.CollectionChanged += OnCountedItemsChanged;
        UpdateStatus();
    }

    private void OnCountedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateStatus();

    private string ComputeTitle() => Location.Kind switch
    {
        LocationKind.Computer => "This PC",
        LocationKind.Tag => AppServices.TagStore.GetTags().FirstOrDefault(t => t.Id == Location.TagId)?.Name ?? "Tag",
        LocationKind.Search => $"Searching “{Location.Query}”",
        _ => FolderDisplayName(Location.Path),
    };

    public static string FolderDisplayName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "This PC";
        string name = Path.GetFileName(path.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) return path.TrimEnd('\\'); // drive root: "C:"
        if (string.Equals(path, KnownFolders.Home, StringComparison.OrdinalIgnoreCase)) return name;
        return name;
    }

    // ----- view mode & sorting --------------------------------------------------------------

    public void SetViewMode(ViewMode mode)
    {
        if (mode == ViewMode) return;

        if (IsColumnView && Location.IsFolder)
        {
            // Leaving column view: the current (selected) folder is what the list/icon view shows.
            string target = Location.Path;
            string? select = SelectedItems.FirstOrDefault(i =>
                string.Equals(PathUtil.Parent(i.Path), target, StringComparison.OrdinalIgnoreCase))?.Path;
            ViewMode = mode;
            SaveFolderState(target);
            // The location keeps its column root, so switching straight back to columns restores
            // the same columns. List and icon views ignore it.
            Load(select);
            return;
        }

        string? keep = SelectedItems.FirstOrDefault()?.Path;
        ViewMode = mode;
        if (Location.IsFolder) SaveFolderState(Location.Path);
        Load(keep);
    }

    public void SetSort(SortKey key)
    {
        if (key == SortKey) SortDescending = !SortDescending;
        else
        {
            SortKey = key;
            SortDescending = key is SortKey.Modified or SortKey.Size; // newest/largest first, like Finder
        }
        Contents?.SetSort(SortKey, SortDescending);
        Columns.SetSort(SortKey, SortDescending);
        if (Location.IsFolder) SaveFolderState(Location.Path);
    }

    private void SaveFolderState(string folder) =>
        AppServices.Settings.SetFolderState(folder, new FolderViewState { View = ViewMode, Sort = SortKey, Descending = SortDescending });

    // ----- search ---------------------------------------------------------------------------

    public void Search(string text, bool? everywhere = null)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            if (IsSearch)
            {
                var back = string.IsNullOrEmpty(Location.Path) ? Location.Computer : Location.Folder(Location.Path);
                _history.ReplaceCurrent(back);
                Load(null);
            }
            return;
        }

        string scope = IsSearch ? Location.Path : CurrentFolder ?? "";
        bool all = everywhere ?? (IsSearch ? Location.SearchEverywhere : string.IsNullOrEmpty(scope));
        var loc = Location.Search(text, scope, all);
        if (IsSearch) _history.ReplaceCurrent(loc);
        else _history.Navigate(loc);
        Load(null);
    }

    // ----- path bar & status ----------------------------------------------------------------

    private void UpdatePathSegments()
    {
        PathSegments.Clear();
        switch (Location.Kind)
        {
            case LocationKind.Computer:
                PathSegments.Add(new PathSegment("This PC", "", SegmentKind.Computer));
                break;
            case LocationKind.Tag:
                PathSegments.Add(new PathSegment(Title, "", SegmentKind.Tag));
                break;
            case LocationKind.Search:
                PathSegments.Add(new PathSegment(Title, "", SegmentKind.Search));
                break;
            default:
                PathSegments.Add(new PathSegment("This PC", "", SegmentKind.Computer));
                string deepest = IsColumnView ? Columns.SelectedPath ?? Location.Path : Location.Path;
                foreach (var seg in PathUtil.Segments(Directory.Exists(deepest) ? deepest : PathUtil.Parent(deepest) ?? deepest))
                    PathSegments.Add(new PathSegment(FolderDisplayName(seg), seg, SegmentKind.Folder));
                if (File.Exists(deepest))
                    PathSegments.Add(new PathSegment(Path.GetFileName(deepest), deepest, SegmentKind.File));
                break;
        }
    }

    partial void OnSelectedItemsChanged(IReadOnlyList<FileItemViewModel> value) => UpdateStatus();

    public void UpdateStatus()
    {
        var contents = ActiveContents;
        int count = contents?.Items.Count ?? 0;
        int selected = SelectedItems.Count;
        string text = selected > 0 ? $"{selected:N0} of {count:N0} selected" : Format.ItemCount(count);
        if (contents?.IsLoading == true && count == 0) text = "Loading…";

        string? folder = CurrentFolder;
        if (folder is not null && Path.GetPathRoot(folder) is { Length: > 0 } root)
        {
            if (_freeSpace.TryGetValue(root, out long free) && free >= 0)
                text += $", {Format.Size(free)} available";
            else if (!_freeSpace.ContainsKey(root))
            {
                _freeSpace[root] = -1;
                _ = Task.Run(() => Drives.FreeSpace(root)).ContinueWith(t =>
                {
                    AppServices.Dispatcher.TryEnqueue(() =>
                    {
                        _freeSpace[root] = t.Result;
                        UpdateStatus();
                    });
                });
            }
        }
        StatusText = text;
    }

    /// <summary>Forget cached free space (after file operations) so the status bar stays honest.</summary>
    public void InvalidateFreeSpace()
    {
        _freeSpace.Clear();
        UpdateStatus();
    }

    // ----- file actions ---------------------------------------------------------------------

    /// <summary>Opens an item in this tab: folders navigate, files launch with their default app.</summary>
    public void OpenItem(FileItemViewModel item, IntPtr hwnd)
    {
        if (item.IsFolder)
        {
            if (IsColumnView && !item.IsDrive && Columns.ColumnContaining(item) is { } column)
            {
                OpenInColumns(column, item);
                return;
            }
            Navigate(Location.Folder(item.Path));
            return;
        }
        ShellInfo.Open(item.Path, hwnd);
    }

    /// <summary>
    /// Double-click / Enter on a folder in column view, Finder-style: open it as the next column
    /// (making it the current folder) and move into that column, keeping all parent columns.
    /// </summary>
    private void OpenInColumns(ColumnViewModel column, FileItemViewModel item)
    {
        Columns.OnSelectionChanged(column, [item]); // no-op if the click already opened it
        if (Columns.ColumnFor(item.Path) is { } opened)
        {
            Columns.SetActive(opened);
            RequestFocus();
        }
    }

    public void BeginRename(FileItemViewModel item)
    {
        if (item.IsDrive) return;
        foreach (var other in (ActiveContents?.Items ?? []).Where(i => i.IsRenaming)) other.IsRenaming = false;
        item.RenameText = item.Name;
        item.IsRenaming = true;
    }

    public void CancelRename(FileItemViewModel item) => item.IsRenaming = false;

    public async Task CommitRenameAsync(FileItemViewModel item)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;
        string newName = item.RenameText.Trim();
        if (newName.Length == 0 || newName == item.Name) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;

        var result = await AppServices.FileOps.RenameAsync(item.Path, newName);
        if (result.ResultPaths.FirstOrDefault() is { } renamed)
        {
            ActiveContents?.Remove(item.Path);
            ActiveContents?.EnsureItem(renamed);
            RequestSelect([renamed]);
        }
        RequestFocus();
    }

    public async Task NewFolderAsync()
    {
        if (CurrentFolder is not { } folder) return;
        string name = PathUtil.UniqueChildName(folder, "New folder", isFolder: true);
        var result = await AppServices.FileOps.NewFolderAsync(folder, name);
        if (result.ResultPaths.FirstOrDefault() is { } created)
        {
            var contents = ActiveContents;
            if (contents?.EnsureItem(created) is { } vm)
            {
                RequestSelect([created], beginRename: true);
                BeginRename(vm);
            }
        }
    }

    public async Task RecycleAsync(IReadOnlyList<FileItemViewModel> items, bool permanently)
    {
        var paths = items.Where(i => !i.IsDrive).Select(i => i.Path).ToList();
        if (paths.Count == 0) return;
        var result = permanently
            ? await AppServices.FileOps.DeletePermanentlyAsync(paths)
            : await AppServices.FileOps.RecycleAsync(paths);
        foreach (var p in paths)
            if (!File.Exists(p) && !Directory.Exists(p))
                ActiveContents?.Remove(p);
        InvalidateFreeSpace();
        RequestFocus();
    }

    /// <summary>After copy/move/paste/drop into the current folder: show and select the new items.</summary>
    public void RevealNewItems(IReadOnlyList<string> paths)
    {
        var contents = ActiveContents;
        if (contents is null) return;
        var shown = paths.Where(p => contents.EnsureItem(p) is not null).ToList();
        if (shown.Count > 0) RequestSelect(shown);
        InvalidateFreeSpace();
    }

    public void Dispose()
    {
        Contents?.Dispose();
        Columns.Dispose();
        if (_countedItems is not null) _countedItems.CollectionChanged -= OnCountedItemsChanged;
    }
}
