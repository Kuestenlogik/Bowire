// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// Reverse-proxy middleware for the standalone <c>bowire interceptor</c>
/// CLI (#307 — Phase C of #153; YARP migration in #323). Sits in front
/// of an upstream service: the client points at Bowire's listener,
/// every request is forwarded upstream via YARP's
/// <see cref="IHttpForwarder"/>, and the request + response are captured
/// into the same <see cref="InterceptedFlowStore"/> the embedded
/// middleware (#153 Phase A/B) populates.
/// </summary>
/// <remarks>
/// <para>
/// Two stores, two surfaces — but the rail surface and the on-disk
/// recording shape are intentionally identical between the embedded
/// (in-process) and standalone (reverse-proxy) modes. The workbench's
/// "Intercepted" rail doesn't know which mode produced a flow; the
/// detail pane, the send-to-recording action, and the SSE live feed
/// reuse <see cref="BowireInterceptorMiddleware"/>'s output shape
/// verbatim.
/// </para>
/// <para>
/// Streaming responses (SSE / chunked / WebSocket-101 / gRPC) are
/// detected after the upstream's response headers land and are
/// proxied through without buffering — the rail surfaces them with a
/// <c>streaming</c> badge instead of an empty body.
/// </para>
/// <para>
/// #323 — YARP migration. We hand the request off to YARP's
/// <see cref="IHttpForwarder.SendAsync(HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer)"/>
/// instead of building a <see cref="HttpRequestMessage"/> by hand. The
/// capture seam moves into a per-request
/// <see cref="CapturingHttpTransformer"/>: we override
/// <see cref="HttpTransformer.TransformRequestAsync(HttpContext, HttpRequestMessage, string, CancellationToken)"/>
/// to inject the pre-buffered body, and
/// <see cref="HttpTransformer.TransformResponseAsync(HttpContext, HttpResponseMessage?, CancellationToken)"/>
/// to snapshot status/headers, detect streaming, and (for non-streaming
/// responses) tee the body into the flow store before letting YARP copy
/// it to the client. Streaming bodies are copied straight through by
/// YARP's built-in pipeline.
/// </para>
/// </remarks>
internal sealed class BowireReverseProxyMiddleware
{
    private readonly InterceptedFlowStore _store;
    private readonly IHttpForwarder _forwarder;
    private readonly HttpMessageInvoker _invoker;
    private readonly ForwarderRequestConfig _requestConfig;
    private readonly Uri _upstream;
    private readonly BowireInterceptorOptions _options;
    private readonly ILogger<BowireReverseProxyMiddleware>? _logger;

