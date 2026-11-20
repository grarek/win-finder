using System.Diagnostics;
using System.IO.Compression;
using WinFinder.Core.Navigation;

namespace WinFinder.Core.Shell;

public enum ArchiveFormat { Zip, SevenZip, Tar }

/// <summary>Explorer's "Compress to" (ZIP, 7z, TAR): archives files and folders into a new file beside them.</summary>
public static class Compressor
{
    public static string Extension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => ".7z",
        ArchiveFormat.Tar => ".tar",
        _ => ".zip",
    };

    /// <summary>7z and TAR use Windows' own tar.exe (libarchive), as Explorer does; it ships with Windows 10 1803 and later.</summary>
    public static string? TarExe
    {
        get
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            return File.Exists(path) ? path : null;
        }
    }

    public static bool IsAvailable(ArchiveFormat format) => format == ArchiveFormat.Zip || TarExe is not null;

    /// <summary>
    /// The archive name Explorer picks: the first item's name (without a file's extension) plus the
    /// format's extension, numbered when that name is taken, e.g. "report (2).zip".
    /// </summary>
    public static string ArchiveNameFor(IReadOnlyList<string> sources, string folder, ArchiveFormat format)
    {
        string first = sources[0].TrimEnd('\\');
        string stem = Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first);
        if (string.IsNullOrEmpty(stem)) stem = "Archive";
        return PathUtil.UniqueChildName(folder, stem + Extension(format), isFolder: false);
    }

    /// <summary>
    /// Archives <paramref name="sources"/> (which share a parent folder) into a new file in that folder and
    /// returns its path. A partly written archive is deleted if anything fails.
    /// </summary>
    public static Task<string> CompressAsync(IReadOnlyList<string> sources, ArchiveFormat format, CancellationToken cancel = default) => Task.Run(async () =>
    {
        if (sources.Count == 0) throw new ArgumentException("Nothing to compress.", nameof(sources));
        string folder = Path.GetDirectoryName(sources[0].TrimEnd('\\')) ?? throw new InvalidOperationException("Can't compress a drive.");
        string archive = Path.Combine(folder, ArchiveNameFor(sources, folder, format));
        try
        {
            if (format == ArchiveFormat.Zip) WriteZip(sources, archive, cancel);
            else await RunTarAsync(sources, folder, archive, cancel);
            return archive;
        }
        catch
        {
            try { File.Delete(archive); } catch { }
            throw;
        }
    }, cancel);

    private static void WriteZip(IReadOnlyList<string> sources, string zipPath, CancellationToken cancel)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var source in sources)
        {
            cancel.ThrowIfCancellationRequested();
            string path = source.TrimEnd('\\');
            string name = Path.GetFileName(path);
            if (Directory.Exists(path)) AddFolder(zip, path, name, cancel);
            else zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
        }
    }

    private static void AddFolder(ZipArchive zip, string folder, string entryPrefix, CancellationToken cancel)
    {
        bool empty = true;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            cancel.ThrowIfCancellationRequested();
            zip.CreateEntryFromFile(file, entryPrefix + "/" + Path.GetFileName(file), CompressionLevel.Optimal);
            empty = false;
        }
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            // Don't follow junctions/symlinks (e.g. "Application Data"), like Explorer.
            if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            AddFolder(zip, sub, entryPrefix + "/" + Path.GetFileName(sub), cancel);
            empty = false;
        }
        if (empty) zip.CreateEntry(entryPrefix + "/"); // keep empty folders
    }

    private static async Task RunTarAsync(IReadOnlyList<string> sources, string folder, string archive, CancellationToken cancel)
    {
        string tar = TarExe ?? throw new InvalidOperationException("Windows' tar.exe wasn't found.");
        var psi = new ProcessStartInfo(tar)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        // -a picks the format from the extension (.7z or .tar); -C makes entry names relative.
        foreach (var arg in new[] { "-a", "-cf", archive, "-C", folder }) psi.ArgumentList.Add(arg);
        foreach (var source in sources)
        {
            string name = Path.GetFileName(source.TrimEnd('\\'));
            psi.ArgumentList.Add(name.StartsWith('-') ? "./" + name : name);
        }
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start tar.exe.");
        var stderr = process.StandardError.ReadToEndAsync(cancel);
        try { await process.WaitForExitAsync(cancel); }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new IOException((await stderr).Trim() is { Length: > 0 } message ? message : $"tar.exe failed ({process.ExitCode}).");
    }
}
