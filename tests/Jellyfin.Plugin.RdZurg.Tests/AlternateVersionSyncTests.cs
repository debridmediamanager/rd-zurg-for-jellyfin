using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using Emby.Naming.Video;
using Jellyfin.Plugin.RdZurg.Configuration;
using Jellyfin.Plugin.RdZurg.Library;
using Jellyfin.Plugin.RdZurg.RealDebrid;
using Jellyfin.Plugin.RdZurg.Streaming;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

/// <summary>
/// Replays a real Real-Debrid library as RD zurg 1.0.2.0 left it on Jellyfin 12.0.
/// </summary>
/// <remarks>
/// <para>
/// <c>Fixtures/rd-library-1.0.2.0.json</c> was read out of zen's jellyfin.db: every version group
/// behind the nine films whose primary was a copy 1.0.0.0 had added, every group holding a release
/// whose name carries no year, and each item's twin on the same link. Beside them are the account's
/// own /torrents entries and /torrents/info responses for every torrent those items came from.
/// </para>
/// <para>
/// Jellyfin is modelled only where the sync depends on it, each from the 12.0 source: an item query
/// leaves alternate versions out unless it asks for owned items, an item id is an MD5 of the type
/// name and key, and deleting a film deletes the versions linked to it whose path is not a file.
/// </para>
/// </remarks>
[Collection(PluginInstanceCollection.Name)]
public class AlternateVersionSyncTests
{
    private static readonly Lazy<LibraryFixture> Captured = new(() =>
        JsonSerializer.Deserialize<LibraryFixture>(Fixture.Read("rd-library-1.0.2.0.json"))!);

    private static readonly NamingOptions Naming = new();

    /// <summary>
    /// The fixture's ids have to be the ones this model computes, or every other test here is
    /// checking a formula of its own invention.
    /// </summary>
    [Fact]
    public void TheModelledItemIdsAreTheOnesJellyfinWrote()
    {
        foreach (var item in Captured.Value.Items)
        {
            Assert.Equal(
                item.IdKind == "canonical",
                JellyfinItemId("rd-zurg-movie:" + item.Key, typeof(Movie)) == item.Id);
        }

        Assert.Contains(Captured.Value.Items, i => i.IdKind == "urlDerived");
    }

    /// <summary>
    /// Only a torrent holding a link that no item holds needs its files listed. On the real library
    /// the hidden versions kept 307 more torrents unrecognised on every pass.
    /// </summary>
    [Fact]
    public async Task RecognisesEveryVersionWithoutAskingRealDebridAgain()
    {
        var replay = new Replay();

        var result = await replay.SyncAsync();

        Assert.Equal(replay.Fixture.PartialTorrents.Order(), replay.DetailRequests.Distinct().Order());
        Assert.Equal(0, result.MoviesAdded);
        Assert.Equal(0, result.EpisodesAdded);
    }

    /// <summary>
    /// 1.0.0.0 derived an item's id from its playback URL. A later pass that could not see such an
    /// item added its file again under the id derived from the link, so the library held both, and
    /// the older one still played from an unsigned URL that now answers 401.
    /// </summary>
    [Fact]
    public async Task RemovesTheCopiesAnEarlierBuildAddedAndKeepsTheirTwins()
    {
        var replay = new Replay();
        var items = replay.Fixture.Items;
        var copies = items
            .Where(i => i.IdKind == "urlDerived" && items.Any(t => t.IdKind == "canonical" && t.Key == i.Key))
            .Select(i => i.Id)
            .Order()
            .ToList();

        await replay.SyncAsync();

        Assert.NotEmpty(copies);
        Assert.Equal(copies, replay.Deleted.Order());
        Assert.All(items.Where(i => !copies.Contains(i.Id)), i => Assert.NotNull(replay.Get(i.Id)));
    }

    [Fact]
    public async Task LeavesEveryVersionOnASignedPlaybackUrl()
    {
        var replay = new Replay();

        await replay.SyncAsync();

        Assert.All(replay.Movies, movie =>
        {
            var entry = replay.Fixture.Items.Single(i => i.Id == movie.Id);
            Assert.Equal(LinkResolver.BuildUrl(replay.Config.PublicBaseUrl, entry.Key, entry.FileName), movie.Path);
        });
    }

