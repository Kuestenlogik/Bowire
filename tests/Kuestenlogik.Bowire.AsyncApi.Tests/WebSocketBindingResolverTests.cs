// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Phase B WebSocket resolver coverage. Verifies the single-shot
/// open + send + close translation without standing up a real
/// WebSocket server (the WebSocket plugin's own integration tests
/// cover the wire-level handshake + frame transport).
///
/// Pins the behaviour that's specific to this resolver:
///   1. AsyncAPI <c>send</c> over a ws-bound channel results in an
///      <c>OpenChannelAsync</c> call carrying the channel address as
///      both service and method, plus the binding fields on the
///      metadata bag.
///   2. The resolver sends exactly one frame, then closes + disposes
///      the channel — no leaked sockets.
///   3. AsyncAPI <c>bindings.ws.subprotocol</c> is forwarded as the
///      <c>X-Bowire-Subprotocol</c> marker the WebSocket plugin reads.
///   4. Without a WebSocket plugin loaded the resolver returns a
///      clear error pointing at the NuGet package to add.
/// </summary>
public sealed class WebSocketBindingResolverTests
{
    [Fact]
    public async Task InvokeAsync_OpensChannel_SendsOneFrame_ThenClosesAndDisposes()
    {
        var captured = new CapturingWebSocketProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new WebSocketBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "ws://api.example.com",
            ChannelAddress: "/events",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"event":"hello"}"""],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal(1, captured.OpenChannelCallCount);
        Assert.Equal("ws://api.example.com", captured.LastServerUrl);
        Assert.Equal("/events", captured.LastService);    // service = channel address
        Assert.Equal("/events", captured.LastMethod);     // method  = channel address
        Assert.Equal(1, captured.LastChannel?.SendCount);
        Assert.True(captured.LastChannel?.IsClosed);
        Assert.True(captured.LastChannel?.IsDisposed);
    }

    [Fact]
    public async Task InvokeAsync_ForwardsSubprotocolAsBowireMarker()
    {
        var captured = new CapturingWebSocketProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new WebSocketBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "ws://api.example.com",
            ChannelAddress: "/chat",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["subprotocol"] = "soap-1.2"
            });

        await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        // The literal binding field stays on the bag (verbatim
        // pass-through) AND maps onto the marker the WebSocket
        // plugin reads at the upgrade layer. Both keys present.
        Assert.Equal("soap-1.2", captured.LastMetadata?["subprotocol"]);
        Assert.Equal("soap-1.2", captured.LastMetadata?["X-Bowire-Subprotocol"]);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsErrorWhenWebSocketPluginNotLoaded()
    {
        var registry = new BowireProtocolRegistry();
        var resolver = new WebSocketBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "ws://api.example.com",
            ChannelAddress: "/events",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("WebSocket", result.Metadata["error"]);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.WebSocket", result.Metadata["error"]);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsErrorWhenChannelOpenReturnsNull()
    {
        // Plugin is loaded but rejects the open (e.g. unreachable
        // URL, subprotocol mismatch, mTLS failure). The resolver
        // should surface that as a structured error instead of
        // calling SendAsync on a null channel.
        var nullOpener = new NullChannelProtocol("websocket");
        var registry = new BowireProtocolRegistry();
        registry.Register(nullOpener);

        var resolver = new WebSocketBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "ws://nope",
            ChannelAddress: "/x",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("no channel", result.Metadata["error"], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stand-in WebSocket plugin that records the OpenChannelAsync
    /// args and hands back a probe channel. Lets the tests verify
    /// the resolver's open/send/close sequence without a real wire.
    /// </summary>
    private sealed class CapturingWebSocketProtocol : IBowireProtocol
    {
        public string Id => "websocket";
        public string Name => "Capturing WebSocket";
        public string IconSvg => "<svg/>";

        public int OpenChannelCallCount { get; private set; }
        public string? LastServerUrl { get; private set; }
        public string? LastService { get; private set; }
        public string? LastMethod { get; private set; }
        public Dictionary<string, string>? LastMetadata { get; private set; }
        public ProbeChannel? LastChannel { get; private set; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            OpenChannelCallCount++;
            LastServerUrl = serverUrl;
            LastService = service;
            LastMethod = method;
            LastMetadata = metadata is null ? null : new Dictionary<string, string>(metadata);
            LastChannel = new ProbeChannel();
            return Task.FromResult<IBowireChannel?>(LastChannel);
        }
    }

    /// <summary>
    /// Stand-in plugin that always returns a null channel — exercises
    /// the resolver's "open failed" branch.
    /// </summary>
    private sealed class NullChannelProtocol(string id) : IBowireProtocol
    {
        public string Id { get; } = id;
        public string Name => "Null-channel " + Id;
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    /// <summary>
    /// Minimal IBowireChannel probe — counts SendAsync calls, flips
    /// IsClosed on CloseAsync, IsDisposed on DisposeAsync. Lets the
    /// tests assert exactly-one-frame + clean teardown without
    /// driving real WebSocket bytes.
    /// </summary>
    private sealed class ProbeChannel : IBowireChannel
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public bool IsClientStreaming => false;
        public bool IsServerStreaming => false;
        public int SentCount => SendCount;
        public int SendCount { get; private set; }
        public bool IsClosed { get; private set; }
        public bool IsDisposed { get; private set; }
        public long ElapsedMs => 0;

        public Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
        {
            SendCount++;
            return Task.FromResult(true);
        }

        public Task CloseAsync(CancellationToken ct = default)
        {
            IsClosed = true;
            return Task.CompletedTask;
        }

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> ReadResponsesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
