// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Recording;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// Transparent in-process HTTP interceptor (#153). Sits in the host's
/// pipeline (registered via <c>app.UseBowireInterceptor()</c>) and
/// observes every request flowing through the host: method, path,
/// headers, body, response status, response headers, response body,
/// end-to-end latency. Recorded flows land in
/// <see cref="InterceptedFlowStore"/> and surface in the workbench's
/// "Intercepted" rail.
/// </summary>
/// <remarks>
/// <para>
/// Phase A (this shipment) is pass-through only — the middleware never
/// mutates the request or the response, and never short-circuits the
/// downstream pipeline. The bytes are tee'd into the capture store and
/// then forwarded verbatim. This honours the issue's acceptance test:
/// "a sample ASP.NET host with the interceptor on returns identical
/// responses to a baseline run for non-modified traffic."
/// </para>
/// <para>
/// Phase B opt-in: when an active <see cref="BowireRecordingSession"/>
/// is running, the middleware also appends the intercepted flow as a
/// recording step. The recording-session integration is the bridge that
/// lets the operator "click record, drive my host from any client,
/// click stop" — exactly the workflow the standalone proxy already
/// supports for tunnelled traffic.
/// </para>
/// <para>
/// Streaming-style responses (SSE, chunked transfer, WebSocket upgrade,
/// gRPC bi-di) are detected and recorded with empty bodies — the rail
/// renders a "streaming" badge instead of buffering the open stream.
/// This sidesteps the "buffer of doom" trap the ticket explicitly calls
/// out as Phase 4 polish.
/// </para>
/// </remarks>
internal sealed class BowireInterceptorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly InterceptedFlowStore _store;
    private readonly BowireRecordingSession? _session;
    private readonly BowireInterceptorOptions _options;
    private readonly ILogger<BowireInterceptorMiddleware>? _logger;

    public BowireInterceptorMiddleware(
        RequestDelegate next,
        InterceptedFlowStore store,
        IOptions<BowireInterceptorOptions> options,
        BowireRecordingSession? session = null,
        ILogger<BowireInterceptorMiddleware>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        _store = store;
        _options = options.Value ?? new BowireInterceptorOptions();
        _session = session;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled || ShouldIgnore(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var id = _store.NextId();
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Snapshot request side BEFORE invoking the pipeline. EnableBuffering
        // lets us re-read the body after the endpoint has consumed it — the
        // first read happens here for the capture; ASP.NET's body-reader rewinds
        // the stream so the endpoint sees the full payload.
        var method = context.Request.Method ?? "";
        var (url, path) = ReconstructUrl(context.Request);
        var scheme = context.Request.Scheme ?? "http";
        var requestHeaders = SnapshotHeaders(context.Request.Headers);

        string? requestBody = null;
        string? requestBodyBase64 = null;
        bool requestBodyTruncated = false;
        try
        {
            context.Request.EnableBuffering();
            var (body, b64, truncated) = await ReadBodyAsync(
                context.Request.Body, _options.MaxBodyBytes, context.RequestAborted).ConfigureAwait(false);
            requestBody = body;
            requestBodyBase64 = b64;
            requestBodyTruncated = truncated;

            // Rewind so the endpoint sees the untouched body. EnableBuffering
            // backs the stream with FileBufferingReadStream — Seek is supported.
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }
        }
        // Body capture is best-effort: a misconfigured ContentLength or a
        // disposed request stream should not break the host's response path.
        // Log + carry on with whatever bytes were captured up to the failure.
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (_logger is { } log && log.IsEnabled(LogLevel.Debug))
                log.LogDebug(ex, "bowire.interceptor: request body capture failed for {Method} {Path}", method, path);
        }

        // Response capture pattern: swap the host's IHttpResponseBodyFeature
        // for one backed by a MemoryStream, run the pipeline, then copy the
        // buffered bytes back to the real body before returning. Replacing the
        // feature (not just Response.Body) is the only path that also redirects
        // the PipeWriter-based fast paths minimal APIs use for JSON / Results.
        var originalBodyFeature = context.Features.Get<IHttpResponseBodyFeature>();
        using var captureBuffer = new MemoryStream();
        var captureFeature = new StreamResponseBodyFeature(captureBuffer);
        context.Features.Set<IHttpResponseBodyFeature>(captureFeature);

        string? error = null;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        // The host's own endpoint can throw anything. We let it propagate AFTER
        // recording the flow with the error message — so the workbench rail
        // shows the failure rather than silently swallowing it.
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = ex.Message;
            sw.Stop();
            try
            {
                // Restore feature before re-throwing so the host's error handler
                // can write to the real stream.
                if (originalBodyFeature is not null)
                {
                    context.Features.Set<IHttpResponseBodyFeature>(originalBodyFeature);
                }
                RecordFlow(id, startedAt, method, url, scheme, path,
                    requestHeaders, requestBody, requestBodyBase64, requestBodyTruncated,
                    context.Response.StatusCode,
                    SnapshotHeaders(context.Response.Headers),
                    null, null, false,
                    streaming: false,
                    latencyMs: (int)sw.ElapsedMilliseconds,
                    error: error);
            }
