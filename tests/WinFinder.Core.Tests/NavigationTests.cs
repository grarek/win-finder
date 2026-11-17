using WinFinder.Core.Navigation;

namespace WinFinder.Core.Tests;

public class ColumnChainTests
{
    [Fact]
    public void SelectingFolder_AppendsColumn()
    {
        var chain = new ColumnChain(@"C:\Users");
        var change = chain.Select(0, @"C:\Users\Sam", isDirectory: true);

        Assert.Equal(1, change.TruncateFrom);
        Assert.Equal(@"C:\Users\Sam", change.AddedFolder);
        Assert.Equal([@"C:\Users", @"C:\Users\Sam"], chain.Folders);
        Assert.Null(chain.PreviewFile);
    }

    [Fact]
    public void SelectingInEarlierColumn_TruncatesEverythingToTheRight()
    {
        var chain = ColumnChain.FromPath(@"C:\Users", @"C:\Users\Sam\Documents\Work");
        Assert.Equal(4, chain.Folders.Count);

        var change = chain.Select(1, @"C:\Users\Sam\Downloads", isDirectory: true);

        Assert.Equal(2, change.TruncateFrom);
        Assert.Equal([@"C:\Users", @"C:\Users\Sam", @"C:\Users\Sam\Downloads"], chain.Folders);
    }

    [Fact]
    public void SelectingFile_SetsPreviewWithoutAddingColumn()
    {
        var chain = ColumnChain.FromPath(@"C:\Users", @"C:\Users\Sam");
        var change = chain.Select(1, @"C:\Users\Sam\notes.txt", isDirectory: false);

        Assert.Null(change.AddedFolder);
        Assert.Equal(@"C:\Users\Sam\notes.txt", change.PreviewFile);
        Assert.Equal(2, chain.Folders.Count);
        Assert.Equal(@"C:\Users\Sam\notes.txt", chain.PreviewFile);
    }

    [Fact]
    public void SelectingFolderAfterFile_ClearsPreview()
    {
        var chain = new ColumnChain(@"C:\A");
        chain.Select(0, @"C:\A\file.txt", false);
        chain.Select(0, @"C:\A\B", true);
        Assert.Null(chain.PreviewFile);
        Assert.Equal(@"C:\A\B", chain.Deepest);
    }

    [Fact]
    public void ClearSelection_RemovesColumnsToTheRight()
    {
        var chain = ColumnChain.FromPath(@"C:\A", @"C:\A\B\C");
        var change = chain.ClearSelection(0);
        Assert.Equal(1, change.TruncateFrom);
        Assert.Single(chain.Folders);
    }

    [Fact]
    public void FromPath_OutsideRoot_KeepsOnlyRoot()
    {
        var chain = ColumnChain.FromPath(@"C:\A", @"D:\Other");
        Assert.Equal([@"C:\A"], chain.Folders);
    }

    [Fact]
    public void Select_OutOfRange_Throws()
    {
        var chain = new ColumnChain(@"C:\A");
        Assert.Throws<ArgumentOutOfRangeException>(() => chain.Select(3, @"C:\A\x", true));
    }
}

public class NavigationHistoryTests
{
    [Fact]
    public void BackAndForward_Work()
    {
        var h = new NavigationHistory<string>("a");
        h.Navigate("b");
        h.Navigate("c");

        Assert.Equal("b", h.GoBack());
        Assert.Equal("a", h.GoBack());
        Assert.Null(h.GoBack());
        Assert.Equal("b", h.GoForward());
        Assert.True(h.CanGoForward);
    }

    [Fact]
    public void NavigatingAfterBack_ClearsForward()
    {
        var h = new NavigationHistory<string>("a");
        h.Navigate("b");
        h.GoBack();
        h.Navigate("c");
        Assert.False(h.CanGoForward);
        Assert.Equal("a", h.GoBack());
    }

    [Fact]
    public void ReplaceCurrent_DoesNotAddHistory()
    {
        var h = new NavigationHistory<string>("a");
        h.Navigate("search:x");
        h.ReplaceCurrent("search:xy");
        Assert.Equal("search:xy", h.Current);
        Assert.Equal("a", h.GoBack());
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void History_IsBounded()
    {
        var h = new NavigationHistory<string>("0", limit: 5);
        for (int i = 1; i <= 20; i++) h.Navigate(i.ToString());
        int backs = 0;
        while (h.GoBack() is not null) backs++;
        Assert.Equal(5, backs);
        Assert.Equal("15", h.Current);
    }
}

public class LocationAndPathTests
{
    [Theory]
    [InlineData(@"C:\Users\", @"C:\Users")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Users\Sam\..\Sam", @"C:\Users\Sam")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, PathUtil.Normalize(input));

