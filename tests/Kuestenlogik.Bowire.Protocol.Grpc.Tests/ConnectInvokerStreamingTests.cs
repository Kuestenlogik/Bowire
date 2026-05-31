// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// End-to-end coverage for <see cref="ConnectInvoker.InvokeServerStreamAsync"/>
/// — the framing helpers are unit-tested elsewhere; this file drives
/// the full HTTP path against an in-process Connect-compatible
/// listener that emits length-prefixed envelope frames over the
/// wire. Verifies:
/// <list type="bullet">
///   <item>Sequential frame yields, decoded through the descriptor as
///     JSON.</item>
///   <item>End-of-stream marker terminates the enumeration when the
///     payload carries no <c>error</c>.</item>
///   <item>End-of-stream marker with an <c>error</c> block surfaces as
///     a trailing <c>connect:&lt;code&gt;</c> frame.</item>
///   <item>Non-2xx HTTP responses surface as one trailing error frame
///     with the parsed JSON code, no message frames.</item>
///   <item>Request body sanity: the server receives exactly one
///     framed input envelope decoded as the input message type.</item>
/// </list>
/// </summary>
public sealed class ConnectInvokerStreamingTests
{
    private const string ServiceName = "test.StreamService";
    private const string MethodName = "Server";

    [Fact]
    public async Task InvokeServerStreamAsync_yields_three_message_frames_then_completes_on_end_of_stream()
    {
        var serverPayloads = new[] { "first", "second", "third" };
        using var server = await ConnectTestServer.StartAsync(
            requestObserver: _ => { },
            responseFrames: BuildSuccessResponse(serverPayloads));

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var received = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeServerStreamAsync(
            ServiceName, MethodName,
            StringValue.Descriptor, StringValue.Descriptor,
            "\"client\"", // StringValue's JSON form is a bare quoted string (well-known type)
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            received.Add(frame);
        }

        Assert.Equal(3, received.Count);
        Assert.All(received, f => Assert.Null(f.ErrorCode));
        // JSON formatter for protobuf StringValue emits the bare string
        // value (well-known type special-case).
        Assert.Equal("\"first\"", received[0].Json);
        Assert.Equal("\"second\"", received[1].Json);
        Assert.Equal("\"third\"", received[2].Json);
    }

    [Fact]
    public async Task InvokeServerStreamAsync_surfaces_end_of_stream_error_as_trailing_frame()
    {
        // Two normal frames + an end-of-stream frame whose payload
        // carries an `error` block. Spec maps the code onto
        // `connect:<code>`; the invoker yields it as a trailing
        // frame so the workbench can render a stream-end marker.
        var frames = new List<byte[]>();
        frames.Add(BuildMessageFrame("ok-1"));
        frames.Add(BuildMessageFrame("ok-2"));
        frames.Add(BuildEndStreamFrame(
            """{"error":{"code":"unavailable","message":"upstream cut"}}"""));

        using var server = await ConnectTestServer.StartAsync(
            requestObserver: _ => { },
            responseFrames: frames);

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var received = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeServerStreamAsync(
            ServiceName, MethodName,
            StringValue.Descriptor, StringValue.Descriptor,
            "\"client\"", // StringValue's JSON form is a bare quoted string (well-known type)
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            received.Add(frame);
        }

        Assert.Equal(3, received.Count);
        Assert.Null(received[0].ErrorCode);
        Assert.Null(received[1].ErrorCode);
        Assert.Equal("connect:unavailable", received[2].ErrorCode);
        Assert.Equal("upstream cut", received[2].Json);
    }

    [Fact]
    public async Task InvokeServerStreamAsync_pre_stream_http_error_yields_single_error_frame()
    {
        using var server = await ConnectTestServer.StartAsync(
            requestObserver: _ => { },
            responseFrames: [],
            statusOverride: 500,
            errorBodyOverride: """{"code":"internal","message":"boom"}""");

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var received = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeServerStreamAsync(
            ServiceName, MethodName,
            StringValue.Descriptor, StringValue.Descriptor,
            "\"client\"", // StringValue's JSON form is a bare quoted string (well-known type)
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            received.Add(frame);
        }

        Assert.Single(received);
        Assert.Equal("connect:internal", received[0].ErrorCode);
        Assert.Equal("boom", received[0].Json);
    }