#pragma warning disable CA1031
            catch (Exception recordEx)
#pragma warning restore CA1031
            {
                if (_logger is { } log && log.IsEnabled(LogLevel.Debug))
                    log.LogDebug(recordEx, "bowire.interceptor: flow record failed during exception path");
            }
            throw;
        }

        sw.Stop();

        // Restore the original feature so the framework's response-completion
        // hooks see the host's real stream when the request unwinds.
        if (originalBodyFeature is not null)
        {
            context.Features.Set<IHttpResponseBodyFeature>(originalBodyFeature);
        }

        var streaming = IsStreamingResponse(context.Response);
        string? respBody = null;
        string? respB64 = null;
        bool respTruncated = false;

        if (!streaming && captureBuffer.Length > 0)
        {
            captureBuffer.Position = 0;
            // Copy buffered response back to the real body so the client
            // actually sees the payload. Done in chunks for any size — the
            // cap only governs how much we KEEP for the rail, not how much
            // we forward to the client.
            if (originalBodyFeature is not null)
            {
                await captureBuffer.CopyToAsync(originalBodyFeature.Stream, context.RequestAborted).ConfigureAwait(false);
            }

            captureBuffer.Position = 0;
            (respBody, respB64, respTruncated) = await SnapshotCapturedBodyAsync(captureBuffer, _options.MaxBodyBytes, context.RequestAborted).ConfigureAwait(false);
        }
        else if (streaming && captureBuffer.Length > 0 && originalBodyFeature is not null)
        {
            // Streaming responses still need to reach the client. We don't
            // capture the body content (Streaming flag is set) but we do
            // forward whatever bytes the endpoint emitted before we swapped
            // back.
            captureBuffer.Position = 0;
            await captureBuffer.CopyToAsync(originalBodyFeature.Stream, context.RequestAborted).ConfigureAwait(false);
        }

        RecordFlow(id, startedAt, method, url, scheme, path,
            requestHeaders, requestBody, requestBodyBase64, requestBodyTruncated,
            context.Response.StatusCode,
            SnapshotHeaders(context.Response.Headers),
            respBody, respB64, respTruncated,
            streaming: streaming,
            latencyMs: (int)sw.ElapsedMilliseconds,
            error: null);
    }

    private static async Task<(string? text, string? base64, bool truncated)> SnapshotCapturedBodyAsync(
        MemoryStream capture, int cap, CancellationToken ct)
    {
        var total = (int)capture.Length;
        var truncated = total > cap;
        var take = Math.Min(total, cap);
        var buf = new byte[take];
        var read = 0;
        while (read < take)
        {
            var n = await capture.ReadAsync(buf.AsMemory(read, take - read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        if (read == 0) return (null, null, truncated);
        if (read < buf.Length) Array.Resize(ref buf, read);
        var (text, base64) = ClassifyBytes(buf);
        return (text, base64, truncated);
    }

    private void RecordFlow(long id, DateTimeOffset startedAt, string method, string url, string scheme, string path,
        IReadOnlyList<KeyValuePair<string, string>> requestHeaders, string? requestBody, string? requestBodyBase64, bool requestBodyTruncated,
        int responseStatus, IReadOnlyList<KeyValuePair<string, string>> responseHeaders,
        string? responseBody, string? responseBodyBase64, bool responseBodyTruncated,
        bool streaming, int latencyMs, string? error)
    {
        var flow = new InterceptedFlow
        {
            Id = id,
            CapturedAt = startedAt,
            Method = method,
            Url = url,
            Scheme = scheme,
            Path = path,
            RequestHeaders = requestHeaders,
            RequestBody = requestBody,
            RequestBodyBase64 = requestBodyBase64,
            RequestBodyTruncated = requestBodyTruncated,
            ResponseStatus = responseStatus,
            ResponseHeaders = responseHeaders,
            ResponseBody = responseBody,
            ResponseBodyBase64 = responseBodyBase64,
            ResponseBodyTruncated = responseBodyTruncated,
            Streaming = streaming,
            LatencyMs = latencyMs,
            Error = error,
        };
        _store.Add(flow);

        // Phase B — when a recording session is open, append the intercepted
        // flow as a recording step. This is the bridge that lets an operator
        // "click record, drive any client at the host, click stop" — the same
        // workflow the standalone proxy supports for tunnelled traffic. The
        // session does the lookup itself; we just push the step in.
        var session = _session;
        if (session is not null)
        {
            try
            {
                var active = session.Active;
                if (active is not null && active.Mode == BowireRecordingMode.Capture)
                {
                    session.AppendStep(BuildRecordingStep(flow));
                }
            }
            // Recording append must not break the host's response: log + drop.
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_logger is { } log && log.IsEnabled(LogLevel.Debug))
                    log.LogDebug(ex, "bowire.interceptor: recording append failed");
            }
        }
    }

    private static BowireRecordingStep BuildRecordingStep(InterceptedFlow flow)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in flow.RequestHeaders)
        {
            // First-write-wins so duplicate headers (Set-Cookie style) don't
            // smash the slot — the workbench detail pane still shows every
            // value via the RequestHeaders list.
            if (!metadata.ContainsKey(k)) metadata[k] = v;
        }

        Uri.TryCreate(flow.Url, UriKind.Absolute, out var parsed);
        return new BowireRecordingStep
        {
            Id = Guid.NewGuid().ToString("N"),
            CapturedAt = flow.CapturedAt.ToUnixTimeMilliseconds(),
            Protocol = "rest",
            Service = parsed?.Host ?? "",
            Method = flow.Method,
            MethodType = "Unary",
            ServerUrl = parsed is null ? null : $"{parsed.Scheme}://{parsed.Authority}",
            HttpVerb = flow.Method,
            HttpPath = parsed?.PathAndQuery ?? flow.Path,
            Body = flow.RequestBody,
            Status = flow.ResponseStatus == 0
                ? "Error"
                : flow.ResponseStatus.ToString(CultureInfo.InvariantCulture),
            DurationMs = flow.LatencyMs,
            Response = flow.ResponseBody,
            Metadata = metadata,
        };
    }

    private bool ShouldIgnore(PathString requestPath)
    {
        if (!requestPath.HasValue) return false;
        var value = requestPath.Value!;
        foreach (var prefix in _options.IgnoredPathPrefixes)
        {
            if (string.IsNullOrEmpty(prefix)) continue;
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
            {
                list.Add(new KeyValuePair<string, string>(pair.Key, v ?? string.Empty));
            }
        }
        return list;
    }

    private static async Task<(string? text, string? base64, bool truncated)> ReadBodyAsync(
        Stream body, int cap, CancellationToken ct)
    {
        if (body is null || cap <= 0) return (null, null, false);
        if (body is { CanRead: false }) return (null, null, false);

        using var buffer = new MemoryStream();
        var pool = new byte[8192];
        var truncated = false;
        var totalCapacity = cap;
        while (buffer.Length < totalCapacity)
        {
            var remaining = (int)(totalCapacity - buffer.Length);
            var read = await body.ReadAsync(pool.AsMemory(0, Math.Min(pool.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0) break;
            // In-memory MemoryStream.Write is fine to call synchronously —
            // CA1849 fires generically, but there's no real I/O to await.
#pragma warning disable CA1849
            buffer.Write(pool, 0, read);
#pragma warning restore CA1849
        }

        // Probe for overflow only when we hit the cap — if we read fewer
        // bytes than the cap we already have the full body.
        if (buffer.Length == totalCapacity)
        {
            var probe = await body.ReadAsync(pool.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (probe > 0) truncated = true;
        }

        if (buffer.Length == 0) return (null, null, truncated);
        var bytes = buffer.ToArray();
        var (text, base64) = ClassifyBytes(bytes);
        return (text, base64, truncated);
    }

    private static bool IsStreamingResponse(HttpResponse response)
    {
        if (response.Headers.TryGetValue("Content-Type", out var ct))
        {
            foreach (var v in ct)
            {
                if (v is null) continue;
                if (v.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)) return true;
                if (v.Contains("application/grpc", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        if (response.Headers.TryGetValue("Transfer-Encoding", out var te))
        {
            foreach (var v in te)
            {
                if (v is null) continue;
                if (v.Contains("chunked", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        if (response.StatusCode == 101) return true; // protocol switch (WebSocket upgrade)
        return false;
    }

    internal static (string? text, string? base64) ClassifyBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return (null, null);
        if (IsLikelyUtf8(bytes))
        {
            try
            {
                var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
                return (utf8.GetString(bytes), null);
            }
            catch (DecoderFallbackException) { /* fall through to base64 */ }
        }
        return (null, Convert.ToBase64String(bytes));
    }

    private static bool IsLikelyUtf8(byte[] bytes)
    {
        var probe = Math.Min(bytes.Length, 4096);
        for (var i = 0; i < probe; i++)
        {
            if (bytes[i] == 0) return false;
        }
        return true;
    }

}
