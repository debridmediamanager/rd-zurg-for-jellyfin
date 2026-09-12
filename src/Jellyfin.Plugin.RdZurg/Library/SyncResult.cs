namespace Jellyfin.Plugin.RdZurg.Library;

/// <summary>What one sync pass did.</summary>
public sealed record SyncResult
{
    /// <summary>Gets the number of torrents the account reported.</summary>
    public int TorrentsSeen { get; init; }

    /// <summary>Gets how many torrents were skipped because every one of their links was already known.</summary>
    public int TorrentsAlreadyKnown { get; init; }

    /// <summary>Gets the number of movies added.</summary>
    public int MoviesAdded { get; init; }

    /// <summary>Gets the number of episodes added.</summary>
    public int EpisodesAdded { get; init; }

    /// <summary>Gets the number of series the episodes landed under.</summary>
    public int SeriesTouched { get; init; }

    /// <summary>Gets how many byte-identical re-adds were dropped.</summary>
    public int DuplicatesSkipped { get; init; }

    /// <summary>Gets how many releases were folded into another as an alternate version.</summary>
    public int VersionsMerged { get; init; }

    /// <summary>Gets how many earlier merges were undone because the release may not be merged.</summary>
    public int VersionsReleased { get; init; }

    /// <summary>Gets how many items were removed because their torrent is gone.</summary>
    public int ItemsRemoved { get; init; }

    /// <summary>Gets how many items were removed for repeating a file another item already holds.</summary>
    public int LeftoversRemoved { get; init; }
}
