using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinFinder.Core.FileSystem;

namespace WinFinder.Core.Shell;

public static class ShellInfo
{
    private static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Explorer's "Type" text ("Text Document", "File folder"), cached per extension.</summary>
    public static string GetKind(FileEntry entry)
    {
        if (entry.IsDrive) return "Drive";
        if (entry.IsDirectory) return GetTypeName("", true);
        return GetTypeName(entry.Extension, false);
    }

    public static string GetTypeName(string extension, bool isDirectory)
    {
        string key = isDirectory ? "<dir>" : extension;
        return TypeNames.GetOrAdd(key, _ =>
        {
            var info = new SHFILEINFO();
            string probe = isDirectory ? "folder" : "file" + extension;
            uint attrs = isDirectory ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
            Native.SHGetFileInfo(probe, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                Native.SHGFI_TYPENAME | Native.SHGFI_USEFILEATTRIBUTES);
            if (!string.IsNullOrEmpty(info.szTypeName)) return info.szTypeName;
            return isDirectory ? "File folder" :
                string.IsNullOrEmpty(extension) ? "File" : $"{extension.TrimStart('.').ToUpperInvariant()} File";
        });
    }

    /// <summary>Opens a file with its default handler (like double-clicking in Explorer).</summary>
    public static bool Open(string path, IntPtr hwnd = default) => Execute(path, null, hwnd);

    /// <summary>Shows the "Open with" chooser.</summary>
    public static bool OpenWith(string path, IntPtr hwnd = default) => Execute(path, "openas", hwnd);

    public static bool ShowProperties(string path, IntPtr hwnd = default) =>
        Native.SHObjectProperties(hwnd, 0x2 /* SHOP_FILEPATH */, path, null);

    private static bool Execute(string path, string? verb, IntPtr hwnd)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = Native.SEE_MASK_INVOKEIDLIST,
            hwnd = hwnd,
            lpVerb = verb,
            lpFile = path,
            lpDirectory = Path.GetDirectoryName(path),
            nShow = 1, // SW_SHOWNORMAL
        };
        return Native.ShellExecuteEx(ref info);
    }
}
