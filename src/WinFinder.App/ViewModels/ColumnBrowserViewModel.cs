using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using WinFinder.App.Services;
using WinFinder.Core.FileSystem;
using WinFinder.Core.Navigation;
using WinFinder.Core.Shell;

namespace WinFinder.App.ViewModels;

/// <summary>One folder column in column view.</summary>
public sealed partial class ColumnViewModel : ObservableObject, IDisposable
{
    private readonly ColumnBrowserViewModel _owner;

    public ColumnViewModel(ColumnBrowserViewModel owner, string folder, SortKey key, bool descending)
    {
        _owner = owner;
        Folder = folder;
        Contents = new FolderContents(Location.Folder(folder), key, descending);
        _width = AppServices.Settings.Current.ColumnWidths.TryGetValue(folder, out var w)
            ? w : AppServices.Settings.Current.ColumnWidth;
        Contents.Loaded += () => PendingSelectionReady?.Invoke(this);
    }

    public string Folder { get; }
    public FolderContents Contents { get; }

    /// <summary>Item to select once loaded (used when revealing a deep path).</summary>
    public string? PendingSelectPath { get; set; }

    public event Action<ColumnViewModel>? PendingSelectionReady;

    [ObservableProperty]
    private double _width;

    /// <summary>Set while applying a width to every column, to avoid feedback loops.</summary>
    internal bool SuppressWidthPropagation { get; set; }

    partial void OnWidthChanged(double value)
    {
        if (SuppressWidthPropagation) return;
        _owner.OnColumnResized(this, value);
    }

    public void Dispose() => Contents.Dispose();
}

/// <summary>The last column in column view: a large preview of the selected file.</summary>
public sealed partial class PreviewColumnViewModel : ObservableObject
{
    public PreviewColumnViewModel(FileItemViewModel item)
    {
        Item = item;
        Load();
    }

    public FileItemViewModel Item { get; }
    public string Name => Item.Name;
    public string KindAndSize => Item.IsFolder ? Item.KindText : $"{Item.KindText} - {Item.SizeText}";
    public string Created => Format.Date(Item.Entry.Created, DateTime.Now);
    public string Modified => Format.Date(Item.Entry.Modified, DateTime.Now);
    public IReadOnlyList<Brush> TagBrushes => Item.TagBrushes;

    [ObservableProperty]
    private ImageSource? _image;

    private async void Load()
    {
        Image = await AppServices.Icons.GetAsync(Item.Entry, 220, ThumbnailMode.Thumbnail);
    }
}

/// <summary>Finder's column (Miller) view, driven by the pure <see cref="ColumnChain"/> model.</summary>
public sealed partial class ColumnBrowserViewModel : ObservableObject, IDisposable
{
    private ColumnChain? _chain;
    private SortKey _sortKey = SortKey.Name;
    private bool _descending;

    public ObservableCollection<object> Columns { get; } = new();

    [ObservableProperty]
    private ColumnViewModel? _activeColumn;

    /// <summary>Raised after a column is appended (the view scrolls it into view).</summary>
    public event Action? ColumnAppended;

    /// <summary>
    /// Raised when the user's selection changes which folder is "current": the selected folder,
    /// or the column's own folder when a file (or nothing) is selected there.
    /// Not raised while restoring columns for an existing location.
    /// </summary>
    public event Action<string>? CurrentFolderChanged;

    /// <summary>Raised when a pending path should be selected in a column (view performs selection).</summary>
    public event Action<ColumnViewModel, string>? SelectInColumnRequested;

    public string? Root => _chain?.Root;

    /// <summary>The folder commands such as New Folder and Paste act on.</summary>
    public string? ActiveFolder => ActiveColumn?.Folder ?? _chain?.Root;

    /// <summary>Deepest selected path (file or folder) — shown in the path bar.</summary>
    public string? SelectedPath => _chain?.PreviewFile ?? (_chain is { Folders.Count: > 1 } ? _chain.Deepest : null);

    public IEnumerable<ColumnViewModel> FolderColumns => Columns.OfType<ColumnViewModel>();

    public void SetSort(SortKey key, bool descending)
    {
        _sortKey = key;
        _descending = descending;
        foreach (var c in FolderColumns) c.Contents.SetSort(key, descending);
    }

