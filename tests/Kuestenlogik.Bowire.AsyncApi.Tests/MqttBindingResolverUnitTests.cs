// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// In-process unit coverage for <see cref="MqttBindingResolver"/> that
/// doesn't need an embedded broker (the integration test next door
/// already covers the happy publish path). These pin the branches the
/// integration test can't easily exercise — the no-MQTT-plugin error
/// path, the QoS-string translation table, the doc-field vs
/// caller-metadata merge precedence, and the BuildMethod-is-Phase-A4
/// guard.
/// </summary>
public sealed class MqttBindingResolverUnitTests
{
    private static AsyncApiChannelContext MakeChannel(
        IReadOnlyDictionary<string, string>? bindingFields = null)
    {
        return new AsyncApiChannelContext(
            ServerUrl: "mqtt://localhost:1883",
            ChannelAddress: "sensors/lux",
            OperationAction: "send",
            BindingFields: bindingFields ?? new Dictionary<string, string>());
    }

    [Fact]
    public void Ctor_DefaultBindingId_IsMqtt()
    {
        var resolver = new MqttBindingResolver(new BowireProtocolRegistry());
        Assert.Equal("mqtt", resolver.BindingId);
    }

    [Fact]
    public void Ctor_AcceptsMqtt5BindingId()
    {
        // BowireAsyncApiProtocol.Initialize registers the resolver twice
        // — once with "mqtt", once with "mqtt5". Same type, different
        // bag of binding fields the AsyncAPI extractor pulls.
        var resolver = new MqttBindingResolver(new BowireProtocolRegistry(), bindingId: "mqtt5");
        Assert.Equal("mqtt5", resolver.BindingId);
    }

    [Fact]
    public void BuildMethod_ThrowsNotImplemented_PointingAtPhaseA4()
    {
        var resolver = new MqttBindingResolver(new BowireProtocolRegistry());
        var ex = Assert.Throws<NotImplementedException>(() => resolver.BuildMethod(MakeChannel()));
        Assert.Contains("Phase A4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_NoMqttPluginRegistered_ReturnsErrorWithInstallHint()
    {
        // Empty registry — the resolver's defensive branch fires and we
        // get an error InvokeResult instead of a NullReferenceException.
        // The error string is the user-facing install hint, asserted
        // explicitly because copy-edits here regress the install flow.
        var resolver = new MqttBindingResolver(new BowireProtocolRegistry());

        var result = await resolver.InvokeAsync(
            MakeChannel(),
            jsonMessages: ["{\"v\":1}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Null(result.Response);
        Assert.Equal(0, result.DurationMs);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata!.ContainsKey("error"));
        Assert.Contains("no MQTT plugin is loaded", result.Metadata["error"], StringComparison.Ordinal);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.Mqtt", result.Metadata["error"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0", "AtMostOnce")]
    [InlineData("1", "AtLeastOnce")]
    [InlineData("2", "ExactlyOnce")]
    [InlineData("AtLeastOnce", "AtLeastOnce")] // already-textual value passes through
    [InlineData("nonsense", "nonsense")]       // anything else passes through verbatim
    public async Task InvokeAsync_QosBindingField_MapsNumericToTextualName(string docQos, string expectedQos)
    {
        // We don't need a real plugin to test the merge map — but we
        // do need to observe what the resolver tries to forward. The
        // no-plugin error path skips Merge*, so we use a recording
        // protocol that records the call.
        var registry = new BowireProtocolRegistry();
        var recorder = new RecordingMqttProtocol();
        registry.Register(recorder);

        var resolver = new MqttBindingResolver(registry);
        var channel = MakeChannel(new Dictionary<string, string>
        {
            ["qos"] = docQos,
            ["retain"] = "true",
        });

        await resolver.InvokeAsync(
            channel, jsonMessages: ["{}"], metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(recorder.LastMetadata);
        Assert.Equal(expectedQos, recorder.LastMetadata!["qos"]);
        Assert.Equal("true", recorder.LastMetadata["retain"]);
    }

    [Fact]
    public async Task InvokeAsync_CallerMetadataWinsOverDocBinding()
    {
        // Doc says qos:2 / retain:true; caller insists on qos:0 and
        // ditches retain. Resolver must honour the caller — the doc
        // is the default, not the rule.
        var registry = new BowireProtocolRegistry();
        var recorder = new RecordingMqttProtocol();
        registry.Register(recorder);

        var resolver = new MqttBindingResolver(registry);
        var channel = MakeChannel(new Dictionary<string, string>
        {
            ["qos"] = "2",
            ["retain"] = "true",
        });

        await resolver.InvokeAsync(
            channel,
            jsonMessages: ["{}"],
            metadata: new Dictionary<string, string>
            {
                ["qos"] = "AtMostOnce",
                ["retain"] = "false",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("AtMostOnce", recorder.LastMetadata!["qos"]);
        Assert.Equal("false", recorder.LastMetadata["retain"]);
    }

    [Fact]
    public async Task InvokeAsync_ForwardsChannelAddressAsServiceAndMethod()
    {
        // The MQTT plugin uses the channel address as both the sidebar
        // grouping (service) AND the actual topic (method). Pin that
        // mapping so a future "split service/method" refactor flags
        // the impact here.
        var registry = new BowireProtocolRegistry();
        var recorder = new RecordingMqttProtocol();
        registry.Register(recorder);

        var resolver = new MqttBindingResolver(registry);
        var channel = MakeChannel();

        await resolver.InvokeAsync(
            channel, jsonMessages: ["{}"], metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("mqtt://localhost:1883", recorder.LastServerUrl);
        Assert.Equal("sensors/lux", recorder.LastService);
        Assert.Equal("sensors/lux", recorder.LastMethod);
    }

    /// <summary>
    /// Minimal stand-in for the MQTT protocol — captures whatever the
    /// resolver tried to forward so the unit tests can inspect the
    /// merge result without standing up a broker. Only the surface
    /// the resolver touches is implemented; everything else throws so
    /// accidental reuse from a wider test is loud.
    /// </summary>
    private sealed class RecordingMqttProtocol : IBowireProtocol
    {
        public string Name => "MQTT (recording test double)";
        public string Id => "mqtt";
        public string IconSvg => "";

        public string? LastServerUrl { get; private set; }
        public string? LastService { get; private set; }
        public string? LastMethod { get; private set; }
        public Dictionary<string, string>? LastMetadata { get; private set; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new NotSupportedException("Discover not used by the resolver path.");

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            LastServerUrl = serverUrl;
            LastService = service;
            LastMethod = method;
            LastMetadata = metadata;
            return Task.FromResult(new InvokeResult(
                Response: "{}",
                DurationMs: 1,
                Status: "OK",
                Metadata: new Dictionary<string, string>()));
        }

        public IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new NotSupportedException("Stream not used by the resolver path.");

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("Open channel not used by the resolver path.");
    }
}
