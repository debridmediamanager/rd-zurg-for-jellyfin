using Jellyfin.Plugin.RdZurg.Library;
using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

/// <summary>
/// The merge guard has to read the release name, not the item. Metadata providers rewrite an
/// item's title and year; the path this plugin wrote is the only durable record of the release.
/// </summary>
public class VersionMergeIdentityTests
{
    [Theory]
    [InlineData(
        "http://zen:8096/RdZurg/Stream/abc/%5BErai-raws%5D%20One%20Piece%20-%201004%20%5B1080p%5D%5BHEVC%5D.mkv",
        "[Erai-raws] One Piece - 1004 [1080p][HEVC]")]
    [InlineData(
        "http://zen:8096/RdZurg/Stream/abc/The.Matrix.1999.2160p.UHD.BluRay.mkv",
        "The Matrix 1999 2160p UHD BluRay")]
    public void RecoversTheReleaseNameFromThePlaybackUrl(string path, string expected)
        => Assert.Equal(expected, LibrarySync.ReleaseNameFromPath(path));

    [Fact]
    public void IgnoresAPathThisPluginDidNotWrite()
    {
        Assert.Null(LibrarySync.ReleaseNameFromPath(null));
        Assert.Null(LibrarySync.ReleaseNameFromPath("/mnt/zurg/movies/The.Matrix.1999.mkv"));
        Assert.Null(LibrarySync.ReleaseNameFromPath("http://zen:8096/OtherPlugin/Stream/abc/The.Matrix.1999.mkv"));
    }

    /// <summary>
    /// Two absolute-numbered anime episodes carry no year of their own. Once TMDb has matched them
    /// to the series and stamped a year on both, reading the year off the item lets them merge.
    /// </summary>
    [Fact]
    public void AYearInventedByAMetadataProviderCannotAuthorizeAMerge()
    {
        Assert.False(LibrarySync.MayMerge("One Piece", null));
        Assert.True(LibrarySync.MayMerge("One Piece", 2026));
    }

    /// <summary>A real pair of releases of one film still merges: both names carry the year.</summary>
    [Fact]
    public void ReleasesThatNameTheirOwnYearStillMerge()
        => Assert.True(LibrarySync.MayMerge("The Matrix", 1999));
}
