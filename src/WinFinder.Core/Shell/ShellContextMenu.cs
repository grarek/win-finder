using System.Runtime.InteropServices;

namespace WinFinder.Core.Shell;

/// <summary>An app-defined item placed at the top of the native context menu.</summary>
public sealed record MenuExtra(int Id, string Text, IReadOnlyList<MenuExtra>? Children = null, bool Checked = false, bool Enabled = true)
{
    public bool IsSeparator { get; init; }
    public static MenuExtra Separator => new(0, "") { IsSeparator = true };
}

/// <param name="ExtraId">Id of the app item chosen, if any.</param>
/// <param name="InterceptedVerb">Shell verb the caller chose to handle itself (e.g. "rename").</param>
public sealed record ContextMenuResult(int? ExtraId, string? InterceptedVerb);

/// <summary>
/// Shows Explorer's real context menu (with every shell extension: 7-Zip, VS Code, Git, …)
/// for files or for a folder's background. Must run on the UI (STA) thread that owns the
/// window, and the window procedure must forward menu messages to <see cref="HandleWindowMessage"/>
/// so owner-drawn submenus such as "Send to" and "Open with" populate.
/// </summary>
public static class ShellContextMenu
{
    private const uint FirstShellId = 1, LastShellId = 0x6FFF, ExtraBase = 0x7000;

    private static IContextMenu2? _active2;
    private static IContextMenu3? _active3;

    public static bool IsActive => _active2 is not null || _active3 is not null;

    public static bool HandleWindowMessage(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (msg is not (Native.WM_INITMENUPOPUP or Native.WM_DRAWITEM or Native.WM_MEASUREITEM or Native.WM_MENUCHAR))
            return false;
        try
        {
            if (_active3 is not null && _active3.HandleMenuMsg2(msg, wParam, lParam, out result) >= 0)
                return true;
            if (_active2 is not null && _active2.HandleMenuMsg(msg, wParam, lParam) >= 0)
                return true;
        }
        catch { }
        return false;
    }

