using System;
using System.Globalization;
using System.IO;
using Emby.Naming.Common;
using Emby.Naming.TV;

namespace Jellyfin.Plugin.RdZurg.Library;

/// <summary>
/// Reads identity out of release names, using the same parsers the filesystem scanner uses.
/// </summary>
public sealed class ReleaseNames
{
    private static readonly string[] _videoExtensions =
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov", ".wmv", ".mpg", ".mpeg", ".flv", ".webm"
    };

    private readonly EpisodeResolver _episodes = new(new NamingOptions());

    /// <summary>Reports whether a path looks like a video file worth publishing.</summary>
    /// <param name="path">A path from a torrent's file list.</param>
    /// <returns><c>true</c> when the extension is one of the video extensions.</returns>
    /// <remarks>
    /// An archive-wrapped release still lists its inner name here, so <c>.mkv</c> covers the case
    /// where the link goes on to serve <c>.mkv.rar</c>.
    /// </remarks>
    public static bool IsVideo(string path)
    {
        var extension = Path.GetExtension(path);

        foreach (var candidate in _videoExtensions)
        {
            if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a file as an episode, when it is one.
    /// </summary>
    /// <param name="torrentName">The release name, which stands in for the containing folder.</param>
    /// <param name="filePath">The file's path within the torrent.</param>
    /// <returns>The episode, or <c>null</c> when the file is not one.</returns>
    /// <remarks>
    /// <para>
    /// Two things here are load bearing. Jellyfin's episode expressions take the series name from
    /// the containing folder, so a bare filename resolves a number and no series at all; the
    /// torrent name is synthesized as that folder to give the expressions the shape they expect.
    /// </para>
    /// <para>
    /// And the optimistic expressions are turned off, because they read a release year as a season
    /// and episode pair. Left on, <c>The.Matrix.Reloaded.2003.2160p</c> parses as season 20 episode
    /// 3 and a shelf of films becomes a shelf of one-episode shows.
    /// </para>
    /// </remarks>
    public EpisodeInfo? ParseEpisode(string torrentName, string filePath)
    {
        ArgumentNullException.ThrowIfNull(torrentName);
        ArgumentNullException.ThrowIfNull(filePath);

        var synthesized = string.Format(
            CultureInfo.InvariantCulture,
            "/{0}/{1}",
            torrentName.Replace('/', '_'),
            Path.GetFileName(filePath));

        var parsed = _episodes.Resolve(synthesized, false, isOptimistic: false);

        if (parsed?.SeasonNumber is null || parsed.EpisodeNumber is null || string.IsNullOrWhiteSpace(parsed.SeriesName))
        {
            return null;
        }

        return parsed;
    }

    /// <summary>Turns a dotted release name into something a metadata provider can search for.</summary>
    /// <param name="value">A release name or series name.</param>
    /// <returns>The name with separators turned back into spaces.</returns>
    public static string Humanise(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace('.', ' ').Replace('_', ' ').Trim();
    }
}