    public BowireReverseProxyMiddleware(
        InterceptedFlowStore store,
        IHttpForwarder forwarder,
        HttpMessageInvoker invoker,
        ForwarderRequestConfig requestConfig,
        Uri upstream,
        BowireInterceptorOptions options,
        ILogger<BowireReverseProxyMiddleware>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(forwarder);
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(requestConfig);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _forwarder = forwarder;
        _invoker = invoker;
        _requestConfig = requestConfig;
        _upstream = upstream;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var id = _store.NextId();
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var method = context.Request.Method ?? "GET";
        var scheme = context.Request.Scheme ?? "http";
        var (clientUrl, path) = ReconstructUrl(context.Request);
        var requestHeaders = SnapshotHeaders(context.Request.Headers);

        // -------- request body capture (buffered, since we forward it again) --------
        byte[] requestBytes = Array.Empty<byte>();
        bool requestBodyTruncated = false;
        try
        {
            context.Request.EnableBuffering();
            (requestBytes, requestBodyTruncated) = await ReadAllAsync(
                context.Request.Body, _options.MaxBodyBytes, context.RequestAborted).ConfigureAwait(false);
            if (context.Request.Body.CanSeek) context.Request.Body.Position = 0;
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (_logger is { } log && log.IsEnabled(LogLevel.Debug))
                log.LogDebug(ex, "bowire.interceptor: request body capture failed for {Method} {Path}",
                    LogSanitizer.Strip(method), LogSanitizer.Strip(path));
        }

        var (reqText, reqBase64) = ClassifyBytes(requestBytes);

        // -------- forward upstream via YARP IHttpForwarder --------
        // The transformer is the capture seam: TransformRequestAsync
        // re-injects the buffered request body onto the outbound
        // HttpRequestMessage, TransformResponseAsync taps the response
        // headers + body for the flow record before YARP copies them to
        // the client.
        var transformer = new CapturingHttpTransformer(_options.MaxBodyBytes);

        string? error = null;
        ForwarderError forwarderError;
        var destinationPrefix = BuildDestinationPrefix(_upstream);
        try
        {
            forwarderError = await _forwarder.SendAsync(
                context, destinationPrefix, _invoker, _requestConfig, transformer)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // SendAsync rarely throws — it normally surfaces failure via
            // the returned ForwarderError + IForwarderErrorFeature. Catch
            // here is defence in depth.
            forwarderError = ForwarderError.Request;
            error = ex.Message;
            if (_logger is { } log && log.IsEnabled(LogLevel.Debug))
                log.LogDebug(ex, "bowire.interceptor: YARP SendAsync threw for {Method} {Url}",
                    LogSanitizer.Strip(method), LogSanitizer.Strip(clientUrl));
        }

        if (forwarderError != ForwarderError.None && error is null)
        {
            // Translate YARP's error code into a rail-visible message and,
            // if YARP didn't already write a status, surface a 502 to the
            // client (matches the legacy HttpClient path that wrote
            // "bowire interceptor: upstream unreachable (…)").
            var errorFeature = context.GetForwarderErrorFeature();
            error = errorFeature?.Exception?.Message ?? forwarderError.ToString();
            if (!context.Response.HasStarted)
            {
                try
                {
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await context.Response.WriteAsync(
                        $"bowire interceptor: upstream unreachable ({error})",
                        context.RequestAborted).ConfigureAwait(false);
                }
#pragma warning disable CA1031
                catch (Exception writeEx)
#pragma warning restore CA1031
                {
                    if (_logger is { } log && log.IsEnabled(LogLevel.Debug))
                        log.LogDebug(writeEx, "bowire.interceptor: failed to write 502 response");
                }
            }
        }

        if (context.RequestAborted.IsCancellationRequested && error is null)
        {
            error = "client disconnected";
        }

        sw.Stop();

        // -------- record into the shared store --------
        var statusCode = transformer.ResponseStatus != 0
            ? transformer.ResponseStatus
            : context.Response.StatusCode;
        var flow = new InterceptedFlow
        {
            Id = id,
            CapturedAt = startedAt,
            Method = method,
            Url = clientUrl,
            Scheme = scheme,
            Path = path,
            RequestHeaders = requestHeaders,
            RequestBody = reqText,
            RequestBodyBase64 = reqBase64,
            RequestBodyTruncated = requestBodyTruncated,
            ResponseStatus = statusCode,
            ResponseHeaders = transformer.ResponseHeaders,
            ResponseBody = transformer.ResponseBodyText,
            ResponseBodyBase64 = transformer.ResponseBodyBase64,
            ResponseBodyTruncated = transformer.ResponseBodyTruncated,
            Streaming = transformer.Streaming,
            LatencyMs = (int)sw.ElapsedMilliseconds,
            Error = error,
        };
        _store.Add(flow);
    }

    /// <summary>
    /// YARP destination prefix — the upstream URI's scheme + authority +
    /// base path (anything <see cref="RequestUtilities.MakeDestinationAddress"/>
    /// prepends in front of the incoming path). Mirrors the legacy
    /// HttpClient path's <c>UriBuilder</c> assembly.
    /// </summary>
    private static string BuildDestinationPrefix(Uri upstream)
    {
        // GetLeftPart(Uri.Partial.Authority) gives us scheme://host[:port],
        // and we append the upstream's base path. YARP's default
        // RequestUtilities.MakeDestinationAddress(prefix, path, query) then
        // joins the prefix to the incoming request path verbatim, matching
        // the legacy CombinePath behaviour exactly.
        var authority = upstream.GetLeftPart(UriPartial.Authority);
        var basePath = upstream.AbsolutePath;
        if (string.IsNullOrEmpty(basePath) || basePath == "/") return authority + "/";
        // Trim trailing slash — RequestUtilities.MakeDestinationAddress
        // adds the join slash, and a double slash would 404 most upstreams.
        return authority + basePath.TrimEnd('/');
    }

