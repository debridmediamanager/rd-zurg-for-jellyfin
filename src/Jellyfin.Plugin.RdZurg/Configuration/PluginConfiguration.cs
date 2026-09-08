using System;
using System.Security.Cryptography;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RdZurg.Configuration;

/// <summary>
/// Everything the plugin needs to build a library out of a Real-Debrid account.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the private signing key for per-file playback capabilities.</summary>
    public string StreamSecret { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Gets or sets whether plain files redirect to the CDN instead of using the server's connection.</summary>
    public bool RedirectDirectStreams { get; set; }

    /// <summary>Gets or sets the Real-Debrid API token.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the URL this server is reachable at.
    /// </summary>
    /// <remarks>
    /// Every injected item's path points at this server's own resolver, so players resolve through
    /// it too. <c>localhost</c> works for server-side transcoding and for nothing else.
    /// </remarks>
    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:8096";

    /// <summary>Gets or sets the name of the library that holds movies.</summary>
    public string MovieLibraryName { get; set; } = "Real-Debrid Movies";

    /// <summary>Gets or sets the name of the library that holds shows.</summary>
    public string ShowLibraryName { get; set; } = "Real-Debrid Shows";

    /// <summary>Gets or sets how many of the newest torrents to take. Zero means all of them.</summary>
    public int MaxTorrents { get; set; }

    /// <summary>Gets or sets the minimum gap between Real-Debrid calls, in milliseconds.</summary>
    public int MinRequestIntervalMs { get; set; } = 300;

    /// <summary>Gets or sets a value indicating whether releases of one title become one item with versions.</summary>
    public bool MergeDuplicateVersions { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether items are deleted once their torrent is gone.</summary>
    public bool RemoveVanishedItems { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether stored RAR archives are served through.</summary>
    public bool UnwrapArchives { get; set; } = true;

    /// <summary>Rejects settings that would create unusable or unsafe library paths.</summary>
    public void Validate()
    {
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Server URL must be an absolute HTTP(S) URL without credentials, a query or a fragment.");
        }

        if (string.IsNullOrWhiteSpace(MovieLibraryName) || string.IsNullOrWhiteSpace(ShowLibraryName)
            || string.Equals(MovieLibraryName.Trim(), ShowLibraryName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Movie and show libraries must have distinct, nonempty names.");
        }

        if (MaxTorrents < 0 || MinRequestIntervalMs < 300 || MinRequestIntervalMs > 60000)
        {
            throw new ArgumentException("Torrent limit must be nonnegative and the API interval must be between 300 and 60000 ms.");
        }

        if (string.IsNullOrEmpty(StreamSecret) || StreamSecret.Length != 64 || Convert.FromHexString(StreamSecret, new byte[32], out _, out _) != System.Buffers.OperationStatus.Done)
        {
            throw new ArgumentException("The stream signing key must contain 32 random bytes encoded as hex.");
        }

        PublicBaseUrl = PublicBaseUrl.Trim().TrimEnd('/');
        ApiKey = (ApiKey ?? string.Empty).Trim();
        MovieLibraryName = MovieLibraryName.Trim();
        ShowLibraryName = ShowLibraryName.Trim();
    }
}