    /// <summary>
    /// A pass that could not see a film's versions found the film alone and saved it with none. On
    /// the real library 19 of 191 films still named any of their 623 versions.
    /// </summary>
    [Fact]
    public async Task EachFilmNamesEveryVersionFiledUnderIt()
    {
        var replay = new Replay();

        await replay.SyncAsync();

        var movies = replay.Movies.ToList();
        Assert.All(movies.Where(m => m.PrimaryVersionId is null), film => Assert.Equal(
            movies.Where(m => m.PrimaryVersionId == film.Id).Select(m => m.Id).Order(),
            film.LinkedAlternateVersions.Select(l => l.ItemId!.Value).Order()));
        Assert.All(movies.Where(m => m.PrimaryVersionId is not null), version =>
        {
            Assert.Empty(version.LinkedAlternateVersions);
            Assert.Contains(movies, film => film.Id == version.PrimaryVersionId && film.PrimaryVersionId is null);
        });
        Assert.Contains(movies, m => m.LinkedAlternateVersions.Length > 1);
    }

    /// <summary>
    /// An earlier build grouped on the item's metadata, which TMDb rewrites, so episodes with no year
    /// of their own were filed as versions of one film. The grouping reads the release name now.
    /// </summary>
    [Fact]
    public async Task AReleaseThatMayNotBeMergedIsNoLongerFiledAsAVersion()
    {
        var replay = new Replay();
        Assert.Contains(replay.Movies, m => m.PrimaryVersionId is not null && !MayMerge(m));

        await replay.SyncAsync();

        Assert.All(replay.Movies.Where(m => !MayMerge(m)), movie =>
        {
            Assert.Null(movie.PrimaryVersionId);
            Assert.Empty(movie.LinkedAlternateVersions);
        });
    }

    /// <summary>
    /// Jellyfin deletes a film's linked versions along with it when their path is not a file, and
    /// every path here is a URL. A release still in the account must not go because its film did.
    /// </summary>
    [Fact]
    public async Task AFilmWhoseTorrentIsGoneLeavesItsVersionsInTheLibrary()
    {
        var replay = new Replay();
        await replay.SyncAsync();

        // Real-Debrid keeps one file under several torrents, so the film chosen is the one with the
        // most versions whose file only single-file torrents hold: taking those away takes nothing else.
        var film = replay.Movies
            .Where(m => m.PrimaryVersionId is null)
            .Where(m => replay.TorrentsHolding(replay.KeyOf(m)).All(t => t.GetProperty("links").GetArrayLength() == 1))
            .OrderByDescending(m => replay.Movies.Count(v => v.PrimaryVersionId == m.Id))
            .First();
        var versions = replay.Movies.Where(m => m.PrimaryVersionId == film.Id).Select(m => m.Id).ToList();
        Assert.NotEmpty(versions);

        replay.RemoveTorrentsHolding(replay.KeyOf(film));
        replay.Deleted.Clear();
        await replay.SyncAsync();

        Assert.Equal(new[] { film.Id }, replay.Deleted);
        var promoted = Assert.Single(replay.Movies, m => versions.Contains(m.Id) && m.PrimaryVersionId is null);
        Assert.Equal(
            versions.Where(v => v != promoted.Id).Order(),
            promoted.LinkedAlternateVersions.Select(l => l.ItemId!.Value).Order());
    }

    private static bool MayMerge(BaseItem item)
    {
        var parsed = JellyfinParseName(LibrarySync.ReleaseNameFromPath(item.Path)!);
        return LibrarySync.MayMerge(parsed.Name, parsed.Year);
    }

    /// <summary>LibraryManager.GetNewItemIdInternal on a server with case-sensitive item ids, as zen's is.</summary>
    private static Guid JellyfinItemId(string key, Type type)
        => new(MD5.HashData(Encoding.Unicode.GetBytes(type.FullName + key)));

    /// <summary>LibraryManager.ParseName.</summary>
    private static ItemLookupInfo JellyfinParseName(string name)
    {
        var result = VideoResolver.CleanDateTime(name, Naming);
        return new ItemLookupInfo
        {
            Name = VideoResolver.TryCleanString(result.Name, Naming, out var cleaned) ? cleaned : result.Name,
            Year = result.Year
        };
    }

