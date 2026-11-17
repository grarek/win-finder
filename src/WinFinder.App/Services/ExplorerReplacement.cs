using System.Runtime.InteropServices;
using System.Text;
using WinFinder.Core.Shell;

namespace WinFinder.App.Services;

/// <summary>
/// "Use WinFinder instead of File Explorer" (off by default). When on:
/// <list type="bullet">
/// <item>folders, drives and Win+E open in WinFinder, even when it isn't running
/// (<see cref="DefaultFileManager"/>);</item>
/// <item>while WinFinder is running, new File Explorer windows that show a folder on disk are
/// closed and the folder opens in WinFinder instead, with the same items selected. This covers
/// what the registry can't: a browser's "Show in folder" after a download, "Open file
/// location", and other apps that ask Explorer to show a file.</item>
/// </list>
/// Explorer windows that show something other than a folder on disk (Home, This PC, Control
/// Panel, Recycle Bin...) and windows that were already open are left alone.
/// </summary>
public static class ExplorerReplacement
{
    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WinFinder.exe");

    public static bool IsEnabled => AppServices.Settings.Current.ReplaceExplorer;

    /// <summary>At startup: resume watching, and repair the registration if the app has moved.</summary>
    public static void Initialize()
    {
        if (!IsEnabled) return;
        try
        {
            var registry = DefaultFileManager.ForCurrentUser();
            // A stale registration (WinFinder moved since it was set) would break opening folders.
            if (registry.RegisteredExecutable() is { } registered && !File.Exists(registered))
                registry.Register(ExePath);
        }
        catch { }
        Redirector.Start();
    }

    /// <summary>Turns the feature on. Returns false (and changes nothing) if the registry can't be written.</summary>
    public static bool Enable()
    {
        try { DefaultFileManager.ForCurrentUser().Register(ExePath); }
        catch { return false; }
        AppServices.Settings.Current.ReplaceExplorer = true;
        AppServices.Settings.SaveSoon();
        Redirector.Start();
        return true;
    }

    public static void Disable()
    {
        try { DefaultFileManager.ForCurrentUser().Unregister(); } catch { }
        AppServices.Settings.Current.ReplaceExplorer = false;
        AppServices.Settings.SaveSoon();
        Redirector.Stop();
    }

    public static void Shutdown() => Redirector.Stop();

    /// <summary>
    /// Opens a folder in File Explorer on purpose ("Open in File Explorer"). Explorer's own
    /// "open" verb is used explicitly (WinFinder only replaces the default verb), and the
    /// redirect stands aside for the new window.
    /// </summary>
    public static void OpenInFileExplorer(string folder, IntPtr hwnd = default)
    {
        Redirector.SuppressBriefly();
        ShellInfo.Open(folder, hwnd, verb: "open");
    }

    /// <summary>Watches for new File Explorer windows and moves them into WinFinder.</summary>
    private static class Redirector
    {
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNA = 8;

        private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        // Kept alive for as long as the hook exists (the native side holds a pointer to it).
        private static readonly WinEventDelegate Callback = OnWinEvent;
        private static readonly HashSet<IntPtr> Seen = new();
        private static IntPtr _hook;
        private static StaWorker? _worker;
        private static DateTime _suppressUntilUtc;

        public static void Start()
        {
            if (_hook != IntPtr.Zero) return;
            // Windows that are already open stay in Explorer.
            Seen.Clear();
            EnumWindows((hwnd, _) => { if (IsExplorerWindow(hwnd)) Seen.Add(hwnd); return true; }, IntPtr.Zero);
            _worker ??= new StaWorker("Explorer redirect");
            // Out-of-context: no code is injected into Explorer; events arrive on this (UI) thread.
            _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, Callback, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        public static void Stop()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        public static void SuppressBriefly() => _suppressUntilUtc = DateTime.UtcNow.AddSeconds(6);

        private static bool IsExplorerWindow(IntPtr hwnd)
        {
            var name = new StringBuilder(32);
            return GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == "CabinetWClass";
        }

        private static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // the window itself, not a part of it
            if (!IsExplorerWindow(hwnd) || !Seen.Add(hwnd)) return;
            if (Seen.Count > 64) Seen.RemoveWhere(h => !IsWindow(h));
            if (DateTime.UtcNow < _suppressUntilUtc) return;
            _ = RedirectAsync(hwnd);
        }

        private static async Task RedirectAsync(IntPtr hwnd)
        {
            if (_worker is null) return;
            ExplorerView? view;
            try { view = await _worker.RunAsync(() => ReadView(hwnd)); }
            catch { return; }
            if (view is null) return;

            try
            {
                WindowManager.OpenRedirected(view.Folder, view.Selected);
            }
            catch
            {
                ShowWindow(hwnd, SW_SHOWNA); // couldn't open it here: give the Explorer window back
                return;
            }
            try
            {
                await _worker.RunAsync(() =>
                {
                    try { view.Window.Quit(); }
                    catch { ShowWindow(hwnd, SW_SHOWNA); } // never leave a hidden Explorer window behind
                });
            }
            catch { }
        }

        private sealed record ExplorerView(string Folder, IReadOnlyList<string> Selected, dynamic Window);

        /// <summary>
        /// Finds the Explorer window in Shell.Application's window list and reads its folder and
        /// selection, waiting for it to finish loading. Null if it doesn't show a folder on disk.
        /// Runs on an STA thread.
        /// </summary>
        private static ExplorerView? ReadView(IntPtr hwnd)
        {
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")); // ShellWindows
            if (type is null) return null;
            dynamic windows = Activator.CreateInstance(type)!;

            var deadline = DateTime.UtcNow.AddSeconds(5);
            string? folder = null;
            dynamic? window = null;
            while (DateTime.UtcNow < deadline && IsWindow(hwnd))
            {
                window ??= FindWindow(windows, hwnd);
                if (window is not null)
                {
                    try { folder = (string)window.Document.Folder.Self.Path; } catch { folder = null; } // still loading
                    if (!string.IsNullOrEmpty(folder)) break;
                }
                Thread.Sleep(40);
            }
            if (window is null || string.IsNullOrEmpty(folder)) return null;
            if (!Directory.Exists(folder)) return null; // This PC, Home, Control Panel...: leave it in Explorer

            // It's ours: hide it while the selection (e.g. "Show in folder") is applied.
            ShowWindow(hwnd, SW_HIDE);
            var selected = new List<string>();
            var selectionDeadline = DateTime.UtcNow.AddMilliseconds(700);
            do
            {
                selected.Clear();
                try
                {
                    dynamic items = window.Document.SelectedItems();
                    int count = items.Count;
                    for (int i = 0; i < count; i++) selected.Add((string)items.Item(i).Path);
                }
                catch { }
                if (selected.Count > 0) break;
                Thread.Sleep(50);
            }
            while (DateTime.UtcNow < selectionDeadline && IsWindow(hwnd));

            return new ExplorerView(folder, selected, (object)window);
        }

        private static dynamic? FindWindow(dynamic windows, IntPtr hwnd)
        {
            int count;
            try { count = windows.Count; } catch { return null; }
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic? w = windows.Item(i);
                    if (w is not null && new IntPtr(Convert.ToInt64(w.HWND)) == hwnd) return w;
                }
                catch { }
            }
            return null;
        }
    }
}
