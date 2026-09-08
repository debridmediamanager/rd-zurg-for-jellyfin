using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RdZurg.RealDebrid;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RdZurg.Streaming;

/// <summary>Serves signed playback capabilities to Jellyfin, ffmpeg and external players.</summary>
[ApiController]
[Route("RdZurg")]
public class StreamController : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider _contentTypes = new();
    private readonly LinkResolver _resolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StreamController> _logger;

    /// <summary>Initializes a new instance of the <see cref="StreamController"/> class.</summary>
    /// <param name="resolver">The shared resolver.</param>
    /// <param name="httpClientFactory">Outbound HTTP clients.</param>
    /// <param name="logger">Logger.</param>
    public StreamController(LinkResolver resolver, IHttpClientFactory httpClientFactory, ILogger<StreamController> logger)
    {
        _resolver = resolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Validates a per-file signature before accessing the account.</summary>
    /// <param name="linkKey">The content key.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>Media bytes, an optional CDN redirect, or a controlled error.</returns>
    [HttpGet("Stream/{linkKey}")]
    [HttpHead("Stream/{linkKey}")]
    [HttpGet("Stream/{linkKey}/{fileName}")]
    [HttpHead("Stream/{linkKey}/{fileName}")]
    [AllowAnonymous] // ffmpeg has no Jellyfin session; the mandatory signature is its credential.
    public async Task<ActionResult> Stream(string linkKey, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!StreamAccess.Verify(config, linkKey, Request.Query["signature"].ToString()))
        {
            return Unauthorized();
        }

        try
        {
            var resolved = await _resolver.ResolveAsync(linkKey, null, cancellationToken).ConfigureAwait(false);
            if (!resolved.RequiresStreaming && config.RedirectDirectStreams)
            {
                return Redirect(resolved.Url);
            }

            return await ServeAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or IOException)
        {
            // A player closes its old read when seeking. The source is still valid.
            return Failure(499);
        }
        catch (InvalidDataException)
        {
            _resolver.Invalidate(linkKey);
            return Failure(StatusCodes.Status422UnprocessableEntity);
        }
        catch (Exception ex) when (ex is HttpRequestException or RealDebridRefusedException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _resolver.Invalidate(linkKey);
            // HTTP exceptions can contain signed URLs. Log only the type.
            _logger.LogWarning("Playback source failed ({FailureType})", ex.GetType().Name);
            return Failure(cancellationToken.IsCancellationRequested ? 499 : StatusCodes.Status502BadGateway);
        }
    }

    private ActionResult Failure(int status)
    {
        if (Response.HasStarted)
        {
            HttpContext.Abort();
            return new EmptyResult();
        }

        Response.ContentLength = null;
        Response.Headers.Remove("Content-Range");
        return StatusCode(status);
    }

    private async Task<ActionResult> ServeAsync(ResolvedLink resolved, CancellationToken cancellationToken)
    {
        var length = resolved.PlayableLength;
        // Range applies only to GET. With no entity validators we cannot satisfy If-Range.
        var hasRange = HttpMethods.IsGet(Request.Method) && Request.Headers.ContainsKey("Range")
            && !Request.Headers.ContainsKey("If-Range");
        ByteRange? parsed = null;
        if (hasRange && !ByteRange.TryParse(Request.Headers.Range.ToString(), length, out parsed))
        {
            Response.Headers.ContentRange = string.Format(CultureInfo.InvariantCulture, "bytes */{0}", length);
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        var requested = parsed ?? new ByteRange(0, length - 1);
        if (HttpMethods.IsHead(Request.Method))
        {
            SetHeaders(resolved, requested, hasRange);
            return new EmptyResult();
        }

        var offset = resolved.ArchiveEntry?.DataOffset ?? 0;
        using var http = _httpClientFactory.CreateClient();
        using var upstream = new HttpRequestMessage(HttpMethod.Get, resolved.Url);
        upstream.Headers.Range = new RangeHeaderValue(offset + requested.From, offset + requested.To);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent
            || range?.Unit != "bytes" || range.From != offset + requested.From
            || range.To != offset + requested.To || range.Length != resolved.Size
            || (response.Content.Headers.ContentLength is long received && received != requested.Length))
        {
            _logger.LogWarning("CDN range mismatch: HTTP {Status}, received {Range}, expected {From}-{To}/{Size}",
                (int)response.StatusCode, range?.ToString(), offset + requested.From, offset + requested.To, resolved.Size);
            throw new HttpRequestException("The CDN did not honor the requested byte range.");
        }

        SetHeaders(resolved, requested, hasRange);
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        var remaining = requested.Length;
        while (remaining > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The CDN truncated the requested range.");
            }

            await Response.Body.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            remaining -= read;
        }

        return new EmptyResult();
    }

    private void SetHeaders(ResolvedLink resolved, ByteRange requested, bool partial)
    {
        Response.Headers.AcceptRanges = "bytes";
        Response.ContentType = _contentTypes.TryGetContentType(resolved.FileName, out var mime) ? mime : "application/octet-stream";
        Response.ContentLength = requested.Length;
        Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        if (partial)
        {
            Response.Headers.ContentRange = string.Format(CultureInfo.InvariantCulture, "bytes {0}-{1}/{2}", requested.From, requested.To, resolved.PlayableLength);
        }
    }
}
