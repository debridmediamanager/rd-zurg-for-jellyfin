using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RdZurg.Archive;
using Jellyfin.Plugin.RdZurg.Configuration;
using Jellyfin.Plugin.RdZurg.Streaming;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jellyfin.Plugin.RdZurg.Tests;

public class StreamingSafetyTests
{
    private const string Key = "ZH4JR4PYJ6S2C";

    [Fact]
    public async Task UnsignedRequestCannotSpendTheAccountToken()
    {
        var (controller, handler) = Controller(authorized: false);
        var result = await controller.Stream(Key, CancellationToken.None);
        Assert.Equal(401, Assert.IsAssignableFrom<StatusCodeResult>(result).StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UnsatisfiableRangeReturns416WithoutReadingUpstream()
    {
        var (controller, handler) = Controller();
        controller.Request.Headers.Range = "bytes=100-";
        var result = await controller.Stream(Key, CancellationToken.None);
        Assert.Equal(416, Assert.IsAssignableFrom<StatusCodeResult>(result).StatusCode);
        Assert.Equal("bytes */100", controller.Response.Headers.ContentRange);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AnUpstreamIgnoringRangeCannotBeServedAsMedia()
    {
        var (controller, _) = Controller();
        controller.Request.Headers.Range = "bytes=0-3";
        var result = await controller.Stream(Key, CancellationToken.None);
        Assert.Equal(502, Assert.IsAssignableFrom<StatusCodeResult>(result).StatusCode);
        Assert.Equal(0, controller.Response.Body.Length);
        Assert.Null(controller.Response.ContentLength);
    }

    [Theory]
    [InlineData("bytes=0-3", 10, 13)]
    [InlineData("bytes=-4", 106, 109)]
    public async Task ValidRangesReturnOnlyTheRequestedMedia(string range, long from, long to)
    {
        var (controller, handler) = Controller();
        controller.Request.Headers.Range = range;
        handler.Respond = request =>
        {
            Assert.Equal(from, Assert.Single(request.Headers.Range!.Ranges).From);
            Assert.Equal(to, Assert.Single(request.Headers.Range.Ranges).To);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, 117);
            return response;
        };
        await controller.Stream(Key, CancellationToken.None);
        Assert.Equal(206, controller.Response.StatusCode);
        Assert.Equal(4, controller.Response.Body.Length);
    }

    [Fact]
    public async Task HeadDoesNotDownloadTheMember()
    {
        var (controller, handler) = Controller();
        controller.Request.Method = "HEAD";
        await controller.Stream(Key, CancellationToken.None);
        Assert.Equal(100, controller.Response.ContentLength);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task HeadIgnoresRangeHeaders()
    {
        var (controller, _) = Controller();
        controller.Request.Method = "HEAD";
        controller.Request.Headers.Range = "bytes=0-3";
        await controller.Stream(Key, CancellationToken.None);
        Assert.Equal(200, controller.Response.StatusCode);
        Assert.Equal(100, controller.Response.ContentLength);
    }

    [Fact]
    public async Task PlayerCancellationKeepsTheCachedSource()
    {
        var (controller, handler) = Controller();
        using var cancellation = new CancellationTokenSource();
        handler.Respond = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        await controller.Stream(Key, cancellation.Token);
        // HEAD must reuse the cached resolution, without trying to unrestrict the test URL.
        controller.Request.Method = "HEAD";
        var result = await controller.Stream(Key, CancellationToken.None);
        Assert.IsType<EmptyResult>(result);
        Assert.Equal(1, handler.Calls);
    }

    private static (StreamController Controller, Handler Handler) Controller(bool authorized = true)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(Path.GetTempPath());
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(s => s.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration { ApiKey = "test-token" });
        _ = new Plugin(paths.Object, serializer.Object);
        var handler = new Handler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var resolver = new LinkResolver(factory.Object, NullLogger<LinkResolver>.Instance);
        var cache = (ConcurrentDictionary<string, ResolvedLink>)typeof(LinkResolver).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(resolver)!;
        cache[Key] = new ResolvedLink
        {
            Url = "https://cdn.example.test/movie.rar", FileName = "movie.mkv", Size = 117,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(1),
            ArchiveEntry = new RarEntry { Name = "movie.mkv", DataOffset = 10, Length = 100, IsStored = true }
        };
        var controller = new StreamController(resolver, factory.Object, NullLogger<StreamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Method = "GET";
        if (authorized)
        {
            controller.Request.QueryString = new QueryString(new Uri(LinkResolver.BuildUrl("http://localhost:8096", Key, "movie.mkv")).Query);
        }
        controller.Response.Body = new MemoryStream();
        return (controller, handler);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[117]) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Respond(request));
        }
    }
}
