// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Unit coverage for <see cref="AsyncApiDocumentBuilder"/> — the
/// inverse of the loader. Builds a tiny synthetic discovery result
/// for each supported protocol and pins:
/// <list type="bullet">
///   <item>Top-level doc shape (asyncapi version, info, servers).</item>
///   <item>Channel + operation emission (one channel per method,
///     send/receive action picked correctly).</item>
///   <item>Bindings block carries the right binding key per protocol.</item>
///   <item>Synthesised message payloads round-trip from BowireFieldInfo
///     into JSON-Schema-ish property maps.</item>
///   <item>Pure helpers (ParseServer, ClassifyAction, ToSafeId,
///     MapFieldType) — exhaustive small-input coverage so the
///     mapping rules don't drift.</item>
/// </list>
/// </summary>
public sealed class AsyncApiDocumentBuilderTests
{
    // ---- pure helpers ---------------------------------------------

    [Theory]
    [InlineData("mqtt://broker.example.com:1883", "broker.example.com:1883", "mqtt", "mqtt")]
    [InlineData("mqtts://broker.example.com", "broker.example.com", "mqtts", "mqtts")]
    [InlineData("nats://nats.local:4222", "nats.local:4222", "nats", "nats")]
    [InlineData("kafka://kafka.dev:9092", "kafka.dev:9092", "kafka", "kafka")]
    [InlineData("ws://api.example.com/socket", "api.example.com", "ws", "ws")]
    [InlineData("wss://api.example.com/socket", "api.example.com", "wss", "wss")]
    [InlineData("http://api.example.com/", "api.example.com", "http", "http")]
    [InlineData("amqp://rabbit:5672", "rabbit:5672", "amqp", "amqp")]
    [InlineData("amqp1://artemis:5672", "artemis:5672", "amqp1", "amqp1")]
    public void ParseServer_extracts_host_and_protocol(string url, string host, string protocol, string scheme)
    {
        var (h, p, s) = AsyncApiDocumentBuilder.ParseServer(url);
        Assert.Equal(host, h);
        Assert.Equal(protocol, p);
        Assert.Equal(scheme, s);
    }

    [Fact]
    public void ParseServer_falls_back_for_invalid_url()
    {
        var (h, p, s) = AsyncApiDocumentBuilder.ParseServer("not a url");
        Assert.Equal("not a url", h);
        Assert.Equal("tcp", p);
        Assert.Equal("tcp", s);
    }

    [Theory]
    [InlineData("mqtt/sensors/temp/publish", "send")]
    [InlineData("nats/orders.created/subscribe", "receive")]
    [InlineData("kafka/orders/produce", "send")]
    [InlineData("ws/echo/send", "send")]
    public void ClassifyAction_uses_FullName_verb_when_present(string fullName, string expected)
    {
        var method = SimpleMethod("m", fullName, serverStreaming: false);
        Assert.Equal(expected, AsyncApiDocumentBuilder.ClassifyAction(method));
    }

    [Theory]
    [InlineData(true, "receive")]
    [InlineData(false, "send")]
    public void ClassifyAction_falls_back_to_streaming_shape(bool serverStreaming, string expected)
    {
        var method = SimpleMethod("m", "some/random/path/no-verb", serverStreaming);
        Assert.Equal(expected, AsyncApiDocumentBuilder.ClassifyAction(method));
    }

    [Theory]
    [InlineData("sensors/temperature", "sensors_temperature")]
    [InlineData("orders.created", "orders_created")]
    [InlineData("a/b/c", "a_b_c")]
    [InlineData("__weird---", "weird")]      // trim leading + trailing separators
    [InlineData("123topic", "_123topic")]     // can't start with a digit
    [InlineData("", "_")]                      // empty → safe placeholder
    [InlineData("clean", "clean")]
    public void ToSafeId_normalises_for_yaml_keys(string raw, string expected)
        => Assert.Equal(expected, AsyncApiDocumentBuilder.ToSafeId(raw));

    [Theory]
    [InlineData("string", "string")]
    [InlineData("STRING", "string")]
    [InlineData("int32", "integer")]
    [InlineData("uint64", "integer")]
    [InlineData("double", "number")]
    [InlineData("bool", "boolean")]
    [InlineData("bytes", "string")]   // base64 encoded
    [InlineData(null, "string")]
    [InlineData("UNKNOWN", "string")]
    public void MapFieldType_picks_json_schema_type(string? protoType, string expected)
    {
        var schema = AsyncApiDocumentBuilder.MapFieldType(protoType);
        Assert.Equal(expected, schema["type"]);
    }

