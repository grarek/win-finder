using Microsoft.Data.Sqlite;
using WinFinder.Core.Navigation;

namespace WinFinder.Core.Tags;

public sealed record TagDefinition(int Id, string Name, string Color, int SortOrder);

/// <summary>
/// Finder-style colour tags stored in SQLite. Each tagged file is keyed by path and by its
/// NTFS file id, so tags survive renames/moves (the id lets us find the file again).
/// Alternate data streams were avoided because they are lost when copying to FAT/USB/cloud.
/// </summary>
public sealed class TagStore : IDisposable
{
    public static readonly (string Name, string Color)[] DefaultTags =
    [
        ("Red", "#FF3B30"), ("Orange", "#FF9500"), ("Yellow", "#FFCC00"), ("Green", "#34C759"),
        ("Blue", "#007AFF"), ("Purple", "#AF52DE"), ("Gray", "#8E8E93"),
    ];

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public event Action? Changed;

    public TagStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS tags(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                color TEXT NOT NULL,
                sort INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS file_tags(
                path TEXT NOT NULL COLLATE NOCASE,
                parent TEXT NOT NULL COLLATE NOCASE,
                volume INTEGER,
                file_id INTEGER,
                tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY(path, tag_id));
            CREATE INDEX IF NOT EXISTS ix_file_tags_parent ON file_tags(parent);
            CREATE INDEX IF NOT EXISTS ix_file_tags_tag ON file_tags(tag_id);
            CREATE INDEX IF NOT EXISTS ix_file_tags_id ON file_tags(volume, file_id);
            """);
        Exec("PRAGMA foreign_keys=ON;");

        if (Scalar<long>("SELECT COUNT(*) FROM tags") == 0)
        {
            int sort = 0;
            foreach (var (name, color) in DefaultTags)
                Exec("INSERT INTO tags(name, color, sort) VALUES($n, $c, $s)", ("$n", name), ("$c", color), ("$s", sort++));
        }
    }

    public IReadOnlyList<TagDefinition> GetTags()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, name, color, sort FROM tags ORDER BY sort, id";
            using var r = cmd.ExecuteReader();
            var list = new List<TagDefinition>();
            while (r.Read()) list.Add(new TagDefinition(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
            return list;
        }
    }

    public TagDefinition AddTag(string name, string color)
    {
        int id;
        lock (_gate)
        {
            long sort = Scalar<long>("SELECT COALESCE(MAX(sort), -1) + 1 FROM tags");
            Exec("INSERT INTO tags(name, color, sort) VALUES($n, $c, $s)", ("$n", name), ("$c", color), ("$s", sort));
            id = (int)Scalar<long>("SELECT last_insert_rowid()");
        }
        Changed?.Invoke();
        return GetTags().First(t => t.Id == id);
    }

    public void RenameTag(int id, string name)
    {
        lock (_gate) Exec("UPDATE tags SET name=$n WHERE id=$id", ("$n", name), ("$id", id));
        Changed?.Invoke();
    }

    public void DeleteTag(int id)
    {
        lock (_gate)
        {
            Exec("DELETE FROM file_tags WHERE tag_id=$id", ("$id", id));
            Exec("DELETE FROM tags WHERE id=$id", ("$id", id));
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<int> GetTagIds(string path)
    {
        path = PathUtil.Normalize(path);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT tag_id FROM file_tags WHERE path=$p";
            cmd.Parameters.AddWithValue("$p", path);
            using var r = cmd.ExecuteReader();
            var list = new List<int>();
            while (r.Read()) list.Add(r.GetInt32(0));
            return list;
        }
    }

    /// <summary>Tags of every direct child of <paramref name="folder"/>, in one query (used while listing).</summary>
    public IReadOnlyDictionary<string, List<int>> GetTagIdsInFolder(string folder)
    {
        folder = PathUtil.Normalize(folder);
        var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path, tag_id FROM file_tags WHERE parent=$f";
            cmd.Parameters.AddWithValue("$f", folder);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string p = r.GetString(0);
                if (!result.TryGetValue(p, out var ids)) result[p] = ids = new List<int>();
                ids.Add(r.GetInt32(1));
            }
        }
        return result;
    }

    public void SetTags(string path, IEnumerable<int> tagIds)
    {
        path = PathUtil.Normalize(path);
        var ids = tagIds.Distinct().ToList();
        var fileId = ids.Count > 0 ? FileIdentity.TryGet(path) : null;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            Exec("DELETE FROM file_tags WHERE path=$p", ("$p", path));
            foreach (int id in ids)
                Exec("INSERT INTO file_tags(path, parent, volume, file_id, tag_id) VALUES($p, $parent, $v, $f, $t)",
                    ("$p", path), ("$parent", PathUtil.Parent(path) ?? ""),
                    ("$v", fileId is { } f1 ? (object)(long)f1.VolumeSerial : DBNull.Value),
                    ("$f", fileId is { } f2 ? (object)unchecked((long)f2.Id) : DBNull.Value),
                    ("$t", id));
            tx.Commit();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Every path carrying <paramref name="tagId"/>. Paths that no longer exist are healed by
    /// looking the file up by its NTFS id (catches renames/moves done outside the app).
    /// </summary>
    public IReadOnlyList<string> GetPathsWithTag(int tagId)
    {
        var rows = new List<(string Path, long? Volume, long? FileId)>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path, volume, file_id FROM file_tags WHERE tag_id=$t";
            cmd.Parameters.AddWithValue("$t", tagId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2)));
        }

        var result = new List<string>();
        foreach (var row in rows)
        {
            if (File.Exists(row.Path) || Directory.Exists(row.Path))
            {
                result.Add(row.Path);
                continue;
            }
            if (row.Volume is { } vol && row.FileId is { } fid &&
                FileIdentity.TryResolve(new FileId((uint)vol, unchecked((ulong)fid))) is { } moved)
            {
                UpdatePath(row.Path, moved, raise: false);
                result.Add(moved);
            }
        }
        return result;
    }

    /// <summary>Re-points tags after a move/rename. For folders, descendants are re-pointed too.</summary>
    public void UpdatePath(string oldPath, string newPath) => UpdatePath(oldPath, newPath, raise: true);

    private void UpdatePath(string oldPath, string newPath, bool raise)
    {
        oldPath = PathUtil.Normalize(oldPath);
        newPath = PathUtil.Normalize(newPath);
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return;
        int changed;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            changed = Exec("UPDATE OR REPLACE file_tags SET path=$n, parent=$np WHERE path=$o",
                ("$n", newPath), ("$np", PathUtil.Parent(newPath) ?? ""), ("$o", oldPath));

            // Descendants of a moved folder: rewrite the prefix.
            string oldPrefix = oldPath.TrimEnd('\\') + "\\";
            string newPrefix = newPath.TrimEnd('\\') + "\\";
            changed += Exec("""
                UPDATE OR REPLACE file_tags
                SET path   = $np || substr(path, length($op) + 1),
                    parent = CASE WHEN parent = $o THEN $n ELSE $np || substr(parent, length($op) + 1) END
                WHERE substr(path, 1, length($op)) = $op COLLATE NOCASE
                """, ("$np", newPrefix), ("$op", oldPrefix), ("$o", oldPath), ("$n", newPath));
            tx.Commit();
        }
        if (raise && changed > 0) Changed?.Invoke();
    }

    /// <summary>Removes rows for files that no longer exist anywhere. Returns the number removed.</summary>
    public int PruneMissing()
    {
        var tags = GetTags();
        int before = Scalar<int>("SELECT COUNT(*) FROM file_tags");
        foreach (var t in tags) GetPathsWithTag(t.Id); // heals moved files first

        var missing = new List<string>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT path FROM file_tags";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string p = r.GetString(0);
                // Only prune on local volumes we can actually see; never on offline network/USB drives.
                string? root = Path.GetPathRoot(p);
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                if (!File.Exists(p) && !Directory.Exists(p)) missing.Add(p);
            }
        }
        lock (_gate)
        {
            foreach (var p in missing) Exec("DELETE FROM file_tags WHERE path=$p", ("$p", p));
        }
        int after = Scalar<int>("SELECT COUNT(*) FROM file_tags");
        if (after != before) Changed?.Invoke();
        return before - after;
    }

    private int Exec(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
        }
    }

    public void Dispose() => _db.Dispose();
}
