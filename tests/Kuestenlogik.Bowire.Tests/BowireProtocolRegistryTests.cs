// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for <see cref="BowireProtocolRegistry"/>. Covers the
/// non-discovery surface — Register, GetById, the FindXxx capability
/// lookups (HTTP invoker, SSE subscriber, WebSocket channel) — by
/// hand-rolling stub plugins. The Discover() path needs a live AppDomain
/// scan and is exercised indirectly by the integration tests.
/// </summary>
public class BowireProtocolRegistryTests
{
    [Fact]
    public void Register_Adds_To_Protocols_List()
    {
        var registry = new BowireProtocolRegistry();
        var plugin = new StubProtocol("a", "Alpha");

        registry.Register(plugin);

        Assert.Single(registry.Protocols);
        Assert.Same(plugin, registry.Protocols[0]);
    }

    [Fact]
    public void Register_Preserves_Insertion_Order()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("first", "First"));
        registry.Register(new StubProtocol("second", "Second"));
        registry.Register(new StubProtocol("third", "Third"));

        Assert.Collection(registry.Protocols,
            p => Assert.Equal("first", p.Id),
            p => Assert.Equal("second", p.Id),
            p => Assert.Equal("third", p.Id));
    }

    [Fact]
    public void GetById_Returns_Matching_Protocol()
    {
        var registry = new BowireProtocolRegistry();
        var alpha = new StubProtocol("alpha", "Alpha");
        var beta = new StubProtocol("beta", "Beta");
        registry.Register(alpha);
        registry.Register(beta);

        Assert.Same(beta, registry.GetById("beta"));
        Assert.Same(alpha, registry.GetById("alpha"));
    }

    [Fact]
    public void GetById_Unknown_Returns_Null()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("alpha", "Alpha"));

        Assert.Null(registry.GetById("missing"));
        Assert.Null(registry.GetById(""));
    }

    [Fact]
    public void GetById_Is_Case_Sensitive()
    {
        // Plugin ids are documented as lower-case; case sensitivity matters
        // because allowlist checks and protocol-resolution paths key on the
        // exact id string. This pins the contract.
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("grpc", "gRPC"));

        Assert.Null(registry.GetById("GRPC"));
        Assert.Null(registry.GetById("Grpc"));
        Assert.NotNull(registry.GetById("grpc"));
    }

    [Fact]
    public void FindHttpInvoker_Returns_Null_When_None_Registered()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("plain", "Plain"));

        Assert.Null(registry.FindHttpInvoker());
    }

    [Fact]
    public void FindHttpInvoker_Returns_First_Plugin_That_Implements_Interface()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("plain", "Plain"));
        var invoker = new HttpInvokerPlugin();
        registry.Register(invoker);
        // Second invoker after the first one — FindHttpInvoker must return
        // the first match (the foreach short-circuits on the initial hit).
        registry.Register(new HttpInvokerPlugin());

        Assert.Same(invoker, registry.FindHttpInvoker());
    }

    [Fact]
    public void FindSseSubscriber_Returns_Null_When_None_Registered()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("plain", "Plain"));

        Assert.Null(registry.FindSseSubscriber());
    }

    [Fact]
    public void FindSseSubscriber_Returns_First_Plugin_That_Implements_Interface()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("plain", "Plain"));
        var sse = new SseSubscriberPlugin();
        registry.Register(sse);

        Assert.Same(sse, registry.FindSseSubscriber());
    }

    [Fact]
    public void FindWebSocketChannel_Returns_Null_When_None_Registered()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("plain", "Plain"));

        Assert.Null(registry.FindWebSocketChannel());
    }

    [Fact]
    public void FindWebSocketChannel_Returns_First_Plugin_That_Implements_Interface()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("plain", "Plain"));
        var ws = new WebSocketChannelPlugin();
        registry.Register(ws);

        Assert.Same(ws, registry.FindWebSocketChannel());
    }

    // Note: BowireProtocolRegistry.Discover() exercises an AppDomain-wide
    // assembly scan and instantiates every IBowireProtocol implementation
    // it finds. Some of those plugins hold process-static state that
    // other test classes mutate in parallel; calling Discover() here would
    // race with those tests. The Discover() path is exercised end-to-end
    // by the integration tests instead, where it runs in a clean process
    // per fixture.

    private class StubProtocol(string id, string name) : IBowireProtocol
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));

#pragma warning disable CS1998 // No-op stream stub
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
            CancellationToken ct = default) => Task.FromResult<IBowireChannel?>(null);
    }

    private sealed class HttpInvokerPlugin : StubProtocol, IInlineHttpInvoker
    {
        public HttpInvokerPlugin() : base("http-stub", "HttpStub") { }

        public Task<InvokeResult> InvokeHttpAsync(
            string serverUrl, BowireMethodInfo methodInfo,
            List<string> jsonMessages, Dictionary<string, string>? metadata,
            CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));
    }

    private sealed class SseSubscriberPlugin : StubProtocol, IInlineSseSubscriber
    {
        public SseSubscriberPlugin() : base("sse-stub", "SseStub") { }

#pragma warning disable CS1998 // Stub
        public async IAsyncEnumerable<string> SubscribeAsync(
            string url, Dictionary<string, string>? headers,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998
    }

    private sealed class WebSocketChannelPlugin : StubProtocol, IInlineWebSocketChannel
    {
        public WebSocketChannelPlugin() : base("ws-stub", "WsStub") { }

        public Task<IBowireChannel> OpenAsync(
            string url, IReadOnlyList<string>? subProtocols,
            Dictionary<string, string>? headers, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
