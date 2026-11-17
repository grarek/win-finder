using System.Data.OleDb;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WinFinder.Core.FileSystem;

namespace WinFinder.Core.Search;

/// <summary>
/// Filename search. Uses the Windows Search index when available (fast, whole PC),
/// and falls back to a recursive scan of the scope folder when the index has nothing.
/// </summary>
public static class SearchService
{
    public const int MaxResults = 5000;

    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> SearchAsync(
        string query,
        string? scopeFolder,
        bool showHidden,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0) yield break;

        var channel = Channel.CreateBounded<IReadOnlyList<FileEntry>>(8);
        _ = Task.Run(async () =>
        {
            try
            {
                int found = 0;
                try
                {
                    foreach (var batch in Batch(QueryIndex(query, scopeFolder, ct), 200))
                    {
                        found += batch.Count;
                        await channel.Writer.WriteAsync(batch, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    found = 0; // index unavailable; fall through to the scan
                }

                if (found == 0 && scopeFolder is not null)
                {
                    foreach (var batch in Batch(Scan(query, scopeFolder, showHidden, ct), 100))
                        await channel.Writer.WriteAsync(batch, ct).ConfigureAwait(false);
                }
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex is OperationCanceledException ? null : ex);
            }
        }, ct);

        await foreach (var batch in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return batch;
    }

    internal static string BuildIndexQuery(string query, string? scopeFolder)
    {
        string like = EscapeLike(query).Replace("'", "''");
        string scope = scopeFolder is null
            ? "file:"
            : "file:" + scopeFolder.Replace('\\', '/').Replace("'", "''");
        return $"SELECT TOP {MaxResults} System.ItemPathDisplay FROM SystemIndex " +
               $"WHERE SCOPE='{scope}' AND System.FileName LIKE '%{like}%'";
    }

    internal static string EscapeLike(string s) =>
        s.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    private static IEnumerable<FileEntry> QueryIndex(string query, string? scopeFolder, CancellationToken ct)
    {
        using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        conn.Open();
        using var cmd = new OleDbCommand(BuildIndexQuery(query, scopeFolder), conn);
        using var reader = cmd.ExecuteReader();
        while (reader is not null && reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.IsDBNull(0)) continue;
            var entry = FileEntry.TryFromPath(reader.GetString(0));
            if (entry is not null) yield return entry;
        }
    }

    private static IEnumerable<FileEntry> Scan(string query, string folder, bool showHidden, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? FileAttributes.ReparsePoint : FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple,
        };
        int count = 0;
        IEnumerable<FileSystemInfo> source;
        try { source = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options); }
        catch { yield break; }

        foreach (var info in source)
        {
            ct.ThrowIfCancellationRequested();
            if (!info.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
            yield return FileEntry.FromInfo(info);
            if (++count >= MaxResults) yield break;
        }
    }

    private static IEnumerable<List<FileEntry>> Batch(IEnumerable<FileEntry> source, int size)
    {
        var batch = new List<FileEntry>(size);
        foreach (var e in source)
        {
            batch.Add(e);
            if (batch.Count >= size)
            {
                yield return batch;
                batch = new List<FileEntry>(size);
            }
        }
        if (batch.Count > 0) yield return batch;
    }
}
