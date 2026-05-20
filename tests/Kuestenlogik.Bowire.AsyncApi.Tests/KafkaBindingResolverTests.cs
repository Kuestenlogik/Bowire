// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Phase B Kafka resolver coverage. Verifies the translation layer
/// without spinning up a real broker (the Kafka plugin's own
/// integration suite tests broker round-trips with Testcontainers).
///
/// Three things this file pins:
///   1. The resolver maps AsyncAPI <c>bindings.kafka.*</c> scalars
///      onto the metadata bag the Kafka plugin reads (key, partition,
///      schema-registry fields pass through verbatim).
///   2. Caller-supplied metadata wins over doc-bindings — lets a UI
///      override the doc's key / partition for a one-off produce.
///   3. Without a Kafka plugin loaded the resolver returns a clear
///      error pointing at the NuGet package to add (mirrors the
///      MQTT-not-loaded error path).
/// </summary>
public sealed class KafkaBindingResolverTests
{
    [Fact]
    public async Task InvokeAsync_DispatchesToKafkaPlugin_WithMergedBindingFields()
    {
        var captured = new CapturingProtocol("kafka");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new KafkaBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "kafka://broker:9092",
            ChannelAddress: "orders.created",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["key"] = "user-42",
                ["partition"] = "3",
                ["schemaRegistryUrl"] = "http://schemas:8081"
            });

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"orderId":"o-1"}"""],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("kafka://broker:9092", captured.LastServerUrl);
        Assert.Equal("orders.created", captured.LastService);    // topic ← channel address
        Assert.Equal("produce", captured.LastMethod);             // literal Kafka verb
        Assert.Equal("user-42", captured.LastMetadata?["key"]);
        Assert.Equal("3", captured.LastMetadata?["partition"]);
        Assert.Equal("http://schemas:8081", captured.LastMetadata?["schemaRegistryUrl"]);
    }

    [Fact]
    public async Task InvokeAsync_CallerMetadataOverridesBindingFields()
    {
        var captured = new CapturingProtocol("kafka");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new KafkaBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "kafka://broker:9092",
            ChannelAddress: "topic",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["key"] = "doc-default-key"
            });

        await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"a":1}"""],
            metadata: new Dictionary<string, string> { ["key"] = "caller-override-key" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("caller-override-key", captured.LastMetadata?["key"]);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsErrorWhenKafkaPluginNotLoaded()
    {
        // Empty registry — Kafka plugin is missing on the host.
        var registry = new BowireProtocolRegistry();
        var resolver = new KafkaBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "kafka://broker:9092",
            ChannelAddress: "topic",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("Kafka plugin", result.Metadata["error"]);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.Kafka", result.Metadata["error"]);
    }

    /// <summary>
    /// Records the last set of arguments an InvokeAsync call landed
    /// on — lets the tests assert the resolver's translation (channel
    /// address → topic, fixed "produce" method, merged metadata)
    /// without spinning up the Kafka plugin proper.
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