    [Fact]
    public void MapFieldType_bytes_carries_base64_encoding()
    {
        var schema = AsyncApiDocumentBuilder.MapFieldType("bytes");
        Assert.Equal("base64", schema["contentEncoding"]);
    }

    // ---- end-to-end document shape --------------------------------

    [Fact]
    public void Build_mqtt_emits_channel_per_topic_and_send_receive_operations()
    {
        var svc = MakeMqttService(
            originUrl: "mqtt://broker:1883",
            topic: "sensors/temperature");

        var yaml = AsyncApiDocumentBuilder.Build(
            "mqtt://broker:1883", new[] { svc });

        Assert.Contains("asyncapi: 3.0.0", yaml);
        Assert.Contains("protocol: mqtt", yaml);
        Assert.Contains("host: broker:1883", yaml);
        Assert.Contains("address: sensors/temperature", yaml);
        // Both publish (send) and subscribe (receive) for the topic:
        Assert.Contains("action: send", yaml);
        Assert.Contains("action: receive", yaml);
        // MQTT binding emitted on the channel:
        Assert.Contains("mqtt:", yaml);
        Assert.Contains("topic: sensors/temperature", yaml);
        // Components / messages block emitted because we registered
        // a payload schema:
        Assert.Contains("components:", yaml);
        Assert.Contains("messages:", yaml);
    }

    [Fact]
    public void Build_nats_publish_becomes_send_with_nats_binding()
    {
        var svc = new BowireServiceInfo("orders", "nats", new List<BowireMethodInfo>
        {
            SimpleMethod(
                name: "orders.created",
                fullName: "nats/orders.created/publish",
                serverStreaming: false),
        });
        var yaml = AsyncApiDocumentBuilder.Build("nats://broker:4222", new[] { svc });

        Assert.Contains("protocol: nats", yaml);
        Assert.Contains("address: orders.created", yaml);
        Assert.Contains("action: send", yaml);
        Assert.Contains("nats:", yaml);
    }

    [Fact]
    public void Build_kafka_produce_becomes_send_with_topic_binding()
    {
        var svc = new BowireServiceInfo("orders", "kafka", new List<BowireMethodInfo>
        {
            SimpleMethod(
                name: "orders.events",
                fullName: "kafka/orders.events/produce",
                serverStreaming: false),
        });
        var yaml = AsyncApiDocumentBuilder.Build("kafka://broker:9092", new[] { svc });

        Assert.Contains("protocol: kafka", yaml);
        Assert.Contains("address: orders.events", yaml);
        Assert.Contains("action: send", yaml);
        Assert.Contains("kafka:", yaml);
        Assert.Contains("topic: orders.events", yaml);
    }

    [Fact]
    public void Build_websocket_emits_ws_binding()
    {
        var svc = new BowireServiceInfo("echo", "ws", new List<BowireMethodInfo>
        {
            SimpleMethod("echo", "ws/echo/send", serverStreaming: false),
        });
        var yaml = AsyncApiDocumentBuilder.Build("ws://api.example.com/socket", new[] { svc });

        Assert.Contains("protocol: ws", yaml);
        Assert.Contains("address: echo", yaml);
        Assert.Contains("ws:", yaml);
    }

