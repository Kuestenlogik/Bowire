// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Proxy;

/// <summary>
/// Tier-3 intercepting proxy. Plain HTTP requests are parsed, forwarded
/// to the upstream identified by the absolute-form request line (or by
/// the <c>Host</c> header), and recorded into
/// <see cref="CapturedFlowStore"/>. HTTPS interception (Stage B) is
/// driven by a CONNECT-tunnel TLS termination: the proxy mints a leaf
/// certificate for the requested host via
/// <see cref="BowireProxyCertificateAuthority"/>, presents it on the
/// client side of the tunnel, and then re-issues each request over
/// real TLS to the upstream — so the captured flow has the decrypted
/// request + response body, not opaque encrypted bytes.
/// </summary>
/// <remarks>
/// <para>
/// One TCP connection ⇒ one captured request/response pair. The server
/// closes the client + upstream sockets after each exchange (HTTP/1.0
/// shape) — the simpler streaming code-path drops keep-alive parsing
/// for v1, browsers handle that fine by re-connecting per request.
/// CONNECT-tunnelled TLS connections handle one HTTP request per
/// tunnel for the same reason.
/// </para>
/// </remarks>
public sealed class BowireProxyServer : IAsyncDisposable
{
    private readonly CapturedFlowStore _store;
    private readonly BowireProxyCertificateAuthority? _ca;
    private readonly int _requestedPort;
    private readonly ILogger? _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public BowireProxyServer(CapturedFlowStore store, int port, BowireProxyCertificateAuthority? ca = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _store = store;
        _requestedPort = port;
        _ca = ca;
        _logger = loggerFactory?.CreateLogger("bowire.proxy");
    }

    /// <summary>The actual TCP port the proxy is listening on. 0 until <see cref="StartAsync"/> returns.</summary>
    public int Port { get; private set; }

