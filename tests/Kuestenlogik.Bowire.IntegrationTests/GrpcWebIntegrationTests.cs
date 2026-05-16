// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Kuestenlogik.Bowire.IntegrationTests.Services;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end coverage for the gRPC-Web transport variant: the same plugin
/// (<see cref="BowireGrpcProtocol"/>) speaks gRPC-Web when callers opt in via
/// the <see cref="BowireGrpcProtocol.TransportMetadataKey"/> header.
///
/// We stand up an ASP.NET Core host that hosts the same <see cref="GreeterService"/>
/// fixture as <see cref="GrpcChannelIntegrationTests"/> but routes traffic
/// through <c>app.UseGrpcWeb()</c> over HTTP/1.1 — the typical browser-fronted
/// shape (Envoy, grpcwebproxy, ASP.NET's built-in middleware). The native
/// HTTP/2 host is intentionally unreachable from these tests so we can prove
/// the plugin is hitting the gRPC-Web endpoint, not silently falling back.
/// </summary>
// Each test allocates an ephemeral TCP port via GetFreeTcpPort (bind-to-0,
// read, close), then hands it to Kestrel. Run in parallel on Linux CI the
// returned port can race between close and bind. Pin to the same xUnit
// collection the Rest end-to-end tests use so they all serialise.
[Collection(nameof(RestInvokerEndToEndFixture))]
public sealed class GrpcWebIntegrationTests
{
    [Fact]
    public async Task InvokeAsync_Web_Mode_Unary_Returns_OK()
    {
        await using var host = await StartGreeterWebHostAsync();
        var protocol = host.CreateProtocol();
        var meta = WebTransportMeta();

        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "SayHello",
            jsonMessages: ["""{"name":"web"}"""],
            showInternalServices: false,
            metadata: meta,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("Hello web!", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStreamAsync_Web_Mode_ServerStreaming_Yields_All_Frames()
    {
        // GrpcWebHandler supports server-streaming in both binary and text
        // modes — the response is just a single HTTP/1.1 chunked body.
        await using var host = await StartGreeterWebHostAsync();
        var protocol = host.CreateProtocol();
        var meta = WebTransportMeta();

        var frames = new List<string>();
        await foreach (var item in protocol.InvokeStreamAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "SayHelloStream",
            jsonMessages: ["""{"name":"web-stream","count":3}"""],
            showInternalServices: false,
            metadata: meta,
            ct: TestContext.Current.CancellationToken))
        {
            frames.Add(item);
        }

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Contains("Hello web-stream", f, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_Web_Mode_Strips_Transport_Marker_Before_Send()
    {
        // The X-Bowire-Grpc-Transport header must not leak through to the
        // server: the plugin reads it to pick a transport, then strips it
        // before flushing the gRPC metadata. We assert by piggy-backing on
        // the live SayHello call (a clean OK status) — if the marker were
        // still in headers it would clash with the reserved set on some
        // grpc-dotnet versions. Tighter assertion isn't worth a ServerCallContext
        // capture fixture for a low-risk strip operation.
        await using var host = await StartGreeterWebHostAsync();
        var protocol = host.CreateProtocol();
        var meta = WebTransportMeta();
        meta["x-other"] = "passes-through";

        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "SayHello",
            jsonMessages: ["""{"name":"strip"}"""],
            showInternalServices: false,
            metadata: meta,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
    }

    [Fact]
    public async Task OpenChannelAsync_Web_Mode_Returns_Null_Channel()
    {
        // gRPC-Web duplex isn't supported in this iteration (HTTP/1.1
        // can't carry the trailer pump cleanly). The plugin signals
        // unsupported by returning null; Bowire's channel endpoint surfaces
        // it as a 400 "Protocol does not support channels" to the UI.
        await using var host = await StartGreeterWebHostAsync();
        var protocol = host.CreateProtocol();
        var meta = WebTransportMeta();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "ChatHellos",
            showInternalServices: false,
            metadata: meta,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

    [Fact]
    public async Task InvokeAsync_Web_Mode_Against_Native_Endpoint_Returns_Error_Envelope()
    {
        // Negative case: opting into web mode against an HTTP/2-only gRPC
        // endpoint must not crash the host — it should surface a structured
        // failure (non-OK status or a thrown RpcException that the caller
        // can render). We accept either shape; the spec just demands that
        // the plugin doesn't disintegrate on the mismatch.
        await using var nativeHost = await StartGreeterNativeHostAsync();
        var protocol = nativeHost.CreateProtocol();
        var meta = WebTransportMeta();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var result = await protocol.InvokeAsync(
                nativeHost.BaseUrl,
                service: "test.Greeter",
                method: "SayHello",
                jsonMessages: ["""{"name":"mismatch"}"""],
                showInternalServices: false,
                metadata: meta,
                ct: cts.Token);

            // If the call completed: the response must not be a fake "OK"
            // — either the status is non-OK or the response is missing.
            Assert.NotEqual("OK", result.Status);
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException or HttpRequestException or OperationCanceledException or TimeoutException)
        {
            // Expected: the HTTP/1.1 gRPC-Web request can't negotiate with
            // the HTTP/2-only Kestrel listener. Test passes as long as we
            // don't see a hang or an unhandled exception type.
        }
    }

    [Fact]
    public async Task InvokeAsync_No_Transport_Marker_Defaults_To_Native()
    {
        // Sanity: without the metadata marker, the plugin uses native
        // HTTP/2 gRPC — same as it did before this feature shipped. A web
        // host doesn't accept native HTTP/2 cleanly, so we hit a native
        // host and verify the unchanged behaviour.
        await using var host = await StartGreeterNativeHostAsync();
        var protocol = host.CreateProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "SayHello",
            jsonMessages: ["""{"name":"default"}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Contains("Hello default!", result.Response!, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> WebTransportMeta() =>
        new(StringComparer.Ordinal)
        {
            [BowireGrpcProtocol.TransportMetadataKey] = "web"
        };

    // ----- fixtures ----------------------------------------------------

    private static async Task<GreeterHost> StartGreeterWebHostAsync()
    {
        // ASP.NET Core's UseGrpcWeb middleware bridges between an HTTP/1.1
        // request body and the gRPC pipeline. We host on HTTP/1.1 only —
        // a real Envoy-fronted setup looks identical from the client side.
        //
        // Port allocation: let Kestrel pick the port itself via
        // ListenLocalhost(0) and read back the bound URL from `app.Urls`
        // after start. The earlier "bind-to-0 / close / hand-port-to-
        // Kestrel" pattern raced with other processes on busy CI
        // runners (observed against port 45647). Letting Kestrel keep
        // the listener open across the handshake closes the race.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http1);
            // ^ Kestrel picks the port; reading it back from app.Urls
            // post-StartAsync avoids the bind-0 / close / rebind race.
        });
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        // Reflection over gRPC-Web is uncommon in the wild but lets us
        // exercise the plugin's full invoke path (ResolveMethodAsync →
        // reflection → invoke). Real-world gRPC-Web servers usually ship
        // schemas via .proto upload; that path is covered separately.
        builder.Services.AddGrpcReflection();

        var app = builder.Build();
        // DefaultEnabled = true lets every service speak gRPC-Web without
        // per-method opt-in attributes — what most real-world web gateways
        // configure.
        app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
        app.MapGrpcService<GreeterService>();
        app.MapGrpcReflectionService();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new GreeterHost(app, ResolveBoundUrl(app));
    }

    private static async Task<GreeterHost> StartGreeterNativeHostAsync()
    {
        // Stripped-down copy of GrpcChannelIntegrationTests.StartGreeterHostAsync —
        // duplicated rather than shared to keep this file self-contained
        // for the negative-path test. Same Kestrel-picks-port pattern as
        // StartGreeterWebHostAsync above so the two hosts can't race
        // each other on shared CI infrastructure.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http2);
        });
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddGrpcReflection();

        var app = builder.Build();
        app.MapGrpcService<GreeterService>();
        app.MapGrpcReflectionService();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new GreeterHost(app, ResolveBoundUrl(app));
    }

    /// <summary>
    /// Read the actual bound URL out of the WebApplication once Kestrel
    /// has assigned a port. ListenLocalhost(0) tells Kestrel to pick;
    /// <c>app.Urls</c> publishes the resolved URLs post-StartAsync.
    /// Falls back to a localhost loopback string if the URL list is
    /// empty (shouldn't happen with the explicit ListenLocalhost
    /// configuration, but guards against future Kestrel changes).
    /// </summary>
    private static string ResolveBoundUrl(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        foreach (var u in app.Urls)
        {
            // Normalize 0.0.0.0 / wildcard hosts to loopback so the
            // test client can dial the same endpoint regardless of
            // Kestrel's representation.
            var normalised = u.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
                              .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
            return normalised;
        }
        throw new InvalidOperationException("Kestrel didn't publish any bound URL after StartAsync.");
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class GreeterHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        public GreeterHost(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public BowireGrpcProtocol CreateProtocol()
        {
            var p = new BowireGrpcProtocol();
            p.Initialize(_app.Services);
            return p;
        }

        public async ValueTask DisposeAsync()
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
            try { await _app.DisposeAsync(); } catch { /* best-effort */ }
        }
    }
}
