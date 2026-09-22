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
    private readonly HashSet<Guid> _repairedProviderIds = new();

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
        config.Validate();

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
            throw new InvalidOperationException("Could not establish the RD zurg library folders.");
        }

        using var apiHttp = _httpClientFactory.CreateClient();
        var client = new RealDebridClient(apiHttp, config.ApiKey, config.MinRequestIntervalMs);
        var torrents = await client.GetTorrentsAsync(config.MaxTorrents, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Real-Debrid reported {Count} torrents", torrents.Count);

        var leftovers = new List<BaseItem>();
        var known = ExistingItemsByLink(movieFolder, showFolder, leftovers);
        var leftoversRemoved = 0;

        if (leftovers.Count > 0)
        {
            foreach (var leftover in leftovers)
            {
                _logger.LogInformation(
                    "Removing {Name} ({Id}): an earlier build added the same file under another id",
                    leftover.Name,
                    leftover.Id);
                await DeleteOwnedItemAsync(leftover, cancellationToken).ConfigureAwait(false);
                leftoversRemoved++;
            }

            // Deleting an item rewrites the versions filed around it, so what was read before is stale.
            known = ExistingItemsByLink(movieFolder, showFolder, new List<BaseItem>());
        }

        var episodesRefiled = await RefileMisreadEpisodesAsync(torrents, known, cancellationToken).ConfigureAwait(false);
        var yearsCorrected = await CorrectResolutionYearsAsync(known.Values, cancellationToken).ConfigureAwait(false);

        // Build liveness from the complete listing, before any detail requests can fail.
        // Pending or temporarily errored torrents also retain their existing links.
        var liveKeys = new HashSet<string>(torrents.SelectMany(t => t.Links).Select(RealDebridClient.LinkKey), StringComparer.Ordinal);
        await UpdatePlaybackUrlsAsync(known.Values, config, cancellationToken).ConfigureAwait(false);

        var seriesByName = new Dictionary<string, Series>(StringComparer.OrdinalIgnoreCase);
        var seasonsBySeries = new Dictionary<Guid, Dictionary<int, Season>>();

        // Seeded from what is already there, not empty. A byte-identical re-add is only recognised
        // by having seen the release before, and "before" has to include previous runs: seeded
        // per-pass, the copy dropped today is added tomorrow, once its twin counts as known and the
        // torrent-level check stops the pass ever reaching the comparison.
        var seenReleases = ExistingReleases(known.Where(p => liveKeys.Contains(p.Key)).Select(p => p.Value));

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
            // All links must be accounted for before skipping a torrent. Otherwise cancellation
            // after the first episode leaves the rest of a season pack permanently missing.
            // Packs containing non-video files may need a detail call again on later passes.
            if (keys.All(known.ContainsKey))
            {
                alreadyKnown++;
                continue;
            }

            // Fail the task on provider errors; do not report a partial pass as successful.
            var info = await client.GetTorrentInfoAsync(torrent.Id, cancellationToken).ConfigureAwait(false);

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

        // A limited listing cannot prove that an older torrent was deleted.
        var removed = config.RemoveVanishedItems && config.MaxTorrents == 0
            ? await RemoveVanishedAsync(known, liveKeys, cancellationToken).ConfigureAwait(false)
            : 0;

        var (versionsMerged, versionsReleased) = config.MergeDuplicateVersions
            ? await MergeVersionsAsync(movieFolder, cancellationToken).ConfigureAwait(false)
            : (0, 0);

        progress.Report(95);
        QueueMetadata(newMovies.Concat(yearsCorrected), seriesByName.Values);
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
            VersionsReleased = versionsReleased,
            ItemsRemoved = removed,
            LeftoversRemoved = leftoversRemoved,
            EpisodesRefiled = episodesRefiled,
            YearsCorrected = yearsCorrected.Count
        };
    }

    private async Task UpdatePlaybackUrlsAsync(IEnumerable<BaseItem> items, PluginConfiguration config, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            if (!Uri.TryCreate(item.Path, UriKind.Absolute, out var old)) continue;
            var name = Uri.UnescapeDataString(Path.GetFileName(old.AbsolutePath));
            var url = LinkResolver.BuildUrl(config.PublicBaseUrl, item.GetProviderId(LinkProviderId)!, name);
            if (item.Path == url && !_repairedProviderIds.Contains(item.Id)) continue;
            item.Path = url;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
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

            releases.Add(ReleaseFingerprint(Uri.UnescapeDataString(Path.GetFileName(new Uri(item.Path).AbsolutePath)), item.Size.Value));
        }

        return releases;
    }

    private static string ReleaseFingerprint(string fileName, long bytes)
        => string.Format(CultureInfo.InvariantCulture, "{0}|{1}", fileName, bytes);

    /// <summary>Builds the query every pass over this plugin's own items goes through.</summary>
    /// <param name="kinds">The item kinds to return.</param>
    /// <param name="topParentIds">The library folders to look under.</param>
    /// <returns>The query.</returns>
    /// <remarks>
    /// <para>
    /// ProviderIds has to be asked for by name. BaseItemRepository.ApplyNavigations only joins the
    /// provider table when the query's DtoOptions contain that field, so a query without it returns
    /// every item with an empty ProviderIds and the whole library reads as unknown. Settings also loads
    /// metadata field locks, which must survive subsequent item updates.
    /// </para>
    /// <para>
    /// IncludeOwnedItems is what makes alternate versions visible. Without it ApplyAccessFiltering
    /// drops every item with a PrimaryVersionId, and a version nothing can see is never given a new
    /// playback URL, never removed when its torrent goes, and re-examined against Real-Debrid on every
    /// pass. The merge pass then found each film alone and saved it with no versions: on a real library
    /// 19 of 191 films still named any of their 623 alternates. Walking a film's links to its versions
    /// cannot recover that, because the links are what was lost.
    /// </para>
    /// </remarks>
    public static InternalItemsQuery OwnedItemsQuery(BaseItemKind[] kinds, params Guid[] topParentIds)
        => new()
        {
            IncludeItemTypes = kinds,
            TopParentIds = topParentIds,
            Recursive = true,
            IncludeOwnedItems = true,
            DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(false)
            {
                Fields = new[] { ItemFields.ProviderIds, ItemFields.Settings }
            }
        };

    /// <summary>Indexes what the library already holds by the link each item came from.</summary>
    /// <param name="movieFolder">The movie library folder.</param>
    /// <param name="showFolder">The show library folder.</param>
    /// <param name="leftovers">Receives items repeating a link that another item holds under its proper id.</param>
    /// <returns>The items, by link.</returns>
    private Dictionary<string, BaseItem> ExistingItemsByLink(Folder movieFolder, Folder showFolder, List<BaseItem> leftovers)
    {
        var query = OwnedItemsQuery(new[] { BaseItemKind.Movie, BaseItemKind.Episode }, movieFolder.Id, showFolder.Id);
        var map = new Dictionary<string, BaseItem>(StringComparer.Ordinal);

        foreach (var item in _libraryManager.GetItemList(query))
        {
            var key = item.GetProviderId(LinkProviderId);
            // Older builds saved version merges without loading ProviderIds, which erased this
            // association. Recover only paths pointing at this plugin's exact stream route.
            if (string.IsNullOrEmpty(key) && Uri.TryCreate(item.Path, UriKind.Absolute, out var uri))
            {
                var marker = uri.AbsolutePath.IndexOf("/RdZurg/Stream/", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    var candidate = uri.AbsolutePath[(marker + "/RdZurg/Stream/".Length)..].Split('/')[0];
                    if (StreamAccess.IsValidKey(candidate))
                    {
                        key = candidate;
                        item.SetProviderId(LinkProviderId, key);
                        _repairedProviderIds.Add(item.Id);
                    }
                }
            }
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (!map.TryGetValue(key, out var held))
            {
                map[key] = item;
                continue;
            }

            // Two items for one link. 1.0.0.0 derived an item's id from its playback URL and every
            // later build derives it from the link. A film 1.0.0.0 filed as an alternate version was
            // invisible to the next pass, which added the file again under the new id, so an upgraded
            // library holds both - measured on one, 327 films twice, the older copy still on its
            // unsigned URL. Only the item under the id this build gives the link is kept.
            if (item.Id == ItemIdFor(item, key))
            {
                leftovers.Add(held);
                map[key] = item;
            }
            else if (held.Id == ItemIdFor(held, key))
            {
                leftovers.Add(item);
            }
            else
            {
                _logger.LogWarning(
                    "{First} and {Second} both claim link {Key} and neither has the id this build gives it, so both are kept",
                    held.Id,
                    item.Id,
                    key);
            }
        }

        return map;
    }

    private Guid ItemIdFor(BaseItem item, string key)
        => item is Episode
            ? _libraryManager.GetNewItemId(EpisodeIdKey(key), typeof(Episode))
            : _libraryManager.GetNewItemId(MovieIdKey(key), typeof(Movie));

    private static string MovieIdKey(string key) => "rd-zurg-movie:" + key;

    private static string EpisodeIdKey(string key) => "rd-zurg-episode:" + key;

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
        var id = builder.IdFor(MovieIdKey(key), typeof(Movie));

        if (_libraryManager.GetItemById(id) is not null)
        {
            return null;
        }

        var parsed = ReleaseNames.ParseName(_libraryManager, ReleaseNames.Humanise(Path.GetFileNameWithoutExtension(filePath)));
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
        known[key] = movie;
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
        var seriesName = ReleaseNames.ParseName(_libraryManager, raw).Name;
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            seriesName = raw;
        }

        var series = GetOrCreateSeries(builder, showFolder, seriesByName, seriesName);
        var season = GetOrCreateSeason(builder, series, seasonsBySeries, parsed.SeasonNumber!.Value);

        var episodeNumber = parsed.EpisodeNumber!.Value;
        var url = LinkResolver.BuildUrl(config.PublicBaseUrl, key, filePath);
        var id = builder.IdFor(EpisodeIdKey(key), typeof(Episode));

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
        known[key] = episode;
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

    /// <summary>Reads the release name back out of an item's playback URL.</summary>
    /// <param name="path">The item's path.</param>
    /// <returns>The release name, or <c>null</c> when the path is not one of ours.</returns>
    /// <remarks>
    /// The path is written by this plugin and is never touched by a metadata provider, so it is the
    /// only durable record of what the release actually called itself.
    /// </remarks>
    public static string? ReleaseNameFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Uri.TryCreate(path, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.AbsolutePath.Contains("/RdZurg/Stream/", StringComparison.Ordinal))
        {
            return null;
        }

        var file = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        return string.IsNullOrEmpty(file) ? null : ReleaseNames.Humanise(Path.GetFileNameWithoutExtension(file));
    }

    /// <summary>
    /// Whether two releases sharing this title may be folded into one film.
    /// </summary>
    /// <param name="name">The title parsed from the release name.</param>
    /// <param name="productionYear">The year parsed from the release name.</param>
    /// <returns>Whether the pair is safe to merge.</returns>
    /// <remarks>
    /// A year is what makes the grouping safe, and it must come from the <em>release name</em>.
    /// Reading it off the item instead looks equivalent and is not: absolute-numbered anime such as
    /// "One Piece - 1004" carries no season marker and no year, so every episode lands as its own
    /// movie under the show's name; TMDb then matches the series and writes a ProductionYear onto
    /// every one of them, after which they share a title and a year and any item-based guard
    /// passes. Measured on a real account, that folded 155 One Piece episodes into a single film.
    /// </remarks>
    public static bool MayMerge(string? name, int? productionYear)
        => !string.IsNullOrWhiteSpace(name) && productionYear.HasValue;

    private (string? Name, int? Year) ReleaseIdentity(string? path)
    {
        var release = ReleaseNameFromPath(path);

        if (release is null)
        {
            return (null, null);
        }

        var parsed = ReleaseNames.ParseName(_libraryManager, release);
        return (parsed.Name, parsed.Year);
    }

    /// <summary>
    /// Folds releases of one film into a single item with the rest behind it as versions.
    /// </summary>
    /// <param name="movieFolder">The movie library folder.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many releases were folded in, and how many earlier merges were let go of.</returns>
    /// <remarks>
    /// <para>
    /// This mirrors the Merge Versions button in the web UI. Different releases of a film are real
    /// alternatives worth keeping; what they are not is different films. Runs over the whole library
    /// rather than the new arrivals so a release added today joins the item created last week.
    /// </para>
    /// <para>
    /// A film's versions are rebuilt from what groups with it on every pass, so a film whose links were
    /// erased gets them back. A release that may not be merged at all is let go of if an earlier build
    /// filed it as a version or gave it versions. That cannot flap: the grouping reads the release name
    /// in the path this plugin wrote, and no metadata refresh touches it.
    /// </para>
    /// </remarks>
    private async Task<(int Merged, int Released)> MergeVersionsAsync(Folder movieFolder, CancellationToken cancellationToken)
    {
        var merged = 0;
        var released = 0;

        // Grouped on what this plugin parsed out of the release name, never on the item's current
        // Name and ProductionYear. A metadata provider rewrites those - see MayMerge.
        var movies = _libraryManager.GetItemList(OwnedItemsQuery(new[] { BaseItemKind.Movie }, movieFolder.Id))
            .OfType<Movie>()
            .Where(m => !string.IsNullOrEmpty(m.GetProviderId(LinkProviderId)))
            .ToList();
        var identities = new Dictionary<Guid, (string? Name, int? Year)>();

        foreach (var movie in movies)
        {
            identities[movie.Id] = ReleaseIdentity(movie.Path) is var parsed && parsed.Name is not null
                ? parsed
                : (movie.Name, movie.ProductionYear);
        }

        // A release that may not be merged is a group of one. The two key shapes cannot collide: only
        // the mergeable one contains a separator.
        var groups = movies.GroupBy(
            m => MayMerge(identities[m.Id].Name, identities[m.Id].Year)
                ? string.Format(CultureInfo.InvariantCulture, "{0}|{1}", identities[m.Id].Name, identities[m.Id].Year)
                : m.Id.ToString("N", CultureInfo.InvariantCulture),
            StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The biggest file keeps the poster: it is the version most likely to be the best source.
            var ordered = group.OrderByDescending(m => m.Size ?? 0).ThenBy(m => m.Id).ToList();
            var primary = ordered[0];

            foreach (var alternate in ordered.Skip(1))
            {
                // A version names no versions of its own; one that does was filed as a film until now.
                if (alternate.PrimaryVersionId == primary.Id && alternate.LinkedAlternateVersions.Length == 0)
                {
                    continue;
                }

                if (alternate.PrimaryVersionId != primary.Id)
                {
                    merged++;
                }

                alternate.SetPrimaryVersionId(primary.Id);
                alternate.LinkedAlternateVersions = Array.Empty<LinkedChild>();
                await alternate.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            var versions = ordered.Skip(1).Select(a => a.Id).ToHashSet();
            var linked = primary.LinkedAlternateVersions.Where(l => l.ItemId.HasValue).Select(l => l.ItemId!.Value).ToList();

            if (!primary.PrimaryVersionId.HasValue && linked.Count == versions.Count && versions.SetEquals(linked))
            {
                continue;
            }

            if (ordered.Count == 1)
            {
                released++;
            }

            primary.LinkedAlternateVersions = ordered
                .Skip(1)
                .Select(a => new LinkedChild { ItemId = a.Id, Type = LinkedChildType.LinkedAlternateVersion })
                .ToArray();

            primary.SetPrimaryVersionId(null);
            await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        return (merged, released);
    }

    /// <summary>Deletes items whose link is no longer in the account.</summary>
    private async Task<int> RemoveVanishedAsync(Dictionary<string, BaseItem> known, HashSet<string> liveKeys, CancellationToken cancellationToken)
    {
        var removed = 0;

        foreach (var (key, item) in known)
        {
            if (liveKeys.Contains(key))
            {
                continue;
            }

            _logger.LogInformation("Removing {Name}: its torrent is gone from Real-Debrid", item.Name);
            await DeleteOwnedItemAsync(item, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        return removed;
    }

    /// <summary>Lets go of films an earlier build filed as episodes, so this pass adds them as films.</summary>
    /// <param name="torrents">The account listing.</param>
    /// <param name="known">The library's items by link, from which refiled items are removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many episodes were let go of.</returns>
    /// <remarks>
    /// <para>
    /// Builds before 1.0.4.0 read a tag such as <c>AC3-2.0</c> or a pack's <c>1999-2021</c> as a season and
    /// episode. A pass never reads a torrent whose links are all known again, so without this those films
    /// would stay one-episode shows for good.
    /// </para>
    /// <para>
    /// Only a torrent whose links hold exactly one item is refiled. A torrent without episodes publishes
    /// its biggest video as a film, so refiling a pack of films would leave one where the library had
    /// several. Such a pack keeps its episodes.
    /// </para>
    /// <para>
    /// This runs before the pass seeds the releases it has seen. Run later, the film would be dropped as a
    /// byte-identical copy of the episode it replaces.
    /// </para>
    /// </remarks>
    private async Task<int> RefileMisreadEpisodesAsync(IEnumerable<RdTorrent> torrents, Dictionary<string, BaseItem> known, CancellationToken cancellationToken)
    {
        var refiled = 0;

        foreach (var torrent in torrents)
        {
            if (!string.Equals(torrent.Status, "downloaded", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var held = torrent.Links
                .Select(RealDebridClient.LinkKey)
                .Distinct(StringComparer.Ordinal)
                .Where(known.ContainsKey)
                .ToList();

            if (held.Count != 1 || known[held[0]] is not Episode episode || !Uri.TryCreate(episode.Path, UriKind.Absolute, out var url))
            {
                continue;
            }

            var fileName = Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath));
            if (!_names.IsMisreadEpisode(torrent.Filename, fileName, episode.ParentIndexNumber, episode.IndexNumber))
            {
                continue;
            }

            _logger.LogInformation(
                "Refiling {Release} as a film: an earlier build filed it as {Series} season {Season} episode {Episode}",
                torrent.Filename,
                episode.SeriesName,
                episode.ParentIndexNumber,
                episode.IndexNumber);

            var seasonId = episode.SeasonId;
            var seriesId = episode.SeriesId;
            await DeleteOwnedItemAsync(episode, cancellationToken).ConfigureAwait(false);
            known.Remove(held[0]);
            RemoveEmptyShowFolders(seasonId, seriesId);
            refiled++;
        }

        return refiled;
    }

    /// <summary>Gives a film back the name and year an earlier build read out of its resolution instead.</summary>
    /// <param name="items">The library's items.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The films corrected, whose metadata has to be looked up again.</returns>
    /// <remarks>
    /// <para>
    /// Builds before 1.0.6.0 filed <c>Fruits Basket 2nd Season - 03 [BD 1920x1080 …]</c> as a film from 1920, and TMDb
    /// matches nothing under that year, so the film keeps the name and year it was filed with for good.
    /// </para>
    /// <para>
    /// A film is corrected only when its name and year are still exactly what Jellyfin's own <c>ParseName</c> reads out
    /// of the release name in its path, the parse reads a different year once the resolution is renamed, nothing has
    /// matched it to a TMDb or IMDb title and neither the item nor its name is locked. So a film a metadata provider or a person has
    /// named is never touched, and a corrected one cannot be corrected again: its year no longer matches the old parse.
    /// Its id stays the same, so its watch state and versions stay with it.
    /// </para>
    /// </remarks>
    private async Task<List<Movie>> CorrectResolutionYearsAsync(IEnumerable<BaseItem> items, CancellationToken cancellationToken)
    {
        var corrected = new List<Movie>();

        foreach (var movie in items.OfType<Movie>())
        {
            var release = ReleaseNameFromPath(movie.Path);
            if (release is null
                || movie.IsLocked
                || movie.LockedFields.Contains(MetadataField.Name)
                || movie.HasProviderId(MetadataProvider.Tmdb)
                || movie.HasProviderId(MetadataProvider.Imdb))
            {
                continue;
            }

            var filed = _libraryManager.ParseName(release);
            var parsed = ReleaseNames.ParseName(_libraryManager, release);

            if (filed.Year == parsed.Year
                || movie.ProductionYear != filed.Year
                || string.IsNullOrWhiteSpace(filed.Name)
                || !string.Equals(movie.Name, filed.Name, StringComparison.Ordinal))
            {
                continue;
            }

            _logger.LogInformation(
                "Refiling {Name} ({Year}) as {NewName} ({NewYear}): an earlier build read the year out of its resolution",
                movie.Name,
                movie.ProductionYear,
                string.IsNullOrWhiteSpace(parsed.Name) ? movie.Name : parsed.Name,
                parsed.Year);

            if (!string.IsNullOrWhiteSpace(parsed.Name))
            {
                movie.Name = parsed.Name;
            }

            movie.ProductionYear = parsed.Year;
            await movie.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            corrected.Add(movie);
        }

        return corrected;
    }

    /// <summary>Removes the season and series a refiled episode left with nothing in them.</summary>
    /// <param name="seasonId">The season the episode was filed under.</param>
    /// <param name="seriesId">The series the episode was filed under.</param>
    private void RemoveEmptyShowFolders(Guid seasonId, Guid seriesId)
    {
        if (_libraryManager.GetItemById(seasonId) is Season season && !HasChildren(season.Id, BaseItemKind.Episode))
        {
            _libraryManager.DeleteItem(season, new DeleteOptions { DeleteFileLocation = false }, season.GetParent(), false);
        }

        if (_libraryManager.GetItemById(seriesId) is Series series && !HasChildren(series.Id, BaseItemKind.Season))
        {
            _libraryManager.DeleteItem(series, new DeleteOptions { DeleteFileLocation = false }, series.GetParent(), false);
        }
    }

    private bool HasChildren(Guid parentId, BaseItemKind kind)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = parentId,
            IncludeItemTypes = new[] { kind },
            IncludeOwnedItems = true,
            Limit = 1
        }).Count > 0;

    /// <summary>Deletes one of this plugin's items without taking the versions filed under it along.</summary>
    /// <remarks>
    /// Jellyfin 12's LibraryManager.DeleteItem deletes a film's linked versions with it whenever their
    /// path is not a file on disk, and every path here is a URL. A release still in the account would
    /// go because the film it was filed under did. So the versions are let go of first, and the merge
    /// pass files them under a film again.
    /// </remarks>
    private async Task DeleteOwnedItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is Video film && !film.PrimaryVersionId.HasValue && film.LinkedAlternateVersions.Length > 0)
        {
            foreach (var link in film.LinkedAlternateVersions)
            {
                if (link.ItemId is { } id && _libraryManager.GetItemById(id) is Video version && version.PrimaryVersionId == film.Id)
                {
                    version.SetPrimaryVersionId(null);
                    await version.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }
            }

            film.LinkedAlternateVersions = Array.Empty<LinkedChild>();
            await film.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        _libraryManager.DeleteItem(
            item,
            new DeleteOptions { DeleteFileLocation = false },
            item.GetParent(),
            false);
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