    private static (string url, string path) ReconstructUrl(HttpRequest req)
    {
        var path = req.Path.HasValue ? req.Path.Value! : "/";
        var query = req.QueryString.HasValue ? req.QueryString.Value! : string.Empty;
        var host = req.Host.HasValue ? req.Host.Value! : "localhost";
        var scheme = req.Scheme ?? "http";
        return ($"{scheme}://{host}{path}{query}", path + query);
    }

    private static List<KeyValuePair<string, string>> SnapshotHeaders(IHeaderDictionary headers)
    {
        var list = new List<KeyValuePair<string, string>>(headers.Count);
        foreach (var pair in headers)
        {
            foreach (var v in pair.Value)
                list.Add(new KeyValuePair<string, string>(pair.Key, v ?? string.Empty));
        }
        return list;
    }

    internal static List<KeyValuePair<string, string>> CombineResponseHeaders(HttpResponseMessage resp)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var h in resp.Headers)
        {
            foreach (var v in h.Value)
                list.Add(new KeyValuePair<string, string>(h.Key, v));
        }
        if (resp.Content is not null)
        {
            foreach (var h in resp.Content.Headers)
            {
                foreach (var v in h.Value)
                    list.Add(new KeyValuePair<string, string>(h.Key, v));
            }
        }
        return list;
    }

    internal static bool IsStreamingResponse(HttpResponseMessage resp)
    {
        // WebSocket / generic 101 Switching Protocols.
        if ((int)resp.StatusCode == 101) return true;
        // Media-type-driven streams. We intentionally do NOT flag
        // Transfer-Encoding: chunked — Kestrel chunks every dynamic body
        // (e.g. Results.Ok(...)), so a chunked flag would over-tag the rail.
        if (resp.Content?.Headers.ContentType is { } ct)
        {
            var mt = ct.MediaType;
            if (mt is not null)
            {
                if (mt.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)) return true;
                if (mt.Contains("application/grpc", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    internal static bool IsBodyLessMethod(string method)
        => string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "TRACE", StringComparison.OrdinalIgnoreCase);

    private static async Task<(byte[] bytes, bool truncated)> ReadAllAsync(
        Stream stream, int cap, CancellationToken ct)
    {
        if (stream is null || cap <= 0) return (Array.Empty<byte>(), false);
        using var buffer = new MemoryStream();
        var pool = new byte[8192];
        var truncated = false;
        while (buffer.Length < cap)
        {
            var remaining = (int)(cap - buffer.Length);
            var read = await stream.ReadAsync(pool.AsMemory(0, Math.Min(pool.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0) break;
#pragma warning disable CA1849
            buffer.Write(pool, 0, read);
#pragma warning restore CA1849
        }
        if (buffer.Length == cap)
        {
            var probe = await stream.ReadAsync(pool.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (probe > 0) truncated = true;
        }
        return (buffer.ToArray(), truncated);
    }

    internal static (string? text, string? base64) ClassifyBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return (null, null);
        var likelyUtf8 = true;
        var probe = Math.Min(bytes.Length, 4096);
        for (var i = 0; i < probe; i++)
        {
            if (bytes[i] == 0) { likelyUtf8 = false; break; }
        }
        if (likelyUtf8)
        {
            try
            {
                var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
                return (utf8.GetString(bytes), null);
            }
            catch (DecoderFallbackException) { /* fall through */ }
        }
        return (null, Convert.ToBase64String(bytes));
    }

    /// <summary>
    /// Per-request YARP <see cref="HttpTransformer"/> that taps the
    /// request body (injecting the pre-buffered bytes onto the outbound
    /// <see cref="HttpRequestMessage"/>) and the response body (teeing
    /// non-streaming bodies into the flow record before YARP copies
    /// them to the client).
    /// </summary>
    /// <remarks>
    /// One instance per request: cheap to allocate, holds the captured
    /// state across the request → response lifecycle. The
    /// <c>ResponseHeaders</c> / <c>ResponseStatus</c> / <c>ResponseBody*</c>
    /// surface lets <see cref="BowireReverseProxyMiddleware.InvokeAsync"/>
    /// read the captured values back after <see cref="IHttpForwarder.SendAsync(HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer)"/>
    /// returns.
    /// </remarks>
    private sealed class CapturingHttpTransformer : HttpTransformer
    {
        private readonly int _maxBodyBytes;

        public CapturingHttpTransformer(int maxBodyBytes)
        {
            _maxBodyBytes = maxBodyBytes;
        }

        public List<KeyValuePair<string, string>> ResponseHeaders { get; private set; } = new();
        public int ResponseStatus { get; private set; }
        public string? ResponseBodyText { get; private set; }
        public string? ResponseBodyBase64 { get; private set; }
        public bool ResponseBodyTruncated { get; private set; }
        public bool Streaming { get; private set; }

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            // Default: copy headers (modulo HTTP/2 pseudo-headers + the
            // hop-by-hop set), build the destination URI from the prefix
            // + incoming path/query, and (if the request has a body) wrap
            // HttpContext.Request.Body as proxyRequest.Content. The
            // outer middleware already called EnableBuffering() and
            // rewound the request body to position 0, so YARP's
            // StreamContent reads our captured bytes verbatim.
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken)
                .ConfigureAwait(false);

            // YARP's direct-forwarding sample suppresses the original
            // Host header so the destination Uri's authority is used —
            // matches the legacy middleware which skipped Host when
            // copying headers.
            proxyRequest.Headers.Host = null;
        }

        public override async ValueTask<bool> TransformResponseAsync(
            HttpContext httpContext,
            HttpResponseMessage? proxyResponse,
            CancellationToken cancellationToken)
        {
            // YARP calls TransformResponseAsync with a null response when
            // the upstream never produced one (connection reset, DNS
            // failure, timeout). The middleware's error-path picks it up
            // from the ForwarderError return code; nothing to capture here.
            if (proxyResponse is null)
            {
                return await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken)
                    .ConfigureAwait(false);
            }

            ResponseStatus = (int)proxyResponse.StatusCode;
            ResponseHeaders = CombineResponseHeaders(proxyResponse);
            Streaming = IsStreamingResponse(proxyResponse);

            if (Streaming)
            {
                // Streaming: let YARP copy the body straight through —
                // base.TransformResponseAsync returns true, which signals
                // "proxy the body as-is" without buffering anywhere on our
                // side. Rail surfaces an empty body + the streaming flag.
                return await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Non-streaming: read up to the capture cap, classify, write
            // the captured bytes back to the client, then return false so
            // YARP does NOT attempt a second body copy on top of ours.
            //
            // Behaviour parity with the legacy HttpClient path: we forward
            // up to MaxBodyBytes and mark the flow truncated past that. A
            // future polish is the tee-while-streaming pattern (#323
            // follow-up).
            byte[] respBytes;
            bool truncated;
            try
            {
                await using var upstreamStream = await proxyResponse.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                (respBytes, truncated) = await ReadAllAsync(
                    upstreamStream, _maxBodyBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            ResponseBodyTruncated = truncated;
            if (respBytes.Length > 0)
            {
                (ResponseBodyText, ResponseBodyBase64) = ClassifyBytes(respBytes);

                // Mirror the status + headers onto the outgoing response
                // ourselves — when we return false, YARP skips its own
                // status/header copy as well. The base transformer
                // already copied them BEFORE we got here (per YARP docs,
                // status + headers land on HttpContext.Response before
                // TransformResponseAsync), so we only need to flush the
                // captured body.
                try
                {
                    await httpContext.Response.Body.WriteAsync(
                        respBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031
                catch (Exception)
#pragma warning restore CA1031
                {
                    // Client disconnected mid-write — the middleware
                    // records "client disconnected" via the cancellation
                    // path; the flow itself is intact.
                }
            }

            return false;
        }
    }
}
