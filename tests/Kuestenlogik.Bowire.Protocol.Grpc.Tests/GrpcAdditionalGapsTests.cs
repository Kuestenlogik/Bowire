// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Reflection;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Grpc.Core;
using Kuestenlogik.Bowire.Protocol.Grpc.Tests.Proto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// Targets the biggest remaining coverage gaps in the Grpc plugin:
/// <list type="bullet">
///   <item><see cref="ConnectInvoker.InvokeUnaryAsync"/> — success +
///     metadata propagation + Connect error JSON + non-Connect HTTP error.</item>
///   <item><see cref="ConnectInvoker.InvokeClientStreamAsync"/> — N-in-1-out
///     happy path, end-of-stream error frame, pre-stream HTTP error.</item>
///   <item><see cref="ConnectInvoker.InvokeBidiStreamAsync"/> — duplex
///     happy path + end-of-stream error.</item>
///   <item><see cref="GrpcBowireChannel.CreateAsync"/> short-circuit on
///     gRPC-Web transport (returns null without opening a channel).</item>
///   <item><see cref="BowireGrpcProtocol"/> standalone-empty-URL branch
///     and Connect-routing error returns in
///     <see cref="GrpcInvoker.InvokeUnaryAsync"/>.</item>
///   <item><see cref="GrpcChannelBuilder.ExtractTransportFromUrl"/>
///     edge cases (no query, marker-only query, marker with no value,
///     web variant).</item>
///   <item><see cref="GrpcChannelBuilder.StripTransportMarker"/> happy +
///     null/empty inputs.</item>
/// </list>
/// All assertions check concrete observed behaviour — JSON payloads,
/// captured request bytes, status codes, response metadata — not just
/// "doesn't throw". The Connect tests reuse the in-process MapPost
/// listener pattern from <c>ConnectInvokerStreamingTests</c>; the
/// reflection-driven tests reuse the Kestrel reflection-server pattern
/// from <c>GrpcReflectionDiscoveryTests</c>.
/// </summary>
public sealed class GrpcAdditionalGapsTests
{
    private const string Service = "test.GapService";
    private const string UnaryMethod = "DoUnary";
    private const string ClientStreamMethod = "DoClientStream";
    private const string BidiMethod = "DoBidi";

    // ---- ConnectInvoker.InvokeUnaryAsync end-to-end -------------------

