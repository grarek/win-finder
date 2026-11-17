using WinFinder.Core.Tags;

namespace WinFinder.Core.Tests;

public sealed class TagStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TagStore _store;

    public TagStoreTests()
    {
        _store = new TagStore(Path.Combine(_dir.Path, "tags.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private int Red => _store.GetTags().First(t => t.Name == "Red").Id;
    private int Blue => _store.GetTags().First(t => t.Name == "Blue").Id;

    [Fact]
    public void Seeds_SevenFinderColors()
    {
        var tags = _store.GetTags();
        Assert.Equal(["Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Gray"], tags.Select(t => t.Name));
    }

    [Fact]
    public void SetAndGet_RoundTrips()
    {
        var file = _dir.File("a.txt");
        _store.SetTags(file, [Red, Blue]);
        Assert.Equal([Red, Blue], _store.GetTagIds(file).Order());
        _store.SetTags(file, []);
        Assert.Empty(_store.GetTagIds(file));
    }

    [Fact]
    public void PathLookup_IsCaseInsensitive()
    {
        var file = _dir.File("Case.txt");
        _store.SetTags(file, [Red]);
        Assert.Equal([Red], _store.GetTagIds(file.ToUpperInvariant()));
    }

    [Fact]
    public void GetTagIdsInFolder_ReturnsOnlyDirectChildren()
    {
        var a = _dir.File("a.txt");
        var sub = _dir.Dir("sub");
        var nested = Path.Combine(sub, "n.txt");
        File.WriteAllText(nested, "");
        _store.SetTags(a, [Red]);
        _store.SetTags(nested, [Blue]);

        var map = _store.GetTagIdsInFolder(_dir.Path);
        Assert.Single(map);
        Assert.Equal([Red], map[a]);
    }

    [Fact]
    public void UpdatePath_FollowsRenamedFolderAndDescendants()
    {
        var sub = _dir.Dir("old");
        var child = Path.Combine(sub, "c.txt");
        File.WriteAllText(child, "");
        _store.SetTags(sub, [Red]);
        _store.SetTags(child, [Blue]);

        string renamed = Path.Combine(_dir.Path, "new");
        Directory.Move(sub, renamed);
        _store.UpdatePath(sub, renamed);

        Assert.Equal([Red], _store.GetTagIds(renamed));
        Assert.Equal([Blue], _store.GetTagIds(Path.Combine(renamed, "c.txt")));
        Assert.Single(_store.GetTagIdsInFolder(renamed));
    }

    [Fact]
    public void TagsSurvive_RenameOutsideTheApp_ViaFileId()
    {
        var file = _dir.File("before.txt");
        _store.SetTags(file, [Red]);

        string after = Path.Combine(_dir.Path, "after.txt");
        File.Move(file, after); // nobody told the store

        var paths = _store.GetPathsWithTag(Red);
        Assert.Equal([after], paths);
        Assert.Equal([Red], _store.GetTagIds(after)); // healed
    }

    [Fact]
    public void PruneMissing_RemovesDeletedFiles()
    {
        var file = _dir.File("gone.txt");
        _store.SetTags(file, [Red]);
        File.Delete(file);
        Assert.Equal(1, _store.PruneMissing());
        Assert.Empty(_store.GetPathsWithTag(Red));
    }

    [Fact]
    public void Toggle_RemovesOnlyWhenAllHaveTag()
    {
        var service = new TagService(_store);
        var a = _dir.File("a.txt");
        var b = _dir.File("b.txt");
        _store.SetTags(a, [Red]);

        service.Toggle([a, b], Red);           // not all have it -> add to all
        Assert.Contains(Red, _store.GetTagIds(b));

        service.Toggle([a, b], Red);           // all have it -> remove from all
        Assert.Empty(_store.GetTagIds(a));
        Assert.Empty(_store.GetTagIds(b));
    }

    [Fact]
    public void DeleteTag_RemovesAssignments()
    {
        var a = _dir.File("a.txt");
        var custom = _store.AddTag("Work", "#123456");
        _store.SetTags(a, [custom.Id]);
        _store.DeleteTag(custom.Id);
        Assert.Empty(_store.GetTagIds(a));
        Assert.DoesNotContain(_store.GetTags(), t => t.Id == custom.Id);
    }
}
