using WinFinder.Core.Shell;

namespace WinFinder.Core.Tests;

public sealed class DropRulesTests
{
    private static readonly string[] Download = [@"C:\Users\Sam\Downloads\report.pdf"];
    private const DropEffect All = DropEffect.Copy | DropEffect.Move | DropEffect.Link;
    private const DropEffect Browser = DropEffect.Copy | DropEffect.Link; // what Chrome/Edge offer for a download

    [Fact]
    public void BrowserDownload_IsCopied_EvenOnTheSameDrive()
    {
        var effect = DropRules.Decide(Download, @"C:\Users\Sam\Documents", Browser, ctrl: false, shift: false);
        Assert.Equal(DropEffect.Copy, effect);
        Assert.False(DropRules.IsInvalidTarget(Download, @"C:\Users\Sam\Documents", effect));
    }

    [Fact]
    public void BrowserDownload_ShiftStillCopies_BecauseMoveIsNotOffered() =>
        Assert.Equal(DropEffect.Copy, DropRules.Decide(Download, @"C:\Users\Sam\Documents", Browser, false, shift: true));

    [Fact]
    public void SourceThatReportsNothing_IsTreatedAsCopyOrMove()
    {
        Assert.Equal(DropEffect.Move, DropRules.Decide(Download, @"C:\Data", DropEffect.None, false, false));
        Assert.Equal(DropEffect.Copy, DropRules.Decide(Download, @"D:\Data", DropEffect.None, false, false));
    }

    [Theory]
    [InlineData(@"C:\Data", false, false, DropEffect.Move)]  // same drive moves
    [InlineData(@"D:\Data", false, false, DropEffect.Copy)]  // other drive copies
    [InlineData(@"C:\Data", true, false, DropEffect.Copy)]   // Ctrl forces copy
    [InlineData(@"D:\Data", false, true, DropEffect.Move)]   // Shift forces move
    public void ExplorerRules_WhenEverythingIsAllowed(string destination, bool ctrl, bool shift, DropEffect expected) =>
        Assert.Equal(expected, DropRules.Decide(Download, destination, All, ctrl, shift));

    [Fact]
    public void LinkOnlySource_CannotBeDropped() =>
        Assert.Equal(DropEffect.None, DropRules.Decide(Download, @"C:\Data", DropEffect.Link, false, false));

    [Fact]
    public void MovingIntoItsOwnFolder_OrIntoItself_IsInvalid()
    {
        Assert.True(DropRules.IsInvalidTarget(Download, @"C:\Users\Sam\Downloads", DropEffect.Move));
        Assert.True(DropRules.IsInvalidTarget([@"C:\Data"], @"C:\Data\Sub", DropEffect.Copy));
        Assert.True(DropRules.IsInvalidTarget(Download, @"C:\Data", DropEffect.None));
    }
}
