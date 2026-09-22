using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
/// Replays a real Real-Debrid film library on Jellyfin 12.0: the items read out of zen's jellyfin.db, the
/// account's own /torrents listing, and /torrents/info for every torrent a pass asks about.
/// </summary>
/// <remarks>
/// Jellyfin is modelled only where the sync depends on it, each from the 12.0 source: an item query
/// leaves alternate versions out unless it asks for owned items, an item id is an MD5 of the type
/// name and key, <c>ParseName</c> is VideoResolver's two cleaners, and deleting a film deletes the
/// versions linked to it whose path is not a file.
/// </remarks>
internal sealed class LibraryReplay
{
    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly LibrarySync _sync;

    private static readonly NamingOptions Naming = new();

    public LibraryReplay(LibraryFixture fixture)
    {
        Fixture = fixture;
        Listing = Fixture.Listing.ToList();

        var data = Path.Combine(Path.GetTempPath(), "rd-zurg-unit-" + Guid.NewGuid().ToString("N"));
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

        var movies = new Folder { Id = Guid.NewGuid(), Path = Path.Combine(data, "rd-zurg", "movies") };
        var shows = new Folder { Id = Guid.NewGuid(), Path = Path.Combine(data, "rd-zurg", "shows") };
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

            foreach (var (provider, value) in entry.ProviderIds)
            {
                movie.SetProviderId(provider, value);
            }

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
        manager
            .Setup(m => m.CreateItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<BaseItem> items, BaseItem _, CancellationToken _) =>
            {
                foreach (var item in items)
                {
                    _items[item.Id] = item;
                }
            });
        BaseItem.LibraryManager = manager.Object;

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new RealDebrid(this)));
        var providers = new Mock<IProviderManager>();
        providers
            .Setup(p => p.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
            .Callback((Guid id, MetadataRefreshOptions _, RefreshPriority _) => Refreshed.Add(id));
        _sync = new LibrarySync(manager.Object, providers.Object, Mock.Of<IFileSystem>(), factory.Object, NullLogger.Instance);
    }

    /// <summary>LibraryManager.GetNewItemIdInternal on a server with case-sensitive item ids, as zen's is.</summary>
    public static Guid JellyfinItemId(string key, Type type)
        => new(MD5.HashData(Encoding.Unicode.GetBytes(type.FullName + key)));

    /// <summary>LibraryManager.ParseName, which is VideoResolver's two cleaners with the stock options.</summary>
    public static ItemLookupInfo JellyfinParseName(string name)
    {
        var result = VideoResolver.CleanDateTime(name, Naming);
        return new ItemLookupInfo
        {
            Name = VideoResolver.TryCleanString(result.Name, Naming, out var cleaned) ? cleaned : result.Name,
            Year = result.Year
        };
    }

    public LibraryFixture Fixture { get; }

    public PluginConfiguration Config { get; }

    public List<JsonElement> Listing { get; }

    public List<Guid> Deleted { get; } = new();

    public List<string> DetailRequests { get; } = new();

    /// <summary>Gets the items a pass queued a metadata refresh for.</summary>
    public List<Guid> Refreshed { get; } = new();

    public IEnumerable<Movie> Movies => _items.Values.OfType<Movie>();

    public BaseItem? Get(Guid id) => _items.GetValueOrDefault(id);

    public Task<SyncResult> SyncAsync() => _sync.RunAsync(Config, new Progress<double>(), CancellationToken.None);

    public string KeyOf(BaseItem item) => Fixture.Items.Single(i => i.Id == item.Id).Key;

    public List<JsonElement> TorrentsHolding(string key)
        => Listing.Where(t => t.GetProperty("links").EnumerateArray().Any(l => RealDebridClient.LinkKey(l.GetString()!) == key)).ToList();

    /// <summary>Takes an item out of the library, as if it had never been added.</summary>
    public void Forget(Guid id) => Assert.True(_items.Remove(id));

    /// <summary>Every film the replay holds, in a form two passes can be compared by.</summary>
    public IReadOnlyDictionary<Guid, string> Snapshot()
        => Movies.ToDictionary(
            m => m.Id,
            m => string.Join(
                '|',
                m.Name,
                m.ProductionYear,
                m.PrimaryVersionId,
                string.Join(',', m.LinkedAlternateVersions.Select(l => l.ItemId).Order()),
                string.Join(',', m.ProviderIds.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)),
                m.Path?.Split('?')[0]));

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

internal sealed class RealDebrid(LibraryReplay replay) : HttpMessageHandler
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

internal sealed class LibraryFixture
{
    /// <summary>Reads a captured library, gzipped or not.</summary>
    public static LibraryFixture Load(string name)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        using var stream = name.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
        return JsonSerializer.Deserialize<LibraryFixture>(stream)!;
    }

    [JsonPropertyName("items")]
    public List<FixtureItem> Items { get; set; } = new();

    [JsonPropertyName("listing")]
    public List<JsonElement> Listing { get; set; } = new();

    [JsonPropertyName("info")]
    public Dictionary<string, JsonElement> Info { get; set; } = new();

    [JsonPropertyName("partialTorrents")]
    public List<string> PartialTorrents { get; set; } = new();
}

internal sealed class FixtureItem
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

    [JsonPropertyName("providerIds")]
    public Dictionary<string, string> ProviderIds { get; set; } = new();

    [JsonPropertyName("linkedAlternateVersions")]
    public List<Guid> LinkedAlternateVersions { get; set; } = new();

    [JsonPropertyName("idKind")]
    public string IdKind { get; set; } = string.Empty;

    public string Key => Route.Split('/')[0];

    public string EscapedFileName => Route.Split('/')[1];

    public string FileName => Uri.UnescapeDataString(EscapedFileName);

    private string Route => Path[(Path.IndexOf("/RdZurg/Stream/", StringComparison.Ordinal) + "/RdZurg/Stream/".Length)..];
}
