using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RdZurg.Configuration;
using Jellyfin.Plugin.RdZurg.Library;
using Jellyfin.Plugin.RdZurg.RealDebrid;
using Jellyfin.Plugin.RdZurg.Streaming;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

public class LibrarySyncSafetyTests
{
    [Fact]
    public async Task LimitedListingPreservesItemsOutsideTheWindow()
    {
        var (sync, manager, config) = Fixture(false);
        var result = await sync.RunAsync(config, new Progress<double>(), CancellationToken.None);
        Assert.Equal(0, result.ItemsRemoved);
        manager.Verify(m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<BaseItem>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ProviderRefusalFailsTheTaskBeforeCleanup()
    {
        var (sync, manager, config) = Fixture(true);
        await Assert.ThrowsAsync<RealDebridRefusedException>(() => sync.RunAsync(config, new Progress<double>(), CancellationToken.None));
        manager.Verify(m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<BaseItem>(), It.IsAny<bool>()), Times.Never);
    }

    private static (LibrarySync, Mock<ILibraryManager>, PluginConfiguration) Fixture(bool refuse)
    {
        var data = Path.Combine(Path.GetTempPath(), "rd-zurg-unit-" + Guid.NewGuid().ToString("N"));
        var config = new PluginConfiguration { ApiKey = "test", MaxTorrents = refuse ? 0 : 1, MergeDuplicateVersions = false };
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(data);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(data);
        paths.SetupGet(p => p.DataPath).Returns(data);
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(s => s.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(config);
        _ = new Plugin(paths.Object, serializer.Object);
        var manager = new Mock<ILibraryManager>();
        var movies = new Folder { Id = Guid.NewGuid(), Path = Path.Combine(data, "rd-zurg", "movies") };
        var shows = new Folder { Id = Guid.NewGuid(), Path = Path.Combine(data, "rd-zurg", "shows") };
        var movieCollection = new CollectionFolder { Id = Guid.NewGuid(), PhysicalFolderIds = new[] { movies.Id } };
        var showCollection = new CollectionFolder { Id = Guid.NewGuid(), PhysicalFolderIds = new[] { shows.Id } };
        manager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { Name = config.MovieLibraryName, ItemId = movieCollection.Id.ToString(), Locations = new[] { movies.Path }, CollectionType = CollectionTypeOptions.movies },
            new() { Name = config.ShowLibraryName, ItemId = showCollection.Id.ToString(), Locations = new[] { shows.Path }, CollectionType = CollectionTypeOptions.tvshows }
        });
        foreach (var item in new BaseItem[] { movies, shows, movieCollection, showCollection })
            manager.Setup(m => m.GetItemById(item.Id)).Returns(item);
        var known = new List<BaseItem>();
        foreach (var key in new[] { "AAAAAAAAAAAAA", "BBBBBBBBBBBBB" })
        {
            var item = new Movie { Id = Guid.NewGuid(), Name = key, Size = 100, Path = LinkResolver.BuildUrl(config.PublicBaseUrl, key, key + ".mkv") };
            item.SetProviderId(LibrarySync.LinkProviderId, key);
            known.Add(item);
        }
        manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(known);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new SyncHandler(refuse)));
        return (new LibrarySync(manager.Object, Mock.Of<IProviderManager>(), Mock.Of<IFileSystem>(), factory.Object, NullLogger.Instance), manager, config);
    }

    private sealed class SyncHandler(bool refuse) : HttpMessageHandler
    {
        private int _pages;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/info/", StringComparison.Ordinal))
                throw new RealDebridRefusedException("Fixture: provider refused the first detail request.");
            var body = refuse
                ? "[{\"id\":\"new\",\"status\":\"downloaded\",\"links\":[\"CCCCCCCCCCCCC\"]},{\"id\":\"old\",\"status\":\"downloaded\",\"links\":[\"AAAAAAAAAAAAA\",\"BBBBBBBBBBBBB\"]}]"
                : "[{\"id\":\"first\",\"status\":\"downloaded\",\"links\":[\"AAAAAAAAAAAAA\"]}]";
            return Task.FromResult(new HttpResponseMessage(++_pages == 1 ? HttpStatusCode.OK : HttpStatusCode.NoContent) { Content = new StringContent(body) });
        }
    }
}
