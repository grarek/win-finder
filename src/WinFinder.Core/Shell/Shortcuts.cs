using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using WinFinder.Core.Navigation;

namespace WinFinder.Core.Shell;

/// <summary>Creates .lnk shortcuts, as Explorer's "Paste shortcut" does.</summary>
public static class Shortcuts
{
    /// <summary>Explorer's name for a shortcut: "notes.txt - Shortcut.lnk", numbered when taken.</summary>
    public static string NameFor(string target, string folder)
    {
        string name = Path.GetFileName(target.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = target.TrimEnd('\\', ':'); // a drive: "C - Shortcut.lnk"
        return PathUtil.UniqueChildName(folder, name + " - Shortcut.lnk", isFolder: false);
    }

    /// <summary>Creates a shortcut in <paramref name="folder"/> for each target and returns their paths.</summary>
    public static IReadOnlyList<string> Create(IEnumerable<string> targets, string folder)
    {
        var created = new List<string>();
        foreach (var target in targets)
        {
            object? link = null;
            try
            {
                link = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellLink)!)!;
                ((IShellLinkW)link).SetPath(target);
                string? workDir = Path.GetDirectoryName(target.TrimEnd('\\'));
                if (workDir is not null && File.Exists(target)) ((IShellLinkW)link).SetWorkingDirectory(workDir);
                string path = Path.Combine(folder, NameFor(target, folder));
                ((IPersistFile)link).Save(path, true);
                created.Add(path);
            }
            catch { }
            finally
            {
                if (link is not null) Marshal.ReleaseComObject(link);
            }
        }
        return created;
    }

    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