    private sealed class Replay
    {
        private readonly Dictionary<Guid, BaseItem> _items = new();
        private readonly LibrarySync _sync;

        public Replay()
        {
            Fixture = Captured.Value;
            Listing = Fixture.Listing.ToList();

            var data = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rd-zurg-unit-" + Guid.NewGuid().ToString("N"));
            Config = new PluginConfiguration
            {
                ApiKey = "test",
                PublicBaseUrl = "http://zen:8096",
                MinRequestIntervalMs = 300,
                MaxTorrents = 0,
                RemoveVanishedItems = true,
                MergeDuplicateVersions = true
            };
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(p => p.PluginsPath).Returns(data);
            paths.SetupGet(p => p.PluginConfigurationsPath).Returns(data);
            paths.SetupGet(p => p.DataPath).Returns(data);
            var serializer = new Mock<IXmlSerializer>();
            serializer.Setup(s => s.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(Config);
            _ = new Plugin(paths.Object, serializer.Object);

            var movies = new Folder { Id = Guid.NewGuid(), Path = System.IO.Path.Combine(data, "rd-zurg", "movies") };
            var shows = new Folder { Id = Guid.NewGuid(), Path = System.IO.Path.Combine(data, "rd-zurg", "shows") };
            var movieCollection = new CollectionFolder { Id = Guid.NewGuid(), PhysicalFolderIds = new[] { movies.Id } };
            var showCollection = new CollectionFolder { Id = Guid.NewGuid(), PhysicalFolderIds = new[] { shows.Id } };
            foreach (var folder in new BaseItem[] { movies, shows, movieCollection, showCollection })
            {
                _items[folder.Id] = folder;
            }

            foreach (var entry in Fixture.Items)
            {
                Assert.Equal("Movie", entry.Type);
                var movie = new Movie
                {
                    Id = entry.Id,
                    Name = entry.Name,
                    ProductionYear = entry.ProductionYear,
                    Size = entry.Size,
                    ParentId = movies.Id,
                    // 1.0.0.0 wrote unsigned URLs; every later build signs them.
                    Path = entry.Signed
                        ? LinkResolver.BuildUrl(Config.PublicBaseUrl, entry.Key, entry.FileName)
                        : Config.PublicBaseUrl + "/RdZurg/Stream/" + entry.Key + "/" + entry.EscapedFileName,
                    PrimaryVersionId = entry.PrimaryVersionId,
                    LinkedAlternateVersions = entry.LinkedAlternateVersions
                        .Select(id => new LinkedChild { ItemId = id, Type = LinkedChildType.LinkedAlternateVersion })
                        .ToArray()
                };

                if (entry.Link is not null)
                {
                    movie.SetProviderId(LibrarySync.LinkProviderId, entry.Link);
                }

                _items[movie.Id] = movie;
            }

            var manager = new Mock<ILibraryManager>();
            manager.Setup(m => m.GetVirtualFolders()).Returns(() => new List<VirtualFolderInfo>
            {
                new() { Name = Config.MovieLibraryName, ItemId = movieCollection.Id.ToString(), Locations = new[] { movies.Path }, CollectionType = CollectionTypeOptions.movies },
                new() { Name = Config.ShowLibraryName, ItemId = showCollection.Id.ToString(), Locations = new[] { shows.Path }, CollectionType = CollectionTypeOptions.tvshows }
            });
            manager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
            manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => Query(query));
            manager.Setup(m => m.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns((string key, Type type) => JellyfinItemId(key, type));
            manager.Setup(m => m.ParseName(It.IsAny<string>())).Returns((string name) => JellyfinParseName(name));
            manager
                .Setup(m => m.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            manager
                .Setup(m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<BaseItem>(), It.IsAny<bool>()))
                .Callback((BaseItem item, DeleteOptions _, BaseItem _, bool _) => Delete(item));
            BaseItem.LibraryManager = manager.Object;

            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new RealDebrid(this)));
            _sync = new LibrarySync(manager.Object, Mock.Of<IProviderManager>(), Mock.Of<IFileSystem>(), factory.Object, NullLogger.Instance);
        }

