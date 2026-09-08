using System;
using Jellyfin.Plugin.RdZurg.Configuration;
using Jellyfin.Plugin.RdZurg.Streaming;
using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

public class ConfigurationTests
{
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:password@example.test")]
    [InlineData("http://example.test/?bad=1")]
    [InlineData("http://example.test/#fragment")]
    [InlineData("example.test")]
    public void RejectsUnusableServerUrls(string url)
        => Assert.Throws<ArgumentException>(() => new PluginConfiguration { PublicBaseUrl = url }.Validate());

    [Fact]
    public void AllowsReverseProxyBasePaths()
    {
        var config = new PluginConfiguration { PublicBaseUrl = "https://example.test/jellyfin/" };
        config.Validate();
        Assert.Equal("https://example.test/jellyfin", config.PublicBaseUrl);
    }

    [Fact]
    public void RejectsLibraryNameCollisions()
        => Assert.Throws<ArgumentException>(() => new PluginConfiguration { MovieLibraryName = "Media", ShowLibraryName = " media " }.Validate());

    [Theory]
    [InlineData(-1, 300)]
    [InlineData(0, 0)]
    [InlineData(0, 299)]
    [InlineData(0, 60001)]
    public void RejectsInvalidLimits(int max, int interval)
        => Assert.Throws<ArgumentException>(() => new PluginConfiguration { MaxTorrents = max, MinRequestIntervalMs = interval }.Validate());

    [Fact]
    public void SignatureCannotAuthorizeAnotherFileOrAccount()
    {
        var config = new PluginConfiguration { ApiKey = "first-account" };
        var signature = StreamAccess.Sign(config, "ZH4JR4PYJ6S2C");
        Assert.True(StreamAccess.Verify(config, "ZH4JR4PYJ6S2C", signature));
        Assert.False(StreamAccess.Verify(config, "7SZNG4LJDIXVB", signature));
        config.ApiKey = "second-account";
        Assert.False(StreamAccess.Verify(config, "ZH4JR4PYJ6S2C", signature));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void MalformedSignaturesAreRejected(string signature)
        => Assert.False(StreamAccess.Verify(new PluginConfiguration { ApiKey = "token" }, "ZH4JR4PYJ6S2C", signature));
}
