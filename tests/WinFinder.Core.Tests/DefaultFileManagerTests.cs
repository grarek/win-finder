using Microsoft.Win32;
using WinFinder.Core.Shell;

namespace WinFinder.Core.Tests;

/// <summary>Runs against a throwaway key under HKCU\Software, never the real HKCU\Software\Classes.</summary>
public sealed class DefaultFileManagerTests : IDisposable
{
    private const string Exe = @"C:\Users\Sam\AppData\Local\Programs\WinFinder\WinFinder.exe";
    private readonly string _path = @"Software\WinFinderTests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public DefaultFileManagerTests() => _root = Registry.CurrentUser.CreateSubKey(_path, writable: true);

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\WinFinderTests", writable: true);
        if (parent is { SubKeyCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\WinFinderTests", false);
    }

    private string? Value(string key, string? name = null)
    {
        using var k = _root.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    [Fact]
    public void Register_MakesWinFinderTheDefaultForFoldersDrivesAndWinE()
    {
        var manager = new DefaultFileManager(_root);
        Assert.Null(manager.RegisteredExecutable());

        manager.Register(Exe);

        foreach (var cls in new[] { "Directory", "Drive" })
        {
            Assert.Equal("WinFinder", Value($@"{cls}\shell"));
            Assert.Equal($"\"{Exe}\" \"%1\"", Value($@"{cls}\shell\WinFinder\command"));
        }
        string winE = $@"CLSID\{DefaultFileManager.WinEClsid}\shell\opennewwindow\command";
        Assert.Equal($"\"{Exe}\"", Value(winE));
        Assert.Equal("", Value(winE, "DelegateExecute"));
        Assert.True(manager.IsRegistered(Exe));
        Assert.Equal(Exe, manager.RegisteredExecutable());
        Assert.False(manager.IsRegistered(@"D:\Elsewhere\WinFinder.exe"));
    }

    [Fact]
    public void Unregister_RemovesEverythingItAdded()
    {
        var manager = new DefaultFileManager(_root);
        manager.Register(Exe);
        manager.Unregister();

        Assert.Null(manager.RegisteredExecutable());
        Assert.Null(Value(@"Directory\shell"));
        using (var dirShell = _root.OpenSubKey(@"Directory\shell"))
            Assert.DoesNotContain("WinFinder", dirShell?.GetSubKeyNames() ?? []);
        using (var clsid = _root.OpenSubKey(@"CLSID"))
            Assert.DoesNotContain(DefaultFileManager.WinEClsid, clsid?.GetSubKeyNames() ?? [], StringComparer.OrdinalIgnoreCase);

        manager.Unregister(); // harmless when not registered
    }

    [Fact]
    public void Unregister_RestoresAnotherAppsDefaultVerb_AndLeavesItsWinEOverride()
    {
        using (var shell = _root.CreateSubKey(@"Directory\shell")) shell.SetValue(null, "otherapp");
        var manager = new DefaultFileManager(_root);
        manager.Register(Exe);
        Assert.Equal("WinFinder", Value(@"Directory\shell"));

        // Another file manager then takes over Win+E.
        string winE = $@"CLSID\{DefaultFileManager.WinEClsid}\shell\opennewwindow\command";
        using (var k = _root.CreateSubKey(winE)) k.SetValue(null, @"""C:\Other\Other.exe""");

        manager.Unregister();
        Assert.Equal("otherapp", Value(@"Directory\shell"));
        Assert.Equal(@"""C:\Other\Other.exe""", Value(winE));
    }

    [Theory]
    [InlineData("C:\"", @"C:\")]          // Explorer's "C:\" after Windows' argument parsing
    [InlineData("  \"D:\\Data\"  ", @"D:\Data")]
    [InlineData(@"E:", @"E:\")]
    [InlineData(@"C:\Users\Sam", @"C:\Users\Sam")]
    public void CleanPath_RepairsDriveArguments(string raw, string expected) =>
        Assert.Equal(expected, CommandLine.CleanPath(raw));

    [Fact]
    public void FirstPathArgument_HandlesTheDriveCommandExplorerRuns()
    {
        // What Explorer runs for a drive with the registered command: "exe" "C:\"
        string commandLine = $"\"{Exe}\" \"C:\\\"";
        Assert.Equal(@"C:\", CommandLine.FirstPathArgument(commandLine));
    }
}
