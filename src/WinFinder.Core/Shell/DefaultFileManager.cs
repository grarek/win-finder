using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinFinder.Core.Shell;

/// <summary>
/// Makes WinFinder the per-user handler for opening folders, the way third-party file managers
/// do it: no admin rights, only keys under HKCU\Software\Classes, all removed again on
/// <see cref="Unregister"/>.
/// <list type="bullet">
/// <item>Folders and drives get an "Open in WinFinder" verb that becomes their default, so
/// double-clicking a folder on the desktop, typing a path in Run, or an app opening a folder
/// starts WinFinder.</item>
/// <item>Win+E runs "open new window" on This PC's CLSID; that command is pointed at WinFinder.</item>
/// </list>
/// </summary>
public sealed class DefaultFileManager
{
    public const string VerbName = "WinFinder";

    /// <summary>The CLSID whose "opennewwindow" verb Win+E runs (This PC / Home).</summary>
    public const string WinEClsid = "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";

    private const string PreviousDefaultValue = "WinFinderPreviousDefault";
    private static readonly string[] FolderClasses = ["Directory", "Drive"];
    private static string WinECommandKey => $@"CLSID\{WinEClsid}\shell\opennewwindow\command";

    private readonly RegistryKey _classes;

    /// <param name="classesRoot">HKCU\Software\Classes, or a scratch key in tests.</param>
    public DefaultFileManager(RegistryKey classesRoot) => _classes = classesRoot;

    public static DefaultFileManager ForCurrentUser() =>
        new(Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true));

    public static string FolderCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>True when folders open in <paramref name="exePath"/>.</summary>
    public bool IsRegistered(string exePath) =>
        RegisteredExecutable() is { } exe && string.Equals(exe, exePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The WinFinder.exe that folders currently open in, or null if WinFinder isn't the default.</summary>
    public string? RegisteredExecutable()
    {
        using var shell = _classes.OpenSubKey(@"Directory\shell");
        if (shell?.GetValue(null) as string != VerbName) return null;
        using var command = shell.OpenSubKey($@"{VerbName}\command");
        return command?.GetValue(null) is string line ? CommandLine.Split(line).FirstOrDefault() : null;
    }

    public void Register(string exePath)
    {
        foreach (var cls in FolderClasses)
        {
            using var shell = _classes.CreateSubKey($@"{cls}\shell", writable: true);
            using var verb = shell.CreateSubKey(VerbName, writable: true);
            // Keep any default verb another app set, to put it back on Unregister.
            if (shell.GetValue(null) is string { Length: > 0 } previous && previous != VerbName)
                verb.SetValue(PreviousDefaultValue, previous);
            verb.SetValue("MUIVerb", "Open in WinFinder");
            verb.SetValue("Icon", $"\"{exePath}\",0");
            using (var command = verb.CreateSubKey("command", writable: true))
                command.SetValue(null, FolderCommand(exePath));
            shell.SetValue(null, VerbName);
        }

        using (var winE = _classes.CreateSubKey(WinECommandKey, writable: true))
        {
            winE.SetValue(null, $"\"{exePath}\"");
            winE.SetValue("DelegateExecute", ""); // an empty DelegateExecute makes Explorer run the command
        }
        NotifyAssociationsChanged();
    }

    /// <summary>Puts folder opening back to File Explorer. Safe to call when not registered.</summary>
    public void Unregister()
    {
        foreach (var cls in FolderClasses)
        {
            using var shell = _classes.OpenSubKey($@"{cls}\shell", writable: true);
            if (shell is null) continue;
            string? previous;
            using (var verb = shell.OpenSubKey(VerbName))
                previous = verb?.GetValue(PreviousDefaultValue) as string;
            if (shell.GetValue(null) as string == VerbName)
            {
                if (previous is { Length: > 0 }) shell.SetValue(null, previous);
                else shell.DeleteValue("", throwOnMissingValue: false);
            }
            shell.DeleteSubKeyTree(VerbName, throwOnMissingSubKey: false);
        }

        // Only remove the Win+E override if it is one of ours.
        bool ours;
        using (var winE = _classes.OpenSubKey(WinECommandKey))
            ours = winE?.GetValue(null) is string line &&
                   CommandLine.Split(line).FirstOrDefault() is { } exe &&
                   Path.GetFileName(exe).Equals("WinFinder.exe", StringComparison.OrdinalIgnoreCase);
        if (ours)
        {
            _classes.DeleteSubKeyTree($@"CLSID\{WinEClsid}\shell\opennewwindow", throwOnMissingSubKey: false);
            DeleteIfEmpty($@"CLSID\{WinEClsid}\shell");
            DeleteIfEmpty($@"CLSID\{WinEClsid}");
        }
        NotifyAssociationsChanged();
    }

    private void DeleteIfEmpty(string path)
    {
        using (var key = _classes.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        }
        _classes.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>Tells Explorer to re-read file associations (SHCNE_ASSOCCHANGED).</summary>
    private static void NotifyAssociationsChanged()
    {
        try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
    }
}
