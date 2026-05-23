// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Phase C AMQP resolver coverage. Mirrors the Kafka/MQTT resolver
/// shape — verifies the translation layer (channel address → service,
/// fixed "send" method, merged metadata, caller-wins-over-doc) plus
/// the two-binding-keys-one-resolver pattern (bindings.amqp and
/// bindings.amqp1 both route at the AMQP wire plugin).
///
/// Real broker round-trips live in the wire plugin's own integration
/// suite (Testcontainers against RabbitMQ + ActiveMQ Artemis once
/// that lands); here we pin the AsyncAPI → plugin call contract
/// without spinning either broker up.
/// </summary>
public sealed class AmqpBindingResolverTests
{
    [Fact]
    public async Task InvokeAsync_DispatchesToAmqpPlugin_WithMergedBindingFields()
    {
        var captured = new CapturingProtocol("amqp");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new AmqpBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "amqp://broker:5672/myvhost",
            ChannelAddress: "orders.exchange",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["routingKey"] = "orders.created",
                ["deliveryMode"] = "2",
                ["expiration"] = "60000"
            });

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"orderId":"o-1"}"""],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("amqp://broker:5672/myvhost", captured.LastServerUrl);
        Assert.Equal("orders.exchange", captured.LastService);    // exchange ← channel address
        Assert.Equal("send", captured.LastMethod);                 // literal AMQP verb
        Assert.Equal("orders.created", captured.LastMetadata?["routingKey"]);
        Assert.Equal("2", captured.LastMetadata?["deliveryMode"]);
        Assert.Equal("60000", captured.LastMetadata?["expiration"]);
    }

    [Fact]
    public async Task InvokeAsync_CallerMetadataOverridesBindingFields()
    {
        var captured = new CapturingProtocol("amqp");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new AmqpBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "amqp://broker:5672",
            ChannelAddress: "exchange",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["routingKey"] = "doc-default-key"
            });

        await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"a":1}"""],
            metadata: new Dictionary<string, string> { ["routingKey"] = "caller-override-key" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("caller-override-key", captured.LastMetadata?["routingKey"]);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsErrorWhenAmqpPluginNotLoaded()
    {
        // Empty registry — AMQP plugin is missing on the host.
        var registry = new BowireProtocolRegistry();
        var resolver = new AmqpBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "amqp://broker:5672",
            ChannelAddress: "exchange",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("AMQP plugin", result.Metadata["error"]);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.Amqp", result.Metadata["error"]);
    }

    [Fact]
    public async Task Amqp1_resolver_dispatches_to_same_plugin_id_amqp()
    {
        // bindings.amqp1 still routes at the wire plugin whose Id is
        // "amqp" — the plugin's URL scheme decides which actual wire
        // (RabbitMQ.Client vs AMQPNetLite) carries the publish. The
        // resolver's BindingId is the only thing that changes.
        var captured = new CapturingProtocol("amqp");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new AmqpBindingResolver(registry, bindingId: "amqp1");
        Assert.Equal("amqp1", resolver.BindingId);

        var context = new AsyncApiChannelContext(
            ServerUrl: "amqp1://servicebus:5671",
            ChannelAddress: "queue.invoices",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["address"] = "/queue.invoices"
            });

        await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("amqp1://servicebus:5671", captured.LastServerUrl);
        Assert.Equal("queue.invoices", captured.LastService);
        Assert.Equal("send", captured.LastMethod);
        Assert.Equal("/queue.invoices", captured.LastMetadata?["address"]);
    }

    /// <summary>Captures the InvokeAsync arguments — see Kafka resolver
    /// tests for the same shape.</summary>
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
