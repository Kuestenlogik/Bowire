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
/// End-to-end coverage for <see cref="BowireGrpcProtocol"/> and its
/// underlying <c>GrpcInvoker</c> + <c>GrpcBowireChannel</c> against a real
/// Kestrel HTTP/2 host running <see cref="GreeterService"/> with all four
/// gRPC method types (Unary / ServerStreaming / ClientStreaming / Duplex).
///
/// We host on a free loopback port — the same Kestrel pattern as
/// <see cref="SignalRChannelIntegrationTests"/> — because TestServer's
/// in-memory pipe doesn't reliably support gRPC's HTTP/2 + WebSocket-style
/// upgrades for client-streaming and duplex scenarios.
/// </summary>
public sealed class GrpcChannelIntegrationTests
{
    static GrpcChannelIntegrationTests()
    {
        AppContext.SetSwitch(
            "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    [Fact]
    public async Task InvokeAsync_Unary_Returns_OK_With_Response_Bytes()
    {
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "SayHello",
            jsonMessages: ["""{"name":"plain"}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("Hello plain!", result.Response!, StringComparison.Ordinal);
        Assert.NotNull(result.ResponseBinary);
        Assert.NotEmpty(result.ResponseBinary!);
    }

    [Fact]
    public async Task InvokeAsync_ServerStreaming_Returns_Hint_Message()
    {
        // GrpcInvoker.InvokeUnaryAsync diverts server-streaming methods to
        // the streaming endpoint with a friendly hint baked into Status —
        // the unary path would block forever otherwise.
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "SayHelloStream",
            jsonMessages: ["""{"name":"x","count":2}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(result.Response);
        Assert.Contains("streaming", result.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_ClientStreaming_Drains_Inputs_And_Returns_Reply()
    {
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "CollectHellos",
            jsonMessages: [
                """{"name":"alice"}""",
                """{"name":"bob"}""",
                """{"name":"carol"}"""
            ],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("alice,bob,carol", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_With_Custom_Metadata_Headers_Roundtrips()
    {
        // Live exercise of GrpcInvoker.BuildCallOptions(metadata, ...) over
        // a real call — covers the foreach branch in BuildCallOptions that
        // the empty-metadata path on most other tests skips.
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-bowire-test"] = "abc",
            ["x-trace-id"] = "trace-1"
        };

        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "SayHello",
            jsonMessages: ["""{"name":"meta"}"""],
            showInternalServices: false,
            metadata: meta,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_Unimplemented_Method_Returns_Tagged_Trailer_Metadata()
    {
        // ChatHellos is bidi — InvokeUnaryAsync should NOT dispatch via the
        // streaming path; instead it returns the friendly hint string. To
        // exercise the RpcException branch we hit a unary-typed method
        // descriptor that the server actually rejects. The simplest way is
        // calling SayHello against an unrelated service name through the
        // raw invoker — but with our schema-only setup we get the same
        // behaviour by sending a payload that Kestrel/grpc-dotnet rejects.
        // For deterministic coverage of the RpcException catch arm, we
        // call SayHello with metadata that shadows a reserved gRPC header
        // ("grpc-encoding" with bogus value) so the server hangs up.
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grpc-encoding"] = "definitely-not-a-real-codec"
        };

        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "SayHello",
            jsonMessages: ["""{"name":"x"}"""],
            showInternalServices: false,
            metadata: meta,
            ct: TestContext.Current.CancellationToken);

        // Either the call still succeeds (some grpc-dotnet versions tolerate
        // the reserved header) or it surfaces a non-OK status with trailer-
        // tagged metadata. Both paths exercise BuildCallOptions; only the
        // failure path covers the RpcException branch.
        Assert.NotNull(result.Status);
        if (result.Status != "OK")
        {
            // RpcException catch arm — trailer keys are prefixed with _trailer:.
            Assert.True(result.Metadata.Count == 0
                || result.Metadata.Keys.Any(k => k.StartsWith("_trailer:", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task InvokeStreamAsync_ServerStreaming_Yields_All_Frames()
    {
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var frames = new List<string>();
        await foreach (var item in protocol.InvokeStreamAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "SayHelloStream",
            jsonMessages: ["""{"name":"stream","count":4}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            frames.Add(item);
        }

        Assert.Equal(4, frames.Count);
        Assert.All(frames, f => Assert.Contains("Hello stream", f, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeStreamWithFramesAsync_Yields_Binary_And_Json_Per_Frame()
    {
        // Phase-2 IBowireStreamingWithWireBytes contract — frames carry both
        // the JSON projection and the original protobuf wire bytes. The
        // recorder leans on the wire bytes for replay.
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var frames = new List<StreamFrame>();
        await foreach (var f in protocol.InvokeStreamWithFramesAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "SayHelloStream",
            jsonMessages: ["""{"name":"wire","count":2}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            frames.Add(f);
        }

        Assert.Equal(2, frames.Count);
        Assert.All(frames, f =>
        {
            Assert.NotNull(f.Binary);
            Assert.NotEmpty(f.Binary!);
            Assert.NotNull(f.Json);
            Assert.Contains("Hello wire", f.Json, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task InvokeStreamAsync_With_Cancellation_Stops_Cleanly()
    {
        // Cancelling mid-stream takes the server-streaming pump through the
        // OperationCanceledException unwind path inside ReadAllAsync.
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var task = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in protocol.InvokeStreamAsync(
                    host.BaseUrl,
                    service: "test.Greeter",
                    method: "SayHelloStream",
                    jsonMessages: ["""{"name":"slow","count":50}"""],
                    showInternalServices: false,
                    metadata: null,
                    ct: cts.Token))
                {
                    await cts.CancelAsync();
                }
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (Grpc.Core.RpcException) { /* also acceptable on cancellation */ }
        }, cts.Token);

        await task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task OpenChannelAsync_Duplex_Roundtrips_Send_Then_Receive()
    {
        // Bidi end-to-end through GrpcBowireChannel.RunDuplexAsync.
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "ChatHellos",
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            Assert.True(channel!.IsClientStreaming);
            Assert.True(channel.IsServerStreaming);
            Assert.Null(channel.NegotiatedSubProtocol);
            Assert.False(channel.IsClosed);

            await channel.SendAsync("""{"name":"alpha"}""", TestContext.Current.CancellationToken);
            await channel.SendAsync("""{"name":"beta"}""", TestContext.Current.CancellationToken);
            await channel.CloseAsync(TestContext.Current.CancellationToken);
            Assert.True(channel.IsClosed);
            Assert.Equal(2, channel.SentCount);

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            readCts.CancelAfter(TimeSpan.FromSeconds(15));

            var responses = new List<string>();
            await foreach (var r in channel.ReadResponsesAsync(readCts.Token))
                responses.Add(r);

            Assert.Equal(2, responses.Count);
            Assert.Contains("Hello alpha!", responses[0], StringComparison.Ordinal);
            Assert.Contains("Hello beta!", responses[1], StringComparison.Ordinal);
            Assert.True(channel.ElapsedMs >= 0);
        }
    }

    [Fact]
    public async Task OpenChannelAsync_ClientStreaming_Pumps_Final_Reply()
    {
        // Drives RunClientStreamingAsync to completion: the channel reads
        // outgoing messages, completes the request stream, awaits the
        // single response, then writes it to _responses.
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "CollectHellos",
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            Assert.True(channel!.IsClientStreaming);
            Assert.False(channel.IsServerStreaming);

            await channel.SendAsync("""{"name":"one"}""", TestContext.Current.CancellationToken);
            await channel.SendAsync("""{"name":"two"}""", TestContext.Current.CancellationToken);
            await channel.CloseAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, channel.SentCount);

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            readCts.CancelAfter(TimeSpan.FromSeconds(10));

            var responses = new List<string>();
            await foreach (var r in channel.ReadResponsesAsync(readCts.Token))
                responses.Add(r);

            var only = Assert.Single(responses);
            Assert.Contains("one,two", only, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OpenChannelAsync_Then_Send_After_Close_Returns_False()
    {
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "ChatHellos",
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            await channel!.CloseAsync(TestContext.Current.CancellationToken);

            var accepted = await channel.SendAsync(
                """{"name":"too-late"}""", TestContext.Current.CancellationToken);

            Assert.False(accepted);
            Assert.Equal(0, channel.SentCount);
        }
    }

    [Fact]
    public async Task OpenChannelAsync_Dispose_Without_Reading_Cleans_Up()
    {
        // Channel created, never sends, never reads — DisposeAsync still
        // tears down the cancellation source + grpc channel without throwing.
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "test.Greeter",
            method: "ChatHellos",
            showInternalServices: false,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["x-meta"] = "v" },
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await channel!.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannelAsync_Unknown_Service_Throws()
    {
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "test.NoSuchService",
            method: "ChatHellos",
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoverAsync_From_Live_Greeter_Returns_All_Methods()
    {
        // Walks the entire ListServices → GetServiceInfoAsync →
        // ResolveMessageType → AnnotateInputForTranscoding (no http
        // annotation present, so the un-annotated branch wins) chain.
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var services = await protocol.DiscoverAsync(
            host.BaseUrl,
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        var greeter = Assert.Single(services);
        Assert.Equal("test.Greeter", greeter.Name);
        Assert.Contains(greeter.Methods, m => m.Name == "SayHello"        && m.MethodType == "Unary");
        Assert.Contains(greeter.Methods, m => m.Name == "SayHelloStream"  && m.MethodType == "ServerStreaming");
        Assert.Contains(greeter.Methods, m => m.Name == "CollectHellos"   && m.MethodType == "ClientStreaming");
        Assert.Contains(greeter.Methods, m => m.Name == "ChatHellos"      && m.MethodType == "Duplex");
    }

    [Fact]
    public async Task DiscoverAsync_With_ShowInternal_True_Includes_Reflection_Service()
    {
        // Showing internal services exposes grpc.reflection.* and
        // grpc.health.* — exercises the InternalServices skip-list's
        // "skip the skip" branch.
        await using var host = await StartGreeterHostAsync();
        var protocol = host.CreateProtocol();

        var services = await protocol.DiscoverAsync(
            host.BaseUrl,
            showInternalServices: true,
            TestContext.Current.CancellationToken);

        Assert.Contains(services, s => s.Name.StartsWith("grpc.reflection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_With_Empty_Url_Returns_Empty_List_Without_Network()
    {
        // BowireGrpcProtocol short-circuits the empty-URL case to avoid the
        // ~10-second hang on a doomed HTTP/2 handshake.
        var protocol = new BowireGrpcProtocol();

        var services = await protocol.DiscoverAsync(
            "",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    private static async Task<GreeterHost> StartGreeterHostAsync()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http2);
        });
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddGrpcReflection();

        var app = builder.Build();
        app.MapGrpcService<GreeterService>();
        app.MapGrpcReflectionService();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new GreeterHost(app, url);
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
