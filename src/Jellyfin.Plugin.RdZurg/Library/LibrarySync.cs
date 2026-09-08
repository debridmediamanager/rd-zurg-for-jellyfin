using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RdZurg.Configuration;
using Jellyfin.Plugin.RdZurg.RealDebrid;
using Jellyfin.Plugin.RdZurg.Streaming;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RdZurg.Library;

/// <summary>
/// Builds a Jellyfin library out of a Real-Debrid account.
/// </summary>
/// <remarks>
/// Items carry an http path pointing at this server's own resolver, so nothing is written to disk
/// and nothing is scanned. Jellyfin leaves them alone during a library scan because its scanner only
/// enumerates, and only reaps, items whose protocol is <c>File</c>.
/// </remarks>
public sealed class LibrarySync
{
    /// <summary>The provider id under which an item remembers which Real-Debrid link it came from.</summary>
    public const string LinkProviderId = "RdZurgLink";

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ReleaseNames _names = new();

    /// <summary>Initializes a new instance of the <see cref="LibrarySync"/> class.</summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="providerManager">Jellyfin's provider manager.</param>
    /// <param name="fileSystem">Jellyfin's filesystem abstraction.</param>
    /// <param name="httpClientFactory">Factory for outbound requests.</param>
    /// <param name="logger">Logger.</param>
    public LibrarySync(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IHttpClientFactory httpClientFactory,
        ILogger logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Runs one pass over the account.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the pass did.</returns>
    public async Task<SyncResult> RunAsync(
        PluginConfiguration config,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(progress);

        var builder = new LibraryBuilder(_libraryManager, _logger);

        var movieFolder = await builder.EnsureLibraryAsync(
            config.MovieLibraryName,
            MovieLibraryPath(config),
            CollectionTypeOptions.movies,
            cancellationToken).ConfigureAwait(false);

        var showFolder = await builder.EnsureLibraryAsync(
            config.ShowLibraryName,
            ShowLibraryPath(config),
            CollectionTypeOptions.tvshows,
            cancellationToken).ConfigureAwait(false);

        if (movieFolder is null || showFolder is null)
        {
            return new SyncResult();
        }

        var client = new RealDebridClient(_httpClientFactory.CreateClient(), config.ApiKey, config.MinRequestIntervalMs);
        var torrents = await client.GetTorrentsAsync(config.MaxTorrents, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Real-Debrid reported {Count} torrents", torrents.Count);

        var known = ExistingItemsByLink(movieFolder, showFolder);
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);

        var seriesByName = new Dictionary<string, Series>(StringComparer.OrdinalIgnoreCase);
        var seasonsBySeries = new Dictionary<Guid, Dictionary<int, Season>>();

        // Seeded from what is already there, not empty. A byte-identical re-add is only recognised
        // by having seen the release before, and "before" has to include previous runs: seeded
        // per-pass, the copy dropped today is added tomorrow, once its twin counts as known and the
        // torrent-level check stops the pass ever reaching the comparison.
        var seenReleases = ExistingReleases(known.Values);

        var newMovies = new List<BaseItem>();
        var episodesAdded = 0;
        var duplicatesSkipped = 0;
        var alreadyKnown = 0;
        var index = 0;

        foreach (var torrent in torrents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(++index * 90.0 / Math.Max(torrents.Count, 1));

            if (!string.Equals(torrent.Status, "downloaded", StringComparison.OrdinalIgnoreCase) || torrent.Links.Count == 0)
            {
                continue;
            }

            var keys = torrent.Links.Select(RealDebridClient.LinkKey).ToList();
            foreach (var key in keys)
            {
                liveKeys.Add(key);
            }

            // The listing already carries the links, so recognising a torrent costs nothing and the
            // per-torrent detail call, which is the expensive half, only happens for new arrivals.
            //
            // One known link is enough. A torrent's links cover every selected file including the
            // subtitles and sample clips that never become items, so requiring all of them to be
            // known would recognise almost nothing. A downloaded torrent's contents never change,
            // so having seen any of it means having seen all of it.
            if (keys.Exists(known.ContainsKey))
            {
                alreadyKnown++;
                continue;
            }

            RdTorrentInfo? info;
            try
            {
                info = await client.GetTorrentInfoAsync(torrent.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (RealDebridRefusedException ex)
            {
                _logger.LogWarning(ex, "Stopping this pass early");
                break;
            }

            if (info is null)
            {
                continue;
            }

            var selected = info.Files.Where(f => f.Selected == 1).ToList();
            if (selected.Count == 0 || selected.Count != info.Links.Count)
            {
                _logger.LogDebug(
                    "Skipping {Torrent}: {Selected} selected files against {Links} links",
                    info.Filename,
                    selected.Count,
                    info.Links.Count);
                continue;
            }

            var videos = selected
                .Select((file, i) => (File: file, Link: info.Links[i]))
                .Where(x => ReleaseNames.IsVideo(x.File.Path))
                .OrderByDescending(x => x.File.Bytes)
                .ToList();

            if (videos.Count == 0)
            {
                continue;
            }

            // A season pack is one torrent holding many episodes, so every video file is offered to
            // the episode parser. Only when none of them is an episode does the biggest file become
            // a movie.
            var anyEpisode = false;

            foreach (var video in videos)
            {
                var episode = _names.ParseEpisode(info.Filename, video.File.Path);
                if (episode is null)
                {
                    continue;
                }

                anyEpisode = true;

                if (AddEpisode(builder, showFolder, seriesByName, seasonsBySeries, known, config, episode, video.Link, video.File.Path, video.File.Bytes))
                {
                    episodesAdded++;
                }
            }

            if (anyEpisode)
            {
                continue;
            }

            var primary = videos[0];

            // Real-Debrid does not dedupe by infohash, so the same release is routinely present
            // twice under different hashes with byte-identical content. Those are noise.
            var releaseKey = ReleaseFingerprint(Path.GetFileName(primary.File.Path), primary.File.Bytes);

            if (!seenReleases.Add(releaseKey))
            {
                duplicatesSkipped++;
                continue;
            }

            var movie = BuildMovie(builder, movieFolder, known, config, primary.Link, primary.File.Path, primary.File.Bytes, info.Filename);
            if (movie is not null)
            {
                newMovies.Add(movie);
            }
        }

        if (newMovies.Count > 0)
        {
            _libraryManager.CreateItems(newMovies, movieFolder, cancellationToken);
        }

        var versionsMerged = config.MergeDuplicateVersions
            ? await MergeVersionsAsync(movieFolder, cancellationToken).ConfigureAwait(false)
            : 0;

        var removed = config.RemoveVanishedItems
            ? RemoveVanished(known, liveKeys)
            : 0;

        progress.Report(95);
        QueueMetadata(newMovies, seriesByName.Values);
        progress.Report(100);

        return new SyncResult
        {
            TorrentsSeen = torrents.Count,
            TorrentsAlreadyKnown = alreadyKnown,
            MoviesAdded = newMovies.Count,
            EpisodesAdded = episodesAdded,
            SeriesTouched = seriesByName.Count,
            DuplicatesSkipped = duplicatesSkipped,
            VersionsMerged = versionsMerged,
            ItemsRemoved = removed
        };
    }

    private static string MovieLibraryPath(PluginConfiguration config)
        => Path.Combine(Plugin.Instance!.DataPath, "rd-zurg", "movies");

    private static string ShowLibraryPath(PluginConfiguration config)
        => Path.Combine(Plugin.Instance!.DataPath, "rd-zurg", "shows");

    /// <summary>Fingerprints what the library already holds, so a re-add is recognised across runs.</summary>
    private static HashSet<string> ExistingReleases(IEnumerable<BaseItem> items)
    {
        var releases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (item.Size is null || string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            releases.Add(ReleaseFingerprint(Uri.UnescapeDataString(Path.GetFileName(item.Path)), item.Size.Value));
        }

        return releases;
    }

    private static string ReleaseFingerprint(string fileName, long bytes)
        => string.Format(CultureInfo.InvariantCulture, "{0}|{1}", fileName, bytes);

    /// <summary>Indexes what the library already holds by the link each item came from.</summary>
    private Dictionary<string, BaseItem> ExistingItemsByLink(Folder movieFolder, Folder showFolder)
    {
        // ProviderIds has to be asked for by name. BaseItemRepository.ApplyNavigations only joins
        // the provider table when the query's DtoOptions contain that field, so a query without it
        // returns every item with an empty ProviderIds and the whole library reads as unknown.
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            TopParentIds = new[] { movieFolder.Id, showFolder.Id },
            Recursive = true,
            DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(false)
            {
                Fields = new[] { ItemFields.ProviderIds }
            }
        };

        var map = new Dictionary<string, BaseItem>(StringComparer.Ordinal);

        foreach (var item in _libraryManager.GetItemList(query))
        {
            var key = item.GetProviderId(LinkProviderId);
            if (!string.IsNullOrEmpty(key))
            {
                map[key] = item;
            }
        }

        return map;
    }

    private Movie? BuildMovie(
        LibraryBuilder builder,
        Folder folder,
        Dictionary<string, BaseItem> known,
        PluginConfiguration config,
        string link,
        string filePath,
        long bytes,
        string torrentName)
    {
        var key = RealDebridClient.LinkKey(link);
        if (known.ContainsKey(key))
        {
            return null;
        }

        var url = LinkResolver.BuildUrl(config.PublicBaseUrl, key, filePath);
        var id = builder.IdFor(url, typeof(Movie));

        if (_libraryManager.GetItemById(id) is not null)
        {
            return null;
        }

        var parsed = _libraryManager.ParseName(ReleaseNames.Humanise(Path.GetFileNameWithoutExtension(filePath)));
        var title = string.IsNullOrWhiteSpace(parsed.Name) ? ReleaseNames.Humanise(torrentName) : parsed.Name;

        var movie = new Movie
        {
            Id = id,
            Name = title,
            ProductionYear = parsed.Year,
            Path = url,
            ParentId = folder.Id,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow,
            Size = bytes,
            IsVirtualItem = false,

            // Movie.GetLookupInfo() replaces the search name with the containing folder's name,
            // because Jellyfin's convention is "Movies/Absolute Power (1997)/file.mkv". Our
            // containing folder is a URL segment, so without this every lookup asks TMDB to find a
            // film called something like "3BWN4XEKCZMJQ". It is also true in substance: one
            // endpoint serves every unrelated title in the library.
            IsInMixedFolder = true,

            // Jellyfin fills this during a metadata refresh, but /Items groups on it, so until then
            // every unrefreshed item collapses into a single visible row.
            PresentationUniqueKey = id.ToString("N", CultureInfo.InvariantCulture)
        };

        movie.SetProviderId(LinkProviderId, key);
        return movie;
    }

    private bool AddEpisode(
        LibraryBuilder builder,
        Folder showFolder,
        Dictionary<string, Series> seriesByName,
        Dictionary<Guid, Dictionary<int, Season>> seasonsBySeries,
        Dictionary<string, BaseItem> known,
        PluginConfiguration config,
        Emby.Naming.TV.EpisodeInfo parsed,
        string link,
        string filePath,
        long bytes)
    {
        var key = RealDebridClient.LinkKey(link);
        if (known.ContainsKey(key))
        {
            return false;
        }

        var raw = ReleaseNames.Humanise(parsed.SeriesName!);
        var seriesName = _libraryManager.ParseName(raw).Name;
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            seriesName = raw;
        }

        var series = GetOrCreateSeries(builder, showFolder, seriesByName, seriesName);
        var season = GetOrCreateSeason(builder, series, seasonsBySeries, parsed.SeasonNumber!.Value);

        var episodeNumber = parsed.EpisodeNumber!.Value;
        var url = LinkResolver.BuildUrl(config.PublicBaseUrl, key, filePath);
        var id = builder.IdFor(url, typeof(Episode));

        if (_libraryManager.GetItemById(id) is not null)
        {
            return false;
        }

        var episode = new Episode
        {
            Id = id,
            Name = string.Format(CultureInfo.InvariantCulture, "Episode {0}", episodeNumber),
            IndexNumber = episodeNumber,
            IndexNumberEnd = parsed.EndingEpisodeNumber,
            ParentIndexNumber = season.IndexNumber,
            Path = url,
            Size = bytes,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow,
            IsVirtualItem = false,
            SeasonId = season.Id,
            SeasonName = season.Name,
            SeriesId = series.Id,
            SeriesName = series.Name,
            SeriesPresentationUniqueKey = series.GetPresentationUniqueKey()
        };

        episode.PresentationUniqueKey = episode.CreatePresentationUniqueKey();
        episode.SetProviderId(LinkProviderId, key);
        season.AddChild(episode);
        return true;
    }

    private Series GetOrCreateSeries(LibraryBuilder builder, Folder showFolder, Dictionary<string, Series> cache, string name)
    {
        if (cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var id = builder.IdFor(LibraryBuilder.SeriesKey(name), typeof(Series));

        if (_libraryManager.GetItemById(id) is Series existing)
        {
            cache[name] = existing;
            return existing;
        }

        var series = new Series
        {
            Id = id,
            Name = name,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow,
            IsVirtualItem = false
        };

        series.PresentationUniqueKey = series.CreatePresentationUniqueKey();
        showFolder.AddChild(series);
        cache[name] = series;
        return series;
    }

    private Season GetOrCreateSeason(LibraryBuilder builder, Series series, Dictionary<Guid, Dictionary<int, Season>> cache, int seasonNumber)
    {
        if (!cache.TryGetValue(series.Id, out var seasons))
        {
            seasons = new Dictionary<int, Season>();
            cache[series.Id] = seasons;
        }

        if (seasons.TryGetValue(seasonNumber, out var cached))
        {
            return cached;
        }

        var id = builder.IdFor(LibraryBuilder.SeasonKey(series.Id, seasonNumber), typeof(Season));

        if (_libraryManager.GetItemById(id) is not Season season)
        {
            season = new Season
            {
                Id = id,
                Name = string.Format(CultureInfo.InvariantCulture, "Season {0}", seasonNumber),
                IndexNumber = seasonNumber,
                DateCreated = DateTime.UtcNow,
                DateModified = DateTime.UtcNow,
                IsVirtualItem = false,
                SeriesId = series.Id,
                SeriesName = series.Name,
                SeriesPresentationUniqueKey = series.GetPresentationUniqueKey()
            };

            season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
            series.AddChild(season);
        }

        seasons[seasonNumber] = season;
        return season;
    }

    /// <summary>
    /// Folds releases of one film into a single item with the rest behind it as versions.
    /// </summary>
    /// <remarks>
    /// This mirrors the Merge Versions button in the web UI. Different releases of a film are real
    /// alternatives worth keeping; what they are not is different films. Runs over the whole library
    /// rather than the new arrivals so a release added today joins the item created last week.
    /// </remarks>
    private async Task<int> MergeVersionsAsync(Folder movieFolder, CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            TopParentIds = new[] { movieFolder.Id },
            Recursive = true,
            DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(false)
        };

        var merged = 0;

        var groups = _libraryManager.GetItemList(query)
            .OfType<Movie>()
            .GroupBy(
                m => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1}",
                    m.Name,
                    m.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "?"),
                StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The biggest file keeps the poster: it is the version most likely to be the best source.
            var ordered = group.OrderByDescending(m => m.Size ?? 0).ThenBy(m => m.Id).ToList();
            var primary = ordered[0];
            var alternates = ordered.Skip(1).Where(m => m.PrimaryVersionId != primary.Id).ToList();

            if (alternates.Count == 0)
            {
                continue;
            }

            foreach (var alternate in alternates)
            {
                alternate.SetPrimaryVersionId(primary.Id);
                await alternate.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                merged++;
            }

            primary.LinkedAlternateVersions = ordered
                .Skip(1)
                .Select(a => new LinkedChild { ItemId = a.Id, Type = LinkedChildType.LinkedAlternateVersion })
                .ToArray();

            primary.SetPrimaryVersionId(null);
            await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        return merged;
    }

    /// <summary>Deletes items whose link is no longer in the account.</summary>
    private int RemoveVanished(Dictionary<string, BaseItem> known, HashSet<string> liveKeys)
    {
        var removed = 0;

        foreach (var (key, item) in known)
        {
            if (liveKeys.Contains(key))
            {
                continue;
            }

            _logger.LogInformation("Removing {Name}: its torrent is gone from Real-Debrid", item.Name);

            _libraryManager.DeleteItem(
                item,
                new DeleteOptions { DeleteFileLocation = false },
                item.GetParent(),
                false);

            removed++;
        }

        return removed;
    }

    private void QueueMetadata(IEnumerable<BaseItem> movies, IEnumerable<Series> series)
    {
        // Only the primaries: an alternate version inherits the item it hangs off, and refreshing
        // each one separately is a pile of lookups for metadata nobody sees.
        var targets = movies
            .OfType<Movie>()
            .Where(m => !m.PrimaryVersionId.HasValue)
            .Cast<BaseItem>()
            .Concat(series);

        foreach (var item in targets)
        {
            _providerManager.QueueRefresh(
                item.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh
                },
                RefreshPriority.Low);
        }
    }
}