    public static ContextMenuResult ShowForItems(IntPtr hwnd, IReadOnlyList<string> paths, int screenX, int screenY,
        IReadOnlyList<MenuExtra> extras, Func<string, bool> interceptVerb, bool extendedVerbs)
    {
        if (paths.Count == 0) return new(null, null);

        // The shell can only build one menu for items that share a parent folder.
        string? parentDir = Path.GetDirectoryName(paths[0]);
        var samePaths = paths.Where(p => string.Equals(Path.GetDirectoryName(p), parentDir, StringComparison.OrdinalIgnoreCase)).ToList();

        var pidls = new List<IntPtr>();
        object? parentObj = null, menuObj = null;
        try
        {
            foreach (var p in samePaths)
                if (Native.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0)
                    pidls.Add(pidl);
            if (pidls.Count == 0) return new(null, null);

            var iidFolder = Native.IID_IShellFolder;
            if (Native.SHBindToParent(pidls[0], ref iidFolder, out var parentPtr, out _) != 0) return new(null, null);
            parentObj = Marshal.GetObjectForIUnknown(parentPtr);
            Marshal.Release(parentPtr);
            var parent = (IShellFolder)parentObj;

            var children = pidls.Select(Native.ILFindLastID).ToArray();
            var iidMenu = Native.IID_IContextMenu;
            parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out var menuPtr);
            menuObj = Marshal.GetObjectForIUnknown(menuPtr);
            Marshal.Release(menuPtr);

            uint flags = Native.CMF_EXPLORE | Native.CMF_CANRENAME | (extendedVerbs ? Native.CMF_EXTENDEDVERBS : 0);
            return Run(hwnd, (IContextMenu)menuObj, screenX, screenY, extras, interceptVerb, flags, parentDir);
        }
        catch
        {
            return new(null, null);
        }
        finally
        {
            foreach (var pidl in pidls) Native.ILFree(pidl);
            if (menuObj is not null) Marshal.ReleaseComObject(menuObj);
            if (parentObj is not null) Marshal.ReleaseComObject(parentObj);
        }
    }

    public static ContextMenuResult ShowForFolderBackground(IntPtr hwnd, string folder, int screenX, int screenY,
        IReadOnlyList<MenuExtra> extras, Func<string, bool> interceptVerb, bool extendedVerbs)
    {
        IntPtr pidl = IntPtr.Zero;
        object? folderObj = null, menuObj = null;
        IShellFolder? desktop = null;
        try
        {
            if (Native.SHParseDisplayName(folder, IntPtr.Zero, out pidl, 0, out _) != 0) return new(null, null);
            Native.SHGetDesktopFolder(out desktop);

            IShellFolder target;
            if (Native.ILIsEmpty(pidl))
            {
                target = desktop;
            }
            else
            {
                var iidFolder = Native.IID_IShellFolder;
                desktop.BindToObject(pidl, IntPtr.Zero, ref iidFolder, out var folderPtr);
                folderObj = Marshal.GetObjectForIUnknown(folderPtr);
                Marshal.Release(folderPtr);
                target = (IShellFolder)folderObj;
            }

            var iidMenu = Native.IID_IContextMenu;
            target.CreateViewObject(hwnd, ref iidMenu, out var menuPtr);
            menuObj = Marshal.GetObjectForIUnknown(menuPtr);
            Marshal.Release(menuPtr);

            uint flags = Native.CMF_NORMAL | (extendedVerbs ? Native.CMF_EXTENDEDVERBS : 0);
            return Run(hwnd, (IContextMenu)menuObj, screenX, screenY, extras, interceptVerb, flags, folder);
        }
        catch
        {
            return new(null, null);
        }
        finally
        {
            if (pidl != IntPtr.Zero) Native.ILFree(pidl);
            if (menuObj is not null) Marshal.ReleaseComObject(menuObj);
            if (folderObj is not null) Marshal.ReleaseComObject(folderObj);
            if (desktop is not null) Marshal.ReleaseComObject(desktop);
        }
    }

    private static ContextMenuResult Run(IntPtr hwnd, IContextMenu menu, int x, int y,
        IReadOnlyList<MenuExtra> extras, Func<string, bool> interceptVerb, uint flags, string? directory)
    {
        IntPtr hmenu = Native.CreatePopupMenu();
        try
        {
            uint pos = 0;
            foreach (var extra in extras)
                InsertExtra(hmenu, extra, ref pos);
            if (extras.Count > 0)
                Native.InsertMenu(hmenu, pos++, Native.MF_BYPOSITION | Native.MF_SEPARATOR, UIntPtr.Zero, null);

            menu.QueryContextMenu(hmenu, pos, FirstShellId, LastShellId, flags);

            _active2 = menu as IContextMenu2;
            _active3 = menu as IContextMenu3;
            int cmd;
            try
            {
                cmd = Native.TrackPopupMenuEx(hmenu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, x, y, hwnd, IntPtr.Zero);
            }
            finally
            {
                _active2 = null;
                _active3 = null;
            }

            if (cmd <= 0) return new(null, null);
            if (cmd >= ExtraBase) return new(cmd - (int)ExtraBase, null);

            uint offset = (uint)cmd - FirstShellId;
            string? verb = GetVerb(menu, offset);
            if (verb is not null && interceptVerb(verb))
                return new(null, verb);

            Invoke(menu, offset, hwnd, x, y, directory);
            return new(null, verb);
        }
        finally
        {
            Native.DestroyMenu(hmenu);
        }
    }

    private static void InsertExtra(IntPtr hmenu, MenuExtra extra, ref uint pos)
    {
        if (extra.IsSeparator)
        {
            Native.InsertMenu(hmenu, pos++, Native.MF_BYPOSITION | Native.MF_SEPARATOR, UIntPtr.Zero, null);
            return;
        }
        uint state = (extra.Checked ? Native.MF_CHECKED : 0) | (extra.Enabled ? 0 : Native.MF_GRAYED);
        if (extra.Children is { Count: > 0 })
        {
            IntPtr sub = Native.CreatePopupMenu();
            uint subPos = 0;
            foreach (var child in extra.Children) InsertExtra(sub, child, ref subPos);
            Native.InsertMenu(hmenu, pos++, Native.MF_BYPOSITION | Native.MF_POPUP | state, (UIntPtr)(ulong)sub, extra.Text);
        }
        else
        {
            Native.InsertMenu(hmenu, pos++, Native.MF_BYPOSITION | Native.MF_STRING | state, (UIntPtr)(ExtraBase + (uint)extra.Id), extra.Text);
        }
    }

    private static string? GetVerb(IContextMenu menu, uint offset)
    {
        const int Chars = 256;
        IntPtr buffer = Marshal.AllocHGlobal(Chars * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            int hr = menu.GetCommandString((UIntPtr)offset, Native.GCS_VERBW, IntPtr.Zero, buffer, Chars);
            if (hr < 0) return null;
            var verb = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(verb) ? null : verb;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void Invoke(IContextMenu menu, uint offset, IntPtr hwnd, int x, int y, string? directory)
    {
        IntPtr dir = directory is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(directory);
        IntPtr dirAnsi = directory is null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(directory);
        try
        {
            uint mask = Native.CMIC_MASK_UNICODE | Native.CMIC_MASK_PTINVOKE;
            if ((GetKeyState(0x10) & 0x8000) != 0) mask |= Native.CMIC_MASK_SHIFT_DOWN;
            if ((GetKeyState(0x11) & 0x8000) != 0) mask |= Native.CMIC_MASK_CONTROL_DOWN;
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = mask,
                hwnd = hwnd,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectory = dirAnsi,
                lpDirectoryW = dir,
                nShow = 1,
                ptInvoke = new POINT { x = x, y = y },
            };
            menu.InvokeCommand(ref info);
        }
        catch { }
        finally
        {
            if (dir != IntPtr.Zero) Marshal.FreeHGlobal(dir);
            if (dirAnsi != IntPtr.Zero) Marshal.FreeHGlobal(dirAnsi);
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