    /// <summary>Shows <paramref name="root"/> as the first column, optionally revealing a deeper path.</summary>
    public void SetRoot(string root, string? revealPath, SortKey key, bool descending)
    {
        _sortKey = key;
        _descending = descending;
        Clear();

        string? targetFolder = null;
        if (revealPath is not null && PathUtil.IsSameOrUnder(revealPath, root) &&
            !string.Equals(PathUtil.Normalize(revealPath), PathUtil.Normalize(root), StringComparison.OrdinalIgnoreCase))
            targetFolder = PathUtil.Parent(revealPath);

        _chain = targetFolder is null ? new ColumnChain(root) : ColumnChain.FromPath(root, targetFolder);

        var folders = _chain.Folders;
        for (int i = 0; i < folders.Count; i++)
        {
            var col = CreateColumn(folders[i]);
            col.PendingSelectPath = i + 1 < folders.Count ? folders[i + 1] : revealPath;
            Columns.Add(col);
            _ = col.Contents.LoadAsync();
        }
        ActiveColumn = Columns.OfType<ColumnViewModel>().LastOrDefault();
        OnPropertyChanged(nameof(ActiveFolder));
        OnPropertyChanged(nameof(SelectedPath));
        ColumnAppended?.Invoke();
    }

    private ColumnViewModel CreateColumn(string folder)
    {
        var col = new ColumnViewModel(this, folder, _sortKey, _descending);
        col.PendingSelectionReady += c =>
        {
            if (c.PendingSelectPath is { } p)
            {
                c.PendingSelectPath = null;
                SelectInColumnRequested?.Invoke(c, p);
            }
        };
        return col;
    }

    /// <summary>Called by the view when the selection in a column changes.</summary>
    public void OnSelectionChanged(ColumnViewModel column, IReadOnlyList<FileItemViewModel> selected)
    {
        if (_chain is null) return;
        int index = Columns.IndexOf(column);
        if (index < 0) return;
        ActiveColumn = column;

        ColumnChange change;
        if (selected.Count == 1)
        {
            var item = selected[0];
            // Re-selecting what is already open (e.g. while revealing a path) is a no-op.
            if (item.IsFolder && index + 1 < _chain.Folders.Count &&
                string.Equals(_chain.Folders[index + 1], item.Path, StringComparison.OrdinalIgnoreCase) &&
                Columns.Count > index + 1)
                return;
            if (!item.IsFolder && _chain.PreviewFile == item.Path && Columns.Count > index + 1)
                return;
            change = _chain.Select(index, item.Path, item.IsFolder);
        }
        else
        {
            change = _chain.ClearSelection(index);
        }

        while (Columns.Count > change.TruncateFrom)
        {
            if (Columns[^1] is ColumnViewModel removed) removed.Dispose();
            Columns.RemoveAt(Columns.Count - 1);
        }

        if (change.AddedFolder is not null)
        {
            var col = CreateColumn(change.AddedFolder);
            Columns.Add(col);
            _ = col.Contents.LoadAsync();
        }
        else if (change.PreviewFile is not null && selected.Count == 1)
        {
            Columns.Add(new PreviewColumnViewModel(selected[0]));
        }

        OnPropertyChanged(nameof(ActiveFolder));
        OnPropertyChanged(nameof(SelectedPath));
        ColumnAppended?.Invoke();
        CurrentFolderChanged?.Invoke(change.AddedFolder ?? column.Folder);
    }

    /// <summary>The column whose list contains this exact item, if any.</summary>
    public ColumnViewModel? ColumnContaining(FileItemViewModel item) =>
        FolderColumns.FirstOrDefault(c => ReferenceEquals(c.Contents.Find(item.Path), item));

    /// <summary>The column listing <paramref name="folder"/>'s contents, if open.</summary>
    public ColumnViewModel? ColumnFor(string folder) =>
        FolderColumns.FirstOrDefault(c => string.Equals(c.Folder, PathUtil.Normalize(folder), StringComparison.OrdinalIgnoreCase));

    public void SetActive(ColumnViewModel column)
    {
        ActiveColumn = column;
        OnPropertyChanged(nameof(ActiveFolder));
    }

    internal void OnColumnResized(ColumnViewModel column, double width)
    {
        var s = AppServices.Settings.Current;
        bool shift = Interop.WindowHelper.IsShiftDown();
        if (shift)
        {
            // Shift+drag resizes every column (and becomes the default).
            s.ColumnWidth = width;
            foreach (var c in FolderColumns)
            {
                if (ReferenceEquals(c, column)) continue;
                c.SuppressWidthPropagation = true;
                c.Width = width;
                c.SuppressWidthPropagation = false;
                s.ColumnWidths.Remove(c.Folder);
            }
            s.ColumnWidths.Remove(column.Folder);
        }
        else
        {
            s.ColumnWidths[column.Folder] = width;
        }
        AppServices.Settings.SaveSoon();
    }

    public void Clear()
    {
        foreach (var c in FolderColumns) c.Dispose();
        Columns.Clear();
        ActiveColumn = null;
        _chain = null;
    }

    public int TotalItemsInActive => ActiveColumn?.Contents.Items.Count ?? 0;

    public void Dispose() => Clear();
}
