using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

/// <summary>
/// What a sync does with films an earlier build filed as episodes, replayed on zen's real library.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class MisreadEpisodeRefileTests
{
    private const string Btcc = "GAUYC6OCEYPFJ";
    private const string Odyssey = "LUDK6TVCV742Q";

    private static readonly string[] MatrixPacks =
    {
        "HIBKDBIZKDF3L", "3TI7MKOEC5PDZ", "MXL6CDYB7FCFA", "ZXFMOPU73EY7J",
        "FTMVTM4ACXPAG", "3AJ5BAEF6DBLW", "7NVB6SICSWOEZ", "LMCLXQYK5P3ZU", "CGXHEWH4ZTFS7"
    };

    [Fact]
    public async Task RefilesASingleFilmFiledAsAnEpisodeAsAFilm()
    {
        var replay = new MisreadEpisodeReplay();
        await replay.SyncAsync();

        foreach (var key in new[] { Btcc, Odyssey })
        {
            Assert.DoesNotContain(replay.Items.OfType<Episode>(), e => MisreadEpisodeReplay.KeyOf(e) == key);
            Assert.Single(replay.Items.OfType<Movie>(), m => MisreadEpisodeReplay.KeyOf(m) == key);
        }

        Assert.Equal(new[] { "EKONQZQ3ZKDS5", "QD53S2Q7PYINM" }, replay.DetailRequests.Order());
    }

    [Fact]
    public async Task RemovesTheShowsARefiledFilmLeavesEmpty()
    {
        var replay = new MisreadEpisodeReplay();
        await replay.SyncAsync();

        var shows = replay.Items.OfType<Series>().Select(s => s.Name).ToList();
        Assert.DoesNotContain("BTCC", shows);
        Assert.DoesNotContain("The-Odyssey", shows);
        Assert.All(replay.Items.OfType<Season>(), season => Assert.Contains(replay.Items.OfType<Episode>(), e => e.ParentId == season.Id));
        Assert.All(replay.Items.OfType<Series>(), series => Assert.Contains(replay.Items.OfType<Season>(), s => s.ParentId == series.Id));
    }

    [Fact]
    public async Task LeavesAPackOfFilmsWhereItIsRatherThanKeepOnlyItsBiggestFile()
    {
        var replay = new MisreadEpisodeReplay();
        await replay.SyncAsync();

        foreach (var key in MatrixPacks)
        {
            Assert.Single(replay.Items.OfType<Episode>(), e => MisreadEpisodeReplay.KeyOf(e) == key);
        }

        Assert.Equal(new[] { Btcc, Odyssey }.Order(), replay.Items.OfType<Movie>().Select(MisreadEpisodeReplay.KeyOf).Order());
    }

    [Fact]
    public async Task LeavesRealEpisodesWhereTheyAre()
    {
        var replay = new MisreadEpisodeReplay();
        var before = replay.Items.OfType<Episode>()
            .Where(e => e.SeriesName is "La que no podía amar" or "Some Day or One Day")
            .Select(e => (e.Id, e.ParentIndexNumber, e.IndexNumber))
            .Order()
            .ToList();

        await replay.SyncAsync();

        Assert.Equal(163, before.Count);
        Assert.Equal(
            before,
            replay.Items.OfType<Episode>()
                .Where(e => e.SeriesName is "La que no podía amar" or "Some Day or One Day")
                .Select(e => (e.Id, e.ParentIndexNumber, e.IndexNumber))
                .Order());
    }

    [Fact]
    public async Task ASecondPassChangesNothing()
    {
        var replay = new MisreadEpisodeReplay();
        await replay.SyncAsync();
        var after = replay.Snapshot();
        replay.DetailRequests.Clear();

        var second = await replay.SyncAsync();

        Assert.Equal(after, replay.Snapshot());
        Assert.Empty(replay.DetailRequests);
        Assert.Equal(0, second.MoviesAdded);
        Assert.Equal(0, second.EpisodesAdded);
    }
}
