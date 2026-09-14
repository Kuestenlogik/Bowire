// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Testing;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// #357 — a <c>receive</c> operation used to reach a
/// <see cref="NotSupportedException"/> in the protocol and eight
/// <see cref="NotImplementedException"/> stubs in the resolvers. Neither
/// is reachable now: a binding whose wire plugin subscribes streams
/// through it, a binding that cannot yet yields one error frame that
/// says so, and the protocol answers an undiscovered URL the same way.
/// </summary>
public sealed class ReceiveOperationTests
{
    private static AsyncApiChannelContext Receive(string address) => new(
        ServerUrl: "x://broker",
        ChannelAddress: address,
        OperationAction: "receive",
        BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static async Task<List<string>> Drain(IAsyncEnumerable<string> frames)
    {
        var list = new List<string>();
        await foreach (var f in frames.WithCancellation(TestContext.Current.CancellationToken)) list.Add(f);
        return list;
    }

    [Fact]
    public async Task Mqtt_Receive_Subscribes_To_The_Channel_Topic()
    {
        var mqtt = new CapturingBowireProtocol("mqtt");
        mqtt.StreamFrames.AddRange(["""{"n":1}""", """{"n":2}"""]);
        var registry = new BowireProtocolRegistry();
        registry.Register(mqtt);

        var frames = await Drain(new MqttBindingResolver(registry).InvokeStreamAsync(
            Receive("sensors/temperature"), [], null, TestContext.Current.CancellationToken));

        Assert.Equal(2, frames.Count);
        Assert.Equal("sensors/temperature", mqtt.LastMethod);
        Assert.Equal("x://broker", mqtt.LastServerUrl);
    }

    [Fact]
    public async Task Nats_Receive_Subscribes_Through_The_Route_Form()
    {
        var nats = new CapturingBowireProtocol("nats");
        nats.StreamFrames.Add("""{"n":1}""");
        var registry = new BowireProtocolRegistry();
        registry.Register(nats);

        var frames = await Drain(new NatsBindingResolver(registry).InvokeStreamAsync(
            Receive("orders.created"), [], null, TestContext.Current.CancellationToken));

        Assert.Single(frames);
        Assert.Equal("nats/orders.created/subscribe", nats.LastMethod);
    }

    [Fact]
    public async Task A_Missing_Wire_Plugin_Is_One_Error_Frame_Not_An_Exception()
    {
        var frames = await Drain(new MqttBindingResolver(new BowireProtocolRegistry()).InvokeStreamAsync(
            Receive("t"), [], null, TestContext.Current.CancellationToken));

        var frame = Assert.Single(frames);
        Assert.Contains("\"error\"", frame, StringComparison.Ordinal);
        Assert.Contains("no MQTT plugin is loaded", frame, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("kafka")]
    [InlineData("amqp")]
    [InlineData("ws")]
    [InlineData("sns")]
    [InlineData("sqs")]
    public async Task A_Binding_Without_A_Subscribe_Shape_Says_So_In_The_Stream(string binding)
    {
        var registry = new BowireProtocolRegistry();
        IAsyncApiBindingResolver resolver = binding switch
        {
            "http" => new HttpBindingResolver(),
            "kafka" => new KafkaBindingResolver(registry),
            "amqp" => new AmqpBindingResolver(registry),
            "ws" => new WebSocketBindingResolver(registry),
            "sns" => new SnsBindingResolver(registry),
            _ => new SqsBindingResolver(registry),
        };

        var frames = await Drain(resolver.InvokeStreamAsync(Receive("c"), [], null, TestContext.Current.CancellationToken));

        var frame = Assert.Single(frames);
        Assert.Contains("\"error\"", frame, StringComparison.Ordinal);
        Assert.Contains($"{resolver.BindingId} binding is not supported yet", frame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Protocol_Answers_An_Undiscovered_Url_With_An_Error_Frame()
    {
        var protocol = new BowireAsyncApiProtocol();
        protocol.Initialize(serviceProvider: null);

        var frames = await Drain(protocol.InvokeStreamAsync(
            "http://nowhere.invalid/asyncapi.yaml", "channel", "receiveThing", [],
            showInternalServices: false, metadata: null, ct: TestContext.Current.CancellationToken));

        var frame = Assert.Single(frames);
        Assert.Contains("has not been discovered yet", frame, StringComparison.Ordinal);
    }
}
