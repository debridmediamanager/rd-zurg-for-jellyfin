using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RdZurg.Archive;
using Jellyfin.Plugin.RdZurg.RealDebrid;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RdZurg.Streaming;

/// <summary>
/// Turns a stored link into something playable, and remembers the answer.
/// </summary>
/// <remarks>
/// Two reasons this caches. Unrestricting has its own throttle far tighter than the documented
/// request budget, and a single playback session asks more than once - a probe, then the player,
/// then every seek that reopens the source. Reading an archive's headers costs a range request on
/// top, so that answer is worth keeping for the same period.
/// </remarks>
public sealed class LinkResolver
{
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, ResolvedLink> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LinkResolver> _logger;

    /// <summary>Initializes a new instance of the <see cref="LinkResolver"/> class.</summary>
    /// <param name="httpClientFactory">Factory for outbound requests.</param>
    /// <param name="logger">Logger.</param>
    public LinkResolver(IHttpClientFactory httpClientFactory, ILogger<LinkResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Forgets a resolution, so the next request mints a fresh one.</summary>
    /// <param name="linkKey">The content key.</param>
    public void Invalidate(string linkKey) => _cache.TryRemove(linkKey, out _);

    /// <summary>
    /// Resolves a stored link, minting and probing only when there is nothing usable cached.
    /// </summary>
    /// <param name="linkKey">The 13 character content key.</param>
    /// <param name="clientIp">The address that will pull the bytes, when routable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolution.</returns>
    public async Task<ResolvedLink> ResolveAsync(string linkKey, string? clientIp, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(linkKey, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached;
        }

        // One caller per link does the work; a burst of range requests on a cold link waits rather
        // than each firing its own unrestrict.
        var gate = _locks.GetOrAdd(linkKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_cache.TryGetValue(linkKey, out cached) && cached.ExpiresUtc > DateTime.UtcNow)
            {
                return cached;
            }

            var resolved = await ResolveUncachedAsync(linkKey, clientIp, cancellationToken).ConfigureAwait(false);
            _cache[linkKey] = resolved;
            return resolved;
        }
        finally
        {
            gate.Release();
            _locks.TryRemove(linkKey, out _);
        }
    }

    private async Task<ResolvedLink> ResolveUncachedAsync(string linkKey, string? clientIp, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("The plugin is not loaded.");

        if (string.IsNullOrEmpty(config.ApiKey))
        {
            throw new InvalidOperationException("No Real-Debrid API token is configured.");
        }

        var client = new RealDebridClient(_httpClientFactory.CreateClient(), config.ApiKey, config.MinRequestIntervalMs);
        var unrestricted = await client.UnrestrictAsync(linkKey, clientIp, cancellationToken).ConfigureAwait(false);

        if (unrestricted is null || string.IsNullOrEmpty(unrestricted.Download))
        {
            throw new RealDebridRefusedException("Real-Debrid returned no download URL for " + linkKey);
        }

        RarEntry? entry = null;

        if (config.UnwrapArchives)
        {
            entry = await ProbeArchiveAsync(unrestricted, cancellationToken).ConfigureAwait(false);
        }

        if (entry is not null)
        {
            _logger.LogInformation(
                "{LinkKey} is an archive; serving {Member} from offset {Offset} ({Length} bytes)",
                linkKey,
                entry.Name,
                entry.DataOffset,
                entry.Length);
        }

        return new ResolvedLink
        {
            Url = unrestricted.Download,
            FileName = entry?.Name ?? unrestricted.Filename,
            Size = unrestricted.Filesize,
            ExpiresUtc = DateTime.UtcNow.Add(_lifetime),
            ArchiveEntry = entry
        };
    }

    /// <summary>
    /// Reads the head of a release and, if it is a RAR, finds the member to serve.
    /// </summary>
    /// <remarks>
    /// The name is only a hint. Real-Debrid does serve <c>.mkv.rar</c>, but the check that decides
    /// is the signature in the bytes, because the file list has already been shown to lie about the
    /// name. A compressed member is reported as no member at all: its bytes are not playable as a
    /// range, and pretending otherwise produces a file that opens and then fails.
    /// </remarks>
    private async Task<RarEntry?> ProbeArchiveAsync(RdUnrestricted unrestricted, CancellationToken cancellationToken)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, unrestricted.Download);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, RarReader.HeaderProbeLength - 1);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var head = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (!RarReader.LooksLikeRar(head))
            {
                return null;
            }

            if (!RarReader.TryGetPrimaryEntry(head, out var entry))
            {
                _logger.LogWarning(
                    "{FileName} is a RAR with no stored member. Compressed archives cannot be streamed, so it stays unplayable.",
                    unrestricted.Filename);
                return null;
            }

            // A member that runs past the end of the archive means a volume set, which one link
            // cannot satisfy.
            if (entry.DataOffset + entry.Length > unrestricted.Filesize)
            {
                _logger.LogWarning(
                    "{FileName} holds {Member}, which continues into another volume. Multi-volume sets are not served.",
                    unrestricted.Filename,
                    entry.Name);
                return null;
            }

            return entry;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not read the head of {FileName}", unrestricted.Filename);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Timed out reading the head of {FileName}", unrestricted.Filename);
            return null;
        }
    }

    /// <summary>Builds the resolver URL that an injected item's path points at.</summary>
    /// <param name="baseUrl">The server's public base URL.</param>
    /// <param name="linkKey">The content key.</param>
    /// <param name="fileName">The release filename, which Jellyfin reads identity out of.</param>
    /// <returns>An absolute URL.</returns>
    public static string BuildUrl(string baseUrl, string linkKey, string fileName)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(fileName);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}/RdZurg/Stream/{1}/{2}",
            baseUrl.TrimEnd('/'),
            linkKey,
            Uri.EscapeDataString(System.IO.Path.GetFileName(fileName)));
    }
}
