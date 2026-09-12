using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Jellyfin.Plugin.RdZurg.Library;
using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

/// <summary>
/// Recognising an item an earlier build filed as an episode, over the real corpus and zen's library.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class MisreadEpisodeTests
{
    private readonly ReleaseNames _names = new();
    private readonly EpisodeResolver _stock = new(new NamingOptions());

    [Fact]
    public async Task CountsTheFilmsItRefiled()
    {
        var replay = new MisreadEpisodeReplay();

        Assert.Equal(2, (await replay.SyncAsync()).EpisodesRefiled);
        Assert.Equal(0, (await replay.SyncAsync()).EpisodesRefiled);
    }

    /// <summary>Every film in the corpus the stock expressions read as an episode is recognised as one.</summary>
    [Fact]
    public void RecognisesEveryFilmTheStockExpressionsFiledAsAnEpisode()
    {
        var films = Rows().Where(r => r.Expected == "-").ToList();
        var missed = films
            .Select(r => (Row: r, Stock: _stock.Resolve($"/{r.Release.Replace('/', '_')}/{Path.GetFileName(r.File)}", false, isOptimistic: false)))
            .Where(p => !_names.IsMisreadEpisode(p.Row.Release, p.Row.File, p.Stock?.SeasonNumber, p.Stock?.EpisodeNumber))
            .Select(p => $"{p.Row.Release} | {p.Row.File}")
            .ToList();

        Assert.Equal(2041, films.Count);
        Assert.True(missed.Count == 0, $"{missed.Count} missed, first: {string.Join(Environment.NewLine, missed.Take(10))}");
    }

    /// <summary>No real episode is ever taken for a misread one, whatever numbers it was filed under.</summary>
    [Fact]
    public void NeverTakesARealEpisodeForAMisreadOne()
    {
        var episodes = Rows().Where(r => r.Expected != "-").ToList();
        var taken = episodes
            .Where(r =>
            {
                var numbers = r.Expected[1..].Split('E');
                return _names.IsMisreadEpisode(r.Release, r.File, int.Parse(numbers[0]), int.Parse(numbers[1]));
            })
            .Select(r => $"{r.Expected}: {r.Release} | {r.File}")
            .ToList();

        Assert.Equal(9028, episodes.Count);
        Assert.True(taken.Count == 0, $"{taken.Count} taken, first: {string.Join(Environment.NewLine, taken.Take(10))}");
    }

    [Theory]
    [InlineData("BTCC.2026.Round13-15.Thruxton.Sunday.Coverage.ITVX.WEB.DL.1080p.h264.English-MWR.mkv", "BTCC.2026.Round13-15.Thruxton.Sunday.Coverage.ITVX.WEB.DL.1080p.h264.English-MWR.mkv", 13, 14)]
    [InlineData("The-Odyssey-2026-1080p-TS-V2-WEB.DL-GP-M-NLsubs.mp4", "The-Odyssey-2026-1080p-TS-V2-WEB.DL-GP-M-NLsubs.mp4", 2026, 1081)]
    public void LeavesAnItemFiledUnderOtherNumbersAlone(string release, string file, int season, int episode)
    {
        Assert.False(_names.IsMisreadEpisode(release, file, season, episode));
    }

    private static IEnumerable<(string Release, string File, string Expected)> Rows()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "episode-corpus.tsv.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var columns = line.Split('\t');
            yield return (columns[0], columns[1], columns[2]);
        }
    }
}
