using WinFinder.App.ViewModels;
using WinFinder.Core.Navigation;
using WinFinder.Core.Settings;
using WinFinder.Core.Shell;

namespace WinFinder.App.Services;

/// <summary>Tracks open windows, moves tabs between them, and saves/restores the session.</summary>
public static class WindowManager
{
    private static readonly List<MainWindow> OpenWindows = new();
    private static readonly Stack<TabState> ClosedTabs = new();
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;

    public static IReadOnlyList<MainWindow> Windows => OpenWindows;

    public static MainWindow CreateWindow(IEnumerable<BrowserTabViewModel>? tabs = null, WindowState? placement = null, int selectedIndex = 0)
    {
        var window = new MainWindow(tabs, placement, selectedIndex);
        OpenWindows.Add(window);
        window.Activate();
        return window;
    }

    public static MainWindow CreateWindow(Location location, ViewMode? view = null, IReadOnlyList<string>? select = null)
    {
        var tab = new BrowserTabViewModel(location, view);
        tab.Start(select);
        return CreateWindow([tab]);
    }

    /// <summary>
    /// Restores the previous session (or opens Home). A path argument (e.g. a taskbar jump list
    /// favorite) then opens in its own window on top, so the saved tabs aren't lost.
    /// </summary>
    public static void Start(string? startPath)
    {
        RestoreSessionOrHome();
        if (OpenPath(startPath) is { } requested) requested.BringToFront();
    }

    /// <summary>A launch forwarded from a second copy of the app: open its folder, or Home, in a new window.</summary>
    public static void OpenFromLaunch(string? path)
    {
        var window = OpenPath(path) ?? CreateWindow(Location.Folder(KnownFolders.Home), CurrentViewMode());
        window.BringToFront();
    }

    /// <summary>A folder that File Explorer was asked to show (see <see cref="ExplorerReplacement"/>): a new window with the same selection.</summary>
    public static void OpenRedirected(string folder, IReadOnlyList<string> selected)
    {
        var window = CreateWindow(Location.Folder(folder), CurrentViewMode(), selected);
        window.BringToFront();
    }

    private static ViewMode? CurrentViewMode() => OpenWindows.LastOrDefault()?.ViewModel.SelectedTab?.ViewMode;

    /// <summary>Opens a folder (or a file's folder, with the file selected) in a new window. Null if the path doesn't exist.</summary>
    private static MainWindow? OpenPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string expanded = PathUtil.ExpandUserInput(raw);
        if (Directory.Exists(expanded))
            return CreateWindow(Location.Folder(expanded), CurrentViewMode());
        if (File.Exists(expanded) && PathUtil.Parent(expanded) is { } parent)
        {
            return CreateWindow(Location.Folder(parent), CurrentViewMode(), [expanded]);
        }
        return null;
    }

    private static void RestoreSessionOrHome()
    {
        var session = AppServices.Settings.Current.Session;
        if (session is { Windows.Count: > 0 })
        {
            foreach (var ws in session.Windows)
            {
                var tabs = ws.Tabs.Select(t =>
                {
                    var tab = new BrowserTabViewModel(RestoreLocation(t), t.View);
                    tab.Start();
                    return tab;
                }).ToList();
                if (tabs.Count == 0) continue;
                CreateWindow(tabs, ws, ws.SelectedTab);
            }
            if (OpenWindows.Count > 0) return;
        }
        CreateWindow(Location.Folder(KnownFolders.Home));
    }

    /// <summary>
    /// Rebuilds a saved tab's location. Column-view folders get their leftmost column back
    /// (or a sensible ancestor for sessions saved before that was recorded), so parents show.
    /// </summary>
    public static Location RestoreLocation(TabState state)
    {
        var loc = Location.Parse(state.Location);
        if (loc.IsFolder && !Directory.Exists(loc.Path)) return Location.Folder(KnownFolders.Home);
        if (loc.IsFolder && state.View == ViewMode.Columns)
            loc = loc with { ColumnRoot = Location.RestoredColumnRoot(loc.Path, state.ColumnRoot, KnownFolders.Home) };
        return loc;
    }

    public static TabState CaptureTab(BrowserTabViewModel tab) => new()
    {
        Location = tab.Location.Serialize(),
        View = tab.ViewMode,
        ColumnRoot = tab.IsColumnView ? tab.Columns.Root ?? tab.Location.ColumnRoot : null,
    };

    public static (MainWindow Window, BrowserTabViewModel Tab)? FindTab(Guid id)
    {
        foreach (var w in OpenWindows)
            if (w.ViewModel.Tabs.FirstOrDefault(t => t.Id == id) is { } tab)
                return (w, tab);
        return null;
    }

    public static void OnWindowClosing(MainWindow window)
    {
        if (OpenWindows.Count == 1)
        {
            SaveSession(); // last window: its tabs become the session
            AppServices.Shutdown();
        }
    }

    public static void OnWindowClosed(MainWindow window)
    {
        OpenWindows.Remove(window);
        if (OpenWindows.Count > 0) SaveSessionSoon();
    }

    /// <summary>Saves the session and settings, then exits so the updater can replace the app's files.</summary>
    public static void ExitForUpdate()
    {
        SaveSession();
        AppServices.Shutdown();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    public static void SaveSessionSoon()
    {
        if (_saveTimer is null)
        {
            _saveTimer = AppServices.Dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromSeconds(1);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => SaveSession();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void SaveSession()
    {
        var session = new SessionState();
        foreach (var w in OpenWindows)
        {
            var state = w.CaptureState();
            if (state.Tabs.Count > 0) session.Windows.Add(state);
        }
        AppServices.Settings.Current.Session = session;
        AppServices.Settings.Save();
    }

    public static void RememberClosedTab(BrowserTabViewModel tab)
    {
        ClosedTabs.Push(CaptureTab(tab));
        if (ClosedTabs.Count > 25)
        {
            var keep = ClosedTabs.Take(25).Reverse().ToList();
            ClosedTabs.Clear();
            foreach (var t in keep) ClosedTabs.Push(t);
        }
    }

    public static TabState? PopClosedTab() => ClosedTabs.Count > 0 ? ClosedTabs.Pop() : null;
}
