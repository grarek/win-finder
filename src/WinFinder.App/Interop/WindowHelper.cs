using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using WinFinder.Core.Input;
using WinFinder.Core.Shell;

namespace WinFinder.App.Interop;

public static class WindowHelper
{
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINTSTRUCT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINTSTRUCT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTSTRUCT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Lets another process (the running WinFinder) bring its window to the front.</summary>
    public static void AllowSetForeground(int processId) => AllowSetForegroundWindow(processId);

    public static void BringToForeground(IntPtr hwnd) => SetForegroundWindow(hwnd);

    /// <summary>The user's double-click interval (Control Panel setting), in milliseconds.</summary>
    public static uint DoubleClickTimeMs => GetDoubleClickTime();

    // Kept alive for the life of the process (the native side holds a pointer to it).
    private static readonly SubclassProc MenuProc = (hWnd, msg, wParam, lParam, id, data) =>
    {
        if (ShellContextMenu.IsActive && ShellContextMenu.HandleWindowMessage(msg, wParam, lParam, out var result))
            return result;
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    };

    public static IntPtr GetHwnd(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    // Undocumented but stable uxtheme exports (by ordinal) that Explorer, Notepad and Paint use for
    // dark Win32 menus. Ordinal 135 is SetPreferredAppMode on Windows 10 1903 and later.
    [DllImport("uxtheme.dll", EntryPoint = "#133")]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    private static bool? _darkMenus;

    /// <summary>Makes native popup menus (the shell context menu) dark or light to match the app, like Explorer's.</summary>
    public static void UseDarkMenus(IntPtr hwnd, bool dark)
    {
        if (_darkMenus == dark) return;
        try
        {
            const int ForceDark = 2, ForceLight = 3;
            AllowDarkModeForWindow(hwnd, dark);
            SetPreferredAppMode(dark ? ForceDark : ForceLight);
            FlushMenuThemes();
            _darkMenus = dark;
        }
        catch { _darkMenus = dark; } // not available on this Windows build: menus stay light
    }

    /// <summary>Forwards menu messages to the active shell context menu (needed for "Send to", "Open with").</summary>
    public static void InstallShellMenuHook(IntPtr hwnd) => SetWindowSubclass(hwnd, MenuProc, (UIntPtr)0x5746, IntPtr.Zero);

    /// <summary>Converts a point in XAML coordinates (relative to the window content) to screen pixels.</summary>
    public static (int X, int Y) ToScreen(IntPtr hwnd, Point xamlPoint, double scale)
    {
        var p = new POINTSTRUCT { X = (int)(xamlPoint.X * scale), Y = (int)(xamlPoint.Y * scale) };
        ClientToScreen(hwnd, ref p);
        return (p.X, p.Y);
    }

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    public static bool IsCtrlDown() => IsKeyDown(VirtualKey.Control);
    public static bool IsShiftDown() => IsKeyDown(VirtualKey.Shift);
    public static bool IsAltDown() => IsKeyDown(VirtualKey.Menu);

    public static KeyModifiers CurrentModifiers()
    {
        var m = KeyModifiers.None;
        if (IsCtrlDown()) m |= KeyModifiers.Ctrl;
        if (IsShiftDown()) m |= KeyModifiers.Shift;
        if (IsAltDown()) m |= KeyModifiers.Alt;
        return m;
    }
}
