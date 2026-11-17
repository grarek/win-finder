using System.Text;
using WinFinder.Core.Shell;

namespace WinFinder.Core.Tags;

/// <summary>NTFS identity of a file: stable across renames and moves within one volume.</summary>
public readonly record struct FileId(uint VolumeSerial, ulong Id);

public static class FileIdentity
{
    public static FileId? TryGet(string path)
    {
        IntPtr h = Native.CreateFile(path, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h == Native.INVALID_HANDLE_VALUE) return null;
        try
        {
            if (!Native.GetFileInformationByHandle(h, out var info)) return null;
            ulong id = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
            return new FileId(info.dwVolumeSerialNumber, id);
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    /// <summary>Finds the current path of a file by its id (works after renames/moves on the same volume).</summary>
    public static string? TryResolve(FileId fileId)
    {
        string? root = FindVolumeRoot(fileId.VolumeSerial);
        if (root is null) return null;

        IntPtr volume = Native.CreateFile(root, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (volume == Native.INVALID_HANDLE_VALUE) return null;
        try
        {
            var descriptor = new FILE_ID_DESCRIPTOR
            {
                dwSize = System.Runtime.InteropServices.Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
                Type = 0,
                FileId = unchecked((long)fileId.Id),
            };
            IntPtr h = Native.OpenFileById(volume, ref descriptor, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL,
                IntPtr.Zero, Native.FILE_FLAG_BACKUP_SEMANTICS);
            if (h == Native.INVALID_HANDLE_VALUE) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint len = Native.GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                if (len == 0 || len >= sb.Capacity) return null;
                return StripPrefix(sb.ToString());
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }
        finally
        {
            Native.CloseHandle(volume);
        }
    }

    internal static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    private static string? FindVolumeRoot(uint serial)
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            if (Native.GetVolumeInformation(d.Name, null, 0, out uint s, out _, out _, null, 0) && s == serial)
                return d.Name;
        }
        return null;
    }
}
