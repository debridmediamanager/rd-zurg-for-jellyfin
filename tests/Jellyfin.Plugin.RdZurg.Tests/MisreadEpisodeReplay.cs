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
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RdZurg.Configuration;
using Jellyfin.Plugin.RdZurg.Library;
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

namespace Jellyfin.Plugin.RdZurg.Tests;

/// <summary>
/// Replays the torrents behind zen's films that RD zurg 1.0.4.0 and earlier filed as episodes.
/// </summary>
/// <remarks>
/// <para>
/// <c>Fixtures/rd-misfiled-episodes-1.0.4.0.json</c> was read out of zen's jellyfin.db and the Real-Debrid
/// test account: every torrent behind the 11 films filed as episodes (two single films, BTCC and The
/// Odyssey, and two packs of Matrix films) and two real series, one torrent of 162 episodes and one
/// single-episode torrent, with their seasons and series.
/// </para>
/// <para>
/// Jellyfin is modelled only where the sync depends on it, from the 12.0 source: item ids are an MD5 of the
/// type name and key, a query filters on kind and parent, and AddChild and CreateItems add to the library.
/// </para>
/// </remarks>
internal sealed class MisreadEpisodeReplay
{
    private static readonly Lazy<Captured> Fixture = new(() =>
        JsonSerializer.Deserialize<Captured>(Tests.Fixture.Read("rd-misfiled-episodes-1.0.4.0.json"))!);

    private static readonly NamingOptions Naming = new();

    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly LibrarySync _sync;

