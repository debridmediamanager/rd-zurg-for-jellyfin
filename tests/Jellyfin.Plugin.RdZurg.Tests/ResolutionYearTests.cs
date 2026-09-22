using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.RdZurg.Library;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

/// <summary>
/// Jellyfin's <c>ParseName</c> reads the width of a frame size such as <c>1920x1080</c> as a year.
/// </summary>
/// <remarks>
/// <para>
/// <c>resolution-corpus.tsv.gz</c> is every video file in DMM's RD and AllDebrid availability tables whose release or
/// file name carries a four-digit number from 1900 to 2099, an <c>x</c> and three or four digits, drawn on 2026-09-22
/// less one adult release DMM had filed under a film: 32,189 release and file pairs, each with the IMDb type and start year of the title DMM filed it under
/// (<c>-</c> when there is none). It holds the frame sizes, and the shapes around them that are not frames:
/// <c>Formula.1.2025x126</c>, an episode, and <c>2006x264</c>, a year glued to its codec.
/// </para>
/// <para>
/// Read as films, 24,965 of its 28,509 distinct names took a frame width for their year on Jellyfin's stock
/// expressions. Among those filed under an IMDb film, the year read matched IMDb's for none before this change and
/// for 135 after it; the rest carry no year of their own.
/// </para>
/// </remarks>
public class ResolutionYearTests
{
    // Jellyfin's CleanDateTimes accept a year behind these separators, and a frame's height starts at 600.
    private const string FrameWidth = @"(?<=[^_,.()\[\]\-][ _.()\[\]\-]+){0}[xX](?:[6-9][0-9]{{2}}|[1-9][0-9]{{3}})(?![0-9])";

    private static readonly ILibraryManager Jellyfin = JellyfinParser();

    // Every corpus name read both ways once; the two corpus tests share it.
    private static readonly Lazy<List<(string Name, int? Stock, int? Parsed)>> Readings = new(() => Names()
        .Select(name => (name, Jellyfin.ParseName(name).Year, ReleaseNames.ParseName(Jellyfin, name).Year))
        .ToList());

    /// <summary>
    /// Real names whose resolution Jellyfin read as the year. The Fruits Basket release is filed as a 1920 film in
    /// zen's RD library and the Evangelion one in its AllDebrid library; the rest are from DMM's availability tables.
    /// </summary>
    [Theory]
    [InlineData("[Aenianos] Fruits Basket 2nd Season - 03 [BD 1920x1080 x264 AAC-FLAC] [DUAL-EduvalDxD] [AC12C446]", "Fruits Basket 2nd Season", null)]
    [InlineData("[Beatrice-Raws] Evangelion 1.0 You Are (Not) Alone [BDRip 1920x1080 HEVC TrueHD]", "Evangelion 1 0 You Are (Not) Alone", null)]
    [InlineData("[Arid] Samurai Champloo [Dual-Audio][BDRip 1920x1080 HEVC FLAC]", "Samurai Champloo", null)]
    [InlineData("Forbidden.Zone.1980.1920x1080.BDRip.x264.DTS-HD.MA.Eng", "Forbidden Zone", 1980)]
    [InlineData("Brexit The Uncivil War (2019) BDRip (1920x1080) HEVC", "Brexit The Uncivil War", 2019)]
    [InlineData("Evangelion 2.22 You Can (Not) Advance 2009 [BD 1920x1080 23.976fps AVC-yuv420p10 FLAC]", "Evangelion 2 22 You Can (Not) Advance", 2009)]
    [InlineData("[Pandoratv-raws] Doraemon Movie 1993 [14th] (Nobita to Burikinomeikyu) - (WOWOW Cinema 1920x1080)", "Doraemon Movie", 1993)]
    [InlineData("大开眼戒.Eyes Wide Shut.1999.UK.US.Extended Edition.BluRay.1920x1080p.x264.DTS-KOOK.[中英双字]", "大开眼戒 Eyes Wide Shut", 1999)]
    [InlineData("Some.Release.2019.1920x1080", "Some Release", 2019)]
    public void DoesNotReadAResolutionAsAYear(string release, string name, int? year)
    {
        var parsed = ReleaseNames.ParseName(Jellyfin, ReleaseNames.Humanise(release));

        Assert.Equal(year, parsed.Year);
        Assert.Equal(name, parsed.Name);
    }

