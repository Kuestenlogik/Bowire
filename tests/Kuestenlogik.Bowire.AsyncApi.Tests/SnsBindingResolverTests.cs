// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Testing;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Phase C SNS resolver coverage. The SNS wire plugin itself is still
/// pending — the resolver lives now so the AsyncAPI loader recognises
/// the binding key at discovery time and the invoke path returns a
/// meaningful "plugin not loaded" error rather than the generic
/// "no resolver registered" fall-through.
///
/// Three things this file pins:
///   1. With no SNS plugin registered, the resolver returns a clear
///      error pointing at the future NuGet package (mirrors the
///      Kafka/AMQP/NATS "plugin not loaded" error paths).
///   2. With a stand-in plugin registered, binding fields land on
///      the metadata bag the plugin reads — every scalar passes
///      through verbatim (subject, messageAttributes, filterPolicy, …).
///   3. Caller-supplied metadata wins over doc-bindings so a UI
///      override (one-off subject / filter for a single publish)
///      isn't lost to the doc defaults.
/// </summary>
public sealed class SnsBindingResolverTests
{
    [Fact]
    public async Task InvokeAsync_ReturnsErrorWhenSnsPluginNotLoaded()
    {
        var registry = new BowireProtocolRegistry();
        var resolver = new SnsBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "sns://us-east-1",
            ChannelAddress: "arn:aws:sns:us-east-1:123:orders-topic",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("SNS plugin", result.Metadata["error"]);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.Sns", result.Metadata["error"]);
    }

    [Fact]
    public async Task InvokeAsync_DispatchesToSnsPlugin_WithMergedBindingFields()
    {
        var captured = new CapturingBowireProtocol("sns");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new SnsBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "sns://us-east-1",
            ChannelAddress: "orders-topic",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["subject"] = "OrderCreated",
                ["filterPolicy"] = """{"type":["order"]}""",
                ["bindingVersion"] = "0.1.0"
            });

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"orderId":"o-1"}"""],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("sns://us-east-1", captured.LastServerUrl);
        Assert.Equal("orders-topic", captured.LastService);
        Assert.Equal("publish", captured.LastMethod);
        Assert.Equal("OrderCreated", captured.LastMetadata?["subject"]);
        Assert.Equal("""{"type":["order"]}""", captured.LastMetadata?["filterPolicy"]);
        Assert.Equal("0.1.0", captured.LastMetadata?["bindingVersion"]);
    }

    [Fact]
    public async Task InvokeAsync_CallerMetadataOverridesBindingFields()
    {
        var captured = new CapturingBowireProtocol("sns");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new SnsBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "sns://us-east-1",
            ChannelAddress: "events-topic",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["subject"] = "doc-default-subject"
            });

        await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"a":1}"""],
            metadata: new Dictionary<string, string> { ["subject"] = "caller-override-subject" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("caller-override-subject", captured.LastMetadata?["subject"]);
    }

    [Fact]
    public void BindingId_IsSns() =>
        Assert.Equal("sns", new SnsBindingResolver(new BowireProtocolRegistry()).BindingId);

    [Fact]
    public void BuildMethod_NotYetImplemented_DocumentsThePhase()
    {
        // Same intentional deferral as Kafka / AMQP / NATS — guards
        // against someone wiring BuildMethod into the loader before
        // the per-binding method-metadata phase lands.
        var resolver = new SnsBindingResolver(new BowireProtocolRegistry());
        var ctx = new AsyncApiChannelContext(
            ServerUrl: "sns://r", ChannelAddress: "t", OperationAction: "send",
            BindingFields: new Dictionary<string, string>());
        Assert.Throws<NotImplementedException>(() => resolver.BuildMethod(ctx));
    }
}
