using Jellyfin.Plugin.RdZurg.Archive;
using Jellyfin.Plugin.RdZurg.RealDebrid;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _configurationKey = ConfigurationKey();
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
        if (!StreamAccess.IsValidKey(linkKey))
        {
            throw new InvalidOperationException("Invalid content key.");
        }

        if (_configurationKey == ConfigurationKey()
            && _cache.TryGetValue(linkKey, out var warm) && warm.ExpiresUtc > DateTime.UtcNow)
        {
            return warm;
        }

        // Keep one gate for cold resolutions. Removing per-key locks while callers still wait
        // creates two independent locks for the same key after a failed resolution.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configurationKey = ConfigurationKey();
            if (_configurationKey != configurationKey)
            {
                _cache.Clear();
                _configurationKey = configurationKey;
            }

            if (_cache.TryGetValue(linkKey, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            {
                return cached;
            }

            foreach (var key in _cache.Where(p => p.Value.ExpiresUtc <= DateTime.UtcNow).Select(p => p.Key))
            {
                _cache.TryRemove(key, out _);
            }

            if (_cache.Count >= 1024)
            {
                _cache.TryRemove(_cache.MinBy(p => p.Value.ExpiresUtc).Key, out _);
            }

            var resolved = await ResolveUncachedAsync(linkKey, null, cancellationToken).ConfigureAwait(false);
            _cache[linkKey] = resolved;
            return resolved;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ConfigurationKey()
    {
        var config = Plugin.Instance?.Configuration;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            config?.ApiKey + "|" + config?.UnwrapArchives + "|" + config?.RedirectDirectStreams)));
    }

    private async Task<ResolvedLink> ResolveUncachedAsync(string linkKey, string? clientIp, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("The plugin is not loaded.");

        if (string.IsNullOrEmpty(config.ApiKey))
        {
            throw new InvalidOperationException("No Real-Debrid API token is configured.");
        }

        using var apiHttp = _httpClientFactory.CreateClient();
        var client = new RealDebridClient(apiHttp, config.ApiKey, config.MinRequestIntervalMs);
        var unrestricted = await client.UnrestrictAsync(linkKey, clientIp, cancellationToken).ConfigureAwait(false);

        if (unrestricted is null || string.IsNullOrEmpty(unrestricted.Download))
        {
            throw new RealDebridRefusedException("Real-Debrid returned no download URL for " + linkKey);
        }

        if (!Uri.TryCreate(unrestricted.Download, UriKind.Absolute, out var download)
            || (download.Scheme != Uri.UriSchemeHttp && download.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(download.UserInfo) || unrestricted.Filesize <= 0)
        {
            throw new InvalidDataException("Real-Debrid returned invalid media information.");
        }

        var entry = await ProbeArchiveAsync(unrestricted, cancellationToken).ConfigureAwait(false);
        if (entry is not null && !config.UnwrapArchives)
        {
            throw new InvalidDataException("This release is an archive and archive playback is disabled.");
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var http = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, unrestricted.Download);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, RarReader.HeaderProbeLength - 1);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.From != 0)
        {
            throw new InvalidDataException("The CDN returned the wrong archive header range.");
        }

        // Some CDNs ignore Range. Never buffer their entire multi-gigabyte response.
        var head = new byte[RarReader.HeaderProbeLength];
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var count = 0;
        while (count < head.Length)
        {
            var read = await body.ReadAsync(head.AsMemory(count), timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }

        if (count < 8)
        {
            throw new InvalidDataException("The CDN returned an empty or truncated media header.");
        }

        if (!RarReader.LooksLikeRar(head.AsSpan(0, count)))
        {
            if (unrestricted.Filename.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The archive signature is missing.");
            }
            return null;
        }

        if (!RarReader.TryGetPrimaryEntry(head.AsSpan(0, count), out var entry)
            || entry.DataOffset > unrestricted.Filesize
            || entry.Length > unrestricted.Filesize - entry.DataOffset)
        {
            throw new InvalidDataException("This archive cannot be streamed: a complete, unencrypted stored video member is required.");
        }

        return entry;
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
            "{0}/RdZurg/Stream/{1}/{2}?signature={3}",
            baseUrl.TrimEnd('/'),
            linkKey,
            Uri.EscapeDataString(System.IO.Path.GetFileName(fileName)),
            StreamAccess.Sign(Plugin.Instance?.Configuration ?? throw new InvalidOperationException("The plugin is not loaded."), linkKey));
    }
}