    /// <summary>
    /// Names in which a number shaped like a year sits before an <c>x</c> and is not a frame width, all real. Each
    /// reads exactly as Jellyfin reads it.
    /// </summary>
    [Theory]
    [InlineData("MythBusters.2005x17.Jaws.Special.1080p.Rus.Eng")]
    [InlineData("Formula.1.2024x27.Round.05.ChineseGP.Qualifying.International.MULTi.1080p.SS")]
    [InlineData("Formula.1.2025x126.R24.AbuDhabiGP.Race.MULTi.1080p.SS")]
    [InlineData("Rang De Basanti 2006x264 720p Esub BluRay Dual Audio English Hindi GOPISAHI")]
    [InlineData("Mini-Skirt Gang[1974x264 DVDrip(ShawBros)")]
    [InlineData("A.Series.of.Unfortunate.Events.2017.1x01.WEBRip.1080p.x265-KITE-METeam")]
    [InlineData("Blade.Runner.2049.2017.3840x2160")]
    [InlineData("1387_02_popasquealie1920x1080")]
    [InlineData("(1920x1080-gogoanime)dragon-ball-heroes-episode-20")]
    public void ReadsEverythingElseAsJellyfinDoes(string release)
    {
        var name = ReleaseNames.Humanise(release);
        var stock = Jellyfin.ParseName(name);
        var parsed = ReleaseNames.ParseName(Jellyfin, name);

        Assert.Equal((stock.Name, stock.Year), (parsed.Name, parsed.Year));
    }

    /// <summary>
    /// No name in either corpus, read as a film, takes a frame width for its year: the release and the file alike,
    /// since the episode corpus carries the shape only in series rows.
    /// </summary>
    [Fact]
    public void NoNameInTheCorporaReadsAResolutionAsItsYear()
    {
        var readings = Readings.Value;
        var wrong = readings
            .Where(r => r.Parsed is int year && ReadsOnlyAsAFrameWidth(r.Name, year))
            .Select(r => FormattableString.Invariant($"{r.Parsed} <= {r.Name}"))
            .ToList();

        Assert.True(readings.Count > 38000, $"only {readings.Count} names");
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {readings.Count} read a resolution as a year:{Environment.NewLine}{string.Join(Environment.NewLine, wrong.Take(20))}");
    }

    /// <summary>
    /// The only year that changes is one Jellyfin read out of a frame width.
    /// </summary>
    [Fact]
    public void ChangesNoOtherYear()
    {
        var wrong = Readings.Value
            .Where(r => r.Stock != r.Parsed && !(r.Stock is int year && Regex.IsMatch(r.Name, Frame(year))))
            .Select(r => FormattableString.Invariant($"{r.Stock} -> {r.Parsed} <= {r.Name}"))
            .ToList();

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong.Take(20)));
    }

    /// <summary>
    /// A series is found by its name, so a name read differently would start a second series beside the first.
    /// Every series name either corpus yields reads as it did, taking one file from each release: the episode
    /// resolver is slow, and a series name comes from the release far more than from the file.
    /// </summary>
    [Fact]
    public void ReadsEverySeriesNameAsBefore()
    {
        var names = new ReleaseNames();
        var series = Rows("episode-corpus.tsv.gz").Concat(Rows("resolution-corpus.tsv.gz"))
            .DistinctBy(r => r[0], StringComparer.Ordinal)
            .Select(r => names.ParseEpisode(r[0], r[1])?.SeriesName)
            .OfType<string>()
            .Select(ReleaseNames.Humanise)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var renamed = series.Where(s => Jellyfin.ParseName(s).Name != ReleaseNames.ParseName(Jellyfin, s).Name).ToList();

        Assert.True(series.Count > 3500, $"only {series.Count} series names");
        Assert.True(renamed.Count == 0, string.Join(Environment.NewLine, renamed.Take(20)));
    }

    private static bool ReadsOnlyAsAFrameWidth(string name, int year)
        => Regex.IsMatch(name, Frame(year))
            && !Regex.IsMatch(name, FormattableString.Invariant($"(?<![0-9]){year}(?![0-9])(?![xX](?:[6-9][0-9]{{2}}|[1-9][0-9]{{3}})(?![0-9]))"));

    private static string Frame(int year) => string.Format(System.Globalization.CultureInfo.InvariantCulture, FrameWidth, year);

    private static List<string> Names()
        => Rows("episode-corpus.tsv.gz").Concat(Rows("resolution-corpus.tsv.gz"))
            .SelectMany(r => new[] { r[0], Path.GetFileNameWithoutExtension(r[1]) })
            .Select(ReleaseNames.Humanise)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string[]> Rows(string fixture)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line.Split('\t');
        }
    }

    private static ILibraryManager JellyfinParser()
    {
        var manager = new Mock<ILibraryManager>();
        manager.Setup(m => m.ParseName(It.IsAny<string>())).Returns((string name) => LibraryReplay.JellyfinParseName(name));
        return manager.Object;
    }
}
