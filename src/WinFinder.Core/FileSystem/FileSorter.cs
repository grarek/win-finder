using System.Runtime.InteropServices;

namespace WinFinder.Core.FileSystem;

public enum SortKey { Name, Modified, Size, Kind }

public static partial class FileSorter
{
    [LibraryImport("shlwapi.dll", EntryPoint = "StrCmpLogicalW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);

    /// <summary>Explorer-style "natural" comparison: file2 sorts before file10.</summary>
    public static int CompareNames(string a, string b) => StrCmpLogicalW(a, b);

    public static IComparer<FileEntry> CreateComparer(
        SortKey key,
        bool descending,
        bool foldersFirst = true,
        Func<FileEntry, string>? kindSelector = null)
    {
        kindSelector ??= DefaultKind;
        return Comparer<FileEntry>.Create((x, y) =>
        {
            if (foldersFirst && x.IsDirectory != y.IsDirectory)
                return x.IsDirectory ? -1 : 1;

            int c = key switch
            {
                SortKey.Modified => x.Modified.CompareTo(y.Modified),
                SortKey.Size => x.Size.CompareTo(y.Size),
                SortKey.Kind => string.Compare(kindSelector(x), kindSelector(y), StringComparison.CurrentCultureIgnoreCase),
                _ => 0,
            };
            if (descending) c = -c;
            if (c != 0) return c;

            int n = CompareNames(x.Name, y.Name);
            return key == SortKey.Name && descending ? -n : n;
        });
    }

    public static void Sort(List<FileEntry> entries, SortKey key, bool descending, bool foldersFirst = true, Func<FileEntry, string>? kindSelector = null)
        => entries.Sort(CreateComparer(key, descending, foldersFirst, kindSelector));

    private static string DefaultKind(FileEntry e) => e.IsDirectory ? "" : e.Extension.ToLowerInvariant();
}
