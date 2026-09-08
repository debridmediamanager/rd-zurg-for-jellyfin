using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RdZurg.Configuration;

/// <summary>
/// Everything the plugin needs to build a library out of a Real-Debrid account.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
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
}
