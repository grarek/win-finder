using System.Text.Json;
using System.Text.Json.Serialization;
using WinFinder.Core.FileSystem;

namespace WinFinder.Core.Settings;

public enum ViewMode { Icons, List, Columns }

public sealed class FolderViewState
{
    public ViewMode View { get; set; }
    public SortKey Sort { get; set; } = SortKey.Name;
    public bool Descending { get; set; }
}

public sealed class TabState
{
    public string Location { get; set; } = "";
    public ViewMode View { get; set; }

    /// <summary>Column view: the folder in the leftmost column, so parents come back on restore.</summary>
    public string? ColumnRoot { get; set; }
}

public sealed class WindowState
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public int SelectedTab { get; set; }
    public List<TabState> Tabs { get; set; } = new();
}

public sealed class SessionState
{
    public List<WindowState> Windows { get; set; } = new();
}

public sealed class AppSettings
{
    public List<string> Favorites { get; set; } = new();
    public bool FavoritesInitialized { get; set; }
    public bool ShowHidden { get; set; }
    public bool FoldersFirst { get; set; } = true;
    public ViewMode DefaultView { get; set; } = ViewMode.List;
    public double IconSize { get; set; } = 96;
    public double ColumnWidth { get; set; } = 240;
    public double SidebarWidth { get; set; } = 210;
    public double NameColumnWidth { get; set; } = 320;
    public double DateColumnWidth { get; set; } = 200;
    public double SizeColumnWidth { get; set; } = 90;
    public double KindColumnWidth { get; set; } = 150;
    public Dictionary<string, FolderViewState> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> ColumnWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SessionState? Session { get; set; }

    // Updates
    public bool AutoCheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
    public string? SkippedVersion { get; set; }
}

/// <summary>JSON settings in %LocalAppData%\WinFinder\settings.json, written atomically and debounced.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Timer _timer;
    private readonly object _gate = new();

    public SettingsStore(string? directory = null)
    {
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "settings.json");
        _timer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        Current = Load();
    }

    /// <summary>
    /// %LocalAppData%\WinFinder, or WINFINDER_DATA_DIR when set (lets a second, test instance
    /// run with its own settings, session and tags without touching yours).
    /// </summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("WINFINDER_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinFinder");

    public AppSettings Current { get; }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions);
                if (s is not null)
                {
                    s.Folders = new Dictionary<string, FolderViewState>(s.Folders, StringComparer.OrdinalIgnoreCase);
                    s.ColumnWidths = new Dictionary<string, double>(s.ColumnWidths, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch
        {
            // Corrupt settings: keep a copy for diagnosis and start fresh.
            try { File.Copy(_path, _path + ".bad", true); } catch { }
        }
        return new AppSettings();
    }

    public void SaveSoon() => _timer.Change(750, Timeout.Infinite);

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                string json = JsonSerializer.Serialize(Current, JsonOptions);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch
            {
                // Settings are best-effort; never crash the app over them.
            }
        }
    }

    public FolderViewState? GetFolderState(string path) =>
        Current.Folders.TryGetValue(path, out var s) ? s : null;

    public void SetFolderState(string path, FolderViewState state)
    {
        Current.Folders[path] = state;
        // Keep the per-folder memory bounded.
        if (Current.Folders.Count > 2000)
            foreach (var key in Current.Folders.Keys.Take(500).ToList())
                Current.Folders.Remove(key);
        SaveSoon();
    }
}