    [Fact]
    public async Task InvokeServerStreamAsync_sends_one_framed_input_envelope_to_the_server()
    {
        // Observer captures the raw request body; we assert that it's
        // a single Connect envelope (1-byte flag + 4-byte BE length +
        // protobuf-encoded StringValue("hello")).
        byte[]? capturedRequest = null;
        using var server = await ConnectTestServer.StartAsync(
            requestObserver: bytes => capturedRequest = bytes,
            responseFrames: BuildSuccessResponse(["done"]));

        using var invoker = new ConnectInvoker(server.BaseUrl);
        await foreach (var _ in invoker.InvokeServerStreamAsync(
            ServiceName, MethodName,
            StringValue.Descriptor, StringValue.Descriptor,
            "\"hello\"", // StringValue well-known type — JSON form is the bare string
            metadata: null,
            TestContext.Current.CancellationToken)) { /* drain */ }

        Assert.NotNull(capturedRequest);
        // 5-byte header + at least 1-byte protobuf wire form for the
        // "hello" payload. Decode the header and confirm shape.
        Assert.True(capturedRequest!.Length > 5);
        Assert.Equal(0x00, capturedRequest[0]); // not end-of-stream
        var declared = BinaryPrimitives.ReadUInt32BigEndian(capturedRequest.AsSpan(1, 4));
        Assert.Equal((uint)(capturedRequest.Length - 5), declared);
        // Decode the body through the StringValue descriptor to
        // verify the JSON-to-protobuf encode landed the right field.
        var decoded = StringValue.Parser.ParseFrom(capturedRequest.AsSpan(5).ToArray());
        Assert.Equal("hello", decoded.Value);
    }

    [Fact]
    public async Task InvokeServerStreamAsync_completes_cleanly_when_end_of_stream_carries_no_error()
    {
        // Empty body end-of-stream — Connect spec says that's the
        // normal "OK, no trailing error" case. Should terminate
        // silently without a trailing error frame.
        var frames = new List<byte[]>
        {
            BuildMessageFrame("only-message"),
            BuildEndStreamFrame(string.Empty), // empty payload
        };
        using var server = await ConnectTestServer.StartAsync(
            requestObserver: _ => { },
            responseFrames: frames);

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var received = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeServerStreamAsync(
            ServiceName, MethodName,
            StringValue.Descriptor, StringValue.Descriptor,
            "\"client\"", // StringValue's JSON form is a bare quoted string (well-known type)
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            received.Add(frame);
        }

        Assert.Single(received);
        Assert.Null(received[0].ErrorCode);
    }

    // ---- helpers --------------------------------------------------

    /// <summary>
    /// Build a list of response frames carrying <paramref name="payloads"/>
    /// as <see cref="StringValue"/> protobuf messages, terminated by a
    /// clean end-of-stream marker (empty body).
    /// </summary>
    private static List<byte[]> BuildSuccessResponse(IEnumerable<string> payloads)
    {
        var list = new List<byte[]>();
        foreach (var p in payloads) list.Add(BuildMessageFrame(p));
        list.Add(BuildEndStreamFrame(string.Empty));
        return list;
    }

    private static byte[] BuildMessageFrame(string value)
    {
        var bytes = new StringValue { Value = value }.ToByteArray();
        return ConnectInvoker.EncodeFrame(flags: 0x00, payload: bytes);
    }

    private static byte[] BuildEndStreamFrame(string json)
    {
        var bytes = json.Length == 0 ? [] : Encoding.UTF8.GetBytes(json);
        return ConnectInvoker.EncodeFrame(flags: ConnectInvoker.EndStreamFlag, payload: bytes);
    }

    /// <summary>
    /// In-process Connect-compatible listener bound to a random free
    /// port. The single registered POST handler reads the request
    /// body verbatim into the supplied request observer, then writes
    /// the configured response frames back as the response body.
    /// When a status override is set, the handler returns that status
    /// code with a JSON error body instead of streaming frames —
    /// exercises the pre-stream-HTTP-error branch.
    /// </summary>
    private sealed class ConnectTestServer : IAsyncDisposable, IDisposable
    {
        public required WebApplication App { get; init; }
        public required string BaseUrl { get; init; }

        public static async Task<ConnectTestServer> StartAsync(
            Action<byte[]> requestObserver,
            List<byte[]> responseFrames,
            int? statusOverride = null,
            string? errorBodyOverride = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");

            app.MapPost($"/{ServiceName}/{MethodName}", async (HttpContext ctx) =>
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                requestObserver(ms.ToArray());

                if (statusOverride is { } status)
                {
                    ctx.Response.StatusCode = status;
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
                {
                    await ctx.Response.Body.WriteAsync(frame, ctx.RequestAborted);
                }
            });

            await app.StartAsync();
            var baseUrl = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();
            return new ConnectTestServer { App = app, BaseUrl = baseUrl };
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
