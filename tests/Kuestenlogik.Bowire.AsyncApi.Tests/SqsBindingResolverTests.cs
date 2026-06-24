// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Testing;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Phase C SQS resolver coverage. The SQS wire plugin itself is still
/// pending — the resolver lives now so the AsyncAPI loader recognises
/// the binding key at discovery time and the invoke path returns a
/// meaningful "plugin not loaded" error rather than the generic
/// "no resolver registered" fall-through.
///
/// Three things this file pins:
///   1. With no SQS plugin registered, the resolver returns a clear
///      error pointing at the future NuGet package (mirrors the
///      Kafka/AMQP/NATS/SNS "plugin not loaded" error paths).
///   2. With a stand-in plugin registered, binding fields land on
///      the metadata bag the plugin reads — every scalar passes
///      through verbatim (messageGroupId, messageDeduplicationId,
///      deadLetterQueue, …).
///   3. Caller-supplied metadata wins over doc-bindings.
/// </summary>
public sealed class SqsBindingResolverTests
{
    [Fact]
    public async Task InvokeAsync_ReturnsErrorWhenSqsPluginNotLoaded()
    {
        var registry = new BowireProtocolRegistry();
        var resolver = new SqsBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "sqs://us-east-1",
            ChannelAddress: "arn:aws:sqs:us-east-1:123:orders.fifo",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Contains("SQS plugin", result.Metadata["error"]);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.Sqs", result.Metadata["error"]);
    }

    [Fact]
    public async Task InvokeAsync_DispatchesToSqsPlugin_WithMergedBindingFields()
    {
        var captured = new CapturingBowireProtocol("sqs");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new SqsBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "sqs://us-east-1",
            ChannelAddress: "orders.fifo",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["messageGroupId"] = "orders",
                ["messageDeduplicationId"] = "o-1-2026",
                ["deadLetterQueue"] = "arn:aws:sqs:us-east-1:123:orders-dlq",
                ["bindingVersion"] = "0.2.0"
            });

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"orderId":"o-1"}"""],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("sqs://us-east-1", captured.LastServerUrl);
        Assert.Equal("orders.fifo", captured.LastService);
        Assert.Equal("send", captured.LastMethod);
        Assert.Equal("orders", captured.LastMetadata?["messageGroupId"]);
        Assert.Equal("o-1-2026", captured.LastMetadata?["messageDeduplicationId"]);
        Assert.Equal("arn:aws:sqs:us-east-1:123:orders-dlq", captured.LastMetadata?["deadLetterQueue"]);
        Assert.Equal("0.2.0", captured.LastMetadata?["bindingVersion"]);
    }

    [Fact]
    public async Task InvokeAsync_CallerMetadataOverridesBindingFields()
    {
        var captured = new CapturingBowireProtocol("sqs");
        var registry = new BowireProtocolRegistry();
        registry.Register(captured);

        var resolver = new SqsBindingResolver(registry);
        var context = new AsyncApiChannelContext(
            ServerUrl: "sqs://us-east-1",
            ChannelAddress: "events",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["messageGroupId"] = "doc-default-group"
            });

        await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"a":1}"""],
            metadata: new Dictionary<string, string> { ["messageGroupId"] = "caller-override-group" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("caller-override-group", captured.LastMetadata?["messageGroupId"]);
    }

    [Fact]
    public void BindingId_IsSqs() =>
        Assert.Equal("sqs", new SqsBindingResolver(new BowireProtocolRegistry()).BindingId);

    [Fact]
    public void BuildMethod_NotYetImplemented_DocumentsThePhase()
    {
        var resolver = new SqsBindingResolver(new BowireProtocolRegistry());
        var ctx = new AsyncApiChannelContext(
            ServerUrl: "sqs://r", ChannelAddress: "q", OperationAction: "send",
            BindingFields: new Dictionary<string, string>());
        Assert.Throws<NotImplementedException>(() => resolver.BuildMethod(ctx));
    }
}
