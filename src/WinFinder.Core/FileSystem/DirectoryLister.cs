using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WinFinder.Core.FileSystem;

/// <summary>
/// Enumerates a folder off the UI thread and streams the entries back in batches,
/// so huge or slow (network) folders never block the caller.
/// </summary>
public static class DirectoryLister
{
    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> ListAsync(
        string path,
        bool showHidden,
        int batchSize = 400,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<IReadOnlyList<FileEntry>>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var batch = new List<FileEntry>(batchSize);
                foreach (var entry in Enumerate(path, showHidden))
                {
                    ct.ThrowIfCancellationRequested();
                    batch.Add(entry);
                    if (batch.Count >= batchSize)
                    {
                        await channel.Writer.WriteAsync(batch, ct).ConfigureAwait(false);
                        batch = new List<FileEntry>(batchSize);
                    }
                }
                if (batch.Count > 0)
                    await channel.Writer.WriteAsync(batch, ct).ConfigureAwait(false);
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

    /// <summary>Synchronous listing (used by tests and small helpers).</summary>
    public static List<FileEntry> List(string path, bool showHidden) => Enumerate(path, showHidden).ToList();

    public static IEnumerable<FileEntry> Enumerate(string path, bool showHidden)
    {
        var options = new EnumerationOptions
        {
            // Opening the root throws (so the UI can show "access denied"); per-entry
            // metadata comes from the directory listing itself, so nothing else can fail.
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            if (showHidden && IsProtectedOsFile(info.Attributes))
                continue;
            if (!showHidden && IsDotName(info.Name))
                continue;
            yield return FileEntry.FromInfo(info);
        }
    }

    /// <summary>Hidden + System together marks "protected operating system files"; Explorer hides them even with hidden files shown.</summary>
    public static bool IsProtectedOsFile(FileAttributes a) =>
        (a & FileAttributes.Hidden) != 0 && (a & FileAttributes.System) != 0;

    /// <summary>".git", ".vscode" etc. — hidden like Finder does unless hidden files are shown.</summary>
    public static bool IsDotName(string name) => name.Length > 1 && name[0] == '.';

    public static bool HasSubfolders(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            }).Any();
        }
        catch { return false; }
    }
}
