// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Kuestenlogik.Bowire.IntegrationTests.Hubs;
using Kuestenlogik.Bowire.Protocol.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end coverage for <see cref="SignalRBowireChannel"/> against a real
/// Kestrel host running <see cref="ChatHub"/>. The channel buffers outgoing
/// messages and pumps responses out of an internal channel, so we drive both
/// the unary (Echo) and server-streaming (Counter) paths plus the close /
/// dispose / error branches that <see cref="MtlsSignalRIntegrationTests"/>
/// only touches via the invoker.
///
/// Why real Kestrel and not <c>WebApplicationFactory</c>'s in-memory
/// TestServer: <c>HubConnection.StartAsync</c> needs a transport that wraps
/// a real socket — TestServer's <c>HttpMessageHandler</c> doesn't support
/// the WebSocket / SSE upgrades SignalR negotiates, and falling back to
/// long-polling through the test pipe behaves erratically under load. The
/// rest of the integration suite uses the same Kestrel-on-loopback pattern
/// (<see cref="PluginTestHost"/>) for protocol channels.
/// </summary>
public sealed class SignalRChannelIntegrationTests
{
    [Fact]
    public async Task OpenChannel_Echo_Unary_RoundTrips_Through_Channel()
    {
        await using var host = await StartChatHubHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "/chathub",
            method: "Echo",
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            Assert.False(channel!.IsServerStreaming);
            Assert.False(channel.IsClientStreaming);
            Assert.False(channel.IsClosed);
            Assert.NotEmpty(channel.Id);
            Assert.Null(channel.NegotiatedSubProtocol);

            var sent = await channel.SendAsync("\"hello\"", TestContext.Current.CancellationToken);
            Assert.True(sent);
            Assert.Equal(1, channel.SentCount);

            await channel.CloseAsync(TestContext.Current.CancellationToken);
            Assert.True(channel.IsClosed);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var responses = new List<string>();
            await foreach (var r in channel.ReadResponsesAsync(cts.Token))
                responses.Add(r);

            var single = Assert.Single(responses);
            Assert.Contains("Echo: hello", single, StringComparison.Ordinal);
            Assert.True(channel.ElapsedMs >= 0);
        }
    }

    [Fact]
    public async Task OpenChannel_Counter_ServerStreaming_Yields_All_Items()
    {
        await using var host = await StartChatHubHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "/chathub",
            method: "Counter",
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            Assert.True(channel!.IsServerStreaming);
            Assert.False(channel.IsClientStreaming);

            // Form-mode payload: a single object whose properties become the
            // positional args. The channel forwards that string into _outgoing
            // verbatim and StreamAsyncCore unwraps it server-side.
            await channel.SendAsync("3", TestContext.Current.CancellationToken);
            await channel.SendAsync("5", TestContext.Current.CancellationToken);
            await channel.CloseAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, channel.SentCount);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var items = new List<string>();
            await foreach (var r in channel.ReadResponsesAsync(cts.Token))
                items.Add(r);

            Assert.Equal(3, items.Count);
            Assert.Equal("1", items[0]);
            Assert.Equal("2", items[1]);
            Assert.Equal("3", items[2]);
        }
    }

    [Fact]
    public async Task OpenChannel_UnknownMethod_Surfaces_Error_Envelope()
    {
        await using var host = await StartChatHubHostAsync();
        var protocol = host.CreateProtocol();

        // Discovery only knows Echo + Counter, so an unknown method falls
        // through to defaults (client + server streaming = true) which
        // routes through StreamAsyncCore. The hub rejects it; the pump's
        // catch branch should serialise the error into a response envelope.
        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "/chathub",
            method: "DoesNotExist",
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            await channel!.SendAsync("\"x\"", TestContext.Current.CancellationToken);
            await channel.CloseAsync(TestContext.Current.CancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var responses = new List<string>();
            await foreach (var r in channel.ReadResponsesAsync(cts.Token))
                responses.Add(r);

            var only = Assert.Single(responses);
            Assert.Contains("error", only, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SendAsync_After_Close_Returns_False_And_Does_Not_Increment_Count()
    {
        await using var host = await StartChatHubHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "/chathub",
            method: "Echo",
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            await channel!.CloseAsync(TestContext.Current.CancellationToken);

            var accepted = await channel.SendAsync("\"too-late\"", TestContext.Current.CancellationToken);

            Assert.False(accepted);
            Assert.Equal(0, channel.SentCount);
        }
    }

    [Fact]
    public async Task DisposeAsync_Cancels_Pump_Mid_Stream_Without_Throwing()
    {
        await using var host = await StartChatHubHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "/chathub",
            method: "Counter",
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(channel);

        // Big delay between items so we tear down while the pump is still
        // awaiting StreamAsyncCore. DisposeAsync should cancel cleanly.
        await channel!.SendAsync("100", TestContext.Current.CancellationToken);
        await channel.SendAsync("500", TestContext.Current.CancellationToken);
        await channel.CloseAsync(TestContext.Current.CancellationToken);

        // Read just one item so the pump is mid-stream, then dispose.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        readCts.CancelAfter(TimeSpan.FromSeconds(10));

        var first = await FirstAsync(channel.ReadResponsesAsync(readCts.Token));
        Assert.Equal("1", first);

        await channel.DisposeAsync();

        // Calling DisposeAsync twice should not throw — the impl cancels +
        // disposes the connection on the first call. We don't re-await
        // here because IAsyncDisposable contract only requires one call,
        // but the surrounding `await using` would do exactly that.
    }

    [Fact]
    public async Task Channel_ElapsedMs_Increases_Over_Time()
    {
        await using var host = await StartChatHubHostAsync();
        var protocol = host.CreateProtocol();

        var channel = await protocol.OpenChannelAsync(
            host.BaseUrl,
            service: "/chathub",
            method: "Echo",
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            var t0 = channel!.ElapsedMs;
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.True(channel.ElapsedMs >= t0);
        }
    }

    [Fact]
    public async Task InvokeStreamAsync_Streams_Counter_Responses()
    {
        // Covers BowireSignalRProtocol.InvokeStreamAsync end-to-end —
        // baseline showed this whole state machine + SignalRInvoker.StreamAsync
        // were 0% covered.
        await using var host = await StartChatHubHostAsync();
        var protocol = host.CreateProtocol();

        var items = new List<string>();
        await foreach (var item in protocol.InvokeStreamAsync(
            host.BaseUrl,
            service: "/chathub",
            method: "Counter",
            jsonMessages: ["{\"count\":4,\"delayMs\":5}"],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        Assert.Equal(4, items.Count);
        Assert.Equal("1", items[0]);
        Assert.Equal("4", items[3]);
    }

    [Fact]
    public async Task InvokeAsync_Echo_Returns_OK_Status()
    {
        // Existing mTLS suite already exercises the happy unary path, but
        // it does so over Kestrel HTTPS with client certs — the plain HTTP
        // flow is what most users hit and was uncovered above the
        // shared connect path.
        await using var host = await StartChatHubHostAsync();
        var protocol = host.CreateProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "/chathub",
            method: "Echo",
            jsonMessages: ["\"plain\""],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("Echo: plain", result.Response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_With_Trace_Header_Strips_To_Plain_Metadata()
    {
        // Pass a non-mTLS metadata bag: TryParseFromMetadata returns null
        // and the original dictionary flows through StripMarker → unchanged.
        // Just want the code path exercised against a live hub.
        await using var host = await StartChatHubHostAsync();
        var protocol = host.CreateProtocol();

        var meta = new Dictionary<string, string> { ["X-Trace-Id"] = "abc-123" };

        var result = await protocol.InvokeAsync(
            host.BaseUrl,
            service: "/chathub",
            method: "Echo",
            jsonMessages: ["\"with-headers\""],
            showInternalServices: false,
            metadata: meta,
            TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
    }

    [Fact]
    public async Task DiscoverAsync_From_Live_Host_Sees_ChatHub()
    {
        await using var host = await StartChatHubHostAsync();
        var protocol = host.CreateProtocol();

        var services = await protocol.DiscoverAsync(
            host.BaseUrl,
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal(nameof(ChatHub), svc.Name);
        Assert.Equal("/chathub", svc.Package);
        Assert.Contains(svc.Methods, m => m.Name == "Echo");
        Assert.Contains(svc.Methods, m => m.Name == "Counter");
    }

    private static async ValueTask<string> FirstAsync(IAsyncEnumerable<string> source)
    {
        await foreach (var s in source) return s;
        throw new InvalidOperationException("Stream produced no items.");
    }

    private static async Task<ChatHubHost> StartChatHubHostAsync()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.MapHub<ChatHub>("/chathub");

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new ChatHubHost(app, url);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ChatHubHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        public ChatHubHost(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public BowireSignalRProtocol CreateProtocol()
        {
            var p = new BowireSignalRProtocol();
            p.Initialize(_app.Services);
            return p;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(TestContext.Current.CancellationToken);
            await _app.DisposeAsync();
        }
    }
}
