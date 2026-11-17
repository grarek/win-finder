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

    public static MainWindow CreateWindow(Location location, ViewMode? view = null)
    {
        var tab = new BrowserTabViewModel(location, view);
        tab.Start();
        return CreateWindow([tab]);
    }

    /// <summary>Restores the previous session, or opens Home. A path argument opens there instead.</summary>
    public static void Start(string? startPath)
    {
        if (!string.IsNullOrWhiteSpace(startPath))
        {
            string expanded = PathUtil.ExpandUserInput(startPath);
            if (Directory.Exists(expanded))
            {
                CreateWindow(Location.Folder(expanded));
                return;
            }
            if (File.Exists(expanded) && PathUtil.Parent(expanded) is { } parent)
            {
                var w = CreateWindow(Location.Folder(parent));
                w.ViewModel.SelectedTab?.RequestSelect([expanded]);
                return;
            }
        }

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
