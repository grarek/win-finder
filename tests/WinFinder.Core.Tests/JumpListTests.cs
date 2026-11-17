using WinFinder.Core.Shell;

namespace WinFinder.Core.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData(@"C:\Users\Sam\Documents")]
    [InlineData(@"C:\")]
    [InlineData(@"N:\Movies\10 Things I Hate About You (1999)")]
    [InlineData(@"\\server\share\folder with spaces")]
    public void QuotedPaths_RoundTripThroughWindowsParsing(string path)
    {
        string commandLine = $"\"C:\\Program Files\\WinFinder\\WinFinder.exe\" {JumpList.Quote(path)}";
        Assert.Equal(path, CommandLine.FirstPathArgument(commandLine));
    }

    [Fact]
    public void FirstPathArgument_WithoutExe_AndWithoutArguments()
    {
        Assert.Equal(@"D:\Data", CommandLine.FirstPathArgument("\"D:\\Data\""));
        Assert.Null(CommandLine.FirstPathArgument("\"C:\\Apps\\WinFinder.exe\""));
        Assert.Null(CommandLine.FirstPathArgument(""));
        Assert.Null(CommandLine.FirstPathArgument(null));
    }

    [Fact]
    public void DriveRootQuote_DoublesTrailingBackslash() =>
        Assert.Equal("\"C:\\\\\"", JumpList.Quote(@"C:\"));
}
