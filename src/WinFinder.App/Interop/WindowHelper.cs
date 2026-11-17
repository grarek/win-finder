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
