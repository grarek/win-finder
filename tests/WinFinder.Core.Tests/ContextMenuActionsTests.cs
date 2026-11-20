using System.IO.Compression;
using WinFinder.Core.Shell;

namespace WinFinder.Core.Tests;

/// <summary>The Explorer right-click actions WinFinder implements itself: Compress to, Paste shortcut.</summary>
public sealed class ContextMenuActionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WinFinderMenuTests", Guid.NewGuid().ToString("N"));

    public ContextMenuActionsTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Photos", "Empty"));
        File.WriteAllText(Path.Combine(_dir, "Photos", "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(_dir, "report.txt"), "report");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void ArchiveName_UsesTheFirstItem_WithoutAFilesExtension_AndNumbersWhenTaken()
    {
        Assert.Equal("report.zip", Compressor.ArchiveNameFor([P("report.txt")], _dir, ArchiveFormat.Zip));
        Assert.Equal("Photos.7z", Compressor.ArchiveNameFor([P("Photos"), P("report.txt")], _dir, ArchiveFormat.SevenZip));
        File.WriteAllText(P("report.zip"), "");
        Assert.Equal("report (2).zip", Compressor.ArchiveNameFor([P("report.txt")], _dir, ArchiveFormat.Zip));
    }

    [Fact]
    public async Task CompressToZip_KeepsFolderStructure_AndEmptyFolders()
    {
        string zip = await Compressor.CompressAsync([P("Photos"), P("report.txt")], ArchiveFormat.Zip);

        Assert.Equal(P("Photos.zip"), zip);
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Photos/Empty/", "Photos/a.txt", "report.txt"], names);
    }

    [Theory]
    [InlineData(ArchiveFormat.SevenZip, ".7z")]
    [InlineData(ArchiveFormat.Tar, ".tar")]
    public async Task CompressTo7zAndTar_UseWindowsTar(ArchiveFormat format, string extension)
    {
        if (!Compressor.IsAvailable(format)) return; // tar.exe ships with Windows 10 1803+

        string archive = await Compressor.CompressAsync([P("Photos")], format);

        Assert.Equal(P("Photos" + extension), archive);
        Assert.True(new FileInfo(archive).Length > 0);
    }

    [Fact]
    public async Task FailedCompression_LeavesNoPartialArchive()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Compressor.CompressAsync([P("missing.txt")], ArchiveFormat.Zip));
        Assert.False(File.Exists(P("missing.zip")));
    }

    [Fact]
    public void ShortcutName_FollowsExplorer()
    {
        Assert.Equal("report.txt - Shortcut.lnk", Shortcuts.NameFor(P("report.txt"), _dir));
        Assert.Equal("C - Shortcut.lnk", Shortcuts.NameFor(@"C:\", _dir));
    }

    [Fact]
    public void PasteShortcut_CreatesLinks_NumberedWhenTaken()
    {
        var first = Shortcuts.Create([P("report.txt"), P("Photos")], _dir);
        var second = Shortcuts.Create([P("report.txt")], _dir);

        Assert.Equal([P("report.txt - Shortcut.lnk"), P("Photos - Shortcut.lnk")], first);
        Assert.Equal([P("report.txt - Shortcut (2).lnk")], second);
        Assert.All(first.Concat(second), p => Assert.True(new FileInfo(p).Length > 0));
    }
}
