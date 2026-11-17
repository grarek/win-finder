using WinFinder.Core.FileSystem;
using WinFinder.Core.Input;
using WinFinder.Core.Search;
using WinFinder.Core.Shell;

namespace WinFinder.Core.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir() => Path = Directory.CreateTempSubdirectory("winfinder-tests").FullName;
    public string Path { get; }
    public string File(string name, string content = "x")
    {
        string p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }
    public string Dir(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;
    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
        catch { }
    }
}

public class FileSorterTests
{
    private static FileEntry F(string name, long size = 0, int day = 1) =>
        new(@"C:\x\" + name, name, false, size, new DateTime(2026, 1, day), new DateTime(2026, 1, 1), FileAttributes.Normal);

    private static FileEntry D(string name) =>
        new(@"C:\x\" + name, name, true, -1, new DateTime(2026, 1, 1), new DateTime(2026, 1, 1), FileAttributes.Directory);

    [Fact]
    public void Names_SortNaturally_WithFoldersFirst()
    {
        var list = new List<FileEntry> { F("file10.txt"), F("file2.txt"), D("zeta"), F("File1.txt"), D("alpha") };
        FileSorter.Sort(list, SortKey.Name, descending: false);
        Assert.Equal(["alpha", "zeta", "File1.txt", "file2.txt", "file10.txt"], list.Select(e => e.Name));
    }

    [Fact]
    public void Descending_KeepsFoldersFirst()
    {
        var list = new List<FileEntry> { F("a"), D("dir"), F("b") };
        FileSorter.Sort(list, SortKey.Name, descending: true);
        Assert.Equal(["dir", "b", "a"], list.Select(e => e.Name));
    }

    [Fact]
    public void Size_TiesBreakByName()
    {
        var list = new List<FileEntry> { F("b", 10), F("a", 10), F("c", 99) };
        FileSorter.Sort(list, SortKey.Size, descending: true);
        Assert.Equal(["c", "a", "b"], list.Select(e => e.Name));
    }

    [Fact]
    public void Mixed_WhenFoldersFirstDisabled()
    {
        var list = new List<FileEntry> { F("b"), D("c"), F("a") };
        FileSorter.Sort(list, SortKey.Name, false, foldersFirst: false);
        Assert.Equal(["a", "b", "c"], list.Select(e => e.Name));
    }
}

public class DirectoryListerTests
{
    [Fact]
    public void HidesHiddenAndDotFiles_UnlessShowHidden()
    {
        using var t = new TempDir();
        t.File("visible.txt");
        t.File(".dotfile");
        t.Dir(".git");
        var hidden = t.File("hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var protectedOs = t.File("desktop.ini");
        File.SetAttributes(protectedOs, FileAttributes.Hidden | FileAttributes.System);

        var normal = DirectoryLister.List(t.Path, showHidden: false).Select(e => e.Name).ToHashSet();
        Assert.Equal(["visible.txt"], normal);

        var all = DirectoryLister.List(t.Path, showHidden: true).Select(e => e.Name).ToHashSet();
        Assert.Contains(".dotfile", all);
        Assert.Contains(".git", all);
        Assert.Contains("hidden.txt", all);
        Assert.DoesNotContain("desktop.ini", all); // protected OS files stay hidden, like Explorer
    }

    [Fact]
    public async Task ListAsync_StreamsBatches()
    {
        using var t = new TempDir();
        for (int i = 0; i < 25; i++) t.File($"f{i}.txt");
        int batches = 0, total = 0;
        await foreach (var batch in DirectoryLister.ListAsync(t.Path, false, batchSize: 10))
        {
            batches++;
            total += batch.Count;
        }
        Assert.Equal(25, total);
        Assert.Equal(3, batches);
    }

    [Fact]
    public async Task ListAsync_MissingFolder_Throws()
    {
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await foreach (var _ in DirectoryLister.ListAsync(@"C:\definitely\not\here\" + Guid.NewGuid(), false)) { }
        });
    }
}

public class FolderWatcherTests
{
    [Fact]
    public void Coalesce_DropsCreateThenDeletePairs()
    {
        var changes = new List<FolderChange>
        {
            new(ChangeKind.Created, @"C:\x\tmp"),
            new(ChangeKind.Changed, @"C:\x\tmp"),
            new(ChangeKind.Deleted, @"C:\x\tmp"),
            new(ChangeKind.Created, @"C:\x\keep"),
        };
        var result = FolderWatcher.Coalesce(changes);
        Assert.Single(result);
        Assert.Equal(@"C:\x\keep", result[0].FullPath);
    }

    [Fact]
    public void Coalesce_MergesRepeatedChanges()
    {
        var changes = Enumerable.Repeat(new FolderChange(ChangeKind.Changed, @"C:\x\a"), 5).ToList();
        Assert.Single(FolderWatcher.Coalesce(changes));
    }

    [Fact]
    public async Task Watcher_ReportsNewFile()
    {
        using var t = new TempDir();
        using var watcher = new FolderWatcher(t.Path, debounceMs: 50);
        var tcs = new TaskCompletionSource<IReadOnlyList<FolderChange>>();
        watcher.Changed += c => tcs.TrySetResult(c);
        t.File("new.txt");
        var changes = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(changes, c => c.Kind == ChangeKind.Created && c.FullPath.EndsWith("new.txt"));
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(-1, "--")]
    [InlineData(0, "Zero bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(500, "500 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    public void Size(long bytes, string expected) => Assert.Equal(expected, Format.Size(bytes));

    [Fact]
    public void Date_UsesTodayAndYesterday()
    {
        var now = new DateTime(2026, 11, 16, 15, 0, 0);
        Assert.StartsWith("Today at", Format.Date(now.AddHours(-1), now));
        Assert.StartsWith("Yesterday at", Format.Date(now.AddDays(-1), now));
        Assert.StartsWith("Nov 3, 2026 at", Format.Date(new DateTime(2026, 11, 3, 9, 12, 0), now));
        Assert.Equal("--", Format.Date(DateTime.MinValue, now));
    }
}

public class KeyMapTests
{
    [Theory]
    [InlineData(KeyMap.VkSpace, KeyModifiers.None, FinderCommand.QuickLook)]
    [InlineData(KeyMap.Vk0 + 3, KeyModifiers.Ctrl, FinderCommand.ViewColumns)]
    [InlineData(KeyMap.Vk0 + 1, KeyModifiers.Ctrl | KeyModifiers.Shift, FinderCommand.Tag1)]
    [InlineData(KeyMap.Vk0 + 7, KeyModifiers.Ctrl | KeyModifiers.Shift, FinderCommand.Tag7)]
    [InlineData(KeyMap.Vk0, KeyModifiers.Ctrl | KeyModifiers.Shift, FinderCommand.ClearTags)]
    [InlineData(KeyMap.Vk0 + 9, KeyModifiers.Ctrl | KeyModifiers.Alt, FinderCommand.JumpTab9)]
    [InlineData(KeyMap.VkEnter, KeyModifiers.None, FinderCommand.Open)]
    [InlineData(KeyMap.VkF2, KeyModifiers.None, FinderCommand.Rename)]
    [InlineData(KeyMap.VkBack, KeyModifiers.Ctrl, FinderCommand.Recycle)]
    [InlineData(KeyMap.VkDelete, KeyModifiers.Shift, FinderCommand.DeletePermanently)]
    [InlineData(KeyMap.VkG, KeyModifiers.Ctrl | KeyModifiers.Shift, FinderCommand.GoToFolder)]
    [InlineData(KeyMap.VkOemPeriod, KeyModifiers.Ctrl | KeyModifiers.Shift, FinderCommand.ToggleHidden)]
    [InlineData(KeyMap.VkT, KeyModifiers.Ctrl | KeyModifiers.Shift, FinderCommand.ReopenClosedTab)]
    [InlineData(KeyMap.VkUp, KeyModifiers.Alt, FinderCommand.Parent)]
    [InlineData(KeyMap.VkD, KeyModifiers.Ctrl, FinderCommand.AddToFavorites)]
    [InlineData(KeyMap.VkBrowserBack, KeyModifiers.None, FinderCommand.Back)]
    [InlineData(KeyMap.VkBrowserForward, KeyModifiers.None, FinderCommand.Forward)]
    public void Resolves(int key, KeyModifiers mods, FinderCommand expected) =>
        Assert.Equal(expected, KeyMap.Resolve(key, mods));

    [Fact]
    public void UnmappedChord_ReturnsNull() => Assert.Null(KeyMap.Resolve(KeyMap.VkSpace, KeyModifiers.Alt));

    [Fact]
    public void ViewKeysDoNotCollideWithTagKeys()
    {
        Assert.Equal(FinderCommand.ViewIcons, KeyMap.Resolve(KeyMap.Vk0 + 1, KeyModifiers.Ctrl));
        Assert.Equal(FinderCommand.Tag1, KeyMap.Resolve(KeyMap.Vk0 + 1, KeyModifiers.Ctrl | KeyModifiers.Shift));
    }

    [Fact]
    public void DestructiveKeys_AreIgnoredWhileTyping()
    {
        Assert.False(KeyMap.AppliesWhileEditingText(FinderCommand.Recycle));
        Assert.False(KeyMap.AppliesWhileEditingText(FinderCommand.Back));      // Backspace edits text
        Assert.False(KeyMap.AppliesWhileEditingText(FinderCommand.QuickLook)); // Space types a space
        Assert.True(KeyMap.AppliesWhileEditingText(FinderCommand.NewTab));
    }
}

public class SearchQueryTests
{
    [Fact]
    public void IndexQuery_EscapesQuotesAndWildcards()
    {
        string sql = SearchService.BuildIndexQuery("it's 50%_[x]", @"C:\Users\O'Neil");
        Assert.Contains("SCOPE='file:C:/Users/O''Neil'", sql);
        Assert.Contains("LIKE '%it''s 50[%][_][[]x]%'", sql);
    }

    [Fact]
    public void IndexQuery_WithoutScope_SearchesAllFiles() =>
        Assert.Contains("SCOPE='file:'", SearchService.BuildIndexQuery("a", null));

    [Fact]
    public async Task Scan_FindsFilesWhenIndexHasNothing()
    {
        using var t = new TempDir();
        var sub = t.Dir("sub");
        File.WriteAllText(Path.Combine(sub, "needle-" + Guid.NewGuid().ToString("N") + ".txt"), "");
        var found = new List<FileEntry>();
        await foreach (var batch in SearchService.SearchAsync("needle-", t.Path, false))
            found.AddRange(batch);
        Assert.Single(found);
    }
}

public class UndoStackTests
{
    [Fact]
    public void PopsMostRecentFirst_AndIsBounded()
    {
        var stack = new UndoStack(limit: 3);
        for (int i = 0; i < 5; i++) stack.Push(new UndoEntry(UndoKind.Copy, [new UndoItem($"a{i}", $"b{i}")]));
        Assert.Equal(3, stack.Count);
        Assert.Equal("a4", stack.Pop()!.Items[0].Original);
        Assert.Equal("a3", stack.Pop()!.Items[0].Original);
    }

    [Fact]
    public void EmptyEntries_AreIgnored()
    {
        var stack = new UndoStack();
        stack.Push(new UndoEntry(UndoKind.Move, []));
        Assert.Null(stack.Pop());
    }

    [Fact]
    public void FixAlpha_MakesOpaqueWhenAlphaMissing()
    {
        var pixels = new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 };
        ShellThumbnails.FixAlpha(pixels, 32);
        Assert.Equal(255, pixels[3]);
        Assert.Equal(255, pixels[7]);

        var withAlpha = new byte[] { 1, 2, 3, 128, 4, 5, 6, 0 };
        ShellThumbnails.FixAlpha(withAlpha, 32);
        Assert.Equal(128, withAlpha[3]);
        Assert.Equal(0, withAlpha[7]);
    }
}
