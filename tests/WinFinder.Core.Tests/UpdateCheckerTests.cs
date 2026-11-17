using System.Runtime.InteropServices;
using WinFinder.Core.Updates;

namespace WinFinder.Core.Tests;

public class UpdateCheckerTests
{
    private const string LatestJson = """
    {
      "tag_name": "v1.0.12",
      "name": "WinFinder 1.0.12",
      "html_url": "https://github.com/owner/repo/releases/tag/v1.0.12",
      "draft": false,
      "prerelease": false,
      "assets": [
        { "name": "WinFinder-1.0.12-win-x64.zip", "size": 1000, "browser_download_url": "https://github.com/owner/repo/releases/download/v1.0.12/WinFinder-1.0.12-win-x64.zip" },
        { "name": "WinFinder-1.0.12-win-arm64.zip", "size": 2000, "browser_download_url": "https://github.com/owner/repo/releases/download/v1.0.12/WinFinder-1.0.12-win-arm64.zip" },
        { "name": "notes.txt", "size": 5, "browser_download_url": "http://insecure.example/notes.txt" }
      ]
    }
    """;

    [Theory]
    [InlineData("v1.0.12", "1.0.12")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v2.0", "2.0.0")]
    [InlineData("v1.0.12-beta", "1.0.12")]
    public void ParseTag_ReadsVersions(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void ParseTag_RejectsNonVersions(string? tag) => Assert.Null(UpdateChecker.ParseTag(tag));

    [Fact]
    public void IsNewer_IgnoresRevisionAndComparesNumerically()
    {
        Assert.True(UpdateChecker.IsNewer(new Version(1, 0, 12), new Version(1, 0, 9, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(1, 0, 12), new Version(1, 0, 12, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(1, 0, 3), new Version(1, 0, 12, 0)));
        Assert.True(UpdateChecker.IsNewer(new Version(1, 0, 1), new Version(1, 0, 0, 0))); // local builds are 1.0.0
    }

    [Fact]
    public void Parse_ReadsReleaseAndPicksAssetForArchitecture()
    {
        var release = UpdateChecker.Parse(LatestJson)!;
        Assert.Equal(new Version(1, 0, 12), release.Version);
        Assert.Equal("WinFinder 1.0.12", release.Name);
        Assert.Equal(2, release.Assets.Count); // the non-https asset is dropped
        Assert.EndsWith("-win-x64.zip", release.AssetFor(Architecture.X64)!.Name);
        Assert.EndsWith("-win-arm64.zip", release.AssetFor(Architecture.Arm64)!.Name);
    }

    [Fact]
    public void IsCheckDue_FollowsTheInterval()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var interval = UpdateChecker.CheckInterval;
        Assert.True(UpdateChecker.IsCheckDue(null, now, interval));                         // never checked
        Assert.False(UpdateChecker.IsCheckDue(now.AddHours(-1), now, interval));            // checked recently
        Assert.True(UpdateChecker.IsCheckDue(now - interval, now, interval));               // exactly due
        Assert.True(UpdateChecker.IsCheckDue(now.AddDays(-2), now, interval));              // e.g. after sleep
        Assert.True(UpdateChecker.IsCheckDue(now.AddHours(3), now, interval));              // clock moved back
        Assert.Equal(TimeSpan.FromHours(4), interval);
    }

    [Fact]
    public void Parse_IgnoresDraftsAndPrereleases()
    {
        Assert.Null(UpdateChecker.Parse(LatestJson.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(UpdateChecker.Parse(LatestJson.Replace("\"draft\": false", "\"draft\": true")));
    }
}
