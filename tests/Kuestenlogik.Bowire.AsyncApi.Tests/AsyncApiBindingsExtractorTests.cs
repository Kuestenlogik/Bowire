// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Tests for the YAML-side bindings-detail extractor that side-steps
/// the Neuroglia SDK reader's `bindings.mqtt.qos` crash. Reflection-
/// backed because the extractor lives as `internal` next to the
/// loader; flipping it public for testing would leak surface area
/// nobody else should consume.
/// </summary>
public sealed class AsyncApiBindingsExtractorTests
{
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
        Extract(string yaml)
    {
        var assembly = typeof(BowireAsyncApiProtocol).Assembly;
        var type = assembly.GetType("Kuestenlogik.Bowire.AsyncApi.AsyncApiBindingsExtractor")
            ?? throw new InvalidOperationException("Extractor type not found.");
        var method = type.GetMethod(
            "ExtractV3OperationBindings", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Extractor method not found.");
        return (IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>)
            method.Invoke(null, new object[] { yaml })!;
    }

    [Fact]
    public void Returns_empty_for_blank_input()
    {
        Assert.Empty(Extract(string.Empty));
    }

    [Fact]
    public void Returns_empty_when_document_has_no_operations()
    {
        var yaml = """
            asyncapi: '3.0.0'
            info:
              title: No-ops
              version: '1.0.0'
            channels:
              empty:
                address: 'empty'
            """;
        Assert.Empty(Extract(yaml));
    }

    [Fact]
    public void Returns_empty_when_operations_have_no_bindings()
    {
        var yaml = """
            asyncapi: '3.0.0'
            info:
              title: Bindings-free
              version: '1.0.0'
            operations:
              sendThing:
                action: send
                channel:
                  $ref: '#/channels/x'
            """;
        Assert.Empty(Extract(yaml));
    }

    [Fact]
    public void Extracts_mqtt_qos_and_retain_per_operation()
    {
        var yaml = """
            asyncapi: '3.0.0'
            info:
              title: With bindings
              version: '1.0.0'
            operations:
              receiveLightMeasurement:
                action: receive
                channel:
                  $ref: '#/channels/light'
                bindings:
                  mqtt:
                    qos: 2
                    retain: false
              sendTurnOnOff:
                action: send
                channel:
                  $ref: '#/channels/action'
                bindings:
                  mqtt:
                    qos: 1
                    retain: true
            """;
        var bindings = Extract(yaml);

        Assert.Equal(2, bindings.Count);

        var receive = bindings["receiveLightMeasurement"];
        var receiveMqtt = receive["mqtt"];
        Assert.Equal("2", receiveMqtt["qos"]);
        Assert.Equal("false", receiveMqtt["retain"]);

        var send = bindings["sendTurnOnOff"];
        var sendMqtt = send["mqtt"];
        Assert.Equal("1", sendMqtt["qos"]);
        Assert.Equal("true", sendMqtt["retain"]);
    }

    [Fact]
    public void Tolerates_malformed_yaml_by_returning_empty()
    {
        // Mismatched indentation — YamlDotNet throws YamlException,
        // the extractor swallows it (discovery already surfaces the
        // parse failure elsewhere) and returns the empty map.
        var yaml = "asyncapi: '3.0.0'\noperations:\n  bad:\n   action: send\n     bindings: {";
        Assert.Empty(Extract(yaml));
    }

    [Fact]
    public void Extracts_v3_operation_messages_via_reflection()
    {
        // Verifies the V3-message walker that drives multi-message
        // overload emission. Single-entry and multi-entry both come
        // out as lists keyed by opKey.
        var yaml = """
            asyncapi: '3.0.0'
            info:
              title: T
              version: '1.0.0'
            operations:
              singleMsg:
                action: send
                channel:
                  $ref: '#/channels/x'
                messages:
                  - $ref: '#/components/messages/foo'
              multiMsg:
                action: receive
                channel:
                  $ref: '#/channels/x'
                messages:
                  - $ref: '#/components/messages/a'
                  - $ref: '#/components/messages/b'
            """;

        var assembly = typeof(BowireAsyncApiProtocol).Assembly;
        var type = assembly.GetType("Kuestenlogik.Bowire.AsyncApi.AsyncApiBindingsExtractor")!;
        var method = type.GetMethod("ExtractV3OperationMessages", BindingFlags.Public | BindingFlags.Static)!;
        var result = (IReadOnlyDictionary<string, IReadOnlyList<string>>)
            method.Invoke(null, new object[] { yaml })!;

        Assert.Equal(2, result.Count);
        Assert.Equal("foo", Assert.Single(result["singleMsg"]));
        Assert.Collection(result["multiMsg"],
            n => Assert.Equal("a", n),
            n => Assert.Equal("b", n));
    }

    [Fact]
    public void Extracts_v2_channel_bindings_per_publish_subscribe_slot()
    {
        var yaml = """
            asyncapi: '2.6.0'
            info:
              title: T
              version: '1.0.0'
            channels:
              t1:
                subscribe:
                  bindings:
                    mqtt:
                      qos: 1
                      retain: false
              t2:
                publish:
                  bindings:
                    mqtt:
                      qos: 2
            """;

        var assembly = typeof(BowireAsyncApiProtocol).Assembly;
        var type = assembly.GetType("Kuestenlogik.Bowire.AsyncApi.AsyncApiBindingsExtractor")!;
        var m = type.GetMethod("ExtractV2ChannelBindings", BindingFlags.Public | BindingFlags.Static)!;
        var result = (IReadOnlyDictionary<string,
            IReadOnlyDictionary<string,
                IReadOnlyDictionary<string,
                    IReadOnlyDictionary<string, string>>>>)
            m.Invoke(null, new object[] { yaml })!;

        Assert.Equal("1", result["t1"]["subscribe"]["mqtt"]["qos"]);
        Assert.Equal("false", result["t1"]["subscribe"]["mqtt"]["retain"]);
        Assert.Equal("2", result["t2"]["publish"]["mqtt"]["qos"]);
    }

    [Fact]
    public void Extracts_v2_operation_messages_oneOf_per_slot()
    {
        // V2 mirror of ExtractV3OperationMessages: only the `oneOf[]`
        // shape produces entries (single-message `message: { $ref }`
        // stays on the typed-model path and yields no entry here).
        var yaml = """
            asyncapi: '2.6.0'
            info:
              title: T
              version: '1.0.0'
            channels:
              t1:
                subscribe:
                  operationId: rcv
                  message:
                    oneOf:
                      - $ref: '#/components/messages/a'
                      - $ref: '#/components/messages/b'
              t2:
                publish:
                  operationId: snd
                  message:
                    $ref: '#/components/messages/c'
            """;

        var assembly = typeof(BowireAsyncApiProtocol).Assembly;
        var type = assembly.GetType("Kuestenlogik.Bowire.AsyncApi.AsyncApiBindingsExtractor")!;
        var method = type.GetMethod("ExtractV2OperationMessages", BindingFlags.Public | BindingFlags.Static)!;
        var result = (IReadOnlyDictionary<string,
            IReadOnlyDictionary<string, IReadOnlyList<string>>>)
            method.Invoke(null, new object[] { yaml })!;

        // Only t1 has a oneOf — t2 has a single-message slot which
        // the walker intentionally skips.
        Assert.Single(result);
        Assert.Collection(result["t1"]["subscribe"],
            n => Assert.Equal("a", n),
            n => Assert.Equal("b", n));
        Assert.False(result.ContainsKey("t2"));
    }

    [Fact]
    public void Skips_non_scalar_binding_fields()
    {
        // A nested mapping (e.g. mqtt server-binding `lastWill`) is
        // legal under the spec but isn't a flat scalar — we drop it
        // silently. Scalar siblings on the same level still come
        // through.
        var yaml = """
            asyncapi: '3.0.0'
            info:
              title: Nested
              version: '1.0.0'
            operations:
              op:
                action: send
                channel:
                  $ref: '#/channels/x'
                bindings:
                  mqtt:
                    qos: 1
                    lastWill:
                      topic: dead-letter
                      qos: 2
            """;
        var bindings = Extract(yaml);
        var mqtt = bindings["op"]["mqtt"];
        Assert.Equal("1", mqtt["qos"]);
        Assert.False(mqtt.ContainsKey("lastWill"));
    }
}