        public LibraryFixture Fixture { get; }

        public PluginConfiguration Config { get; }

        public List<JsonElement> Listing { get; }

        public List<Guid> Deleted { get; } = new();

        public List<string> DetailRequests { get; } = new();

        public IEnumerable<Movie> Movies => _items.Values.OfType<Movie>();

        public BaseItem? Get(Guid id) => _items.GetValueOrDefault(id);

        public Task<SyncResult> SyncAsync() => _sync.RunAsync(Config, new Progress<double>(), CancellationToken.None);

        public string KeyOf(BaseItem item) => Fixture.Items.Single(i => i.Id == item.Id).Key;

        public List<JsonElement> TorrentsHolding(string key)
            => Listing.Where(t => t.GetProperty("links").EnumerateArray().Any(l => RealDebridClient.LinkKey(l.GetString()!) == key)).ToList();

        public void RemoveTorrentsHolding(string key)
        {
            var holding = TorrentsHolding(key);
            Assert.NotEmpty(holding);
            Listing.RemoveAll(t => holding.Contains(t));
        }

        private IReadOnlyList<BaseItem> Query(InternalItemsQuery query)
            => _items.Values
                .Where(i => i is Movie or Episode)
                .Where(i => query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(i.GetBaseItemKind()))
                // BaseItemRepository.ApplyAccessFiltering: an alternate version is left out of every
                // query that does not ask for owned items.
                .Where(i => query.IncludeOwnedItems || i is not Video { PrimaryVersionId: not null })
                .ToList();

        /// <summary>LibraryManager.DeleteItem, as far as versions are concerned.</summary>
        private void Delete(BaseItem item)
        {
            if (item is Video film && !film.PrimaryVersionId.HasValue)
            {
                // A linked version whose path is not a file on disk is deleted along with its film.
                foreach (var link in film.LinkedAlternateVersions)
                {
                    if (link.ItemId is { } id && _items.Remove(id))
                    {
                        Deleted.Add(id);
                    }
                }
            }
            else if (item is Video version && _items.GetValueOrDefault(version.PrimaryVersionId!.Value) is Video primary)
            {
                primary.LinkedAlternateVersions = primary.LinkedAlternateVersions.Where(l => l.ItemId != version.Id).ToArray();
            }

            if (_items.Remove(item.Id))
            {
                Deleted.Add(item.Id);
            }
        }
    }

    private sealed class RealDebrid(Replay replay) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/torrents/info/", StringComparison.Ordinal))
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                replay.DetailRequests.Add(id);
                return Json(replay.Fixture.Info[id].GetRawText());
            }

            if (path.EndsWith("/torrents", StringComparison.Ordinal))
            {
                return request.RequestUri.Query.Contains("page=1&", StringComparison.Ordinal)
                    ? Json(JsonSerializer.Serialize(replay.Listing))
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            throw new InvalidOperationException("The replay has no answer for " + request.RequestUri);
        }

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class LibraryFixture
    {
        [JsonPropertyName("items")]
        public List<FixtureItem> Items { get; set; } = new();

        [JsonPropertyName("listing")]
        public List<JsonElement> Listing { get; set; } = new();

        [JsonPropertyName("info")]
        public Dictionary<string, JsonElement> Info { get; set; } = new();

        [JsonPropertyName("partialTorrents")]
        public List<string> PartialTorrents { get; set; } = new();
    }

    private sealed class FixtureItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("productionYear")]
        public int? ProductionYear { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("signed")]
        public bool Signed { get; set; }

        [JsonPropertyName("primaryVersionId")]
        public Guid? PrimaryVersionId { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("linkedAlternateVersions")]
        public List<Guid> LinkedAlternateVersions { get; set; } = new();

        [JsonPropertyName("idKind")]
        public string IdKind { get; set; } = string.Empty;

        public string Key => Route.Split('/')[0];

        public string EscapedFileName => Route.Split('/')[1];

        public string FileName => Uri.UnescapeDataString(EscapedFileName);

        private string Route => Path[(Path.IndexOf("/RdZurg/Stream/", StringComparison.Ordinal) + "/RdZurg/Stream/".Length)..];
    }
}