    public MisreadEpisodeReplay()
    {
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

        foreach (var folder in Fixture.Value.Folders.Where(f => f.Type == "Series"))
        {
            _items[folder.Id] = new Series { Id = folder.Id, Name = folder.Name, ParentId = shows.Id, PresentationUniqueKey = folder.PresentationUniqueKey };
        }

        foreach (var folder in Fixture.Value.Folders.Where(f => f.Type == "Season"))
        {
            _items[folder.Id] = new Season
            {
                Id = folder.Id,
                Name = folder.Name,
                IndexNumber = folder.IndexNumber,
                ParentId = folder.ParentId!.Value,
                SeriesId = folder.ParentId!.Value,
                PresentationUniqueKey = folder.PresentationUniqueKey
            };
        }

        foreach (var entry in Fixture.Value.Items)
        {
            var episode = new Episode
            {
                Id = entry.Id,
                Name = entry.Name,
                IndexNumber = entry.IndexNumber,
                ParentIndexNumber = entry.ParentIndexNumber,
                Size = entry.Size,
                ParentId = entry.SeasonId,
                SeasonId = entry.SeasonId,
                SeriesId = entry.SeriesId,
                SeasonName = entry.SeasonName,
                SeriesName = entry.SeriesName,
                Path = LinkResolver.BuildUrl(Config.PublicBaseUrl, entry.Link, entry.FileName),
                PresentationUniqueKey = entry.PresentationUniqueKey
            };
            episode.SetProviderId(LibrarySync.LinkProviderId, entry.Link);
            _items[episode.Id] = episode;
        }

        var manager = new Mock<ILibraryManager>();
        manager.Setup(m => m.GetVirtualFolders()).Returns(() => new List<VirtualFolderInfo>
        {
            new() { Name = Config.MovieLibraryName, ItemId = movieCollection.Id.ToString(), Locations = new[] { movies.Path }, CollectionType = CollectionTypeOptions.movies },
            new() { Name = Config.ShowLibraryName, ItemId = showCollection.Id.ToString(), Locations = new[] { shows.Path }, CollectionType = CollectionTypeOptions.tvshows }
        });
        manager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => Query(query));
        manager.Setup(m => m.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string key, Type type) => new Guid(MD5.HashData(Encoding.Unicode.GetBytes(type.FullName + key))));
        manager.Setup(m => m.ParseName(It.IsAny<string>())).Returns((string name) => ParseName(name));
        manager
            .Setup(m => m.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        manager
            .Setup(m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<BaseItem>(), It.IsAny<bool>()))
            .Callback((BaseItem item, DeleteOptions _, BaseItem _, bool _) => _items.Remove(item.Id));
        manager
            .Setup(m => m.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback((BaseItem item, BaseItem _) => _items[item.Id] = item);
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
        _sync = new LibrarySync(manager.Object, Mock.Of<IProviderManager>(), Mock.Of<IFileSystem>(), factory.Object, NullLogger.Instance);
    }

    public PluginConfiguration Config { get; }

    public List<string> DetailRequests { get; } = new();

    public IEnumerable<BaseItem> Items => _items.Values;

    public Task<SyncResult> SyncAsync() => _sync.RunAsync(Config, new Progress<double>(), CancellationToken.None);

    public static string? KeyOf(BaseItem item) => item.GetProviderId(LibrarySync.LinkProviderId);

    /// <summary>Every item the replay holds, in a form two passes can be compared by.</summary>
    public string Snapshot()
        => string.Join(
            Environment.NewLine,
            _items.Values
                .OrderBy(i => i.Id)
                .Select(i => string.Join('|', i.GetType().Name, i.Id, i.Name, i.ParentId, i.Path?.Split('?')[0], (i as Episode)?.ParentIndexNumber, i.IndexNumber)));

    private static ItemLookupInfo ParseName(string name)
    {
        var result = VideoResolver.CleanDateTime(name, Naming);
        return new ItemLookupInfo
        {
            Name = VideoResolver.TryCleanString(result.Name, Naming, out var cleaned) ? cleaned : result.Name,
            Year = result.Year
        };
    }

    private IReadOnlyList<BaseItem> Query(InternalItemsQuery query)
        => _items.Values
            .Where(i => query.IncludeItemTypes.Length == 0 ? i is Movie or Episode : query.IncludeItemTypes.Contains(i.GetBaseItemKind()))
            .Where(i => query.ParentId == Guid.Empty || i.ParentId == query.ParentId)
            .Where(i => query.IncludeOwnedItems || i is not Video { PrimaryVersionId: not null })
            .Take(query.Limit ?? int.MaxValue)
            .ToList();

    private sealed class RealDebrid(MisreadEpisodeReplay replay) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/torrents/info/", StringComparison.Ordinal))
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                replay.DetailRequests.Add(id);
                return Json(Fixture.Value.Info[id].GetRawText());
            }

            if (path.EndsWith("/torrents", StringComparison.Ordinal))
            {
                return request.RequestUri.Query.Contains("page=1&", StringComparison.Ordinal)
                    ? Json(JsonSerializer.Serialize(Fixture.Value.Listing))
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            throw new InvalidOperationException("The replay has no answer for " + request.RequestUri);
        }

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Captured
    {
        [JsonPropertyName("listing")]
        public List<JsonElement> Listing { get; set; } = new();

        [JsonPropertyName("info")]
        public Dictionary<string, JsonElement> Info { get; set; } = new();

        [JsonPropertyName("items")]
        public List<CapturedEpisode> Items { get; set; } = new();

        [JsonPropertyName("folders")]
        public List<CapturedFolder> Folders { get; set; } = new();
    }

    private sealed class CapturedEpisode
    {
        [JsonPropertyName("id")] public Guid Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("seriesName")] public string? SeriesName { get; set; }
        [JsonPropertyName("seasonName")] public string? SeasonName { get; set; }
        [JsonPropertyName("parentIndexNumber")] public int? ParentIndexNumber { get; set; }
        [JsonPropertyName("indexNumber")] public int? IndexNumber { get; set; }
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("seriesId")] public Guid SeriesId { get; set; }
        [JsonPropertyName("seasonId")] public Guid SeasonId { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("presentationUniqueKey")] public string? PresentationUniqueKey { get; set; }
        [JsonPropertyName("link")] public string Link { get; set; } = string.Empty;

        public string FileName => Uri.UnescapeDataString(Path[(Path.LastIndexOf('/') + 1)..]);
    }

    private sealed class CapturedFolder
    {
        [JsonPropertyName("id")] public Guid Id { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("indexNumber")] public int? IndexNumber { get; set; }
        [JsonPropertyName("parentId")] public Guid? ParentId { get; set; }
        [JsonPropertyName("presentationUniqueKey")] public string? PresentationUniqueKey { get; set; }
    }
}
