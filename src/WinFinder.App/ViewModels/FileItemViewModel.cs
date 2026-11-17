using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using WinFinder.App.Services;
using WinFinder.Core.FileSystem;
using WinFinder.Core.Shell;
using WinFinder.Core.Tags;

namespace WinFinder.App.ViewModels;

/// <summary>One file or folder row/cell. Icons, thumbnails and tags load lazily.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private FileEntry _entry;
    private string? _kind;
    private double _iconLoadedSize;
    private double _thumbLoadedSize;
    private bool _iconLoading;
    private bool _thumbLoading;

    public FileItemViewModel(FileEntry entry, int depth = 0)
    {
        _entry = entry;
        Depth = depth;
    }

    public ListLayout Layout => ListLayout.Instance;

    public FileEntry Entry
    {
        get => _entry;
        set
        {
            var old = _entry;
            if (!SetProperty(ref _entry, value)) return;
            _kind = null;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Path));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(ModifiedText));
            OnPropertyChanged(nameof(KindText));
            OnPropertyChanged(nameof(WhereText));
            OnPropertyChanged(nameof(ItemOpacity));
            if (old.Modified != value.Modified || !string.Equals(old.Extension, value.Extension, StringComparison.OrdinalIgnoreCase))
            {
                _iconLoadedSize = 0;
                _thumbLoadedSize = 0;
                if (Icon is not null) EnsureIcon(16);
                if (Thumbnail is not null) EnsureThumbnail(Layout.IconSize);
            }
        }
    }

    public string Path => _entry.FullPath;
    public string Name => _entry.Name;
    public bool IsFolder => _entry.IsDirectory;
    public bool IsDrive => _entry.IsDrive;
    public int Depth { get; }
    public double IndentWidth => Depth * 16;
    public bool CanExpand => IsFolder && !IsDrive;

    /// <summary>Show the "Where" (parent folder) column — for search and tag results.</summary>
    public bool ShowWhere { get; init; }

    public string SizeText => IsFolder ? "--" : Format.Size(_entry.Size);
    public string ModifiedText => IsDrive ? "--" : Format.Date(_entry.Modified, DateTime.Now);
    public string KindText => _kind ??= ShellInfo.GetKind(_entry);
    public string WhereText => System.IO.Path.GetDirectoryName(Path) ?? "";
    public double ItemOpacity => IsCut ? 0.45 : _entry.IsHidden ? 0.6 : 1.0;

    /// <summary>Children currently shown under this row in list view (null when collapsed).</summary>
    public List<FileItemViewModel>? Children { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpanderGlyph))]
    private bool _isExpanded;

    public string ExpanderGlyph => IsExpanded ? "" : "";

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    private IReadOnlyList<Brush> _tagBrushes = Array.Empty<Brush>();

    public IReadOnlyList<int> TagIds { get; private set; } = Array.Empty<int>();
    public bool HasTags => TagBrushes.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRenaming))]
    private bool _isRenaming;

    public bool IsNotRenaming => !IsRenaming;

    [ObservableProperty]
    private string _renameText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ItemOpacity))]
    private bool _isCut;

    /// <summary>True while files are dragged over this folder (shows the drop highlight).</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public void SetTags(IReadOnlyList<int> ids, IReadOnlyDictionary<int, TagDefinition> definitions)
    {
        TagIds = ids;
        TagBrushes = ids.Count == 0
            ? Array.Empty<Brush>()
            : ids.Where(definitions.ContainsKey)
                 .Select(id => definitions[id])
                 .OrderBy(t => t.SortOrder)
                 .Select(t => (Brush)Palette.Brush(t.Color))
                 .ToList();
    }

    public async void EnsureIcon(double size)
    {
        if (_iconLoading || _iconLoadedSize >= size) return;
        _iconLoading = true;
        try
        {
            var img = await AppServices.Icons.GetAsync(_entry, size, ThumbnailMode.Icon);
            if (img is not null)
            {
                Icon = img;
                _iconLoadedSize = size;
            }
        }
        finally { _iconLoading = false; }
    }

    public async void EnsureThumbnail(double size)
    {
        if (_thumbLoading || _thumbLoadedSize >= size) return;
        _thumbLoading = true;
        try
        {
            var img = await AppServices.Icons.GetAsync(_entry, size, ThumbnailMode.Thumbnail);
            if (img is not null)
            {
                Thumbnail = img;
                _thumbLoadedSize = size;
            }
        }
        finally { _thumbLoading = false; }
    }

    public override string ToString() => Name;
}
