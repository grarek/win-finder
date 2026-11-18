using WinFinder.Core.Settings;

namespace WinFinder.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WinFinderSettingsTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void FolderColumnWidths_SurviveARestart_AndIgnoreCase()
    {
        var store = new SettingsStore(_dir);
        Assert.Empty(store.Current.FolderColumnWidths);
        store.Current.FolderColumnWidths[@"C:\Users\Sam\Projects"] = 410;
        store.Save();

        var reloaded = new SettingsStore(_dir);
        Assert.True(reloaded.Current.FolderColumnWidths.TryGetValue(@"c:\users\sam\PROJECTS", out var width));
        Assert.Equal(410, width);
    }

    [Fact]
    public void FolderColumnWidths_DefaultToEmpty_ForOlderSettingsFiles()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "ColumnWidth": 200 }""");

        var store = new SettingsStore(_dir);
        Assert.Equal(200, store.Current.ColumnWidth);
        Assert.Empty(store.Current.FolderColumnWidths);
    }
}
