// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Phase C NATS resolver coverage. Verifies the translation layer
/// without spinning up a real <c>nats-server</c> (the NATS plugin's
/// own live round-trip suite tests broker round-trips against the
/// official release binary).
///
/// Three things this file pins:
///   1. The resolver translates AsyncAPI <c>bindings.nats.queue</c> /
///      <c>replyTo</c> onto the metadata keys the NATS plugin reads
///      (<c>queue_group</c> / <c>reply_to</c>); other binding scalars
///      pass through verbatim.
///   2. Caller-supplied metadata wins over doc-bindings — lets a UI
///      override the doc's queue group for a one-off publish.
///   3. Without a NATS plugin loaded the resolver returns a clear
///      error pointing at the NuGet package to add (mirrors the
///      Kafka- / AMQP-not-loaded error paths).
/// </summary>
public sealed class NatsBindingResolverTests
{
    [Fact]
    public async Task InvokeAsync_DispatchesToNatsPlugin_WithMergedBindingFields()
    {
        var captured = new CapturingProtocol("nats");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new NatsBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "nats://broker:4222",
            ChannelAddress: "orders.created",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["queue"] = "order-workers",
                ["replyTo"] = "orders.replies",
                ["bindingVersion"] = "0.1.0"
            });

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"orderId":"o-1"}"""],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("nats://broker:4222", captured.LastServerUrl);
        Assert.Equal("orders.created", captured.LastService);    // subject ← channel address
        Assert.Equal("publish", captured.LastMethod);             // literal NATS verb
        // `queue` / `replyTo` are renamed onto the keys the NATS
        // plugin reads; bindingVersion stays under its own name as a
        // pass-through diagnostic.
        Assert.Equal("order-workers", captured.LastMetadata?["queue_group"]);
        Assert.Equal("orders.replies", captured.LastMetadata?["reply_to"]);
        Assert.Equal("0.1.0", captured.LastMetadata?["bindingVersion"]);
        Assert.False(captured.LastMetadata?.ContainsKey("queue"));
        Assert.False(captured.LastMetadata?.ContainsKey("replyTo"));
    }

    [Fact]
    public async Task InvokeAsync_CallerMetadataOverridesBindingFields()
    {
        var captured = new CapturingProtocol("nats");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new NatsBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "nats://broker:4222",
            ChannelAddress: "events",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["queue"] = "doc-default-group"
            });

        await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"a":1}"""],
            metadata: new Dictionary<string, string> { ["queue_group"] = "caller-override-group" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("caller-override-group", captured.LastMetadata?["queue_group"]);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsErrorWhenNatsPluginNotLoaded()
    {
        // Empty registry — NATS plugin is missing on the host.
        var registry = new BowireProtocolRegistry();
        var resolver = new NatsBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "nats://broker:4222",
            ChannelAddress: "events",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("NATS plugin", result.Metadata["error"]);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.Nats", result.Metadata["error"]);
    }

    [Fact]
    public void BindingId_IsNats() => Assert.Equal("nats", new NatsBindingResolver(new BowireProtocolRegistry()).BindingId);

    [Fact]
    public void BuildMethod_NotYetImplemented_DocumentsThePhase()
    {
        // Same intentional deferral as Kafka / AMQP — guards against
        // someone wiring BuildMethod into the loader before the
        // per-binding method-metadata phase lands.
        var resolver = new NatsBindingResolver(new BowireProtocolRegistry());
        var ctx = new AsyncApiChannelContext(
            ServerUrl: "nats://b:4222", ChannelAddress: "s", OperationAction: "send",
            BindingFields: new Dictionary<string, string>());
        Assert.Throws<NotImplementedException>(() => resolver.BuildMethod(ctx));
    }

    /// <summary>
    /// Records the last set of arguments an InvokeAsync call landed
    /// on — lets the tests assert the resolver's translation (channel
    /// address → subject, fixed "publish" method, merged metadata)
    /// without spinning up the NATS plugin proper.
    /// </summary>
    internal sealed class CapturingProtocol(string id) : IBowireProtocol
    {
        public string Id { get; } = id;
        public string Name => "Capturing " + Id;
        public string IconSvg => "<svg/>";

        public string? LastServerUrl { get; private set; }
        public string? LastService { get; private set; }
        public string? LastMethod { get; private set; }
        public Dictionary<string, string>? LastMetadata { get; private set; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            LastServerUrl = serverUrl;
            LastService = service;
            LastMethod = method;
            LastMetadata = metadata is null ? null : new Dictionary<string, string>(metadata);
            return Task.FromResult(new InvokeResult(
                Response: "{}", DurationMs: 1, Status: "OK",
                Metadata: new Dictionary<string, string>()));
        }

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
}
