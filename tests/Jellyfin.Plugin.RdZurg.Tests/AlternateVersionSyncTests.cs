using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.RdZurg.Library;
using Jellyfin.Plugin.RdZurg.Streaming;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
    private static readonly Lazy<LibraryFixture> Captured = new(() => LibraryFixture.Load("rd-library-1.0.2.0.json"));

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
                LibraryReplay.JellyfinItemId("rd-zurg-movie:" + item.Key, typeof(Movie)) == item.Id);
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
        var replay = new LibraryReplay(Captured.Value);

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
        var replay = new LibraryReplay(Captured.Value);
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
        var replay = new LibraryReplay(Captured.Value);

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
        var replay = new LibraryReplay(Captured.Value);

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
        var replay = new LibraryReplay(Captured.Value);
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
        var replay = new LibraryReplay(Captured.Value);
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
        var parsed = LibraryReplay.JellyfinParseName(LibrarySync.ReleaseNameFromPath(item.Path)!);
        return LibrarySync.MayMerge(parsed.Name, parsed.Year);
    }
}
