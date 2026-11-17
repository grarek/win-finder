using Microsoft.UI.Dispatching;
using WinFinder.Core.Settings;
using WinFinder.Core.Shell;
using WinFinder.Core.Tags;

namespace WinFinder.App.Services;

/// <summary>Process-wide services shared by every window and tab.</summary>
public static class AppServices
{
    public static SettingsStore Settings { get; private set; } = null!;
    public static TagStore TagStore { get; private set; } = null!;
    public static TagService Tags { get; private set; } = null!;
    public static ShellFileOps FileOps { get; private set; } = null!;
    public static ShellThumbnails Thumbnails { get; private set; } = null!;
    public static IconCache Icons { get; private set; } = null!;
    public static DispatcherQueue Dispatcher { get; private set; } = null!;

    /// <summary>Raster scale of the most recently active window (for crisp icons).</summary>
    public static double DisplayScale { get; set; } = 1.0;

    public static event Action? ShowHiddenChanged;
    public static event Action? FoldersFirstChanged;

    /// <summary>Raised on the UI thread whenever tags or tag definitions change.</summary>
    public static event Action? TagsChanged;

    /// <summary>Raised on the UI thread after a file operation finishes.</summary>
    public static event Action? FileOperationCompleted;

    public static void Initialize(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Settings = new SettingsStore();
        TagStore = new TagStore(Path.Combine(SettingsStore.DefaultDirectory, "tags.db"));
        Tags = new TagService(TagStore);
        FileOps = new ShellFileOps();
        Thumbnails = new ShellThumbnails();
        Icons = new IconCache(Thumbnails);

        FileOps.ItemsRelocated += moves => Tags.OnItemsRelocated(moves);
        FileOps.OperationCompleted += () => dispatcher.TryEnqueue(() => FileOperationCompleted?.Invoke());

        var tagDebounce = dispatcher.CreateTimer();
        tagDebounce.Interval = TimeSpan.FromMilliseconds(120);
        tagDebounce.IsRepeating = false;
        tagDebounce.Tick += (_, _) => TagsChanged?.Invoke();
        TagStore.Changed += () => dispatcher.TryEnqueue(() => { tagDebounce.Stop(); tagDebounce.Start(); });

        var s = Settings.Current;
        if (!s.FavoritesInitialized)
        {
            s.Favorites = KnownFolders.DefaultFavorites().ToList();
            s.FavoritesInitialized = true;
            Settings.SaveSoon();
        }

        // Housekeeping: drop tags of files that were deleted outside the app.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            try { TagStore.PruneMissing(); } catch { }
        });

        // WebView2 (Quick Look for PDF/HTML) needs a writable data folder when unpackaged.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
            Path.Combine(SettingsStore.DefaultDirectory, "WebView2"));
    }

    public static bool ShowHidden
    {
        get => Settings.Current.ShowHidden;
        set
        {
            if (Settings.Current.ShowHidden == value) return;
            Settings.Current.ShowHidden = value;
            Settings.SaveSoon();
            ShowHiddenChanged?.Invoke();
        }
    }

    public static bool FoldersFirst
    {
        get => Settings.Current.FoldersFirst;
        set
        {
            if (Settings.Current.FoldersFirst == value) return;
            Settings.Current.FoldersFirst = value;
            Settings.SaveSoon();
            FoldersFirstChanged?.Invoke();
        }
    }

    public static void Shutdown()
    {
        Settings.Save();
        FileOps.Dispose();
        Thumbnails.Dispose();
        TagStore.Dispose();
    }
}