    [Fact]
    public void Build_synthesises_payload_schema_from_field_list()
    {
        var input = new BowireMessageInfo("PublishRequest", "mqtt.sensors.PublishRequest",
        [
            new BowireFieldInfo("payload", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("qos", 2, "int32", "LABEL_REQUIRED", false, false, null, null),
            new BowireFieldInfo("retain", 3, "bool", "LABEL_REQUIRED", false, false, null, null),
        ]);
        var output = new BowireMessageInfo("PublishAck", "mqtt.sensors.PublishAck",
        [
            new BowireFieldInfo("status", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
        ]);
        var svc = new BowireServiceInfo("sensors", "mqtt", new List<BowireMethodInfo>
        {
            new("temp", "mqtt/sensors/temp/publish",
                ClientStreaming: false, ServerStreaming: false,
                InputType: input, OutputType: output, MethodType: "Unary"),
        });

        var yaml = AsyncApiDocumentBuilder.Build("mqtt://broker:1883", new[] { svc });

        // Send → input message expected in components.messages:
        Assert.Contains("payload:", yaml);
        Assert.Contains("type: object", yaml);
        Assert.Contains("type: integer", yaml);   // qos
        Assert.Contains("type: boolean", yaml);   // retain
        // qos + retain are non-optional → required
        Assert.Contains("required:", yaml);
    }

    [Fact]
    public void Build_json_format_emits_valid_json()
    {
        var svc = MakeMqttService("mqtt://b:1883", "sensors/temp");
        var json = AsyncApiDocumentBuilder.Build("mqtt://b:1883",
            new[] { svc },
            new AsyncApiExportOptions { Format = AsyncApiExportFormat.Json });

        // Quick contract: it parses and the top-level keys are there.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("3.0.0", root.GetProperty("asyncapi").GetString());
        Assert.True(root.TryGetProperty("channels", out _));
        Assert.True(root.TryGetProperty("operations", out _));
        Assert.True(root.TryGetProperty("servers", out _));
    }

    [Fact]
    public void Build_options_override_title_and_version()
    {
        var svc = MakeMqttService("mqtt://b:1883", "sensors/t");
        var yaml = AsyncApiDocumentBuilder.Build(
            "mqtt://b:1883",
            new[] { svc },
            new AsyncApiExportOptions { Title = "Sensor Bus", Version = "2.4.1" });
        Assert.Contains("title: Sensor Bus", yaml);
        Assert.Contains("version: 2.4.1", yaml);
    }

    [Fact]
    public void Build_deduplicates_methods_with_the_same_name()
    {
        // Two services contributing a method with the same topic name.
        // Channel id and operation id should be uniquified rather than
        // colliding silently.
        var svc1 = MakeMqttService("mqtt://b:1883", "topic");
        var svc2 = MakeMqttService("mqtt://b:1883", "topic");
        var yaml = AsyncApiDocumentBuilder.Build("mqtt://b:1883", new[] { svc1, svc2 });

        // The disambiguator suffix '_2' appears for the second copy.
        Assert.Contains("topic_2", yaml);
    }

    [Fact]
    public void Build_rejects_empty_serverUrl()
        => Assert.Throws<ArgumentException>(() =>
            AsyncApiDocumentBuilder.Build("", Array.Empty<BowireServiceInfo>()));

    [Fact]
    public void Build_rejects_null_services()
        => Assert.Throws<ArgumentNullException>(() =>
            AsyncApiDocumentBuilder.Build("mqtt://b:1883", null!));

    // ---- helpers --------------------------------------------------

    private static BowireMethodInfo SimpleMethod(string name, string fullName, bool serverStreaming)
    {
        var msg = new BowireMessageInfo("M", "ns.M",
            [new BowireFieldInfo("payload", 1, "string", "LABEL_OPTIONAL", false, false, null, null)]);
        return new BowireMethodInfo(
            Name: name,
            FullName: fullName,
            ClientStreaming: false,
            ServerStreaming: serverStreaming,
            InputType: msg,
            OutputType: msg,
            MethodType: serverStreaming ? "ServerStreaming" : "Unary");
    }

    private static BowireServiceInfo MakeMqttService(string originUrl, string topic)
    {
        var input = new BowireMessageInfo("PublishRequest", "mqtt.PublishRequest",
        [
            new BowireFieldInfo("payload", 1, "string", "LABEL_OPTIONAL", false, false, null, null)
        ]);
        var output = new BowireMessageInfo("Message", "mqtt.Message",
        [
            new BowireFieldInfo("payload", 1, "string", "LABEL_OPTIONAL", false, false, null, null)
        ]);
        var methods = new List<BowireMethodInfo>
        {
            new(Name: topic, FullName: $"mqtt/{topic}/publish",
                ClientStreaming: false, ServerStreaming: false,
                InputType: input, OutputType: output, MethodType: "Unary"),
            new(Name: topic, FullName: $"mqtt/{topic}/subscribe",
                ClientStreaming: false, ServerStreaming: true,
                InputType: input, OutputType: output, MethodType: "ServerStreaming"),
        };
        return new BowireServiceInfo("mqtt_root", "mqtt", methods) { OriginUrl = originUrl };
    }
}