    /// <summary>Whether HTTPS interception (CONNECT-MITM) is enabled. Driven by the CA being non-null.</summary>
    public bool HttpsInterceptionEnabled => _ca is not null;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _listener?.Dispose();
        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            // Fire and forget — one task per connection so a slow upstream
            // doesn't block other in-flight proxy traffic.
            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var requestLine = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(requestLine)) return;

                if (requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleConnectTunnelAsync(stream, requestLine, ct).ConfigureAwait(false);
                }
                else
                {
                    await HandlePlainHttpAsync(stream, requestLine, scheme: "http", ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (IOException) { /* peer reset */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "bowire.proxy: connection handler failed");
        }
    }

    // ------------------------------------------------------------------
    // Plain HTTP path
    // ------------------------------------------------------------------

    private async Task HandlePlainHttpAsync(Stream clientStream, string requestLine, string scheme, CancellationToken ct)
    {
        if (!TryParseRequestLine(requestLine, out var method, out var target, out _))
        {
            await WriteStatusLineAsync(clientStream, 400, "Bad Request", ct).ConfigureAwait(false);
            return;
        }

        var headers = await ReadHeadersAsync(clientStream, ct).ConfigureAwait(false);
        var bodyBytes = await ReadBodyForRequestAsync(clientStream, method, headers, ct).ConfigureAwait(false);

        var upstream = ResolveUpstreamUri(target, headers, scheme);
        if (upstream is null)
        {
            await WriteStatusLineAsync(clientStream, 400, "Bad Request", ct).ConfigureAwait(false);
            return;
        }

        var id = _store.NextId();
        var startedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var reqHeaders = SnapshotHeaders(headers);
        var (reqBodyText, reqBodyB64) = ClassifyBytes(bodyBytes);

        int status = 0;
        IReadOnlyList<KeyValuePair<string, string>> respHeaders = Array.Empty<KeyValuePair<string, string>>();
        string? respBodyText = null;
        string? respBodyB64 = null;
        string? error = null;
        bool recorded = false;

        try
        {
            var (respStatus, respHdrs, respBytes) = await ForwardAsync(method, upstream, headers, bodyBytes, ct).ConfigureAwait(false);
            status = respStatus;
            respHeaders = respHdrs;
            (respBodyText, respBodyB64) = ClassifyBytes(respBytes);
            sw.Stop();

            recorded = true;
            _store.Add(BuildFlow(id, startedAt, method, upstream, reqHeaders, reqBodyText, reqBodyB64,
                status, respHeaders, respBodyText, respBodyB64, (int)sw.ElapsedMilliseconds, error: null));

            await WriteHttpResponseAsync(clientStream, status, respHdrs, respBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { await WriteStatusLineAsync(clientStream, 502, "Bad Gateway", ct).ConfigureAwait(false); }
            catch (IOException) { /* client already gone */ }
        }
        finally
        {
            sw.Stop();
            if (!recorded)
            {
                _store.Add(BuildFlow(id, startedAt, method, upstream, reqHeaders, reqBodyText, reqBodyB64,
                    status, respHeaders, respBodyText, respBodyB64, (int)sw.ElapsedMilliseconds, error));
            }
        }
    }

    // ------------------------------------------------------------------
    // HTTPS MITM path (CONNECT tunnel)
    // ------------------------------------------------------------------

    private async Task HandleConnectTunnelAsync(NetworkStream clientStream, string requestLine, CancellationToken ct)
    {
        // CONNECT host:port HTTP/1.1
        var parts = requestLine.Split(' ', 3);
        if (parts.Length < 2)
        {
            await WriteStatusLineAsync(clientStream, 400, "Bad Request", ct).ConfigureAwait(false);
            return;
        }
        var hostPort = parts[1];
        var colon = hostPort.LastIndexOf(':');
        var host = colon > 0 ? hostPort[..colon] : hostPort;
        var port = colon > 0 && int.TryParse(hostPort[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 443;

        // Consume the rest of the CONNECT header (we ignore it).
        await ReadHeadersAsync(clientStream, ct).ConfigureAwait(false);

        if (_ca is null)
        {
            // Stage A behaviour kept as a fallback when the operator
            // disabled HTTPS interception via the CLI.
            await WriteStatusLineAsync(clientStream, 501, "Not Implemented", ct,
                body: "Bowire proxy: HTTPS interception disabled (start with --mitm-https to enable).").ConfigureAwait(false);
            return;
        }

        // Acknowledge the tunnel, then upgrade the client stream to TLS
        // with a freshly-minted leaf certificate.
        await WriteStatusLineAsync(clientStream, 200, "Connection Established", ct).ConfigureAwait(false);

        X509Certificate2 leaf;
        try { leaf = _ca.GetOrMintLeaf(host); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "bowire.proxy: leaf-cert mint failed for {Host}", host);
            return;
        }

        await using var tlsClient = new SslStream(clientStream, leaveInnerStreamOpen: false);
        try
        {
            await tlsClient.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(leaf, additionalCertificates: null, offline: true),
                ClientCertificateRequired = false,
#pragma warning disable CA5398 // "Avoid hardcoded SslProtocols" — we explicitly pin TLS 1.2/1.3 because intercepted clients may probe TLS 1.0/1.1 and the MITM proxy is the place to refuse those.
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
#pragma warning restore CA5398
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            if (_logger is { } log && log.IsEnabled(LogLevel.Debug))
                log.LogDebug(ex, "bowire.proxy: client-side TLS handshake failed for {Host}", host);
            return;
        }

        // Now read the inner HTTP request off the TLS stream + forward
        // to the real upstream over real TLS.
        var requestLineInner = await ReadLineAsync(tlsClient, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(requestLineInner)) return;
        await HandleTunneledHttpAsync(tlsClient, requestLineInner, host, port, ct).ConfigureAwait(false);
    }

    private async Task HandleTunneledHttpAsync(Stream tlsClient, string requestLine, string host, int port, CancellationToken ct)
    {
        if (!TryParseRequestLine(requestLine, out var method, out var target, out _))
        {
            await WriteStatusLineAsync(tlsClient, 400, "Bad Request", ct).ConfigureAwait(false);
            return;
        }

        var headers = await ReadHeadersAsync(tlsClient, ct).ConfigureAwait(false);
        var bodyBytes = await ReadBodyForRequestAsync(tlsClient, method, headers, ct).ConfigureAwait(false);

        // The inner request line is path-only (origin-form) for tunneled
        // HTTPS. Reconstruct the absolute URI from CONNECT host:port.
        var path = target.StartsWith('/') ? target : "/" + target;
        var upstream = new Uri($"https://{host}:{port}{path}", UriKind.Absolute);

        var id = _store.NextId();
        var startedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reqHeaders = SnapshotHeaders(headers);
        var (reqBodyText, reqBodyB64) = ClassifyBytes(bodyBytes);

        int status = 0;
        IReadOnlyList<KeyValuePair<string, string>> respHeaders = Array.Empty<KeyValuePair<string, string>>();
        string? respBodyText = null;
        string? respBodyB64 = null;
        string? error = null;
        bool recorded = false;

        try
        {
            var (respStatus, respHdrs, respBytes) = await ForwardAsync(method, upstream, headers, bodyBytes, ct).ConfigureAwait(false);
            status = respStatus;
            respHeaders = respHdrs;
            (respBodyText, respBodyB64) = ClassifyBytes(respBytes);
            sw.Stop();

            recorded = true;
            _store.Add(BuildFlow(id, startedAt, method, upstream, reqHeaders, reqBodyText, reqBodyB64,
                status, respHeaders, respBodyText, respBodyB64, (int)sw.ElapsedMilliseconds, error: null));

            await WriteHttpResponseAsync(tlsClient, status, respHdrs, respBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { await WriteStatusLineAsync(tlsClient, 502, "Bad Gateway", ct).ConfigureAwait(false); }
            catch (IOException) { /* tunnel torn down */ }
        }
        finally
        {
            sw.Stop();
            if (!recorded)
            {
                _store.Add(BuildFlow(id, startedAt, method, upstream, reqHeaders, reqBodyText, reqBodyB64,
                    status, respHeaders, respBodyText, respBodyB64, (int)sw.ElapsedMilliseconds, error));
            }
        }
    }

    // ------------------------------------------------------------------
    // Forwarding via HttpClient (HTTPS upstream gets the real cert chain)
    // ------------------------------------------------------------------

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5400:HttpClient may be created without enabling CheckCertificateRevocationList",
        Justification = "Proxy targets are operator-chosen dev hosts; CRL toggle gate lands with the wider --strict-tls flag.")]
    private static async Task<(int status, IReadOnlyList<KeyValuePair<string, string>> headers, byte[] body)> ForwardAsync(
        string method, Uri upstream, Dictionary<string, string> reqHeaders, byte[] reqBody, CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        using var fwd = new HttpRequestMessage(new HttpMethod(method), upstream);
        if (reqBody.Length > 0 && !HttpMethodHasNoBody(method))
        {
            fwd.Content = new ByteArrayContent(reqBody);
        }
        foreach (var (k, v) in reqHeaders)
        {
            if (IsHopByHop(k)) continue;
            if (k.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                fwd.Content?.Headers.TryAddWithoutValidation(k, v);
                continue;
            }
            if (!fwd.Headers.TryAddWithoutValidation(k, v))
            {
                fwd.Content?.Headers.TryAddWithoutValidation(k, v);
            }
        }

        using var resp = await http.SendAsync(fwd, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var collected = new List<KeyValuePair<string, string>>();
        foreach (var h in resp.Headers)
            foreach (var v in h.Value) collected.Add(new(h.Key, v));
        foreach (var h in resp.Content.Headers)
            foreach (var v in h.Value) collected.Add(new(h.Key, v));

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, collected, bytes);
    }

    private static async Task WriteHttpResponseAsync(Stream client, int status, IReadOnlyList<KeyValuePair<string, string>> headers, byte[] body, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {StatusReason(status)}\r\n");
        var sawConnection = false;
        foreach (var (k, v) in headers)
        {
            if (IsHopByHop(k)) continue;
            if (k.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (k.Equals("Connection", StringComparison.OrdinalIgnoreCase)) sawConnection = true;
            sb.Append(CultureInfo.InvariantCulture, $"{k}: {v}\r\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n");
        if (!sawConnection) sb.Append("Connection: close\r\n");
        sb.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await client.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (body.Length > 0)
            await client.WriteAsync(body, ct).ConfigureAwait(false);
        await client.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteStatusLineAsync(Stream client, int status, string reason, CancellationToken ct, string? body = null)
    {
        var bodyBytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {reason}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {bodyBytes.Length}\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await client.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (bodyBytes.Length > 0) await client.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await client.FlushAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // HTTP-1.x wire format helpers
    // ------------------------------------------------------------------

    private static bool TryParseRequestLine(string line, out string method, out string target, out string version)
    {
        method = target = version = "";
        var first = line.IndexOf(' ');
        var last = line.LastIndexOf(' ');
        if (first <= 0 || last <= first) return false;
        method = line[..first];
        target = line[(first + 1)..last];
        version = line[(last + 1)..];
        return true;
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        var prev = (byte)0;
        while (true)
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return sb.ToString();
            var b = buf[0];
            if (prev == (byte)'\r' && b == (byte)'\n')
            {
                // Drop trailing \r.
                if (sb.Length > 0) sb.Length--;
                return sb.ToString();
            }
            sb.Append((char)b);
            prev = b;
            if (sb.Length > 16 * 1024) throw new InvalidOperationException("request header line too long");
        }
    }

    private static async Task<Dictionary<string, string>> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line)) return headers;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (headers.TryGetValue(name, out var existing))
                headers[name] = existing + ", " + value;
            else
                headers[name] = value;
        }
    }

    private static async Task<byte[]> ReadBodyForRequestAsync(Stream stream, string method, Dictionary<string, string> headers, CancellationToken ct)
    {
        if (HttpMethodHasNoBody(method)) return Array.Empty<byte>();
        if (!headers.TryGetValue("Content-Length", out var clRaw)) return Array.Empty<byte>();
        if (!int.TryParse(clRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contentLength) || contentLength <= 0)
            return Array.Empty<byte>();

        var buf = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read, contentLength - read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        if (read < contentLength) Array.Resize(ref buf, read);
        return buf;
    }

    private static Uri? ResolveUpstreamUri(string target, Dictionary<string, string> headers, string scheme)
    {
        // Absolute-form (proxy-aware client): target IS the URL.
        if (Uri.TryCreate(target, UriKind.Absolute, out var direct)) return direct;

        // Origin-form: combine with Host header.
        if (!headers.TryGetValue("Host", out var host) || string.IsNullOrEmpty(host)) return null;
        var path = target.StartsWith('/') ? target : "/" + target;
        return Uri.TryCreate($"{scheme}://{host}{path}", UriKind.Absolute, out var built) ? built : null;
    }

    private static List<KeyValuePair<string, string>> SnapshotHeaders(Dictionary<string, string> headers)
    {
        var list = new List<KeyValuePair<string, string>>(headers.Count);
        foreach (var (k, v) in headers) list.Add(new(k, v));
        return list;
    }

    private static CapturedFlow BuildFlow(long id, DateTimeOffset startedAt, string method, Uri upstream,
        IReadOnlyList<KeyValuePair<string, string>> reqHeaders, string? reqBodyText, string? reqBodyB64,
        int status, IReadOnlyList<KeyValuePair<string, string>> respHeaders, string? respBodyText, string? respBodyB64,
        int latencyMs, string? error) => new()
    {
        Id = id,
        CapturedAt = startedAt,
        Method = method,
        Url = upstream.ToString(),
        Scheme = upstream.Scheme,
        RequestHeaders = reqHeaders,
        RequestBody = reqBodyText,
        RequestBodyBase64 = reqBodyB64,
        ResponseStatus = status,
        ResponseHeaders = respHeaders,
        ResponseBody = respBodyText,
        ResponseBodyBase64 = respBodyB64,
        LatencyMs = latencyMs,
        Error = error,
    };

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
        var probe = Math.Min(bytes.Length, 4096);
        for (var i = 0; i < probe; i++)
        {
            if (bytes[i] == 0) return false;
        }
        return true;
    }

    private static string StatusReason(int code) => code switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        _ => "OK",
    };
}
