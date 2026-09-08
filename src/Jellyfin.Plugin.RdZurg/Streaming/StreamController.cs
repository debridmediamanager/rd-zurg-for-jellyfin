using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RdZurg.RealDebrid;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RdZurg.Streaming;

/// <summary>
/// The endpoint every library item points at.
/// </summary>
/// <remarks>
/// A plain release is answered with a redirect, so the bytes go straight from Real-Debrid's CDN to
/// whatever is playing and this server carries none of them. A release that turned out to be an
/// archive has to be served, because the file inside it is a byte range of the archive and no
/// redirect can express that. The split is the same one zurg makes.
/// </remarks>
[ApiController]
[Route("RdZurg")]
public class StreamController : ControllerBase
{
    private readonly LinkResolver _resolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StreamController> _logger;

    /// <summary>Initializes a new instance of the <see cref="StreamController"/> class.</summary>
    /// <param name="resolver">The link resolver.</param>
    /// <param name="httpClientFactory">Factory for outbound requests.</param>
    /// <param name="logger">Logger.</param>
    public StreamController(LinkResolver resolver, IHttpClientFactory httpClientFactory, ILogger<StreamController> logger)
    {
        _resolver = resolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Serves, or points at, the content behind a stored link.</summary>
    /// <param name="linkKey">The 13 character content key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A redirect for a plain release, or the bytes of an archive member.</returns>
    /// <response code="200">The whole member.</response>
    /// <response code="206">Part of the member.</response>
    /// <response code="302">Where to fetch a plain release.</response>
    /// <response code="502">Real-Debrid refused.</response>
    /// <response code="503">The plugin is not configured.</response>
    [HttpGet("Stream/{linkKey}")]
    [HttpHead("Stream/{linkKey}")]
    [HttpGet("Stream/{linkKey}/{fileName}")]
    [HttpHead("Stream/{linkKey}/{fileName}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Stream(string linkKey, CancellationToken cancellationToken)
    {
        ResolvedLink resolved;

        try
        {
            resolved = await _resolver.ResolveAsync(linkKey, PublicClientIp(), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (RealDebridRefusedException ex)
        {
            // A link that will not mint is usually a link that has aged out. Drop it so a later
            // request tries again rather than serving the same failure from cache.
            _resolver.Invalidate(linkKey);
            _logger.LogWarning(ex, "Could not resolve {LinkKey}", linkKey);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }

        if (!resolved.RequiresStreaming)
        {
            return Redirect(resolved.Url);
        }

        return await ServeArchiveMemberAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActionResult> ServeArchiveMemberAsync(ResolvedLink resolved, CancellationToken cancellationToken)
    {
        var entry = resolved.ArchiveEntry!;
        var length = entry.Length;

        var requested = ByteRange.TryParse(Request.Headers.Range.ToString(), length, out var parsed)
            ? parsed!.Value
            : new ByteRange(0, length - 1);

        var isPartial = Request.Headers.ContainsKey(HeaderNames.RangeHeader) && parsed is not null;

        Response.Headers.AcceptRanges = "bytes";
        Response.ContentType = "video/x-matroska";
        Response.ContentLength = requested.Length;

        if (isPartial)
        {
            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.Headers.ContentRange = string.Format(
                CultureInfo.InvariantCulture,
                "bytes {0}-{1}/{2}",
                requested.From,
                requested.To,
                length);
        }

        if (HttpMethods.IsHead(Request.Method))
        {
            return new EmptyResult();
        }

        // The member is a contiguous run inside the archive, so the read is the same read shifted.
        var http = _httpClientFactory.CreateClient();
        using var upstream = new HttpRequestMessage(HttpMethod.Get, resolved.Url);
        upstream.Headers.Range = new RangeHeaderValue(
            entry.ToArchiveOffset(requested.From),
            entry.ToArchiveOffset(requested.To));

        using var response = await http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Real-Debrid answered {Status} for a range of {Member}",
                (int)response.StatusCode,
                entry.Name);

            return StatusCode(StatusCodes.Status502BadGateway);
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await body.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A player seeking or closing abandons the response mid-copy. That is normal.
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Gets the caller's address when it is one Real-Debrid can meaningfully record.
    /// </summary>
    /// <remarks>
    /// Real-Debrid treats one account's links being pulled from many addresses as sharing, so links
    /// are minted for whoever will fetch them. A private or loopback address says nothing, and
    /// sending one is worse than sending none.
    /// </remarks>
    private string? PublicClientIp()
    {
        var address = HttpContext.Connection.RemoteIpAddress;

        if (address is null || IPAddress.IsLoopback(address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return address.ToString();
        }

        var octets = address.GetAddressBytes();
        var isPrivate = octets[0] == 10
            || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
            || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 169 && octets[1] == 254);

        return isPrivate ? null : address.ToString();
    }

    private static class HeaderNames
    {
        public const string RangeHeader = "Range";
    }
}
