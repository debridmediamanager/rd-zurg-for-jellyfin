using Jellyfin.Plugin.RdZurg.Library;
using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

public class ReleaseNamesTests
{
    private readonly ReleaseNames _names = new();

    [Theory]
    [InlineData("Sitio.do.Picapau.Amarelo.S03", "/Sitio.do.Picapau.Amarelo.S03E83.1080p.GLBO.WEB.DL.mkv", 3, 83, "Sitio.do.Picapau.Amarelo")]
    [InlineData("Game.of.Thrones.S08.UHD", "/Game.of.Thrones.S08E03.The.Long.Night.2160p.mkv", 8, 3, "Game.of.Thrones")]
    [InlineData("Teen.Wolf.S04.1080p", "/Teen.Wolf.S04.1080p/Teen.Wolf.S04E12.Smoke.and.Mirrors.mkv", 4, 12, "Teen.Wolf")]
    public void ReadsEpisodesOutOfReleaseNames(string torrent, string file, int season, int episode, string series)
    {
        var parsed = _names.ParseEpisode(torrent, file);

        Assert.NotNull(parsed);
        Assert.Equal(season, parsed!.SeasonNumber);
        Assert.Equal(episode, parsed.EpisodeNumber);
        Assert.Equal(series, parsed.SeriesName);
    }

    /// <summary>
    /// The resolver's optimistic expressions read a release year as a season and episode pair, which
    /// turns a shelf of films into a shelf of one-episode shows. These must not parse.
    /// </summary>
    [Theory]
    [InlineData("The.Matrix.Reloaded.2003.2160p.UHD.BluRay", "/The.Matrix.Reloaded.2003.2160p.UHD.BluRay.mkv")]
    [InlineData("Interstellar.2014.2160p.IMAX.REMUX", "/Interstellar.2014.2160p.IMAX.REMUX.mkv")]
    [InlineData("The Matrix 4K", "/The Matrix 4K.mkv")]
    public void DoesNotMistakeAFilmForAnEpisode(string torrent, string file)
    {
        Assert.Null(_names.ParseEpisode(torrent, file));
    }

    [Theory]
    [InlineData("/Some.Release.mkv", true)]
    [InlineData("/Some.Release.mp4", true)]
    [InlineData("/Some.Release.MKV", true)]
    [InlineData("/Some.Release.nfo", false)]
    [InlineData("/Some.Release.rar", false)]
    public void RecognisesVideoFiles(string path, bool expected)
    {
        Assert.Equal(expected, ReleaseNames.IsVideo(path));
    }

    [Fact]
    public void TurnsSeparatorsBackIntoSpaces()
    {
        Assert.Equal("The Matrix Reloaded 2003", ReleaseNames.Humanise("The.Matrix_Reloaded.2003"));
    }
}
