// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Protocol.Grpc;

/// <summary>
/// Connect (Buf) unary invoker. Different wire envelope than gRPC /
/// gRPC-Web — but the same .proto schema, the same descriptor walk,
/// the same JSON↔protobuf round-trip. Talks plain HTTP/1.1 + HTTP/2
/// (whichever the server prefers), so there's no <c>GrpcChannel</c>
/// at all in this path.
/// </summary>
/// <remarks>
/// <para>
/// Wire shape for unary:
/// </para>
/// <list type="bullet">
///   <item><c>POST &lt;baseUrl&gt;/&lt;service&gt;/&lt;method&gt;</c></item>
///   <item><c>Content-Type: application/proto</c> (protobuf binary)</item>
///   <item><c>Connect-Protocol-Version: 1</c></item>
///   <item>Body = single serialised protobuf message</item>
///   <item>200 + protobuf body → success, response decoded via descriptor</item>
///   <item>Non-2xx + JSON body <c>{ "code", "message" }</c> → Connect error;
///   <c>code</c> maps onto the gRPC-canonical code names (invalid_argument,
///   unauthenticated, internal, &amp;c). HTTP status codes map per the
///   Connect spec.</item>
/// </list>
/// <para>
/// Server-streaming (Phase 2) uses a different envelope —
/// <c>application/connect+proto</c> with one length-prefixed frame
/// for the request and a stream of length-prefixed frames for the
/// response, each frame's first byte carrying flag bits (bit 0x02 =
/// end-of-stream marker, payload then is a JSON envelope with
/// <c>error</c> + <c>metadata</c>). Client-streaming + bidi (Phase 3)
/// still surface the not-supported error.
/// </para>
/// </remarks>
internal sealed class ConnectInvoker : IDisposable
{
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _ownedHandler;
    private readonly MtlsHandlerOwner? _mtlsOwner;

    private const string ProtocolVersionHeader = "Connect-Protocol-Version";
    private const string ProtocolVersionValue = "1";
    private const string ProtobufContentType = "application/proto";
    private const string StreamingProtobufContentType = "application/connect+proto";

    /// <summary>
    /// Bit set on the leading flag byte of a Connect end-of-stream
    /// envelope. When read on the server-streaming path, the frame's
    /// payload is JSON (not protobuf) and carries optional
    /// <c>error</c> / <c>metadata</c> blocks instead of a typed
    /// response message.
    /// </summary>
    internal const byte EndStreamFlag = 0x02;

    public ConnectInvoker(
        string serverUrl,
        MtlsConfig? mtlsConfig = null,
        IConfiguration? configuration = null)
    {
        _baseUri = new Uri(NormaliseBaseUrl(serverUrl), UriKind.Absolute);

        if (mtlsConfig is not null)
        {
            _mtlsOwner = MtlsHandlerOwner.CreateSocketsHttpHandler(mtlsConfig, out var mtlsError);
            if (_mtlsOwner is null)
            {
                throw new InvalidOperationException(mtlsError ?? "mTLS configuration invalid");
            }
            // _mtlsOwner owns the handler — pass disposeHandler:false
            // so HttpClient doesn't double-free it on disposal.
            _http = new HttpClient(_mtlsOwner.Handler, disposeHandler: false);
        }
        else
        {
            // Track the handler on _ownedHandler so Dispose can free
            // it deterministically — CA2000 catches the leak window
            // between handler-create and HttpClient-ctor otherwise.
            _ownedHandler = BowireHttpClientFactory.CreateSocketsHttpHandler(
                configuration, "grpc", serverUrl);
            _http = new HttpClient(_ownedHandler, disposeHandler: false);
        }
    }

