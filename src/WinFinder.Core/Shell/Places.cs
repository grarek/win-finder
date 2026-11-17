using System.Runtime.InteropServices;

namespace WinFinder.Core.Shell;

public sealed record DriveDescriptor(string Root, string Label, DriveType Type, long TotalBytes, long FreeBytes, bool IsReady)
{
    public double UsedFraction => TotalBytes > 0 ? 1.0 - (double)FreeBytes / TotalBytes : 0;
}

public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public static string Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public static string Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    public static string Downloads
    {
        get
        {
            var id = DownloadsId;
            if (Native.SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try { return Marshal.PtrToStringUni(ptr) ?? Fallback(); }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
            return Fallback();

            static string Fallback() => Path.Combine(Home, "Downloads");
        }
    }

    /// <summary>The default sidebar Favorites for a fresh install.</summary>
    public static IReadOnlyList<string> DefaultFavorites() =>
        new[] { Home, Desktop, Documents, Downloads, Pictures }
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public static class Drives
{
    /// <summary>Enumerates drives; slow (network/optical) drive queries are tolerated.</summary>
    public static IReadOnlyList<DriveDescriptor> List()
    {
        var result = new List<DriveDescriptor>();
        foreach (var d in DriveInfo.GetDrives())
        {
            string root = d.Name;
            bool ready = false;
            string label = "";
            long total = 0, free = 0;
            try
            {
                ready = d.IsReady;
                if (ready)
                {
                    label = d.VolumeLabel;
                    total = d.TotalSize;
                    free = d.AvailableFreeSpace;
                }
            }
            catch { ready = false; }

            if (string.IsNullOrWhiteSpace(label))
                label = d.DriveType switch
                {
                    DriveType.Fixed => "Local Disk",
                    DriveType.Removable => "USB Drive",
                    DriveType.Network => "Network Drive",
                    DriveType.CDRom => "DVD Drive",
                    _ => "Drive",
                };
            string letter = root.TrimEnd('\\');
            result.Add(new DriveDescriptor(root, $"{label} ({letter})", d.DriveType, total, free, ready));
        }
        return result;
    }

    public static long FreeSpace(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return -1;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return -1; }
    }
}
