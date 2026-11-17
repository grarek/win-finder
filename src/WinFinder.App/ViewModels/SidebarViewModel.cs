using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinFinder.App.Services;
using WinFinder.Core.Navigation;
using WinFinder.Core.Shell;

namespace WinFinder.App.ViewModels;

public enum SidebarKind { Favorite, Computer, Drive, Tag }

public sealed partial class SidebarItem : ObservableObject
{
    public SidebarItem(SidebarKind kind, string name, Location location, string glyph)
    {
        Kind = kind;
        _name = name;
        Location = location;
        Glyph = glyph;
    }

    public SidebarKind Kind { get; }
    public Location Location { get; }
    public string Glyph { get; }
    public string Path => Location.Path;
    public int TagId => Location.TagId;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private Brush? _tagBrush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUsage), nameof(UsedPercent))]
    private double _usedFraction;

    public double UsedPercent => UsedFraction * 100;

    [ObservableProperty]
    private string _detail = "";

    public bool IsTag => Kind == SidebarKind.Tag;
    public bool IsNotTag => Kind != SidebarKind.Tag;
    public bool ShowUsage => Kind == SidebarKind.Drive && UsedFraction > 0;
    public bool CanAcceptDrop => Kind is SidebarKind.Favorite or SidebarKind.Drive;
    public bool IsRemovable => Kind == SidebarKind.Favorite;
}

/// <summary>Sidebar contents shared by every window: Favorites, Locations and Tags.</summary>
public sealed class SidebarViewModel
{
    public static SidebarViewModel Instance { get; } = new();

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _driveTimer;
    private bool _suppressSave;

    public ObservableCollection<SidebarItem> Favorites { get; } = new();
    public ObservableCollection<SidebarItem> Locations { get; } = new();
    public ObservableCollection<SidebarItem> Tags { get; } = new();

    public void Initialize()
    {
        LoadFavorites();
        Favorites.CollectionChanged += (_, _) => SaveFavorites();
        LoadTags();
        AppServices.TagsChanged += LoadTags;
        _ = RefreshDrivesAsync();

        _driveTimer = AppServices.Dispatcher.CreateTimer();
        _driveTimer.Interval = TimeSpan.FromSeconds(5);
        _driveTimer.Tick += async (_, _) => await RefreshDrivesAsync();
        _driveTimer.Start();
    }

    private void LoadFavorites()
    {
        _suppressSave = true;
        Favorites.Clear();
        foreach (var path in AppServices.Settings.Current.Favorites)
            Favorites.Add(MakeFavorite(path));
        _suppressSave = false;
    }

    private static SidebarItem MakeFavorite(string path)
    {
        string glyph = path switch
        {
            _ when Same(path, KnownFolders.Home) => "",       // Home
            _ when Same(path, KnownFolders.Desktop) => "",    // TVMonitor/Desktop
            _ when Same(path, KnownFolders.Documents) => "",  // Document
            _ when Same(path, KnownFolders.Downloads) => "",  // Download
            _ when Same(path, KnownFolders.Pictures) => "",   // Picture
            _ when Same(path, KnownFolders.Music) => "",      // Audio
            _ when Same(path, KnownFolders.Videos) => "",     // Video
            _ => "",                                          // Folder
        };
        string name = BrowserTabViewModel.FolderDisplayName(path);
        return new SidebarItem(SidebarKind.Favorite, name, Location.Folder(path), glyph);

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveFavorites()
    {
        if (_suppressSave) return;
        AppServices.Settings.Current.Favorites = Favorites.Select(f => f.Path).ToList();
        AppServices.Settings.SaveSoon();
    }

    public void AddFavorite(string path, int index = -1)
    {
        path = PathUtil.Normalize(path);
        if (!Directory.Exists(path)) return;
        if (Favorites.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        var item = MakeFavorite(path);
        if (index < 0 || index > Favorites.Count) Favorites.Add(item);
        else Favorites.Insert(index, item);
    }

    public void RemoveFavorite(SidebarItem item) => Favorites.Remove(item);

    public void RemoveFavorite(string path)
    {
        var match = Favorites.FirstOrDefault(f => string.Equals(f.Path, PathUtil.Normalize(path), StringComparison.OrdinalIgnoreCase));
        if (match is not null) Favorites.Remove(match);
    }

    public bool IsFavorite(string path) =>
        Favorites.Any(f => string.Equals(f.Path, PathUtil.Normalize(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>Pins every folder that isn't pinned yet (files are ignored). Returns how many were added.</summary>
    public int AddFavorites(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths.Where(Directory.Exists))
        {
            if (IsFavorite(p)) continue;
            AddFavorite(p);
            added++;
        }
        return added;
    }

    private void LoadTags()
    {
        var tags = AppServices.TagStore.GetTags();
        Tags.Clear();
        foreach (var t in tags)
            Tags.Add(new SidebarItem(SidebarKind.Tag, t.Name, Location.Tag(t.Id), "") { TagBrush = Palette.Brush(t.Color) });
    }

    private async Task RefreshDrivesAsync()
    {
        IReadOnlyList<DriveDescriptor> drives;
        try { drives = await Task.Run(Drives.List); }
        catch { return; }

        if (Locations.Count == 0)
            Locations.Add(new SidebarItem(SidebarKind.Computer, "This PC", Location.Computer, ""));

        var wanted = drives.Select(d => d.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = Locations.Count - 1; i >= 1; i--)
            if (!wanted.Contains(Locations[i].Path)) Locations.RemoveAt(i);

        foreach (var d in drives)
        {
            var existing = Locations.FirstOrDefault(l => l.Kind == SidebarKind.Drive && string.Equals(l.Path, d.Root, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                string glyph = d.Type switch
                {
                    DriveType.Network => "",
                    DriveType.Removable => "",
                    DriveType.CDRom => "",
                    _ => "",
                };
                existing = new SidebarItem(SidebarKind.Drive, d.Label, Location.Folder(d.Root), glyph);
                Locations.Add(existing);
            }
            existing.Name = d.Label;
            existing.UsedFraction = d.UsedFraction;
            existing.Detail = d.IsReady ? $"{Core.FileSystem.Format.Size(d.FreeBytes)} free of {Core.FileSystem.Format.Size(d.TotalBytes)}" : "Not ready";
        }
    }

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