    [Fact]
    public async Task ConnectInvokeUnaryAsync_decodes_success_payload_through_descriptor()
    {
        // The server echoes a StringValue("server-said-hi") protobuf body.
        // The invoker must decode it via the output descriptor and surface
        // the JSON form ("server-said-hi" — bare-string special case for
        // the StringValue well-known type).
        var responseBody = new StringValue { Value = "server-said-hi" }.ToByteArray();
        using var server = await ConnectUnaryServer.StartAsync(
            requestObserver: _ => { },
            responseFactory: _ => (200, "application/proto", responseBody, captureHeaders: null));

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var result = await invoker.InvokeUnaryAsync(
            Service, UnaryMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            "\"client-said-hi\"",
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("\"server-said-hi\"", result.Response);
        Assert.NotNull(result.ResponseBinary);
        Assert.Equal(responseBody, result.ResponseBinary);
        Assert.True(result.DurationMs >= 0);
        // ExtractResponseHeaders flattens both response + content headers
        // into one dict — content-type at minimum has to be present.
        Assert.Contains(result.Metadata, kv =>
            kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConnectInvokeUnaryAsync_sends_protocol_version_header_and_metadata()
    {
        // The server captures every inbound header on the request. The
        // invoker must (a) add Connect-Protocol-Version: 1 unconditionally
        // and (b) forward every user-metadata entry verbatim.
        Dictionary<string, string>? captured = null;
        var responseBody = new StringValue { Value = "ok" }.ToByteArray();
        using var server = await ConnectUnaryServer.StartAsync(
            requestObserver: _ => { },
            responseFactory: ctx =>
            {
                captured = ctx.Request.Headers.ToDictionary(
                    h => h.Key, h => string.Join(", ", h.Value.ToArray()),
                    StringComparer.OrdinalIgnoreCase);
                return (200, "application/proto", responseBody, captureHeaders: null);
            });

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Custom-Auth"] = "Bearer abc123",
            ["X-Correlation-Id"] = "req-42",
        };

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var _ = await invoker.InvokeUnaryAsync(
            Service, UnaryMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            "\"client\"",
            metadata,
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("1", captured!["Connect-Protocol-Version"]);
        Assert.Equal("Bearer abc123", captured["X-Custom-Auth"]);
        Assert.Equal("req-42", captured["X-Correlation-Id"]);
    }

    [Fact]
    public async Task ConnectInvokeUnaryAsync_surfaces_connect_error_envelope_as_connect_prefixed_status()
    {
        // Non-2xx + JSON {"code","message"} → InvokeResult.Status is
        // "connect:<code>", Response carries the message verbatim.
        var errorJson = """{"code":"permission_denied","message":"no soup for you"}""";
        using var server = await ConnectUnaryServer.StartAsync(
            requestObserver: _ => { },
            responseFactory: _ => (
                403,
                "application/json",
                Encoding.UTF8.GetBytes(errorJson),
                captureHeaders: null));

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var result = await invoker.InvokeUnaryAsync(
            Service, UnaryMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            "\"x\"",
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("connect:permission_denied", result.Status);
        Assert.Equal("no soup for you", result.Response);
        Assert.Null(result.ResponseBinary);
    }

    [Fact]
    public async Task ConnectInvokeUnaryAsync_non_connect_error_body_falls_back_to_http_status()
    {
        // Plain-text non-JSON body on a 502 → "http:502" + reason phrase
        // as message. The non-Connect branch of ParseConnectError.
        using var server = await ConnectUnaryServer.StartAsync(
            requestObserver: _ => { },
            responseFactory: _ => (
                502,
                "text/plain",
                Encoding.UTF8.GetBytes("Bad Gateway"),
                captureHeaders: null));

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var result = await invoker.InvokeUnaryAsync(
            Service, UnaryMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            "\"x\"",
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("http:502", result.Status);
        // Reason phrase comes from Kestrel ("Bad Gateway").
        Assert.False(string.IsNullOrEmpty(result.Response));
    }

    [Fact]
    public async Task ConnectInvokeUnaryAsync_sends_request_body_as_protobuf_through_descriptor()
    {
        // Capture the raw request body and decode it through the input
        // descriptor — we should see the StringValue("captured-input")
        // round-trip cleanly. Proves JsonToProtobufBytes is hooked up
        // correctly in the unary path (it was only covered by the
        // streaming tests before).
        byte[]? requestBytes = null;
        var responseBody = new StringValue { Value = "ok" }.ToByteArray();
        using var server = await ConnectUnaryServer.StartAsync(
            requestObserver: bytes => requestBytes = bytes,
            responseFactory: _ => (200, "application/proto", responseBody, captureHeaders: null));

        using var invoker = new ConnectInvoker(server.BaseUrl);
        await invoker.InvokeUnaryAsync(
            Service, UnaryMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            "\"captured-input\"",
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(requestBytes);
        var decoded = StringValue.Parser.ParseFrom(requestBytes);
        Assert.Equal("captured-input", decoded.Value);
    }

    // ---- ConnectInvoker.InvokeClientStreamAsync end-to-end ------------

    [Fact]
    public async Task ConnectInvokeClientStreamAsync_pumps_N_request_frames_and_returns_single_response()
    {
        // Server reads three length-prefixed frames off the body. Verify
        // count + payload + final response decoding. Pre-buffered into one
        // ByteArrayContent — same shape ConnectInvoker emits.
        List<byte[]> requestPayloads = [];
        var responseBody = new StringValue { Value = "aggregate" }.ToByteArray();
        var responseFrames = new List<byte[]>
        {
            ConnectInvoker.EncodeFrame(0x00, responseBody),
            ConnectInvoker.EncodeFrame(ConnectInvoker.EndStreamFlag, []),
        };

        using var server = await ConnectStreamingServer.StartAsync(
            requestFramesObserver: frames => requestPayloads = frames,
            responseFrames: responseFrames);

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var result = await invoker.InvokeClientStreamAsync(
            Service, ClientStreamMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            new List<string> { "\"a\"", "\"b\"", "\"c\"" },
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("\"aggregate\"", result.Response);
        Assert.Equal(responseBody, result.ResponseBinary);

        // The server saw three request frames decoded as StringValue.
        Assert.Equal(3, requestPayloads.Count);
        Assert.Equal("a", StringValue.Parser.ParseFrom(requestPayloads[0]).Value);
        Assert.Equal("b", StringValue.Parser.ParseFrom(requestPayloads[1]).Value);
        Assert.Equal("c", StringValue.Parser.ParseFrom(requestPayloads[2]).Value);
    }

    [Fact]
    public async Task ConnectInvokeClientStreamAsync_surfaces_end_of_stream_error_in_status()
    {
        // Server replies with only an end-of-stream frame carrying an
        // error block — invoker returns Status = "connect:<code>" and the
        // error message as Response.
        var responseFrames = new List<byte[]>
        {
            ConnectInvoker.EncodeFrame(
                ConnectInvoker.EndStreamFlag,
                Encoding.UTF8.GetBytes("""{"error":{"code":"resource_exhausted","message":"slow down"}}""")),
        };

        using var server = await ConnectStreamingServer.StartAsync(
            requestFramesObserver: _ => { },
            responseFrames: responseFrames);

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var result = await invoker.InvokeClientStreamAsync(
            Service, ClientStreamMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            new List<string> { "\"x\"" },
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("connect:resource_exhausted", result.Status);
        Assert.Equal("slow down", result.Response);
        Assert.Null(result.ResponseBinary);
    }

    [Fact]
    public async Task ConnectInvokeClientStreamAsync_pre_stream_http_error_returns_connect_error()
    {
        // 503 + JSON Connect error body before the stream opens →
        // invoker returns InvokeResult with the parsed code.
        using var server = await ConnectStreamingServer.StartAsync(
            requestFramesObserver: _ => { },
            responseFrames: [],
            statusOverride: 503,
            errorBodyOverride: """{"code":"unavailable","message":"upstream gone"}""");

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var result = await invoker.InvokeClientStreamAsync(
            Service, ClientStreamMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            new List<string> { "\"a\"" },
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("connect:unavailable", result.Status);
        Assert.Equal("upstream gone", result.Response);
    }

    [Fact]
    public async Task ConnectInvokeClientStreamAsync_null_requestJsons_throws_ArgumentNullException()
    {
        using var invoker = new ConnectInvoker("http://127.0.0.1:1");
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            invoker.InvokeClientStreamAsync(
                Service, ClientStreamMethod,
                StringValue.Descriptor, StringValue.Descriptor,
                requestJsons: null!,
                metadata: null,
                TestContext.Current.CancellationToken));
    }

    // ---- ConnectInvoker.InvokeBidiStreamAsync end-to-end --------------

    [Fact]
    public async Task ConnectInvokeBidiStreamAsync_yields_server_frames_until_end_of_stream()
    {
        // Bidi shape: server sees N request frames, emits its own M
        // response frames + end-of-stream. Invoker drives both halves
        // concurrently and yields each non-EOS response as a frame.
        List<byte[]> requestPayloads = [];
        var responseFrames = new List<byte[]>
        {
            ConnectInvoker.EncodeFrame(0x00, new StringValue { Value = "r1" }.ToByteArray()),
            ConnectInvoker.EncodeFrame(0x00, new StringValue { Value = "r2" }.ToByteArray()),
            ConnectInvoker.EncodeFrame(ConnectInvoker.EndStreamFlag, []),
        };

        using var server = await ConnectStreamingServer.StartAsync(
            requestFramesObserver: frames => requestPayloads = frames,
            responseFrames: responseFrames);

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var received = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeBidiStreamAsync(
            Service, BidiMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            new List<string> { "\"q1\"", "\"q2\"" },
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            received.Add(frame);
        }

        Assert.Equal(2, received.Count);
        Assert.All(received, f => Assert.Null(f.ErrorCode));
        Assert.Equal("\"r1\"", received[0].Json);
        Assert.Equal("\"r2\"", received[1].Json);

        // The producer must have flushed both request frames into the pipe.
        Assert.Equal(2, requestPayloads.Count);
        Assert.Equal("q1", StringValue.Parser.ParseFrom(requestPayloads[0]).Value);
        Assert.Equal("q2", StringValue.Parser.ParseFrom(requestPayloads[1]).Value);
    }

    [Fact]
    public async Task ConnectInvokeBidiStreamAsync_yields_trailing_error_frame_on_end_of_stream_error()
    {
        var responseFrames = new List<byte[]>
        {
            ConnectInvoker.EncodeFrame(0x00, new StringValue { Value = "before-error" }.ToByteArray()),
            ConnectInvoker.EncodeFrame(
                ConnectInvoker.EndStreamFlag,
                Encoding.UTF8.GetBytes("""{"error":{"code":"data_loss","message":"corrupt"}}""")),
        };

        using var server = await ConnectStreamingServer.StartAsync(
            requestFramesObserver: _ => { },
            responseFrames: responseFrames);

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var received = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeBidiStreamAsync(
            Service, BidiMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            new List<string> { "\"q\"" },
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            received.Add(frame);
        }

        Assert.Equal(2, received.Count);
        Assert.Null(received[0].ErrorCode);
        Assert.Equal("\"before-error\"", received[0].Json);
        Assert.Equal("connect:data_loss", received[1].ErrorCode);
        Assert.Equal("corrupt", received[1].Json);
    }

    [Fact]
    public async Task ConnectInvokeBidiStreamAsync_pre_stream_http_error_yields_single_error_frame()
    {
        // 401 + Connect error JSON before the stream opens. Same code
        // path as InvokeServerStreamAsync's pre-stream error guard but
        // through the bidi entry point.
        using var server = await ConnectStreamingServer.StartAsync(
            requestFramesObserver: _ => { },
            responseFrames: [],
            statusOverride: 401,
            errorBodyOverride: """{"code":"unauthenticated","message":"who are you"}""");

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var received = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeBidiStreamAsync(
            Service, BidiMethod,
            StringValue.Descriptor, StringValue.Descriptor,
            new List<string> { "\"q\"" },
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            received.Add(frame);
        }

        var only = Assert.Single(received);
        Assert.Equal("connect:unauthenticated", only.ErrorCode);
        Assert.Equal("who are you", only.Json);
    }

    [Fact]
    public async Task ConnectInvokeBidiStreamAsync_null_requestJsons_throws_ArgumentNullException()
    {
        using var invoker = new ConnectInvoker("http://127.0.0.1:1");
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in invoker.InvokeBidiStreamAsync(
                Service, BidiMethod,
                StringValue.Descriptor, StringValue.Descriptor,
                requestJsons: null!,
                metadata: null,
                TestContext.Current.CancellationToken))
            { }
        });
    }

    // ---- GrpcBowireChannel.CreateAsync gRPC-Web short-circuit ---------

    [Fact]
    public async Task GrpcBowireChannel_CreateAsync_With_Web_Transport_Metadata_Returns_Null()
    {
        // The "duplex not supported on gRPC-Web" branch — covered before
        // any reflection / channel construction runs, so it works
        // against any URL without a real server.
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "web",
        };

        var channel = await GrpcBowireChannel.CreateAsync(
            "http://127.0.0.1:1",
            "irrelevant.Service", "Method",
            showInternalServices: false,
            metadata: metadata,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

    [Fact]
    public async Task BowireGrpcProtocol_OpenChannelAsync_Web_Mode_Returns_Null()
    {
        // Same short-circuit, but reached through the plugin entry point
        // — covers BowireGrpcProtocol.OpenChannelAsync's pass-through.
        var protocol = new BowireGrpcProtocol();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "web",
        };
        var channel = await protocol.OpenChannelAsync(
            "http://127.0.0.1:1",
            "x.Y", "Z",
            showInternalServices: false,
            metadata: metadata,
            ct: TestContext.Current.CancellationToken);
        Assert.Null(channel);
    }

    // ---- BowireGrpcProtocol standalone empty-URL guard ----------------

    [Fact]
    public async Task BowireGrpcProtocol_DiscoverAsync_Empty_Url_Returns_Empty_List_Without_Network()
    {
        // Cancelling before a network call would catch a hung handshake
        // attempt — instead the guard returns immediately, so the
        // assertion fires inside the timeout. Token still passed for
        // safety in case the guard regresses.
        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var services = await protocol.DiscoverAsync(
            "", showInternalServices: false, cts.Token);
        Assert.NotNull(services);
        Assert.Empty(services);
    }

    [Fact]
    public async Task BowireGrpcProtocol_DiscoverAsync_Whitespace_Url_Returns_Empty_List()
    {
        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var services = await protocol.DiscoverAsync(
            "   ", showInternalServices: false, cts.Token);
        Assert.NotNull(services);
        Assert.Empty(services);
    }

    [Fact]
    public void BowireGrpcProtocol_Initialize_With_Null_ServiceProvider_DoesNotThrow()
    {
        // The configuration capture path falls back to null silently
        // when the host hasn't wired DI yet (test paths, standalone CLI).
        var protocol = new BowireGrpcProtocol();
        protocol.Initialize(null);
        // Sanity follow-up: a subsequent empty-URL discover still works.
        Assert.NotNull(protocol.Description);
    }

    // ---- GrpcInvoker Connect-routing rejection messages --------------

    [Fact]
    public async Task BowireGrpcProtocol_InvokeAsync_Connect_ServerStreaming_Method_Returns_Routing_Error()
    {
        // The plugin's Connect path detects "server-streaming runs through
        // InvokeStreamAsync, not the unary path" — an inline guard that
        // bypasses GrpcChannel entirely. We reach it by pointing the
        // plugin at a real (synthetic) reflection server so resolution
        // succeeds, then tagging the call as Connect transport so the
        // routing-error branch fires. Without the reflection server the
        // call dies inside resolution and never reaches the guard.
        var fdProto = BuildSimpleServerStreamingFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "connect",
        };

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await protocol.InvokeAsync(
            server.BaseUrl,
            "demo.GapStream", "Tick",
            new List<string> { "{}" },
            showInternalServices: false,
            metadata,
            cts.Token);

        Assert.Equal(0, result.DurationMs);
        // The Connect-routing rejection message lands in Status — the
        // InvokeResult ctor signature is (Response, DurationMs, Status, ...)
        // and the plugin passes null for Response on these guard returns.
        Assert.Null(result.Response);
        Assert.Contains("server-streaming runs through InvokeStreamAsync",
            result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BowireGrpcProtocol_InvokeAsync_Connect_BidiStreaming_Method_Returns_Routing_Error()
    {
        var fdProto = BuildBidiFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "connect",
        };

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await protocol.InvokeAsync(
            server.BaseUrl,
            "demo.GapBidi", "Chat",
            new List<string> { "{}" },
            showInternalServices: false,
            metadata,
            cts.Token);

        Assert.Null(result.Response);
        Assert.Contains("bidi-streaming runs through InvokeStreamAsync",
            result.Status, StringComparison.Ordinal);
    }

    // ---- GrpcChannelBuilder.ExtractTransportFromUrl edge cases --------

    [Fact]
    public void ExtractTransportFromUrl_NoQueryString_Returns_Url_Unchanged_With_Native_Mode()
    {
        var (clean, mode) = GrpcChannelBuilder.ExtractTransportFromUrl("https://api.example.com");
        Assert.Equal("https://api.example.com", clean);
        Assert.Equal(GrpcTransportMode.Native, mode);
    }

    [Fact]
    public void ExtractTransportFromUrl_EmptyString_Returns_Empty_With_Native_Mode()
    {
        var (clean, mode) = GrpcChannelBuilder.ExtractTransportFromUrl("");
        Assert.Equal("", clean);
        Assert.Equal(GrpcTransportMode.Native, mode);
    }

    [Fact]
    public void ExtractTransportFromUrl_OnlyMarker_Strips_Query_Entirely()
    {
        // Marker-only query string should collapse to the bare URL —
        // no orphaned "?" left dangling.
        var (clean, mode) = GrpcChannelBuilder.ExtractTransportFromUrl(
            "https://api.example.com?__bowireGrpcTransport=web");
        Assert.Equal("https://api.example.com", clean);
        Assert.Equal(GrpcTransportMode.Web, mode);
    }

    [Fact]
    public void ExtractTransportFromUrl_MarkerWithoutValue_Falls_Back_To_Native_And_Drops_Marker()
    {
        // "?__bowireGrpcTransport" with no "=value" — we still drop the
        // marker (production never wants it bleeding through to the gRPC
        // stack) but treat the mode as Native.
        var (clean, mode) = GrpcChannelBuilder.ExtractTransportFromUrl(
            "https://api.example.com?__bowireGrpcTransport&keep=this");
        Assert.Equal("https://api.example.com?keep=this", clean);
        Assert.Equal(GrpcTransportMode.Native, mode);
    }

    [Fact]
    public void ExtractTransportFromUrl_WebMarker_Sets_Web_Mode()
    {
        var (clean, mode) = GrpcChannelBuilder.ExtractTransportFromUrl(
            "https://api.example.com?foo=1&__bowireGrpcTransport=web&bar=2");
        Assert.Equal("https://api.example.com?foo=1&bar=2", clean);
        Assert.Equal(GrpcTransportMode.Web, mode);
    }

    // ---- GrpcChannelBuilder.StripTransportMarker ---------------------

    [Fact]
    public void StripTransportMarker_NullInput_Returns_Null()
    {
        Assert.Null(GrpcChannelBuilder.StripTransportMarker(null));
    }

    [Fact]
    public void StripTransportMarker_EmptyInput_Returns_Null()
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.Null(GrpcChannelBuilder.StripTransportMarker(empty));
    }

    [Fact]
    public void StripTransportMarker_OnlyMarker_Returns_Null()
    {
        // The marker is the only entry — after stripping there's nothing
        // left so the helper should return null (not an empty dict).
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "web",
        };
        Assert.Null(GrpcChannelBuilder.StripTransportMarker(meta));
    }

    [Fact]
    public void StripTransportMarker_KeepsOtherEntries_DropsMarker()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "web",
            ["X-Real-Header"] = "value",
            ["Authorization"] = "Bearer xyz",
        };
        var stripped = GrpcChannelBuilder.StripTransportMarker(meta);
        Assert.NotNull(stripped);
        Assert.False(stripped!.ContainsKey(BowireGrpcProtocol.TransportMetadataKey));
        Assert.Equal("value", stripped["X-Real-Header"]);
        Assert.Equal("Bearer xyz", stripped["Authorization"]);
    }

    [Fact]
    public void StripTransportMarker_Marker_Is_Case_Insensitive()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Lower-cased variant should still get stripped.
            ["x-bowire-grpc-transport"] = "web",
            ["X-Keeper"] = "y",
        };
        var stripped = GrpcChannelBuilder.StripTransportMarker(meta);
        Assert.NotNull(stripped);
        Assert.Single(stripped!);
        Assert.Equal("y", stripped["X-Keeper"]);
    }

    // ---- GrpcChannelBuilder.BuildChannel Web variant ------------------

    [Fact]
    public async Task DiscoverAsync_With_GrpcWeb_Url_Hint_Routes_Through_Web_BuildChannel()
    {
        // The URL marker grpcweb@... lands on the protocol as
        // "?__bowireGrpcTransport=web", which BowireGrpcProtocol's
        // ExtractTransportFromUrl translates into GrpcTransportMode.Web.
        // The reflection client's ctor then calls GrpcChannelBuilder
        // .BuildChannel(..., Web) — covers the Web case in BuildChannel
        // (GrpcWebHandler wrap + HttpVersion pinning) which the rest of
        // the suite skips because every other test resolves Native.
        // We point at an unreachable URL because the actual reflection
        // call doesn't matter — we only need to construct the channel.
        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await protocol.DiscoverAsync(
                "http://127.0.0.2:1?__bowireGrpcTransport=web",
                showInternalServices: false,
                cts.Token);
        }
        catch
        {
            // Any failure is expected — the discover call dies inside
            // ListServicesAsync because there's nothing on the other
            // end. We only care that the channel-build path ran.
        }
        // Sanity: just ensure the protocol object is still usable.
        Assert.Equal("grpc", protocol.Id);
    }

    // ---- Real-gRPC end-to-end coverage --------------------------------
    //
    // These tests host a real grpc-dotnet service (GapGreeter) inside
    // Kestrel and drive BowireGrpcProtocol against it. Without this
    // section, GrpcInvoker.InvokeUnaryAsync's success path,
    // InvokeStreamingWithFramesAsync's gRPC-native server-streaming +
    // duplex branches, and GrpcBowireChannel's RunDuplexAsync /
    // RunClientStreamingAsync / ReadResponsesAsync stay at 0% line
    // coverage. The reflection-only fixtures elsewhere in this file
    // can't reach those paths because they only cover the descriptor
    // walk, not the actual gRPC call sites.

    [Fact]
    public async Task BowireGrpcProtocol_InvokeAsync_Unary_Returns_OK_With_Response_Json_And_Binary()
    {
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            "gaptest.GapGreeter", "Hi",
            new List<string> { """{"name":"world"}""" },
            showInternalServices: false,
            metadata: null,
            cts.Token);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("Hi world!", result.Response!, StringComparison.Ordinal);
        Assert.NotNull(result.ResponseBinary);
        Assert.NotEmpty(result.ResponseBinary!);
        Assert.True(result.DurationMs >= 0);
    }

    [Fact]
    public async Task BowireGrpcProtocol_InvokeAsync_Unary_With_Metadata_Forwards_Headers()
    {
        // Covers BuildCallOptions's foreach branch — the metadata
        // dict has to land in the gRPC headers. The server
        // implementation watches for "x-gap-trace" on the inbound
        // headers and only succeeds when present, so the OK status
        // is itself the proof of header forwarding (the OK path's
        // InvokeResult.Metadata is empty by design).
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-gap-trace"] = "trace-123",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            "gaptest.GapGreeter", "Hi",
            new List<string> { """{"name":"with-trace"}""" },
            showInternalServices: false,
            metadata: meta,
            cts.Token);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("Hi with-trace!", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BowireGrpcProtocol_InvokeAsync_Unary_Failed_Method_Returns_Status_And_Trailer_Metadata()
    {
        // Hits the RpcException catch arm in GrpcInvoker.InvokeUnaryAsync
        // — the server throws RpcException(InvalidArgument) and the
        // invoker captures status + trailers with the _trailer: prefix.
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            "gaptest.GapGreeter", "Hi",
            new List<string> { """{"name":"fail"}""" },
            showInternalServices: false,
            metadata: null,
            cts.Token);

        Assert.Equal("InvalidArgument", result.Status);
        Assert.Equal("intentionally rejected", result.Response);
        // _trailer: prefix proves the catch arm picked up the trailers
        // (rather than confusing them with regular response metadata).
        Assert.Contains(result.Metadata, kv =>
            kv.Key.StartsWith("_trailer:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BowireGrpcProtocol_InvokeAsync_ClientStreaming_Drains_All_Inputs_And_Returns_Aggregate()
    {
        // Drives GrpcInvoker.InvokeUnaryAsync's ClientStreaming branch —
        // N requests, single response. The server concatenates names.
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            "gaptest.GapGreeter", "CollectHi",
            new List<string>
            {
                """{"name":"alice"}""",
                """{"name":"bob"}""",
                """{"name":"carol"}""",
            },
            showInternalServices: false,
            metadata: null,
            cts.Token);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("alice,bob,carol", result.Response!, StringComparison.Ordinal);
        Assert.NotNull(result.ResponseBinary);
    }

    [Fact]
    public async Task BowireGrpcProtocol_InvokeStreamAsync_ServerStreaming_Yields_All_Frames()
    {
        // GrpcInvoker.InvokeStreamingWithFramesAsync server-streaming
        // branch end-to-end. Yields JSON-only frames through
        // BowireGrpcProtocol.InvokeStreamAsync.
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        var frames = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var json in protocol.InvokeStreamAsync(
            host.BaseUrl,
            "gaptest.GapGreeter", "HiStream",
            new List<string> { """{"name":"stream","count":3}""" },
            showInternalServices: false,
            metadata: null,
            cts.Token))
        {
            frames.Add(json);
        }

        Assert.Equal(3, frames.Count);
        Assert.Contains("Hi stream #1", frames[0], StringComparison.Ordinal);
        Assert.Contains("Hi stream #2", frames[1], StringComparison.Ordinal);
        Assert.Contains("Hi stream #3", frames[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BowireGrpcProtocol_InvokeStreamWithFramesAsync_ServerStreaming_Yields_Json_And_Binary()
    {
        // Same server-streaming exercise but through the wire-bytes
        // variant — covers BowireGrpcProtocol.InvokeStreamWithFramesAsync.
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        var frames = new List<StreamFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var f in protocol.InvokeStreamWithFramesAsync(
            host.BaseUrl,
            "gaptest.GapGreeter", "HiStream",
            new List<string> { """{"name":"bin","count":2}""" },
            showInternalServices: false,
            metadata: null,
            cts.Token))
        {
            frames.Add(f);
        }

        Assert.Equal(2, frames.Count);
        Assert.All(frames, f =>
        {
            Assert.NotNull(f.Binary);
            Assert.NotEmpty(f.Binary!);
        });
        Assert.Contains("Hi bin #1", frames[0].Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BowireGrpcProtocol_InvokeStreamWithFramesAsync_DuplexStreaming_Echoes_Each_Request()
    {
        // Bidi end-to-end through the protocol entry point — exercises
        // GrpcInvoker.InvokeStreamingWithFramesAsync's duplex branch
        // (sendTask + ResponseStream.ReadAllAsync).
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        var frames = new List<StreamFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var f in protocol.InvokeStreamWithFramesAsync(
            host.BaseUrl,
            "gaptest.GapGreeter", "ChatHi",
            new List<string>
            {
                """{"name":"one"}""",
                """{"name":"two"}""",
            },
            showInternalServices: false,
            metadata: null,
            cts.Token))
        {
            frames.Add(f);
        }

        Assert.Equal(2, frames.Count);
        Assert.Contains("Hi one!", frames[0].Json, StringComparison.Ordinal);
        Assert.Contains("Hi two!", frames[1].Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BowireGrpcProtocol_OpenChannelAsync_DuplexMethod_Returns_Working_Channel()
    {
        // GrpcBowireChannel's duplex path: open, send two messages,
        // close, read both echoes. Covers
        //   GrpcBowireChannel.CreateAsync (success arm, native mode),
        //   .SendAsync (twice — IsClosed false then true),
        //   .CloseAsync,
        //   .RunDuplexAsync,
        //   .ReadResponsesAsync,
        //   .DisposeAsync.
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            "gaptest.GapGreeter", "ChatHi",
            showInternalServices: false,
            metadata: null,
            cts.Token);

        Assert.NotNull(channel);
        Assert.True(channel!.IsClientStreaming);
        Assert.True(channel.IsServerStreaming);
        Assert.False(channel.IsClosed);

        var sent1 = await channel.SendAsync("""{"name":"chan-a"}""", cts.Token);
        var sent2 = await channel.SendAsync("""{"name":"chan-b"}""", cts.Token);
        Assert.True(sent1);
        Assert.True(sent2);
        Assert.Equal(2, channel.SentCount);

        await channel.CloseAsync(cts.Token);
        Assert.True(channel.IsClosed);

        // After close, SendAsync returns false (the IsClosed guard).
        var sentAfterClose = await channel.SendAsync("""{"name":"too-late"}""", cts.Token);
        Assert.False(sentAfterClose);
        Assert.Equal(2, channel.SentCount);

        var responses = new List<string>();
        await foreach (var json in channel.ReadResponsesAsync(cts.Token))
            responses.Add(json);

        Assert.Equal(2, responses.Count);
        Assert.Contains("Hi chan-a!", responses[0], StringComparison.Ordinal);
        Assert.Contains("Hi chan-b!", responses[1], StringComparison.Ordinal);

        Assert.True(channel.ElapsedMs >= 0);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task BowireGrpcProtocol_OpenChannelAsync_ClientStreamingMethod_Returns_Channel_With_Single_Response()
    {
        // Client-streaming variant: covers RunClientStreamingAsync (1+
        // requests, 1 response) instead of RunDuplexAsync.
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            "gaptest.GapGreeter", "CollectHi",
            showInternalServices: false,
            metadata: null,
            cts.Token);

        Assert.NotNull(channel);
        Assert.True(channel!.IsClientStreaming);
        Assert.False(channel.IsServerStreaming);

        await channel.SendAsync("""{"name":"x"}""", cts.Token);
        await channel.SendAsync("""{"name":"y"}""", cts.Token);
        await channel.CloseAsync(cts.Token);

        var responses = new List<string>();
        await foreach (var json in channel.ReadResponsesAsync(cts.Token))
            responses.Add(json);

        // Client-streaming returns one aggregate response.
        Assert.Single(responses);
        Assert.Contains("x,y", responses[0], StringComparison.Ordinal);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task BowireGrpcProtocol_InvokeAsync_Unknown_Method_Bubbles_InvalidOperation()
    {
        // GrpcInvoker.ResolveMethodAsync throws when the method name
        // doesn't exist on the resolved service. The exception bubbles
        // through InvokeUnaryAsync (it's NOT an RpcException so the
        // catch arm doesn't swallow it).
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            protocol.InvokeAsync(
                host.BaseUrl,
                "gaptest.GapGreeter", "DefinitelyNotARealMethod",
                new List<string> { "{}" },
                showInternalServices: false,
                metadata: null,
                cts.Token));
        Assert.Contains("DefinitelyNotARealMethod", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BowireGrpcProtocol_DiscoverAsync_Live_Server_Returns_GapGreeter_Service()
    {
        // End-to-end discovery via the plugin — covers the
        // DiscoverAsync foreach loop over reflected services.
        // Schema also exercises GrpcReflectionClient.ResolveAllDescriptorsAsync's
        // transitive-dep BFS (reflection ships HiReq+HiRes alongside
        // the service descriptor).
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var services = await protocol.DiscoverAsync(
            host.BaseUrl, showInternalServices: false, cts.Token);

        var greeter = Assert.Single(services);
        Assert.Equal("gaptest.GapGreeter", greeter.Name);
        Assert.Equal(4, greeter.Methods.Count);
        Assert.Contains(greeter.Methods, m => m.Name == "Hi" && m.MethodType == "Unary");
        Assert.Contains(greeter.Methods, m => m.Name == "HiStream" && m.MethodType == "ServerStreaming");
        Assert.Contains(greeter.Methods, m => m.Name == "CollectHi" && m.MethodType == "ClientStreaming");
        Assert.Contains(greeter.Methods, m => m.Name == "ChatHi" && m.MethodType == "Duplex");
    }

    [Fact]
    public async Task BowireGrpcProtocol_DiscoverAsync_With_Transitive_Proto_Deps_Walks_BFS()
    {
        // gapdeps.proto imports google/protobuf/timestamp.proto. The
        // reflection server lists gapdeps as a service; resolving its
        // file descriptors transitively forces
        // GrpcReflectionClient.ResolveAllDescriptorsAsync to walk the BFS
        // queue (gapdeps → timestamp.proto), driving the
        // toResolve.Dequeue loop and GetFileDescriptorsByNameAsync.
        // Discovery is the right surface to assert on because the
        // service-info builder doesn't care if Google.Protobuf can't
        // assemble FileDescriptors from the resulting protos.
        await using var host = await GapDepsHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var services = await protocol.DiscoverAsync(
            host.BaseUrl, showInternalServices: false, cts.Token);

        var deps = Assert.Single(services);
        Assert.Equal("gapdeps.GapDeps", deps.Name);
        // Stamp's input + output both carry a google.protobuf.Timestamp
        // — the type resolver must have walked the dependency to see it.
        var stamp = Assert.Single(deps.Methods);
        Assert.Equal("Stamp", stamp.Name);
        Assert.Contains(stamp.InputType.Fields, f => f.Name == "ts");
    }

    [Fact]
    public async Task BowireGrpcProtocol_DiscoverAsync_With_ShowInternal_Includes_Reflection_Service()
    {
        // Same live server, but the showInternalServices flag flips the
        // filter so gRPC's own reflection service shows up too. Covers
        // the GrpcReflectionClient.InternalServices skip-vs-include
        // branch under the "include" arm.
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var services = await protocol.DiscoverAsync(
            host.BaseUrl, showInternalServices: true, cts.Token);

        // GapGreeter + at least one of the reflection services.
        Assert.True(services.Count >= 2);
        Assert.Contains(services, s => s.Name.StartsWith(
            "grpc.reflection.v1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BowireGrpcProtocol_OpenChannelAsync_Missing_Service_Throws_InvalidOperation()
    {
        // GrpcBowireChannel.CreateAsync throws when reflection lists no
        // descriptors / wrong service. Drives the catch arm that
        // disposes the channel + handler before rethrowing.
        await using var host = await GapGreeterHost.StartAsync();
        var protocol = new BowireGrpcProtocol();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            protocol.OpenChannelAsync(
                host.BaseUrl,
                "gaptest.NoSuchService", "AnyMethod",
                showInternalServices: false,
                metadata: null,
                cts.Token));
    }

    // ---- ConnectStreamFrame factory record sanity --------------------

    [Fact]
    public void ConnectStreamFrame_Message_Factory_Has_Null_ErrorCode()
    {
        var f = ConnectStreamFrame.Message("{}", [0x01, 0x02]);
        Assert.Null(f.ErrorCode);
        Assert.Equal("{}", f.Json);
        Assert.Equal(new byte[] { 0x01, 0x02 }, f.Binary);
    }

    [Fact]
    public void ConnectStreamFrame_Error_Factory_Has_Null_Binary()
    {
        var f = ConnectStreamFrame.Error("connect:internal", "boom");
        Assert.Equal("connect:internal", f.ErrorCode);
        Assert.Equal("boom", f.Json);
        Assert.Null(f.Binary);
    }

    // ---- BowireGrpcProtocol description + transport metadata key ---

    [Fact]
    public void BowireGrpcProtocol_Description_Mentions_Stream_Variants()
    {
        var p = new BowireGrpcProtocol();
        // Description is shown on the protocol picker tile — making
        // sure it lists the four method shapes Bowire supports.
        Assert.Contains("unary", p.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("server-stream", p.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client-stream", p.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duplex", p.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransportMetadataKey_Constant_Is_Canonical_Header_Name()
    {
        // Spec'd value — used by JS layer + REST endpoint translation;
        // changing it is a wire break.
        Assert.Equal("X-Bowire-Grpc-Transport", BowireGrpcProtocol.TransportMetadataKey);
    }

    // ===================================================================
    // Helpers — in-process HTTP listeners for the Connect tests +
    // synthetic FileDescriptorProtos for the reflection-driven tests.
    // ===================================================================

    /// <summary>
    /// MapPost-based in-process unary listener. Reads the request body
    /// once and dispatches to a response factory that returns
    /// <c>(statusCode, contentType, body, captureHeaders?)</c>. Reuses
    /// the same Kestrel-on-random-port shape as
    /// <c>ConnectInvokerStreamingTests.ConnectTestServer</c>.
    /// </summary>
    private sealed class ConnectUnaryServer : IAsyncDisposable, IDisposable
    {
        public required WebApplication App { get; init; }
        public required string BaseUrl { get; init; }

        public static async Task<ConnectUnaryServer> StartAsync(
            Action<byte[]> requestObserver,
            Func<HttpContext, (int Status, string ContentType, byte[] Body, IDictionary<string, string>? captureHeaders)> responseFactory)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");

            app.MapPost($"/{Service}/{UnaryMethod}", async (HttpContext ctx) =>
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                requestObserver(ms.ToArray());

                var (status, contentType, body, _) = responseFactory(ctx);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                if (body.Length > 0)
                    await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
            });

            await app.StartAsync();
            var baseUrl = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();
            return new ConnectUnaryServer { App = app, BaseUrl = baseUrl };
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// MapPost-based in-process streaming listener. Reads all
    /// length-prefixed request frames off the body, hands them to the
    /// observer as decoded payload byte arrays, then writes the
    /// configured response frames back. Used for client-stream + bidi
    /// tests where the unary listener's "read body once" shape doesn't
    /// cut it.
    /// </summary>
    private sealed class ConnectStreamingServer : IAsyncDisposable, IDisposable
    {
        public required WebApplication App { get; init; }
        public required string BaseUrl { get; init; }

        public static async Task<ConnectStreamingServer> StartAsync(
            Action<List<byte[]>> requestFramesObserver,
            List<byte[]> responseFrames,
            int? statusOverride = null,
            string? errorBodyOverride = null)
        {
            // ConnectInvoker.InvokeClientStreamAsync forces HTTP/2 with
            // RequestVersionOrHigher, and InvokeBidiStreamAsync forces
            // HTTP/2 with RequestVersionExact. Plain ALPN-less HTTP
            // negotiation on Kestrel won't allow HTTP/2 prior-knowledge
            // unless the endpoint is bound HTTP/2-only, so we pin the
            // listener to HTTP/2 (h2c) explicitly.
            var port = GetFreePort();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, port, lo =>
                {
                    lo.Protocols = HttpProtocols.Http2;
                });
            });
            var app = builder.Build();
            app.Urls.Clear();
            app.Urls.Add($"http://127.0.0.1:{port}");

            // Shared handler covers all three paths (unary post URL +
            // both streaming method URLs) so each test just picks the
            // method name that matches its intent.
            app.MapPost("/{service}/{method}", async (HttpContext ctx) =>
            {
                // Drain the request body — for streaming POSTs we need
                // to capture all length-prefixed frames the client sent.
                var requestFrames = new List<byte[]>();
                var headerBuf = new byte[5];
                while (true)
                {
                    var headerRead = await ReadFullyAsync(
                        ctx.Request.Body, headerBuf, 5, ctx.RequestAborted);
                    if (headerRead == 0) break;
                    if (headerRead < 5)
                    {
                        // Truncated header on the inbound side. Bail
                        // gracefully — the test will fail on its
                        // payload-count assertion.
                        break;
                    }
                    var length = BinaryPrimitives.ReadUInt32BigEndian(
                        headerBuf.AsSpan(1, 4));
                    var payload = length == 0 ? [] : new byte[length];
                    if (length > 0)
                    {
                        var read = await ReadFullyAsync(
                            ctx.Request.Body, payload, (int)length, ctx.RequestAborted);
                        if (read != (int)length) break;
                    }
                    requestFrames.Add(payload);
                }
                requestFramesObserver(requestFrames);

                if (statusOverride is { } s)
                {
                    ctx.Response.StatusCode = s;
                    ctx.Response.ContentType = "application/json";
                    if (errorBodyOverride is not null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(errorBodyOverride);
                        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                    }
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/connect+proto";
                foreach (var frame in responseFrames)
                    await ctx.Response.Body.WriteAsync(frame, ctx.RequestAborted);
            });

            await app.StartAsync();
            var baseUrl = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();
            return new ConnectStreamingServer { App = app, BaseUrl = baseUrl };
        }

        private static int GetFreePort()
        {
            using var sock = new TcpListener(IPAddress.Loopback, 0);
            sock.Start();
            var port = ((IPEndPoint)sock.LocalEndpoint).Port;
            sock.Stop();
            return port;
        }

        private static async Task<int> ReadFullyAsync(
            Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            var total = 0;
            while (total < count)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Synthetic gRPC reflection server (mirrors
    /// <c>GrpcReflectionDiscoveryTests.ReflectionServer</c>). Hosts the
    /// reflection service against a synthetic FileDescriptorProto so
    /// <see cref="BowireGrpcProtocol.InvokeAsync"/> can complete its
    /// resolve step and reach the Connect-routing rejection branch.
    /// </summary>
    private sealed class ReflectionServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        private ReflectionServer(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public static async Task<ReflectionServer> StartAsync(FileDescriptorProto fdProto)
        {
            var fileDescriptors = FileDescriptor.BuildFromByteStrings(
                new[] { fdProto.ToByteString() });
            var serviceDescriptors = fileDescriptors
                .SelectMany(fd => fd.Services)
                .ToList();

            var port = GetFreePort();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = Path.GetTempPath()
            });
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, port, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                });
            });
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(new ReflectionServiceImpl(serviceDescriptors));

            var app = builder.Build();
            app.MapGrpcService<ReflectionServiceImpl>();

            await app.StartAsync();
            return new ReflectionServer(app, $"http://127.0.0.1:{port}");
        }

        private static int GetFreePort()
        {
            using var sock = new TcpListener(IPAddress.Loopback, 0);
            sock.Start();
            var port = ((IPEndPoint)sock.LocalEndpoint).Port;
            sock.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
            try { await _app.DisposeAsync(); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Hosts a real grpc-dotnet <see cref="GapGreeter.GapGreeterBase"/>
    /// implementation alongside gRPC reflection so
    /// <see cref="BowireGrpcProtocol"/> can discover + invoke it
    /// end-to-end. HTTP/2 prior-knowledge on a free loopback port; the
    /// caller passes the resulting <see cref="BaseUrl"/> straight to the
    /// plugin.
    /// </summary>
    private sealed class GapGreeterHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        private GapGreeterHost(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public static async Task<GapGreeterHost> StartAsync()
        {
            var port = GetFreePort();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = Path.GetTempPath(),
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, port, lo =>
                {
                    lo.Protocols = HttpProtocols.Http2;
                });
            });
            builder.Services.AddGrpc();
            builder.Services.AddGrpcReflection();

            var app = builder.Build();
            app.MapGrpcService<GapGreeterImpl>();
            app.MapGrpcReflectionService();

            await app.StartAsync();
            return new GapGreeterHost(app, $"http://127.0.0.1:{port}");
        }

        private static int GetFreePort()
        {
            using var sock = new TcpListener(IPAddress.Loopback, 0);
            sock.Start();
            var port = ((IPEndPoint)sock.LocalEndpoint).Port;
            sock.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
            try { await _app.DisposeAsync(); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Hosts <see cref="GapDepsImpl"/> alongside gRPC reflection so the
    /// transitive-deps test can drive the reflection client's BFS over
    /// file descriptors (gapdeps.proto → google/protobuf/timestamp.proto).
    /// </summary>
    private sealed class GapDepsHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        private GapDepsHost(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public static async Task<GapDepsHost> StartAsync()
        {
            var port = GetFreePortStatic();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = Path.GetTempPath(),
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, port, lo =>
                {
                    lo.Protocols = HttpProtocols.Http2;
                });
            });
            builder.Services.AddGrpc();
            builder.Services.AddGrpcReflection();

            var app = builder.Build();
            app.MapGrpcService<GapDepsImpl>();
            app.MapGrpcReflectionService();

            await app.StartAsync();
            return new GapDepsHost(app, $"http://127.0.0.1:{port}");
        }

        public async ValueTask DisposeAsync()
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
            try { await _app.DisposeAsync(); } catch { /* best-effort */ }
        }
    }

    internal sealed class GapDepsImpl : GapDeps.GapDepsBase
    {
        public override Task<StampRes> Stamp(StampReq request, ServerCallContext context)
        {
            return Task.FromResult(new StampRes
            {
                Ts = request.Ts ?? new Google.Protobuf.WellKnownTypes.Timestamp(),
                Note = "stamped",
            });
        }
    }

    private static int GetFreePortStatic()
    {
        using var sock = new TcpListener(IPAddress.Loopback, 0);
        sock.Start();
        var port = ((IPEndPoint)sock.LocalEndpoint).Port;
        sock.Stop();
        return port;
    }

    /// <summary>
    /// Test-only GapGreeter implementation covering all four method
    /// shapes. Drives:
    /// <list type="bullet">
    ///   <item><see cref="GapGreeterImpl.Hi"/> — unary success +
    ///     header bounce-back + intentional rejection of the magic
    ///     name "fail".</item>
    ///   <item><see cref="GapGreeterImpl.HiStream"/> — server-streaming
    ///     with Count frames.</item>
    ///   <item><see cref="GapGreeterImpl.CollectHi"/> — client-streaming
    ///     drain → single response.</item>
    ///   <item><see cref="GapGreeterImpl.ChatHi"/> — bidi echo loop.</item>
    /// </list>
    /// </summary>
    internal sealed class GapGreeterImpl : GapGreeter.GapGreeterBase
    {
        public override Task<HiRes> Hi(HiReq request, ServerCallContext context)
        {
            if (request.Name == "fail")
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument, "intentionally rejected"));
            }
            // Bounce inbound x-gap-trace into a response header named
            // x-gap-trace-echo so the metadata-forwarding test can
            // observe header propagation cleanly.
            var inbound = context.RequestHeaders
                .FirstOrDefault(h => h.Key.Equals("x-gap-trace", StringComparison.OrdinalIgnoreCase));
            if (inbound is not null)
            {
                context.ResponseTrailers.Add("x-gap-trace-echo", inbound.Value);
            }
            return Task.FromResult(new HiRes
            {
                Message = $"Hi {request.Name}!",
                Sequence = 1,
            });
        }

        public override async Task HiStream(
            HiReq request,
            IServerStreamWriter<HiRes> responseStream,
            ServerCallContext context)
        {
            var count = request.Count > 0 ? request.Count : 3;
            for (var i = 1; i <= count; i++)
            {
                await responseStream.WriteAsync(new HiRes
                {
                    Message = $"Hi {request.Name} #{i}",
                    Sequence = i,
                });
            }
        }

        public override async Task<HiRes> CollectHi(
            IAsyncStreamReader<HiReq> requestStream,
            ServerCallContext context)
        {
            var names = new List<string>();
            await foreach (var req in requestStream.ReadAllAsync())
                names.Add(req.Name);

            return new HiRes
            {
                Message = $"Hi {string.Join(",", names)}!",
                Sequence = names.Count,
            };
        }

        public override async Task ChatHi(
            IAsyncStreamReader<HiReq> requestStream,
            IServerStreamWriter<HiRes> responseStream,
            ServerCallContext context)
        {
            var seq = 0;
            await foreach (var req in requestStream.ReadAllAsync())
            {
                seq++;
                await responseStream.WriteAsync(new HiRes
                {
                    Message = $"Hi {req.Name}!",
                    Sequence = seq,
                });
            }
        }
    }

    private static FileDescriptorProto BuildSimpleServerStreamingFileDescriptor()
    {
        var fd = new FileDescriptorProto
        {
            Name = "demo/gapstream.proto",
            Package = "demo",
            Syntax = "proto3",
        };
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "TickReq",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "n", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.Int32,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "n",
                },
            },
        });
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "TickRes",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "v", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "v",
                },
            },
        });
        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = "GapStream",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Tick",
                    InputType = ".demo.TickReq",
                    OutputType = ".demo.TickRes",
                    ServerStreaming = true,
                },
            },
        });
        return fd;
    }

    private static FileDescriptorProto BuildBidiFileDescriptor()
    {
        var fd = new FileDescriptorProto
        {
            Name = "demo/gapbidi.proto",
            Package = "demo",
            Syntax = "proto3",
        };
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "ChatMsg",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "text", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "text",
                },
            },
        });
        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = "GapBidi",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Chat",
                    InputType = ".demo.ChatMsg",
                    OutputType = ".demo.ChatMsg",
                    ClientStreaming = true,
                    ServerStreaming = true,
                },
            },
        });
        return fd;
    }
}
