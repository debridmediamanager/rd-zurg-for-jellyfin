using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RdZurg.Library;

/// <summary>
/// The scheduled task that keeps the libraries level with the account.
/// </summary>
public class RdSyncTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RdSyncTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="RdSyncTask"/> class.</summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="providerManager">Jellyfin's provider manager.</param>
    /// <param name="fileSystem">Jellyfin's filesystem abstraction.</param>
    /// <param name="httpClientFactory">Factory for outbound requests.</param>
    /// <param name="logger">Logger.</param>
    public RdSyncTask(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IHttpClientFactory httpClientFactory,
        ILogger<RdSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Real-Debrid library";

    /// <inheritdoc />
    public string Key => "RdZurgSync";

    /// <inheritdoc />
    public string Description => "Brings the Real-Debrid libraries level with the account.";

    /// <inheritdoc />
    public string Category => "RD zurg";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        }
    };

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;

        if (config is null || string.IsNullOrEmpty(config.ApiKey))
        {
            _logger.LogWarning("No Real-Debrid API token is configured, so there is nothing to sync");
            return;
        }

        var sync = new LibrarySync(_libraryManager, _providerManager, _fileSystem, _httpClientFactory, _logger);
        var result = await sync.RunAsync(config, progress, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Sync finished. {Seen} torrents seen, {Known} already in the library. "
            + "Added {Movies} movies and {Episodes} episodes across {Series} series. "
            + "Skipped {Duplicates} byte-identical re-adds, folded {Merged} in as alternate versions, released {Released} that may not be merged, "
            + "removed {Removed} gone from the account and {Leftovers} added twice by an earlier build, "
            + "refiled {Refiled} films an earlier build had filed as episodes, "
            + "and corrected {Years} films an earlier build had filed under a year read out of their resolution.",
            result.TorrentsSeen,
            result.TorrentsAlreadyKnown,
            result.MoviesAdded,
            result.EpisodesAdded,
            result.SeriesTouched,
            result.DuplicatesSkipped,
            result.VersionsMerged,
            result.VersionsReleased,
            result.ItemsRemoved,
            result.LeftoversRemoved,
            result.EpisodesRefiled,
            result.YearsCorrected);
    }
}