    public async Task<InvokeResult> InvokeUnaryAsync(
        string serviceName, string methodName,
        MessageDescriptor inputType,
        MessageDescriptor outputType,
        string requestJson,
        Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        // Connect URL shape: <baseUri>/<service>/<method>. <service>
        // must be the fully-qualified name (Buf's convention); the
        // caller already passes that in. We join robustly so trailing
        // slashes on baseUri don't double up.
        var path = $"{serviceName.TrimStart('/')}/{methodName.TrimStart('/')}";
        var target = new Uri(_baseUri, path);

        // Encode the request: JSON → protobuf via the descriptor. The
        // descriptor is the same one the native + gRPC-Web paths use, so
        // discovery (gRPC Reflection or a .proto upload) covers Connect
        // for free.
        var requestBytes = JsonToProtobufBytes(requestJson, inputType);

        using var content = new ByteArrayContent(requestBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(ProtobufContentType);

        using var req = new HttpRequestMessage(HttpMethod.Post, target) { Content = content };
        req.Headers.TryAddWithoutValidation(ProtocolVersionHeader, ProtocolVersionValue);

        // User-supplied metadata flows as HTTP headers. Connect doesn't
        // distinguish "trailers" the way gRPC does, so this is a straight
        // header pass-through.
        if (metadata is not null)
        {
            foreach (var (k, v) in metadata)
            {
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        sw.Stop();

        var responseMetadata = ExtractResponseHeaders(resp);

        if (resp.IsSuccessStatusCode)
        {
            var json = ProtobufBytesToJson(bodyBytes, outputType);
            return new InvokeResult(
                Response: json,
                DurationMs: sw.ElapsedMilliseconds,
                Status: "OK",
                Metadata: responseMetadata,
                ResponseBinary: bodyBytes);
        }

        // Connect error envelope is JSON even when the request was
        // protobuf. Decode and surface the code + message; if it isn't
        // valid Connect error JSON, fall back to the HTTP reason +
        // raw body so the user sees *something* useful.
        var (code, message) = ParseConnectError(bodyBytes, resp);
        return new InvokeResult(
            Response: message,
            DurationMs: sw.ElapsedMilliseconds,
            Status: code,
            Metadata: responseMetadata);
    }

    private static string NormaliseBaseUrl(string serverUrl)
    {
        // Treat grpc:// + grpcs:// the same as http:// + https:// for
        // the Connect path — Connect runs on plain HTTP semantics.
        if (serverUrl.StartsWith("grpcs://", StringComparison.OrdinalIgnoreCase))
            return "https://" + serverUrl["grpcs://".Length..];
        if (serverUrl.StartsWith("grpc://", StringComparison.OrdinalIgnoreCase))
            return "http://" + serverUrl["grpc://".Length..];
        return serverUrl;
    }

    private static byte[] JsonToProtobufBytes(string json, MessageDescriptor inputType)
    {
        var message = JsonParser.Default.Parse(json, inputType);
        return message.ToByteArray();
    }

    private static string ProtobufBytesToJson(byte[] bytes, MessageDescriptor outputType)
    {
        if (bytes.Length == 0) return "{}";
        var message = outputType.Parser.ParseFrom(bytes);
        return JsonFormatter.Default.Format(message);
    }

    /// <summary>
    /// Decode the Connect error envelope. Returns ("connect:&lt;code&gt;",
    /// message). When the body isn't a valid Connect error JSON, falls
    /// back to the HTTP status line. The "connect:" prefix lets recording
    /// consumers tell a Connect error apart from a gRPC <c>StatusCode</c>
    /// when the two share names (both have "internal", "unavailable",
    /// &amp;c).
    /// </summary>
    private static (string Code, string Message) ParseConnectError(byte[] body, HttpResponseMessage resp)
    {
        if (body.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    string? code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString() : null;
                    string? message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() : null;
                    if (!string.IsNullOrEmpty(code))
                    {
                        return ("connect:" + code, message ?? string.Empty);
                    }
                }
            }
            catch (JsonException) { /* fall through to HTTP-only shape */ }
        }
        var fallback = string.IsNullOrEmpty(resp.ReasonPhrase)
            ? resp.StatusCode.ToString()
            : resp.ReasonPhrase;
        return ($"http:{(int)resp.StatusCode}", fallback);
    }

    private static Dictionary<string, string> ExtractResponseHeaders(HttpResponseMessage resp)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in resp.Headers)
        {
            dict[k] = string.Join(", ", v);
        }
        foreach (var (k, v) in resp.Content.Headers)
        {
            dict[k] = string.Join(", ", v);
        }
        return dict;
    }

    /// <summary>
    /// Connect server-streaming. POST one length-prefixed input
    /// envelope to the same URL as unary, switch the content type to
    /// <c>application/connect+proto</c>, read the response stream
    /// frame-by-frame and yield each non-end frame as a JSON-formatted
    /// response message. The terminating end-of-stream frame's JSON
    /// payload (with optional <c>error</c> / <c>metadata</c>) ends the
    /// enumeration; if it carries an <c>error</c> the last yielded
    /// item is a synthesized JSON object describing it, prefixed so
    /// callers can distinguish stream errors from real messages.
    /// </summary>
    public async IAsyncEnumerable<ConnectStreamFrame> InvokeServerStreamAsync(
        string serviceName, string methodName,
        MessageDescriptor inputType,
        MessageDescriptor outputType,
        string requestJson,
        Dictionary<string, string>? metadata,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var path = $"{serviceName.TrimStart('/')}/{methodName.TrimStart('/')}";
        var target = new Uri(_baseUri, path);

        // Encode the single request message as one length-prefixed
        // frame: 1 byte flags + 4 byte big-endian length + payload.
        var payload = JsonToProtobufBytes(requestJson, inputType);
        var requestFrame = EncodeFrame(flags: 0x00, payload: payload);

        using var content = new ByteArrayContent(requestFrame);
        content.Headers.ContentType = new MediaTypeHeaderValue(StreamingProtobufContentType);

        using var req = new HttpRequestMessage(HttpMethod.Post, target) { Content = content };
        req.Headers.TryAddWithoutValidation(ProtocolVersionHeader, ProtocolVersionValue);
        if (metadata is not null)
        {
            foreach (var (k, v) in metadata)
                req.Headers.TryAddWithoutValidation(k, v);
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // Non-2xx before any frames flows through the unary error
            // path — Connect uses the same JSON error shape there.
            var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var (code, message) = ParseConnectError(bodyBytes, resp);
            yield return ConnectStreamFrame.Error(code, message);
            yield break;
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);

        await foreach (var frame in ReadFramesAsync(stream, ct).ConfigureAwait(false))
        {
            if ((frame.Flags & EndStreamFlag) == EndStreamFlag)
            {
                // End-of-stream: payload is JSON. If it carries an
                // `error` object, surface it; otherwise terminate
                // silently — Connect's normal OK exit.
                var (errCode, errMessage) = ParseEndOfStreamError(frame.Payload);
                if (errCode is not null)
                    yield return ConnectStreamFrame.Error(errCode, errMessage ?? string.Empty);
                yield break;
            }
            // Normal frame: protobuf-encoded response message.
            var json = ProtobufBytesToJson(frame.Payload, outputType);
            yield return ConnectStreamFrame.Message(json, frame.Payload);
        }
    }

    /// <summary>
    /// Connect Phase 3 — client-streaming. POST multiple length-prefixed
    /// request frames (one per input JSON), receive one length-prefixed
    /// response frame, then an end-of-stream frame. Same envelope as
    /// server-streaming; the asymmetry is that we send N and receive 1
    /// instead of the reverse. Requires HTTP/2 because the request body
    /// has to land in length-prefixed frames before the server
    /// responds — which Connect treats as "stream the request, then
    /// the server emits its reply". HTTP/1.1 would work for the
    /// no-trailer pre-buffered case the BCL does today, but we keep
    /// HTTP/2 explicit so the wire matches the Connect spec.
    /// </summary>
    public async Task<InvokeResult> InvokeClientStreamAsync(
        string serviceName, string methodName,
        MessageDescriptor inputType,
        MessageDescriptor outputType,
        List<string> requestJsons,
        Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requestJsons);

        var path = $"{serviceName.TrimStart('/')}/{methodName.TrimStart('/')}";
        var target = new Uri(_baseUri, path);

        // Encode all request messages into one byte buffer of
        // length-prefixed frames. Pre-buffering is fine for client-
        // streaming v1 because the workbench already holds every
        // request message in memory before the call starts — we don't
        // need true full-duplex for this leg. Bidi (below) does need
        // it and uses a pipe.
        // Pre-allocate the request buffer (sum of every framed payload
        // length + 5 bytes header per frame). Avoids MemoryStream's
        // synchronous-Write CA1849 noise while keeping the call hot
        // path allocation-light.
        var totalLen = 0;
        var encoded = new List<byte[]>(requestJsons.Count);
        foreach (var json in requestJsons)
        {
            var payload = JsonToProtobufBytes(json, inputType);
            var frame = EncodeFrame(flags: 0x00, payload: payload);
            encoded.Add(frame);
            totalLen += frame.Length;
        }
        var requestBuffer = new byte[totalLen];
        var offset = 0;
        foreach (var frame in encoded)
        {
            Buffer.BlockCopy(frame, 0, requestBuffer, offset, frame.Length);
            offset += frame.Length;
        }

        using var content = new ByteArrayContent(requestBuffer);
        content.Headers.ContentType = new MediaTypeHeaderValue(StreamingProtobufContentType);

        using var req = new HttpRequestMessage(HttpMethod.Post, target) { Content = content };
        req.Version = HttpVersion.Version20;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        req.Headers.TryAddWithoutValidation(ProtocolVersionHeader, ProtocolVersionValue);
        if (metadata is not null)
        {
            foreach (var (k, v) in metadata)
                req.Headers.TryAddWithoutValidation(k, v);
        }

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            sw.Stop();
            var (code, message) = ParseConnectError(bodyBytes, resp);
            return new InvokeResult(message, sw.ElapsedMilliseconds, code,
                ExtractResponseHeaders(resp));
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);

        string? responseJson = null;
        byte[]? responseBinary = null;
        string? errCode = null;
        string? errMessage = null;

        await foreach (var frame in ReadFramesAsync(stream, ct).ConfigureAwait(false))
        {
            if ((frame.Flags & EndStreamFlag) == EndStreamFlag)
            {
                var (c, m) = ParseEndOfStreamError(frame.Payload);
                errCode = c;
                errMessage = m;
                break;
            }
            // Client-streaming returns exactly one response message per
            // spec. If a server somehow emits more, the latest one wins
            // and we keep the binary of the last frame — matches gRPC's
            // "trailers contain the last status" pattern.
            responseJson = ProtobufBytesToJson(frame.Payload, outputType);
            responseBinary = frame.Payload;
        }
        sw.Stop();

        var headers = ExtractResponseHeaders(resp);
        if (errCode is not null)
        {
            return new InvokeResult(errMessage ?? string.Empty,
                sw.ElapsedMilliseconds, errCode, headers);
        }
        return new InvokeResult(
            Response: responseJson ?? "{}",
            DurationMs: sw.ElapsedMilliseconds,
            Status: "OK",
            Metadata: headers,
            ResponseBinary: responseBinary);
    }

    /// <summary>
    /// Connect Phase 3 — bidirectional streaming. Full-duplex HTTP/2
    /// POST: the workbench writes its request frames concurrently with
    /// reading the server's response frames. End-of-stream comes from
    /// the server as the terminating frame (flags bit 0x02).
    /// </summary>
    /// <remarks>
    /// Implementation uses <see cref="System.IO.Pipelines.Pipe"/> to
    /// hand the request body off to <see cref="HttpClient"/> while the
    /// caller can still write frames into the producer side. The
    /// reader side runs concurrently as it pulls response frames off
    /// the response stream. The producer loop exits — and the
    /// <c>PipeWriter</c> is completed — once <paramref name="requestJsons"/>
    /// is exhausted, signalling the end of the request half. The
    /// server's end-of-stream frame ends the consumer half.
    /// </remarks>
    public async IAsyncEnumerable<ConnectStreamFrame> InvokeBidiStreamAsync(
        string serviceName, string methodName,
        MessageDescriptor inputType,
        MessageDescriptor outputType,
        List<string> requestJsons,
        Dictionary<string, string>? metadata,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requestJsons);

        var path = $"{serviceName.TrimStart('/')}/{methodName.TrimStart('/')}";
        var target = new Uri(_baseUri, path);

        var pipe = new System.IO.Pipelines.Pipe();

        using var content = new StreamContent(pipe.Reader.AsStream(leaveOpen: false));
        content.Headers.ContentType = new MediaTypeHeaderValue(StreamingProtobufContentType);

        using var req = new HttpRequestMessage(HttpMethod.Post, target) { Content = content };
        // Full-duplex needs HTTP/2: HTTP/1.1 chunked uploads work as a
        // request body but the BCL won't start reading the response
        // until the request body completes (HTTP/1.1 semantics). HTTP/2
        // can interleave both halves.
        req.Version = HttpVersion.Version20;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        req.Headers.TryAddWithoutValidation(ProtocolVersionHeader, ProtocolVersionValue);
        if (metadata is not null)
        {
            foreach (var (k, v) in metadata)
                req.Headers.TryAddWithoutValidation(k, v);
        }

        // Spin up the producer that feeds the pipe with our request
        // frames. Runs concurrently with the SendAsync below so the
        // first frame can land before the server replies, the second
        // can land after the server's first reply frame, &c.
        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var json in requestJsons)
                {
                    var payload = JsonToProtobufBytes(json, inputType);
                    var frame = EncodeFrame(flags: 0x00, payload: payload);
                    await pipe.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
                }
                await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
            }
        }, ct);

        HttpResponseMessage? resp = null;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                var (code, message) = ParseConnectError(bodyBytes, resp);
                yield return ConnectStreamFrame.Error(code, message);
                yield break;
            }

            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var _ = stream.ConfigureAwait(false);

            await foreach (var frame in ReadFramesAsync(stream, ct).ConfigureAwait(false))
            {
                if ((frame.Flags & EndStreamFlag) == EndStreamFlag)
                {
                    var (errCode, errMessage) = ParseEndOfStreamError(frame.Payload);
                    if (errCode is not null)
                        yield return ConnectStreamFrame.Error(errCode, errMessage ?? string.Empty);
                    yield break;
                }
                var json = ProtobufBytesToJson(frame.Payload, outputType);
                yield return ConnectStreamFrame.Message(json, frame.Payload);
            }
        }
        finally
        {
            resp?.Dispose();
            // The producer is normally already done by the time we get
            // here (it completed alongside the response). Await it so
            // any exception inside the producer surfaces — the pipe
            // writer would otherwise swallow it silently.
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on caller-cancel */ }
        }
    }

    // ---- envelope framing -----------------------------------------

    /// <summary>
    /// Build one Connect envelope frame: <c>[flags(1)] [length(4 BE)] [payload]</c>.
    /// </summary>
    internal static byte[] EncodeFrame(byte flags, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var frame = new byte[5 + payload.Length];
        frame[0] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
        return frame;
    }

    /// <summary>
    /// Read the next Connect frame off <paramref name="stream"/> —
    /// 5-byte header (flags + BE length) followed by <c>length</c>
    /// payload bytes. Returns <c>null</c> when the stream ends cleanly
    /// at a frame boundary; throws on truncated header or short
    /// payload (malformed wire).
    /// </summary>
    internal static async Task<ConnectEnvelopeFrame?> ReadOneFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[5];
        var headerRead = await ReadFullyAsync(stream, header, ct).ConfigureAwait(false);
        if (headerRead == 0) return null;
        if (headerRead < 5)
            throw new InvalidDataException(
                $"Connect stream frame header truncated: expected 5 bytes, got {headerRead}.");

        var flags = header[0];
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));

        var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0)
        {
            var bodyRead = await ReadFullyAsync(stream, payload, ct).ConfigureAwait(false);
            if (bodyRead != (int)length)
                throw new InvalidDataException(
                    $"Connect stream frame payload truncated: expected {length} bytes, got {bodyRead}.");
        }
        return new ConnectEnvelopeFrame(flags, payload);
    }

    private static async IAsyncEnumerable<ConnectEnvelopeFrame> ReadFramesAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            var frame = await ReadOneFrameAsync(stream, ct).ConfigureAwait(false);
            if (frame is null) yield break;
            yield return frame;
        }
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Parse a Connect end-of-stream JSON payload — shape is
    /// <c>{ "error"?: { "code", "message" }, "metadata"?: {...} }</c>.
    /// Returns the error tuple when present, <c>(null, null)</c>
    /// otherwise (normal stream completion).
    /// </summary>
    internal static (string? Code, string? Message) ParseEndOfStreamError(byte[] payload)
    {
        if (payload.Length == 0) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            if (!root.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object)
                return (null, null);
            var code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            var message = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
            return (code is null ? null : "connect:" + code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _ownedHandler?.Dispose();
        _mtlsOwner?.Dispose();
    }
}

/// <summary>
/// One parsed Connect envelope frame as it comes off the wire — the
/// raw flag byte + payload bytes. Read by
/// <see cref="ConnectInvoker.ReadOneFrameAsync"/>.
/// </summary>
internal sealed record ConnectEnvelopeFrame(byte Flags, byte[] Payload);

/// <summary>
/// One server-streaming Connect result the invoker emits to its
/// caller: either a decoded response message (<see cref="Message"/>)
/// or a terminating stream error (<see cref="Error"/>).
/// </summary>
internal sealed record ConnectStreamFrame(string Json, byte[]? Binary, string? ErrorCode)
{
    public static ConnectStreamFrame Message(string json, byte[] binary) => new(json, binary, null);
    public static ConnectStreamFrame Error(string code, string message) => new(message, null, code);
}