    [Fact]
    public void IsSameOrUnder_RespectsSegmentBoundaries()
    {
        Assert.True(PathUtil.IsSameOrUnder(@"C:\A\B", @"C:\A"));
        Assert.True(PathUtil.IsSameOrUnder(@"c:\a", @"C:\A"));
        Assert.False(PathUtil.IsSameOrUnder(@"C:\AB", @"C:\A"));
    }

    [Fact]
    public void Segments_GoFromRootDown()
    {
        Assert.Equal([@"C:\", @"C:\Users", @"C:\Users\Sam"], PathUtil.Segments(@"C:\Users\Sam"));
    }

    [Fact]
    public void Location_RoundTripsThroughSerialization()
    {
        Assert.Equal(LocationKind.Computer, Location.Parse(Location.Computer.Serialize()).Kind);
        Assert.Equal(7, Location.Parse(Location.Tag(7).Serialize()).TagId);
        Assert.Equal(@"C:\Users", Location.Parse(Location.Folder(@"C:\Users\").Serialize()).Path);
        // Searches persist as their scope folder.
        Assert.Equal(LocationKind.Folder, Location.Parse(Location.Search("q", @"C:\X", false).Serialize()).Kind);
    }

    [Fact]
    public void ColumnRoot_IsKeptOnlyWhileItIsAnAncestor()
    {
        var inside = Location.Folder(@"C:\Users\Sam\Documents") with { ColumnRoot = @"C:\Users" };
        Assert.Equal(@"C:\Users", inside.EffectiveColumnRoot);

        var outside = Location.Folder(@"D:\Data") with { ColumnRoot = @"C:\Users" };
        Assert.Equal(@"D:\Data", outside.EffectiveColumnRoot);

        Assert.Equal(@"C:\Users", Location.Folder(@"C:\Users").EffectiveColumnRoot);
        // The root doesn't change what counts as "the same place".
        Assert.True(inside.SameAs(Location.Folder(@"C:\Users\Sam\Documents")));
    }

    [Fact]
    public void RestoredColumnRoot_UsesRecordedRootThenHomeThenDrive()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows); // exists, outside home
        string inHome = Path.Combine(home, "Documents", "Work");

        // Recorded root that exists and is an ancestor wins.
        Assert.Equal(home, Location.RestoredColumnRoot(inHome, home, home));
        // No recorded root (old session): home folder for paths inside it...
        Assert.Equal(home, Location.RestoredColumnRoot(inHome, null, home));
        // ...otherwise the drive, e.g. N:\Movies\Film -> N:\
        Assert.Equal(@"N:\", Location.RestoredColumnRoot(@"N:\Movies\Film", null, home));
        // A recorded root that no longer exists or isn't an ancestor is ignored.
        Assert.Equal(@"N:\", Location.RestoredColumnRoot(@"N:\Movies\Film", @"Z:\gone-" + Guid.NewGuid(), home));
        Assert.Equal(Path.GetPathRoot(windows), Location.RestoredColumnRoot(Path.Combine(windows, "System32"), home, home));
    }

    [Fact]
    public void ExpandUserInput_HandlesTildeAndEnvironment()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, PathUtil.ExpandUserInput("~"));
        Assert.Equal(Path.Combine(home, "Documents"), PathUtil.ExpandUserInput(@"~\Documents"));
        Assert.Equal(Environment.GetEnvironmentVariable("WINDIR"), PathUtil.ExpandUserInput("%WINDIR%"));
        Assert.Equal(@"D:\", PathUtil.ExpandUserInput("D:"));
    }

    [Fact]
    public void UniqueChildName_AddsCounter()
    {
        string dir = Directory.CreateTempSubdirectory("wf").FullName;
        try
        {
            Assert.Equal("New folder", PathUtil.UniqueChildName(dir, "New folder", true));
            Directory.CreateDirectory(Path.Combine(dir, "New folder"));
            Assert.Equal("New folder (2)", PathUtil.UniqueChildName(dir, "New folder", true));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "");
            Assert.Equal("a (2).txt", PathUtil.UniqueChildName(dir, "a.txt", false));
        }
        finally { Directory.Delete(dir, true); }
    }
}
