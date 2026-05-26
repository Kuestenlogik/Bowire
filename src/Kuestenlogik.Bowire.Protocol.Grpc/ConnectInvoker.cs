// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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
/// Streaming is out of scope for Phase 1 — Connect streaming uses a
/// different envelope (<c>application/connect+json</c> /
/// <c>application/connect+proto</c> with length-prefixed frames).
/// The unary path here is what 90% of agents and CLI workflows
/// reach for, so it lands first.
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

    public void Dispose()
    {
        _http.Dispose();
        _ownedHandler?.Dispose();
        _mtlsOwner?.Dispose();
    }
}
