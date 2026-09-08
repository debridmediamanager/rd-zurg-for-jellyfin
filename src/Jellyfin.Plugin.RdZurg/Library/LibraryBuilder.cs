using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RdZurg.Library;

/// <summary>
/// Creates the libraries the plugin fills and hands back the folder items belong under.
/// </summary>
public sealed class LibraryBuilder
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="LibraryBuilder"/> class.</summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="logger">Logger.</param>
    public LibraryBuilder(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Finds or creates a library and returns the folder injected items belong under.
    /// </summary>
    /// <param name="name">The library's name.</param>
    /// <param name="path">The directory backing it.</param>
    /// <param name="collectionType">Movies or shows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The physical folder, or <c>null</c> when it could not be established.</returns>
    /// <remarks>
    /// <para>
    /// Items must hang off the physical folder, not the collection folder. Jellyfin stores a
    /// <c>TopParentId</c> taken from the first ancestor for which <c>IsTopParent</c> is true, and
    /// that is only ever the folder directly under the aggregate root. Every library query filters
    /// on that column, so an item parented to the collection folder is saved correctly and is
    /// invisible.
    /// </para>
    /// <para>
    /// The anchor file matters for the same reason. Jellyfin's root resolver skips an empty
    /// directory, so a library backed by one never gets a physical folder at all.
    /// </para>
    /// </remarks>
    public async Task<Folder?> EnsureLibraryAsync(
        string name,
        string path,
        CollectionTypeOptions collectionType,
        CancellationToken cancellationToken)
    {
        var libraries = _libraryManager.GetVirtualFolders();
        var existing = libraries.Find(v => v.Locations.Contains(path, StringComparer.Ordinal));
        if (existing is null && libraries.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The library name is already used by a library not owned by RD zurg: " + name);
        }

        if (existing is not null && existing.CollectionType != collectionType)
        {
            throw new InvalidOperationException("The RD zurg library has the wrong collection type: " + name);
        }

        if (existing is null)
        {
            Directory.CreateDirectory(path);
            await WriteAnchorAsync(path, cancellationToken).ConfigureAwait(false);

            var options = new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo(path) },
                EnableRealtimeMonitor = false,
                EnableChapterImageExtraction = false,
                EnableTrickplayImageExtraction = false
            };

            await _libraryManager.AddVirtualFolder(name, collectionType, options, false).ConfigureAwait(false);

            existing = _libraryManager.GetVirtualFolders()
                .Find(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            await WriteAnchorAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (existing?.ItemId is null)
        {
            _logger.LogError("Could not create the {Name} library", name);
            return null;
        }

        await _libraryManager.ValidateTopLibraryFolders(cancellationToken).ConfigureAwait(false);

        if (_libraryManager.GetItemById(Guid.Parse(existing.ItemId)) is not CollectionFolder collectionFolder)
        {
            return null;
        }

        var physicalId = collectionFolder.PhysicalFolderIds
            .FirstOrDefault(id => string.Equals(_libraryManager.GetItemById(id)?.Path, path, StringComparison.Ordinal));
        if (physicalId.Equals(Guid.Empty))
        {
            _logger.LogError(
                "The {Name} library has no physical folder. Jellyfin skips an empty directory, so {Path} needs to contain something.",
                name,
                path);
            return null;
        }

        return _libraryManager.GetItemById(physicalId) as Folder;
    }

    private static async Task WriteAnchorAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(path);
        var anchor = Path.Combine(path, ".rd-zurg");

        if (!File.Exists(anchor))
        {
            await File.WriteAllTextAsync(
                anchor,
                "This directory stays empty. It exists so Jellyfin creates a folder for the library;\n"
                + "the items themselves are injected by the RD zurg plugin and live only in the database.\n",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Builds the deterministic id for an item, so a resync recognises what it made before.</summary>
    /// <param name="key">A stable key for the item.</param>
    /// <param name="type">The item's type.</param>
    /// <returns>The id.</returns>
    public Guid IdFor(string key, Type type) => _libraryManager.GetNewItemId(key, type);

    /// <summary>Formats the key that identifies a series across runs.</summary>
    /// <param name="seriesName">The cleaned series name.</param>
    /// <returns>The key.</returns>
    public static string SeriesKey(string seriesName)
        => string.Format(CultureInfo.InvariantCulture, "rd-zurg-series:{0}", seriesName);

    /// <summary>Formats the key that identifies a season across runs.</summary>
    /// <param name="seriesId">The series' id.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <returns>The key.</returns>
    public static string SeasonKey(Guid seriesId, int seasonNumber)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0}Season{1}",
            seriesId.ToString("N", CultureInfo.InvariantCulture),
            seasonNumber);
}
