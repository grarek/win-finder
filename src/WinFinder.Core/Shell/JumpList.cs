using System.Runtime.InteropServices;
using System.Text;

namespace WinFinder.Core.Shell;

/// <summary>A folder shown in the taskbar jump list.</summary>
public sealed record JumpListFolder(string Title, string Path);

/// <summary>
/// The taskbar jump list (right-click the taskbar button), built with the shell's
/// ICustomDestinationList, which works for unpackaged apps: a "Favorites" category listing
/// pinned folders (each relaunches WinFinder with the folder as its argument) and a
/// "New window" task. Must run on an STA thread.
/// </summary>
public static class JumpList
{
    public const string FavoritesCategory = "Favorites";

    /// <summary>Where the favorites ended up.</summary>
    public enum Placement { None, FavoritesCategory, Tasks }

    /// <summary>
    /// Replaces the jump list. Favorites go in a "Favorites" section; if Windows blocks custom
    /// sections (it does while "Show recently opened items in Start, Jump Lists, and File Explorer"
    /// is off), they're listed as tasks instead, above "New window". Items the user removed from
    /// the list are respected (not re-added).
    /// </summary>
    public static Placement Update(string exePath, IReadOnlyList<JumpListFolder> favorites, string newWindowTitle = "New window")
    {
        var list = (ICustomDestinationList)new DestinationListClass();
        var iidObjectArray = typeof(IObjectArray).GUID;
        list.BeginList(out _, ref iidObjectArray, out var removedObj);
        try
        {
            var removed = ReadArguments((IObjectArray)removedObj);
            var links = favorites
                .Where(f => !removed.Contains(Quote(f.Path))) // the user chose "Remove from this list"
                .Select(f => CreateLink(exePath, Quote(f.Path), f.Title, f.Path, folderIcon: true))
                .ToList();

            var placement = Placement.None;
            if (links.Count > 0)
            {
                var category = (IObjectCollection)new EnumerableObjectCollectionClass();
                foreach (var link in links) category.AddObject(link);
                try
                {
                    list.AppendCategory(FavoritesCategory, (IObjectArray)category);
                    placement = Placement.FavoritesCategory;
                }
                catch (UnauthorizedAccessException)
                {
                    placement = Placement.Tasks; // custom sections are blocked: fall back to tasks
                }
            }

            var tasks = (IObjectCollection)new EnumerableObjectCollectionClass();
            if (placement == Placement.Tasks)
            {
                foreach (var link in links) tasks.AddObject(link);
                tasks.AddObject(CreateSeparator());
            }
            tasks.AddObject(CreateLink(exePath, "", newWindowTitle, "Open a new WinFinder window", folderIcon: false));
            list.AddUserTasks((IObjectArray)tasks);

            list.CommitList();
            return placement;
        }
        catch
        {
            list.AbortList();
            throw;
        }
        finally
        {
            Marshal.ReleaseComObject(removedObj);
            Marshal.ReleaseComObject(list);
        }
    }

    /// <summary>
    /// Quotes a path as a command-line argument. A trailing backslash (drive roots like C:\) is
    /// doubled, because a single one would escape the closing quote.
    /// </summary>
    public static string Quote(string path) => "\"" + (path.EndsWith('\\') ? path + "\\" : path) + "\"";

    private static HashSet<string> ReadArguments(IObjectArray items)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        items.GetCount(out uint count);
        var iidLink = typeof(IShellLinkW).GUID;
        for (uint i = 0; i < count; i++)
        {
            try
            {
                items.GetAt(i, ref iidLink, out var obj);
                if (obj is IShellLinkW link)
                {
                    var sb = new StringBuilder(1024);
                    link.GetArguments(sb, sb.Capacity);
                    result.Add(sb.ToString());
                }
            }
            catch { }
        }
        return result;
    }

    private static IShellLinkW CreateLink(string exePath, string arguments, string title, string description, bool folderIcon)
    {
        var link = (IShellLinkW)new ShellLinkClass();
        link.SetPath(exePath);
        link.SetArguments(arguments);
        link.SetDescription(description);
        link.SetWorkingDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (folderIcon)
            link.SetIconLocation(Path.Combine(Environment.SystemDirectory, "imageres.dll"), 3); // folder
        else
            link.SetIconLocation(exePath, 0);

        // Jump list items show System.Title, not the link's file name.
        var store = (IPropertyStore)link;
        var key = PKEY_Title;
        var value = new PROPVARIANT { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(title) };
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            PropVariantClear(ref value);
        }
        return link;
    }

    /// <summary>A divider line in the tasks list.</summary>
    private static IShellLinkW CreateSeparator()
    {
        var link = (IShellLinkW)new ShellLinkClass();
        var store = (IPropertyStore)link;
        var key = PKEY_IsDestListSeparator;
        var value = new PROPVARIANT { vt = 11 /* VT_BOOL */, p = new IntPtr(0xFFFF) /* VARIANT_TRUE */ };
        store.SetValue(ref key, ref value);
        store.Commit();
        return link;
    }

    // ----- interop ---------------------------------------------------------------------------

    private static readonly PROPERTYKEY PKEY_Title = new() { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 2 };
    private static readonly PROPERTYKEY PKEY_IsDestListSeparator = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 6 };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p;
        public IntPtr p2;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6")]
    private class DestinationListClass { }

    [ComImport, Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a")]
    private class EnumerableObjectCollectionClass { }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkClass { }

    [ComImport, Guid("6332debf-87b5-4670-90c0-5e57b408a49e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void BeginList(out uint pcMinSlots, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray poa);
        void CommitList();
        void GetRemovedDestinations([In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? pszAppID);
        void AbortList();
    }

    [ComImport, Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("5632b1a4-e38a-400a-928a-d4cd63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        // IObjectArray
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        // IObjectCollection
        void AddObject([MarshalAs(UnmanagedType.Interface)] object punk);
        void AddFromArray(IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue([In] ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue([In] ref PROPERTYKEY key, [In] ref PROPVARIANT pv);
        void Commit();
    }
}
