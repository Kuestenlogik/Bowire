// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using YamlDotNet.RepresentationModel;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// The stripper hides binding objects from the SDK reader. It must
/// remove them from every AsyncAPI object that carries bindings and
/// from nothing else — a schema property, an example value or a
/// channel that happens to be called <c>bindings</c> is the author's
/// data.
/// </summary>
public sealed class AsyncApiBindingsStripperTests
{
    [Fact]
    public void Strips_bindings_from_every_v3_object_that_carries_them()
    {
        const string yaml = """
            asyncapi: '3.0.0'
            servers:
              broker:
                host: localhost
                protocol: mqtt
                bindings:
                  mqtt: { clientId: a }
            channels:
              lights:
                address: lights
                bindings:
                  mqtt: { bindingVersion: '0.2.0' }
                messages:
                  on:
                    bindings:
                      mqtt: { contentType: x }
            operations:
              turnOn:
                action: send
                bindings:
                  mqtt: { qos: 2 }
                traits:
                  - bindings:
                      mqtt: { retain: true }
            components:
              messages:
                off:
                  bindings:
                    mqtt: { contentType: y }
                  traits:
                    - bindings:
                        mqtt: { contentType: z }
              operationTraits:
                common:
                  bindings:
                    mqtt: { qos: 1 }
              messageTraits:
                common:
                  bindings:
                    mqtt: { contentType: w }
            """;

        var stripped = AsyncApiBindingsStripper.Strip(yaml);

        Assert.DoesNotContain("bindings", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Strips_bindings_from_v2_operations_and_their_messages()
    {
        const string yaml = """
            asyncapi: '2.6.0'
            channels:
              lights:
                bindings:
                  mqtt: { bindingVersion: '0.2.0' }
                publish:
                  bindings:
                    mqtt: { qos: 2 }
                  message:
                    bindings:
                      mqtt: { contentType: x }
                subscribe:
                  message:
                    oneOf:
                      - bindings:
                          mqtt: { contentType: y }
            """;

        var stripped = AsyncApiBindingsStripper.Strip(yaml);

        Assert.DoesNotContain("bindings", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_a_schema_property_named_bindings()
    {
        const string yaml = """
            asyncapi: '3.0.0'
            channels:
              devices:
                address: devices
                bindings:
                  mqtt: { bindingVersion: '0.2.0' }
                messages:
                  device:
                    payload:
                      type: object
                      properties:
                        bindings:
                          type: array
                    examples:
                      - payload:
                          bindings: [a, b]
            components:
              schemas:
                Device:
                  type: object
                  properties:
                    bindings:
                      type: string
            """;

        var root = Parse(AsyncApiBindingsStripper.Strip(yaml));

        var channel = Map(Map(root, "channels"), "devices");
        Assert.False(channel.Children.ContainsKey(new YamlScalarNode("bindings")));
        var message = Map(Map(channel, "messages"), "device");
        Assert.True(Map(Map(message, "payload"), "properties").Children.ContainsKey(new YamlScalarNode("bindings")));
        var example = (YamlMappingNode)((YamlSequenceNode)message["examples"]).Children[0];
        Assert.True(Map(example, "payload").Children.ContainsKey(new YamlScalarNode("bindings")));
        var schema = Map(Map(Map(root, "components"), "schemas"), "Device");
        Assert.True(Map(schema, "properties").Children.ContainsKey(new YamlScalarNode("bindings")));
    }

    [Fact]
    public void Keeps_a_channel_and_an_operation_named_bindings()
    {
        const string yaml = """
            asyncapi: '3.0.0'
            channels:
              bindings:
                address: bindings
            operations:
              bindings:
                action: send
            """;

        var root = Parse(AsyncApiBindingsStripper.Strip(yaml));

        Assert.True(Map(root, "channels").Children.ContainsKey(new YamlScalarNode("bindings")));
        Assert.True(Map(root, "operations").Children.ContainsKey(new YamlScalarNode("bindings")));
    }

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Map(YamlMappingNode parent, string key)
        => (YamlMappingNode)parent[key];
}
