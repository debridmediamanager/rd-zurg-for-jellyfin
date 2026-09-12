using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.RdZurg.Library;
using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

/// <summary>
/// Replays real release and file names from DMM's RD and AllDebrid availability tables through the
/// episode parser.
/// </summary>
/// <remarks>
/// <para>
/// Each row of <c>episode-corpus.tsv.gz</c> is a release name, a file path inside it and what the file
/// is, and neither label comes from this parser. A <c>-</c> row is a file Jellyfin's stock expressions
/// read as an episode although IMDb files the release's title as a film, DMM's own parser gave the file
/// no episode and the file name carries no season or episode marker: one file from each of 2,041
/// releases. An <c>SnEm</c> row is a file of an IMDb series on which the stock expressions and DMM's
/// parser agree: 6,000 sampled <c>NxNN</c> names, every range-shaped name without an <c>SxxEyy</c>
/// marker, 3,000 sampled <c>SxxEyy</c> names, and the names the audio channel guard decides.
/// </para>
/// <para>
/// Drawn on 2026-09-13 from 5,063,105 video files (4,242,690 distinct release and file-name pairs).
/// Across all of them the change turned 11,541 files of IMDb films away from being episodes and changed
/// no episode number. The 16,533 files of IMDb series it stopped reading as episodes include none on
/// which DMM's parser agreed with the old reading; they were packs such as <c>Family Guy - season's
/// 1-9</c>, where every file had become S1E9.
/// </para>
/// </remarks>
public class EpisodeCorpusTests
{
    private readonly ReleaseNames _names = new();

    [Fact]
    public void DoesNotReadFilmsAsEpisodes()
    {
        var rows = Rows().Where(r => r.Expected == "-").ToList();
        Assert.Equal(2041, rows.Count);
        AssertReadAsLabelled(rows);
    }

    [Fact]
    public void KeepsEveryEpisodeNumber()
    {
        var rows = Rows().Where(r => r.Expected != "-").ToList();
        Assert.Equal(9028, rows.Count);
        AssertReadAsLabelled(rows);
    }

    private void AssertReadAsLabelled(IReadOnlyCollection<(string Release, string File, string Expected)> rows)
    {
        var wrong = new List<string>();

        foreach (var (release, file, expected) in rows)
        {
            var parsed = _names.ParseEpisode(release, file);
            var read = parsed is null ? "-" : FormattableString.Invariant($"S{parsed.SeasonNumber}E{parsed.EpisodeNumber}");

            if (read != expected)
            {
                wrong.Add($"{expected} read as {read}: {release} | {file}");
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {rows.Count} misread, first: {string.Join(Environment.NewLine, wrong.Take(10))}");
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
