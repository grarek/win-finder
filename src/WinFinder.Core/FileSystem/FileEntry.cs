namespace WinFinder.Core.FileSystem;

/// <summary>Immutable snapshot of one file-system item.</summary>
public sealed record FileEntry(
    string FullPath,
    string Name,
    bool IsDirectory,
    long Size,
    DateTime Modified,
    DateTime Created,
    FileAttributes Attributes)
{
    /// <summary>True for drive roots listed under "This PC".</summary>
    public bool IsDrive { get; init; }

    public string Extension => IsDirectory ? "" : System.IO.Path.GetExtension(Name);
    /// <summary>Hidden attribute, or a Unix-style dot-name (Finder hides those too).</summary>
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0 || (Name.Length > 1 && Name[0] == '.' && !IsDrive);
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    public static FileEntry FromInfo(FileSystemInfo info)
    {
        bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
        long size = isDir ? -1 : SafeLength(info);
        return new FileEntry(
            info.FullName,
            info.Name,
            isDir,
            size,
            SafeTime(() => info.LastWriteTime),
            SafeTime(() => info.CreationTime),
            info.Attributes);
    }

    public static FileEntry? TryFromPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                if (di.Parent is null)
                    return FromDrive(path);
                return FromInfo(di);
            }
            if (File.Exists(path))
                return FromInfo(new FileInfo(path));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    public static FileEntry FromDrive(string root, string? label = null)
    {
        DateTime t = DateTime.MinValue;
        try { t = Directory.GetLastWriteTime(root); } catch { }
        return new FileEntry(root, label ?? root, true, -1, t, t, FileAttributes.Directory) { IsDrive = true };
    }

    private static long SafeLength(FileSystemInfo info)
    {
        try { return ((FileInfo)info).Length; } catch { return 0; }
    }

    private static DateTime SafeTime(Func<DateTime> f)
    {
        try { return f(); } catch { return DateTime.MinValue; }
    }
}
