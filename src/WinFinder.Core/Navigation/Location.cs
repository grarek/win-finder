namespace WinFinder.Core.Navigation;

public enum LocationKind { Folder, Computer, Tag, Search }

/// <summary>Where a tab is pointed: a real folder, "This PC", a tag collection, or search results.</summary>
public sealed record Location(LocationKind Kind, string Path = "", int TagId = 0, string Query = "", bool SearchEverywhere = false)
{
    public static readonly Location Computer = new(LocationKind.Computer);

    public static Location Folder(string path) => new(LocationKind.Folder, PathUtil.Normalize(path));
    public static Location Tag(int tagId) => new(LocationKind.Tag, TagId: tagId);
    public static Location Search(string query, string scopeFolder, bool everywhere) =>
        new(LocationKind.Search, PathUtil.Normalize(scopeFolder), Query: query, SearchEverywhere: everywhere);

    public bool IsFolder => Kind == LocationKind.Folder;

    /// <summary>
    /// In column view, the folder shown in the leftmost column when this location was current.
    /// Lets Back/Forward restore the same columns, with parents still on the left.
    /// Not part of <see cref="SameAs"/> and not persisted.
    /// </summary>
    public string? ColumnRoot { get; init; }

    /// <summary>
    /// Leftmost column for a restored column-view folder: the recorded root if it still exists and
    /// is an ancestor; otherwise (e.g. sessions saved before roots were recorded) the home folder
    /// when the folder is inside it, else the folder's drive — so its parents are always shown.
    /// </summary>
    public static string RestoredColumnRoot(string folder, string? recordedRoot, string home)
    {
        folder = PathUtil.Normalize(folder);
        if (recordedRoot is { Length: > 0 } && PathUtil.IsSameOrUnder(folder, recordedRoot) && Directory.Exists(recordedRoot))
            return PathUtil.Normalize(recordedRoot);
        if (!string.IsNullOrEmpty(home) && PathUtil.IsSameOrUnder(folder, home))
            return PathUtil.Normalize(home);
        return System.IO.Path.GetPathRoot(folder) is { Length: > 0 } drive ? drive : folder;
    }

    /// <summary>The leftmost column to use for this location: its recorded root, if still an ancestor; otherwise itself.</summary>
    public string EffectiveColumnRoot =>
        ColumnRoot is { } root && PathUtil.IsSameOrUnder(Path, root) ? root : Path;

    /// <summary>Compact string used for session persistence. Searches persist as their scope folder.</summary>
    public string Serialize() => Kind switch
    {
        LocationKind.Folder => Path,
        LocationKind.Computer => "computer:",
        LocationKind.Tag => $"tag:{TagId}",
        LocationKind.Search => string.IsNullOrEmpty(Path) ? "computer:" : Path,
        _ => "computer:",
    };

    public static Location Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "computer:") return Computer;
        if (value.StartsWith("tag:", StringComparison.Ordinal) && int.TryParse(value.AsSpan(4), out int id))
            return Tag(id);
        return Folder(value);
    }

    public bool SameAs(Location? other) =>
        other is not null && Kind == other.Kind && TagId == other.TagId &&
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
        Query == other.Query && SearchEverywhere == other.SearchEverywhere;
}

public static class PathUtil
{
    /// <summary>Full path without a trailing separator (except for roots like "C:\").</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch { full = path; }
        string root = System.IO.Path.GetPathRoot(full) ?? "";
        if (full.Length > root.Length)
            full = full.TrimEnd('\\', '/');
        return full;
    }

    public static bool IsSameOrUnder(string path, string folder)
    {
        path = Normalize(path);
        folder = Normalize(folder);
        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = folder.EndsWith('\\') ? folder : folder + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string? Parent(string path)
    {
        var p = System.IO.Path.GetDirectoryName(Normalize(path));
        return string.IsNullOrEmpty(p) ? null : p;
    }

    /// <summary>Ancestors from the root down to (and including) <paramref name="path"/>.</summary>
    public static List<string> Segments(string path)
    {
        var list = new List<string>();
        for (string? p = Normalize(path); !string.IsNullOrEmpty(p); p = Parent(p))
            list.Add(p);
        list.Reverse();
        return list;
    }

    /// <summary>Expands "~", environment variables and quotes typed into "Go to Folder".</summary>
    public static string ExpandUserInput(string input)
    {
        string s = input.Trim().Trim('"');
        if (s == "~") s = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (s.StartsWith("~\\") || s.StartsWith("~/"))
            s = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), s[2..]);
        s = Environment.ExpandEnvironmentVariables(s);
        if (s.Length == 2 && s[1] == ':') s += "\\";
        return s;
    }

    public static string UniqueChildName(string folder, string baseName, bool isFolder)
    {
        string ext = isFolder ? "" : System.IO.Path.GetExtension(baseName);
        string stem = isFolder ? baseName : System.IO.Path.GetFileNameWithoutExtension(baseName);
        string candidate = baseName;
        for (int i = 2; Exists(System.IO.Path.Combine(folder, candidate)); i++)
            candidate = $"{stem} ({i}){ext}";
        return candidate;

        static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
    }
}
