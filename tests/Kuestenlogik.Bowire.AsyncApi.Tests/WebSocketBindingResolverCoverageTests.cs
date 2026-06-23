// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Branch-coverage uplift for <see cref="WebSocketBindingResolver"/>.
/// Complements <c>WebSocketBindingResolverTests</c> by exercising the
/// surface the happy-path tests don't reach: <c>BindingId</c> getter,
/// the deliberately-unimplemented <c>BuildMethod</c> sentinel, and the
/// callerMetadata merge branch (when a caller passes overrides on top
/// of the AsyncAPI <c>bindings.ws.*</c> bag).
/// </summary>
public sealed class WebSocketBindingResolverCoverageTests
{
    [Fact]
    public void BindingId_Is_ws()
    {
        var registry = new BowireProtocolRegistry();
        var resolver = new WebSocketBindingResolver(registry);

        Assert.Equal("ws", resolver.BindingId);
    }

    [Fact]
    public void BuildMethod_Throws_NotImplementedException_With_Phase_Note()
    {
        var registry = new BowireProtocolRegistry();
        var resolver = new WebSocketBindingResolver(registry);
        var ctx = new AsyncApiChannelContext(
            ServerUrl: "ws://example",
            ChannelAddress: "/x",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var ex = Assert.Throws<NotImplementedException>(() => resolver.BuildMethod(ctx));
        Assert.Contains("WebSocketBindingResolver.BuildMethod", ex.Message);
    }

    [Fact]
    public async Task InvokeAsync_CallerMetadata_Overlays_BindingFields_Last_Write_Wins()
    {
        // Binding fields carry one value; caller metadata carries a
        // different value for the same key. The merge order in
        // MergeWebSocketBindingFields applies binding fields first, then
        // caller metadata — so the caller wins on collision.
        var captured = new CapturingWebSocketProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new WebSocketBindingResolver(registry);
        var ctx = new AsyncApiChannelContext(
            ServerUrl: "ws://api",
            ChannelAddress: "/events",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["custom"] = "from-binding",
                ["subprotocol"] = "binding-proto"
            });
        var callerMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["custom"] = "from-caller",
            ["extra"] = "caller-only"
        };

        var result = await resolver.InvokeAsync(
            ctx,
            jsonMessages: ["{}"],
            metadata: callerMetadata,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        // Caller-wins on collision.
        Assert.Equal("from-caller", captured.LastMetadata?["custom"]);
        // Caller-only key still reaches the plugin.
        Assert.Equal("caller-only", captured.LastMetadata?["extra"]);
        // Binding-only key still reaches the plugin verbatim.
        Assert.Equal("binding-proto", captured.LastMetadata?["subprotocol"]);
        // The subprotocol → X-Bowire-Subprotocol marker mapping is
        // sourced from binding fields (not callerMetadata).
        Assert.Equal("binding-proto", captured.LastMetadata?["X-Bowire-Subprotocol"]);
    }

    [Fact]
    public async Task InvokeAsync_BindingFields_Empty_Subprotocol_Does_Not_Set_Marker()
    {
        // When subprotocol is whitespace/empty, the resolver must NOT
        // forward an empty X-Bowire-Subprotocol marker — the upgrade
        // layer treats absent + empty differently.
        var captured = new CapturingWebSocketProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new WebSocketBindingResolver(registry);
        var ctx = new AsyncApiChannelContext(
            ServerUrl: "ws://api",
            ChannelAddress: "/events",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["subprotocol"] = "   "
            });

        await resolver.InvokeAsync(
            ctx,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.False(captured.LastMetadata?.ContainsKey("X-Bowire-Subprotocol"));
    }

    [Fact]
    public async Task InvokeAsync_With_Empty_BindingFields_Succeeds()
    {
        // The Merge helper is robust to an empty dictionary — exercises
        // the "no fields" branch without hitting the subprotocol-marker
        // assignment.
        var captured = new CapturingWebSocketProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new WebSocketBindingResolver(registry);
        var ctx = new AsyncApiChannelContext(
            ServerUrl: "ws://api",
            ChannelAddress: "/events",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            ctx,
            jsonMessages: ["""{"k":1}"""],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.False(captured.LastMetadata?.ContainsKey("X-Bowire-Subprotocol"));
    }

    [Fact]
    public async Task InvokeAsync_With_Empty_JsonMessages_Sends_Empty_Frame()
    {
        // The resolver picks jsonMessages.FirstOrDefault() ?? string.Empty.
        // No messages → one frame with empty payload.
        var captured = new CapturingWebSocketProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new WebSocketBindingResolver(registry);
        var ctx = new AsyncApiChannelContext(
            ServerUrl: "ws://api",
            ChannelAddress: "/events",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            ctx,
            jsonMessages: [],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal(1, captured.LastChannel?.SendCount);
        Assert.Equal("0", result.Metadata["channel.bytesSent"]);
    }

    [Fact]
    public async Task InvokeAsync_SendAsync_Returns_False_Reports_Error_Status()
    {
        // The channel SendAsync returns false (e.g. socket already
        // closed). The resolver should report Status=Error rather than
        // silently OK.
        var rejecting = new RejectingSendProtocol("websocket");
        var registry = new BowireProtocolRegistry();
        registry.Register(rejecting);

        var resolver = new WebSocketBindingResolver(registry);
        var ctx = new AsyncApiChannelContext(
            ServerUrl: "ws://api",
            ChannelAddress: "/x",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            ctx,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Null(result.Response);
    }

    // ---- Test doubles -------------------------------------------------

    private sealed class CapturingWebSocketProtocol : IBowireProtocol
    {
        public string Id => "websocket";
        public string Name => "Capturing WebSocket";
        public string IconSvg => "<svg/>";

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
            LastMetadata = metadata is null ? null : new Dictionary<string, string>(metadata);
            LastChannel = new ProbeChannel();
            return Task.FromResult<IBowireChannel?>(LastChannel);
        }
    }

    private sealed class RejectingSendProtocol(string id) : IBowireProtocol
    {
        public string Id { get; } = id;
        public string Name => "Rejecting " + Id;
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
            => Task.FromResult<IBowireChannel?>(new RejectingChannel());
    }

    private sealed class RejectingChannel : IBowireChannel
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public bool IsClientStreaming => false;
        public bool IsServerStreaming => false;
        public int SentCount => 0;
        public bool IsClosed => false;
        public long ElapsedMs => 0;

        public Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> ReadResponsesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProbeChannel : IBowireChannel
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public bool IsClientStreaming => false;
        public bool IsServerStreaming => false;
        public int SentCount => SendCount;
        public int SendCount { get; private set; }
        public bool IsClosed { get; private set; }
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
