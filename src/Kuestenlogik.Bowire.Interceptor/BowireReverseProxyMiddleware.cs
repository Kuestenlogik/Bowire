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

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// Reverse-proxy middleware for the standalone <c>bowire interceptor</c>
/// CLI (#307 — Phase C of #153). Sits in front of an upstream service:
/// the client points at Bowire's listener, every request is forwarded
/// upstream over HttpClient, and the request + response are captured
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
/// </remarks>
internal sealed class BowireReverseProxyMiddleware
{
    private readonly InterceptedFlowStore _store;
    private readonly HttpClient _client;
    private readonly Uri _upstream;
    private readonly BowireInterceptorOptions _options;
    private readonly ILogger<BowireReverseProxyMiddleware>? _logger;

    public BowireReverseProxyMiddleware(
        InterceptedFlowStore store,
        HttpClient client,
        Uri upstream,
        BowireInterceptorOptions options,
        ILogger<BowireReverseProxyMiddleware>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _client = client;
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
                log.LogDebug(ex, "bowire.interceptor: request body capture failed for {Method} {Path}", method, path);
        }

        var (reqText, reqBase64) = ClassifyBytes(requestBytes);

        // -------- forward upstream --------
        using var upstreamReq = BuildUpstreamRequest(context.Request, method, requestBytes);
        HttpResponseMessage? upstreamResp = null;
        string? error = null;
        int statusCode = 0;
        List<KeyValuePair<string, string>> responseHeaders = new();
        string? respText = null;
        string? respBase64 = null;
        bool respTruncated = false;
        bool streaming = false;

        try
        {
            upstreamResp = await _client.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
            statusCode = (int)upstreamResp.StatusCode;
            responseHeaders = CombineResponseHeaders(upstreamResp);

            // Mirror status + headers onto the client response BEFORE we start
            // streaming bytes back, so a streaming endpoint actually streams.
            context.Response.StatusCode = statusCode;
            foreach (var (k, v) in responseHeaders)
            {
                if (IsHopByHop(k)) continue;
                // Avoid duplicate Content-Length / Transfer-Encoding — Kestrel
                // sets these from the body it ends up writing.
                if (string.Equals(k, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(k, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                context.Response.Headers.Append(k, v);
            }

            streaming = IsStreamingResponse(upstreamResp);

            if (streaming)
            {
                // Streaming: copy the upstream body to the client live, without
                // ever buffering all of it. Rail surfaces an empty body + the
                // streaming flag.
                await using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
                await upstreamStream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                // Non-streaming: buffer up to the capture cap (or the full body
                // if smaller), record it, then forward the captured bytes to the
                // client. We CAN'T forward the remainder past the cap because
                // we've already drained the stream into our buffer; for now we
                // forward up to the cap and mark the flow truncated. Future
                // polish: tee + stream simultaneously.
                await using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
                var (respBytes, truncated) = await ReadAllAsync(
                    upstreamStream, _options.MaxBodyBytes, context.RequestAborted).ConfigureAwait(false);
                respTruncated = truncated;
                if (respBytes.Length > 0)
                {
                    (respText, respBase64) = ClassifyBytes(respBytes);
                    await context.Response.Body.WriteAsync(respBytes.AsMemory(), context.RequestAborted).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            error = "client disconnected";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Upstream failure modes we expect (connection refused, DNS NXDOMAIN,
            // TLS handshake reject, HttpClient.Timeout elapsed). Translate to a
            // 502 + record the error message on the flow so the rail surfaces it.
            error = ex.Message;
            try
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await context.Response.WriteAsync($"bowire interceptor: upstream unreachable ({ex.Message})", context.RequestAborted).ConfigureAwait(false);
                }
                statusCode = context.Response.StatusCode;
            }
#pragma warning disable CA1031
            catch (Exception writeEx)
#pragma warning restore CA1031
            {
                if (_logger is { } log && log.IsEnabled(LogLevel.Debug))
                    log.LogDebug(writeEx, "bowire.interceptor: failed to write 502 response");
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = ex.Message;
            if (_logger is { } log && log.IsEnabled(LogLevel.Debug))
                log.LogDebug(ex, "bowire.interceptor: upstream forward failed for {Method} {Url}", method, clientUrl);
        }
        finally
        {
            upstreamResp?.Dispose();
        }

        sw.Stop();

        // -------- record into the shared store --------
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
            ResponseHeaders = responseHeaders,
            ResponseBody = respText,
            ResponseBodyBase64 = respBase64,
            ResponseBodyTruncated = respTruncated,
            Streaming = streaming,
            LatencyMs = (int)sw.ElapsedMilliseconds,
            Error = error,
        };
        _store.Add(flow);
    }

    private HttpRequestMessage BuildUpstreamRequest(HttpRequest req, string method, byte[] requestBytes)
    {
        // Target URL = upstream base + the incoming path/query. The upstream's
        // own path (e.g. /api) is honoured as a prefix; we append the incoming
        // path verbatim. Example: --upstream https://api.example.com/v1
        // incoming GET /users?id=1 -> https://api.example.com/v1/users?id=1
        var path = req.Path.HasValue ? req.Path.Value! : "/";
        var query = req.QueryString.HasValue ? req.QueryString.Value! : string.Empty;
        var upstreamPath = CombinePath(_upstream.AbsolutePath, path);
        var target = new UriBuilder(_upstream)
        {
            Path = upstreamPath,
            Query = query.StartsWith('?') ? query[1..] : query,
        }.Uri;

        var message = new HttpRequestMessage(new HttpMethod(method), target);
        if (requestBytes.Length > 0 && !IsBodyLessMethod(method))
        {
            message.Content = new ByteArrayContent(requestBytes);
        }

        foreach (var pair in req.Headers)
        {
            if (IsHopByHop(pair.Key)) continue;
            if (string.Equals(pair.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            // System.Net.Http splits headers into request vs content buckets.
            // The TryAddWithoutValidation pair below is the documented escape
            // hatch — let the upstream see whatever the client wrote.
            var values = pair.Value.Where(v => v is not null).ToArray()!;
            if (values.Length == 0) continue;
            if (!message.Headers.TryAddWithoutValidation(pair.Key, values))
            {
                message.Content?.Headers.TryAddWithoutValidation(pair.Key, values);
            }
        }
        return message;
    }

    private static string CombinePath(string upstreamBase, string incoming)
    {
        if (string.IsNullOrEmpty(upstreamBase) || upstreamBase == "/") return incoming;
        var trimmedBase = upstreamBase.TrimEnd('/');
        if (string.IsNullOrEmpty(incoming) || incoming == "/") return trimmedBase + "/";
        return trimmedBase + (incoming.StartsWith('/') ? incoming : "/" + incoming);
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

    private static List<KeyValuePair<string, string>> CombineResponseHeaders(HttpResponseMessage resp)
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

    private static bool IsStreamingResponse(HttpResponseMessage resp)
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

    private static bool IsBodyLessMethod(string method)
        => string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "TRACE", StringComparison.OrdinalIgnoreCase);

    /// <summary>RFC 7230 §6.1 hop-by-hop headers that must not be forwarded.</summary>
    private static bool IsHopByHop(string name)
        => string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "TE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Trailers", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Upgrade", StringComparison.OrdinalIgnoreCase);

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
}
