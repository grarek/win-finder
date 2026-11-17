namespace WinFinder.Core.Navigation;

/// <summary>Result of selecting an item in column view.</summary>
/// <param name="TruncateFrom">Columns at this index and beyond are removed.</param>
/// <param name="AddedFolder">Folder appended as a new column, if a folder was selected.</param>
/// <param name="PreviewFile">File shown in the preview column, if a file was selected.</param>
public readonly record struct ColumnChange(int TruncateFrom, string? AddedFolder, string? PreviewFile);

/// <summary>
/// Pure model of Finder's column (Miller) view: an ordered chain of folders where
/// column <c>i</c> lists <see cref="Folders"/>[i]. Selecting an item in a column
/// truncates every column to its right and then opens the selection.
/// </summary>
public sealed class ColumnChain
{
    private readonly List<string> _folders = new();

    public ColumnChain(string root)
    {
        _folders.Add(PathUtil.Normalize(root));
    }

    public IReadOnlyList<string> Folders => _folders;
    public string Root => _folders[0];
    public string? PreviewFile { get; private set; }

    /// <summary>Deepest open folder (the last column).</summary>
    public string Deepest => _folders[^1];

    /// <summary>Builds a chain from <paramref name="root"/> down to <paramref name="target"/> (a folder under root).</summary>
    public static ColumnChain FromPath(string root, string target)
    {
        var chain = new ColumnChain(root);
        if (!PathUtil.IsSameOrUnder(target, root)) return chain;
        var segments = PathUtil.Segments(target);
        int start = segments.FindIndex(s => string.Equals(s, chain.Root, StringComparison.OrdinalIgnoreCase));
        for (int i = start + 1; i < segments.Count; i++)
            chain._folders.Add(segments[i]);
        return chain;
    }

    public ColumnChange Select(int columnIndex, string path, bool isDirectory)
    {
        if (columnIndex < 0 || columnIndex >= _folders.Count)
            throw new ArgumentOutOfRangeException(nameof(columnIndex));

        int truncateFrom = columnIndex + 1;
        if (_folders.Count > truncateFrom)
            _folders.RemoveRange(truncateFrom, _folders.Count - truncateFrom);

        PreviewFile = null;
        if (isDirectory)
        {
            string folder = PathUtil.Normalize(path);
            _folders.Add(folder);
            return new ColumnChange(truncateFrom, folder, null);
        }

        PreviewFile = path;
        return new ColumnChange(truncateFrom, null, path);
    }

    /// <summary>Clears any selection in <paramref name="columnIndex"/> (e.g. clicking empty space).</summary>
    public ColumnChange ClearSelection(int columnIndex)
    {
        int truncateFrom = columnIndex + 1;
        if (_folders.Count > truncateFrom)
            _folders.RemoveRange(truncateFrom, _folders.Count - truncateFrom);
        PreviewFile = null;
        return new ColumnChange(truncateFrom, null, null);
    }

    public int IndexOf(string folder) =>
        _folders.FindIndex(f => string.Equals(f, PathUtil.Normalize(folder), StringComparison.OrdinalIgnoreCase));
}
