// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Proxy;

/// <summary>
/// Stage-A intercepting proxy: Kestrel-hosted plain-HTTP forward proxy.
/// Every browser-style absolute-form request (<c>GET http://example/foo</c>)
/// is forwarded to the upstream target and the full request+response is
/// recorded into <see cref="CapturedFlowStore"/>. HTTPS via CONNECT
/// tunnelling is acknowledged with <c>200 Connection Established</c> +
/// raw byte pump (no MITM yet) — Stage B will replace that with on-the-fly
/// certificate minting + TLS termination.
/// </summary>
public sealed class BowireProxyServer : IAsyncDisposable
{
    private readonly CapturedFlowStore _store;
    private readonly int _port;
    private readonly ILoggerFactory? _loggerFactory;
    private WebApplication? _app;

    public BowireProxyServer(CapturedFlowStore store, int port, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _store = store;
        _port = port;
        _loggerFactory = loggerFactory;
    }

    /// <summary>The actual TCP port the proxy is listening on. 0 until <see cref="StartAsync"/> returns.</summary>
    public int Port { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (_loggerFactory is not null)
        {
            builder.Services.AddSingleton(_loggerFactory);
        }

        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.Listen(IPAddress.Loopback, _port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
            });
        });

        var app = builder.Build();
        app.Run(ctx => HandleAsync(ctx));

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        // Read back the actually-bound port when the operator passed 0.
        var bound = app.Urls.FirstOrDefault();
        if (bound is not null && Uri.TryCreate(bound, UriKind.Absolute, out var uri))
        {
            Port = uri.Port;
        }
        else
        {
            Port = _port;
        }

        _app = app;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null) return;
        await _app.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5400:HttpClient may be created without enabling CheckCertificateRevocationList",
        Justification = "Stage-A proxy targets are typically dev hosts including self-signed dev certs; CRL toggle returns in Stage B together with operator opt-in.")]
    private async Task HandleAsync(HttpContext ctx)
    {
        // CONNECT — Stage A: passthrough TCP tunnel without MITM so https://
        // traffic isn't broken when the operator already points a browser at us.
        // Stage B will replace this branch with TLS termination + leaf cert minting.
        if (string.Equals(ctx.Request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            await TunnelConnectAsync(ctx).ConfigureAwait(false);
            return;
        }

        // Absolute-form request line (proxy mode): RequestUri is fully-qualified.
        // Relative-form requests from non-proxy-aware clients fall back to using
        // the Host header. Either way we end up with an absolute upstream URI.
        var upstream = BuildUpstreamUri(ctx.Request);
        if (upstream is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Bowire proxy: could not derive upstream URI from request.").ConfigureAwait(false);
            return;
        }

        var id = _store.NextId();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Snapshot the request before forwarding — body buffered into memory,
        // headers copied (minus hop-by-hop headers per RFC 7230 §6.1).
        var (reqBodyText, reqBodyB64) = await ReadBodyAsync(ctx.Request.Body, ctx.RequestAborted).ConfigureAwait(false);
        var reqHeaders = SnapshotHeaders(ctx.Request.Headers);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            // Stage A: accept self-signed by default — the dev host the operator
            // is poking at almost certainly has one. Stage B will gate this
            // behind --strict-tls.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        using var forwardMsg = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstream);
        var bodyBytes = reqBodyB64 is not null
            ? Convert.FromBase64String(reqBodyB64)
            : Encoding.UTF8.GetBytes(reqBodyText ?? "");
        if (bodyBytes.Length > 0 && !HttpMethodHasNoBody(ctx.Request.Method))
        {
            forwardMsg.Content = new ByteArrayContent(bodyBytes);
        }
        CopyRequestHeadersTo(ctx.Request.Headers, forwardMsg);

        int status = 0;
        IReadOnlyList<KeyValuePair<string, string>> respHeaders = Array.Empty<KeyValuePair<string, string>>();
        string? respBodyText = null;
        string? respBodyB64 = null;
        string? error = null;
        bool recorded = false;

        try
        {
            using var resp = await http.SendAsync(forwardMsg, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted)
                                       .ConfigureAwait(false);
            status = (int)resp.StatusCode;

            var collected = new List<KeyValuePair<string, string>>();
            foreach (var h in resp.Headers)
            {
                foreach (var v in h.Value) collected.Add(new(h.Key, v));
            }
            foreach (var h in resp.Content.Headers)
            {
                foreach (var v in h.Value) collected.Add(new(h.Key, v));
            }
            respHeaders = collected;

            var respBytes = await resp.Content.ReadAsByteArrayAsync(ctx.RequestAborted).ConfigureAwait(false);
            (respBodyText, respBodyB64) = ClassifyBytes(respBytes);
            stopwatch.Stop();

            // Record the captured flow BEFORE writing to the client. Tests
            // (and the workbench SSE subscriber) can correlate "client got
            // response" with "flow appears in /api/proxy/flows" without
            // racing the finally block.
            recorded = true;
            _store.Add(new CapturedFlow
            {
                Id = id,
                CapturedAt = startedAt,
                Method = ctx.Request.Method,
                Url = upstream.ToString(),
                Scheme = upstream.Scheme,
                RequestHeaders = reqHeaders,
                RequestBody = reqBodyText,
                RequestBodyBase64 = reqBodyB64,
                ResponseStatus = status,
                ResponseHeaders = respHeaders,
                ResponseBody = respBodyText,
                ResponseBodyBase64 = respBodyB64,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                Error = null,
            });

            // Replay to client — strip hop-by-hop headers so the client
            // socket stays happy (e.g. Transfer-Encoding once we already
            // buffered the body).
            ctx.Response.StatusCode = status;
            foreach (var (name, value) in collected)
            {
                if (IsHopByHop(name)) continue;
                if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                ctx.Response.Headers.Append(name, value);
            }
            ctx.Response.ContentLength = respBytes.Length;
            await ctx.Response.Body.WriteAsync(respBytes, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            status = 0;
            respHeaders = Array.Empty<KeyValuePair<string, string>>();
            respBodyText = null;
            respBodyB64 = null;
            error = ex.Message;
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                await ctx.Response.WriteAsync($"Bowire proxy: upstream forward failed: {ex.Message}").ConfigureAwait(false);
            }
        }
        finally
        {
            stopwatch.Stop();
            // Failure path: the flow wasn't recorded in the try-block, so do it now.
            if (!recorded)
            {
                _store.Add(new CapturedFlow
                {
                    Id = id,
                    CapturedAt = startedAt,
                    Method = ctx.Request.Method,
                    Url = upstream.ToString(),
                    Scheme = upstream.Scheme,
                    RequestHeaders = reqHeaders,
                    RequestBody = reqBodyText,
                    RequestBodyBase64 = reqBodyB64,
                    ResponseStatus = status,
                    ResponseHeaders = respHeaders,
                    ResponseBody = respBodyText,
                    ResponseBodyBase64 = respBodyB64,
                    LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                    Error = error,
                });
            }
        }
    }

    private static Uri? BuildUpstreamUri(HttpRequest req)
    {
        // Proxy-aware clients send absolute-form in the request line — Kestrel
        // surfaces the Host portion of that URL as req.Host. Non-proxy-aware
        // clients (curl http://proxy:8888/foo with Host: target.example) also
        // end up with req.Host pointing at the intended upstream, because
        // we re-host the request via the Host header. Either way the Host
        // header IS the canonical upstream identifier — request line vs.
        // header is just a transport detail.
        var host = req.Headers.Host.ToString();
        if (string.IsNullOrWhiteSpace(host)) return null;
        var path = req.Path.HasValue ? req.Path.Value : "/";
        var query = req.QueryString.HasValue ? req.QueryString.Value : "";
        var scheme = req.IsHttps ? "https" : "http";
        var combined = $"{scheme}://{host}{path}{query}";
        return Uri.TryCreate(combined, UriKind.Absolute, out var built) ? built : null;
    }

    private static async Task TunnelConnectAsync(HttpContext ctx)
    {
        // Stage A: TLS interception lands in Stage B. Until then, CONNECT
        // is rejected with 501 so the operator sees the proxy is reachable
        // but isn't pretending to MITM HTTPS yet.
        ctx.Response.StatusCode = StatusCodes.Status501NotImplemented;
        await ctx.Response.WriteAsync("Bowire proxy Stage A: HTTPS interception (CONNECT) lands in Stage B.").ConfigureAwait(false);
    }

    private static List<KeyValuePair<string, string>> SnapshotHeaders(IHeaderDictionary headers)
    {
        var list = new List<KeyValuePair<string, string>>(headers.Count);
        foreach (var (k, v) in headers)
        {
            foreach (var s in v) list.Add(new(k, s ?? ""));
        }
        return list;
    }

    private static void CopyRequestHeadersTo(IHeaderDictionary headers, HttpRequestMessage msg)
    {
        foreach (var (name, values) in headers)
        {
            if (IsHopByHop(name)) continue;
            if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (msg.Content is not null && values.Count > 0)
                {
                    msg.Content.Headers.TryAddWithoutValidation(name, values.ToArray());
                }
                continue;
            }
            if (!msg.Headers.TryAddWithoutValidation(name, values.ToArray()))
            {
                msg.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }
    }

    private static bool IsHopByHop(string name) => name.ToUpperInvariant() switch
    {
        "CONNECTION" or "KEEP-ALIVE" or "PROXY-AUTHENTICATE" or "PROXY-AUTHORIZATION"
            or "TE" or "TRAILERS" or "TRANSFER-ENCODING" or "UPGRADE" => true,
        _ => false,
    };

    private static bool HttpMethodHasNoBody(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("TRACE", StringComparison.OrdinalIgnoreCase);

    private static async Task<(string? text, string? base64)> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct).ConfigureAwait(false);
        if (ms.Length == 0) return (null, null);
        return ClassifyBytes(ms.ToArray());
    }

    private static (string? text, string? base64) ClassifyBytes(byte[] bytes)
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
        // Cheap heuristic — reject if any NUL bytes in first 4KB.
        var probe = Math.Min(bytes.Length, 4096);
        for (var i = 0; i < probe; i++)
        {
            if (bytes[i] == 0) return false;
        }
        return true;
    }
}
