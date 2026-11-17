using System.Collections.Specialized;
using WinFinder.App.ViewModels;
using WinFinder.Core.Shell;

namespace WinFinder.App.Services;

/// <summary>
/// Keeps the taskbar jump list (right-click WinFinder's taskbar button) in step with the sidebar
/// Favorites: pin, unpin, reorder or rename, and the list follows shortly after.
/// </summary>
public static class JumpListService
{
    private static readonly StaWorker Worker = new("JumpList");
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _debounce;

    /// <summary>Where the favorites went last time (a "Favorites" section, or tasks when Windows blocks sections).</summary>
    public static JumpList.Placement LastPlacement { get; private set; }

    public static void Initialize()
    {
        var favorites = SidebarViewModel.Instance.Favorites;
        favorites.CollectionChanged += OnFavoritesChanged;
        foreach (var f in favorites) f.PropertyChanged += OnFavoritePropertyChanged;

        _debounce = AppServices.Dispatcher.CreateTimer();
        _debounce.Interval = TimeSpan.FromSeconds(1);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Push();
        Push(); // build it once at startup
    }

    private static void OnFavoritesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (SidebarItem item in e.NewItems ?? Array.Empty<SidebarItem>()) item.PropertyChanged += OnFavoritePropertyChanged;
        foreach (SidebarItem item in e.OldItems ?? Array.Empty<SidebarItem>()) item.PropertyChanged -= OnFavoritePropertyChanged;
        Schedule();
    }

    private static void OnFavoritePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarItem.Name)) Schedule();
    }

    // A reorder arrives as a remove plus an add; debounce so the list is rebuilt once.
    private static void Schedule()
    {
        _debounce?.Stop();
        _debounce?.Start();
    }

    private static void Push()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        var items = SidebarViewModel.Instance.Favorites
            .Select(f => new JumpListFolder(f.Name, f.Path))
            .ToList();
        _ = Worker.RunAsync(() =>
        {
            try
            {
                LastPlacement = JumpList.Update(exe, items);
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Core.Settings.SettingsStore.DefaultDirectory, "errors.log"),
                        $"[{DateTime.Now:O}] Jump list update failed: {ex}\n\n");
                }
                catch { }
            }
        });
    }
}
