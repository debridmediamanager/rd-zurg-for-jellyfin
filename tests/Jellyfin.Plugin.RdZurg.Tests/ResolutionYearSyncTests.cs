using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.RdZurg.Library;
using MediaBrowser.Controller.Entities.Movies;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

/// <summary>
/// A film an earlier build filed under the year Jellyfin read out of its resolution, replayed on zen's real library.
/// </summary>
/// <remarks>
/// <para>
/// <c>Fixtures/rd-resolution-year-1.0.5.0.json</c> was read out of zen's jellyfin.db and the RD test account on
/// 2026-09-22, as RD zurg 1.0.5.0 left them. It holds the only film of 1,928 filed under a resolution,
/// <c>[Aenianos] Fruits Basket 2nd Season - 03 [BD 1920x1080 …]</c> as "Fruits Basket 2nd Season" (1920), and three
/// controls: a film TMDb and IMDb matched, an unmatched film with a year of its own, and an episode. Beside them are
/// the listing entries of their torrents and /torrents/info for the 63-file pack the Fruits Basket film came from,
/// which a pass asks about because its other files have no items. Link keys, torrent ids and hashes are stand-ins.
/// </para>
/// <para>
/// No film on zen carried a real year and a resolution, or was locked, so those cases are the name tests' and the
/// lock is set here. Replayed over the whole library of 6,497 items on an isolated copy of zen's server, only the
/// Fruits Basket film changed.
/// </para>
/// </remarks>
[Collection(PluginInstanceCollection.Name)]
public class ResolutionYearSyncTests
{
    private static readonly Lazy<LibraryFixture> Captured = new(() => LibraryFixture.Load("rd-resolution-year-1.0.5.0.json"));

    private static readonly Guid FruitsBasket = Guid.Parse("503E152E-7E99-C40C-FB0C-37CAD61A2F17");

    [Fact]
    public void TheFixtureHoldsTheFilmAsTheEarlierBuildFiledIt()
    {
        var film = Captured.Value.Items.Single(i => i.Id == FruitsBasket);

        Assert.Equal("Fruits Basket 2nd Season", film.Name);
        Assert.Equal(1920, film.ProductionYear);
        Assert.Equal(new[] { "Episode", "Movie", "Movie", "Movie" }, Captured.Value.Items.Select(i => i.Type).Order());
    }

    [Fact]
    public async Task CorrectsTheYearAnEarlierBuildReadOutOfAResolution()
    {
        var replay = new LibraryReplay(Captured.Value);

        var result = await replay.SyncAsync();

        var film = Assert.IsType<Movie>(replay.Get(FruitsBasket));
        Assert.Equal("Fruits Basket 2nd Season", film.Name);
        Assert.Null(film.ProductionYear);
        Assert.Equal(1, result.YearsCorrected);
        Assert.Contains(FruitsBasket, replay.Refreshed);
    }

    /// <summary>
    /// Every other item keeps its name, year, numbering, versions, provider ids and path, and nothing is added or
    /// removed.
    /// </summary>
    [Fact]
    public async Task ChangesNothingElseInTheLibrary()
    {
        var replay = new LibraryReplay(Captured.Value);
        var before = replay.Snapshot();

        var result = await replay.SyncAsync();
        var after = replay.Snapshot();

        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.Equal(
            new[] { FruitsBasket },
            before.Keys.Where(id => before[id] != after[id]).ToArray());
        Assert.Equal(0, result.MoviesAdded + result.ItemsRemoved + result.LeftoversRemoved + result.VersionsMerged + result.VersionsReleased);
        Assert.Equal(new[] { FruitsBasket }, replay.Refreshed);
    }

    [Fact]
    public async Task ASecondPassChangesNothing()
    {
        var replay = new LibraryReplay(Captured.Value);
        await replay.SyncAsync();
        var after = replay.Snapshot();
        replay.Refreshed.Clear();

        var second = await replay.SyncAsync();

        Assert.Equal(after, replay.Snapshot());
        Assert.Equal(0, second.YearsCorrected);
        Assert.Empty(replay.Refreshed);
    }

    /// <summary>
    /// A film that metadata has already matched carries the provider's own name and year, and is left alone even if
    /// they happen to be what the release name reads as.
    /// </summary>
    [Fact]
    public async Task LeavesAFilmAMetadataProviderMatched()
    {
        var replay = new LibraryReplay(Captured.Value);
        var film = Assert.IsType<Movie>(replay.Get(FruitsBasket));
        film.SetProviderId("Tmdb", "1");

        var result = await replay.SyncAsync();

        Assert.Equal(1920, film.ProductionYear);
        Assert.Equal(0, result.YearsCorrected);
    }

    /// <summary>A locked film, or one whose name is locked, is someone's decision and is left alone.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LeavesALockedFilm(bool itemLocked, bool nameLocked)
    {
        var replay = new LibraryReplay(Captured.Value);
        var film = Assert.IsType<Movie>(replay.Get(FruitsBasket));
        film.IsLocked = itemLocked;
        film.LockedFields = nameLocked ? new[] { MetadataField.Name } : Array.Empty<MetadataField>();

        var result = await replay.SyncAsync();

        Assert.Equal(1920, film.ProductionYear);
        Assert.Equal(0, result.YearsCorrected);
    }

    /// <summary>
    /// The same release added today is filed without the year, not corrected afterwards.
    /// </summary>
    [Fact]
    public async Task AFilmAddedNowIsNotFiledUnderItsResolution()
    {
        var replay = new LibraryReplay(Captured.Value);
        replay.Forget(FruitsBasket);

        var result = await replay.SyncAsync();

        var film = Assert.Single(replay.Movies, m => m.GetProviderId(LibrarySync.LinkProviderId) == "MTIVIZEDXBXDM");
        Assert.Equal(1, result.MoviesAdded);
        Assert.Equal("Fruits Basket 2nd Season", film.Name);
        Assert.Null(film.ProductionYear);
    }
}
