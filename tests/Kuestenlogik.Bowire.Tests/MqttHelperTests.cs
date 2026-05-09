// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Mqtt;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the synchronous helpers that the existing MQTT
/// protocol tests don't reach: broker-URL parsing, payload-shape
/// detection, outgoing-frame parsing, and the topic-tree builder.
/// All helpers live in <c>internal</c> classes; reflection bridges the
/// access boundary so the test project doesn't need an
/// <c>InternalsVisibleTo</c> just for coverage scaffolding.
/// </summary>
public sealed class MqttHelperTests
{
    private static readonly Assembly s_pluginAsm = typeof(BowireMqttProtocol).Assembly;

    // ---- MqttConnectionHelper.ParseBrokerUrl ----

    [Theory]
    [InlineData("mqtt://broker:1883", "broker", 1883)]
    [InlineData("mqtts://secure.broker:8883", "secure.broker", 8883)]
    [InlineData("tcp://broker:1883", "broker", 1883)]
    [InlineData("ssl://broker:8883", "broker", 8883)]
    [InlineData("MQTT://Mixed.Case:1234", "Mixed.Case", 1234)]
    public void ParseBrokerUrl_Strips_Mqtt_Schemes(string url, string expectedHost, int expectedPort)
    {
        var result = InvokeParseBrokerUrl(url);

        Assert.NotNull(result);
        Assert.Equal(expectedHost, result!.Value.host);
        Assert.Equal(expectedPort, result.Value.port);
    }

    [Theory]
    [InlineData("http://broker:1883", "broker", 1883)]
    [InlineData("https://broker:8883", "broker", 8883)]
    public void ParseBrokerUrl_Strips_Http_Schemes_For_Convenience(string url, string expectedHost, int expectedPort)
    {
        var result = InvokeParseBrokerUrl(url);

        Assert.NotNull(result);
        Assert.Equal(expectedHost, result!.Value.host);
        Assert.Equal(expectedPort, result.Value.port);
    }

    [Fact]
    public void ParseBrokerUrl_Bare_Host_Port_Pair_Works()
    {
        var result = InvokeParseBrokerUrl("broker.example:8884");

        Assert.NotNull(result);
        Assert.Equal("broker.example", result!.Value.host);
        Assert.Equal(8884, result.Value.port);
    }

    [Fact]
    public void ParseBrokerUrl_Host_Without_Port_Defaults_To_1883()
    {
        var result = InvokeParseBrokerUrl("broker.example");

        Assert.NotNull(result);
        Assert.Equal("broker.example", result!.Value.host);
        Assert.Equal(1883, result.Value.port);
    }

    [Fact]
    public void ParseBrokerUrl_Strips_Trailing_Path_Segments()
    {
        // The user might paste a WebSocket-style URL like `host:1883/mqtt`
        // — the helper drops the path so the TCP connect lands on the
        // bare host:port pair.
        var result = InvokeParseBrokerUrl("broker:1883/mqtt");

        Assert.NotNull(result);
        Assert.Equal("broker", result!.Value.host);
        Assert.Equal(1883, result.Value.port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseBrokerUrl_Empty_Or_Whitespace_Returns_Null(string url)
    {
        Assert.Null(InvokeParseBrokerUrl(url));
    }

    [Fact]
    public void ParseBrokerUrl_Non_Numeric_Port_Falls_Back_To_Default()
    {
        // The colon parses but `not-a-number` isn't an int — the helper
        // gives up on the port-split and treats the whole thing as a
        // hostname with the default MQTT port.
        var result = InvokeParseBrokerUrl("broker:not-a-number");

        Assert.NotNull(result);
        Assert.Equal("broker:not-a-number", result!.Value.host);
        Assert.Equal(1883, result.Value.port);
    }

    // ---- MqttPayloadHelper.PayloadToDisplayString ----

    [Fact]
    public void Payload_Empty_Returns_Empty_String()
    {
        Assert.Equal("", InvokePayloadToDisplayString(Array.Empty<byte>()));
    }

    [Fact]
    public void Payload_Valid_Json_Returns_Pretty_Printed()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("{\"a\":1,\"b\":\"x\"}");

        var display = InvokePayloadToDisplayString(payload);

        // The pretty-print path uses indent and newlines.
        Assert.Contains("\"a\"", display, StringComparison.Ordinal);
        Assert.Contains("\n", display, StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_Plain_Text_Returns_Verbatim()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("hello world");

        var display = InvokePayloadToDisplayString(payload);

        Assert.Equal("hello world", display);
    }

    [Fact]
    public void Payload_Whitespace_Control_Chars_Are_Allowed_As_Text()
    {
        // Tab, newline, carriage return don't trigger the hex fallback.
        var payload = System.Text.Encoding.UTF8.GetBytes("line1\r\nline2\tindented");

        var display = InvokePayloadToDisplayString(payload);

        Assert.Equal("line1\r\nline2\tindented", display);
    }

    [Fact]
    public void Payload_Binary_Data_Falls_Back_To_Hex_Dump()
    {
        // Bytes 0x00..0x05 are non-whitespace control chars — text path
        // rejects them, JSON path fails on the parse, hex dump runs.
        var payload = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

        var display = InvokePayloadToDisplayString(payload);

        Assert.Contains("[binary: 6 bytes]", display, StringComparison.Ordinal);
        Assert.Contains("00 01 02 03 04 05", display, StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_Large_Binary_Truncates_With_More_Bytes_Marker()
    {
        // Hex dump caps at 256 bytes — 300 bytes hits the truncation path.
        var payload = new byte[300];
        for (var i = 0; i < payload.Length; i++) payload[i] = 0x01;

        var display = InvokePayloadToDisplayString(payload);

        Assert.Contains("[binary: 300 bytes]", display, StringComparison.Ordinal);
        Assert.Contains("(44 more bytes)", display, StringComparison.Ordinal);
    }

    // ---- MqttDiscovery.BuildServices (public on internal class) ----

    [Fact]
    public void BuildServices_Empty_Topic_Set_Returns_Empty_List()
    {
        var services = InvokeBuildServices([], "mqtt://broker:1883");

        Assert.Empty(services);
    }

    [Fact]
    public void BuildServices_Single_Segment_Topic_Lands_In_Root_Service()
    {
        var services = InvokeBuildServices(["heartbeat"], "mqtt://broker:1883");

        var svc = Assert.Single(services);
        Assert.Equal("(root)", svc.Name);
        // Each topic surfaces as both a Subscribe (server-streaming) and
        // a Publish (unary) method, so one topic → two methods.
        Assert.Equal(2, svc.Methods.Count);
        Assert.Contains(svc.Methods, m => m.MethodType == "ServerStreaming");
        Assert.Contains(svc.Methods, m => m.MethodType == "Unary");
    }

    [Fact]
    public void BuildServices_Multi_Segment_Topics_Group_By_First_Segment()
    {
        var services = InvokeBuildServices(
            [
                "sensors/temperature/room-a",
                "sensors/humidity/room-a",
                "lights/kitchen/state",
                "lights/kitchen/brightness",
                "alarms/critical"
            ],
            "mqtt://broker:1883");

        Assert.Equal(3, services.Count);
        var sensors = services.Single(s => s.Name == "sensors");
        var lights = services.Single(s => s.Name == "lights");
        var alarms = services.Single(s => s.Name == "alarms");

        // 2 topics × 2 methods each.
        Assert.Equal(4, sensors.Methods.Count);
        Assert.Equal(4, lights.Methods.Count);
        Assert.Equal(2, alarms.Methods.Count);
    }

    [Fact]
    public void BuildServices_Service_Description_Reports_Topic_Count()
    {
        var services = InvokeBuildServices(
            ["sensors/a", "sensors/b", "sensors/c"], "mqtt://broker:1883");

        var svc = Assert.Single(services);
        Assert.Contains("3 topics", svc.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildServices_Single_Topic_Description_Singular()
    {
        var services = InvokeBuildServices(["sensors/only-one"], "mqtt://broker:1883");

        var svc = Assert.Single(services);
        Assert.Contains("1 topic)", svc.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("topics)", svc.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildServices_Origin_Url_Threaded_To_Each_Service()
    {
        var services = InvokeBuildServices(
            ["sensors/temperature"], "mqtt://broker.example:1883");

        Assert.Equal("mqtt://broker.example:1883", services[0].OriginUrl);
        Assert.Equal("mqtt", services[0].Source);
    }

    // ---- MqttDiscovery — private message builders ----

    [Fact]
    public void BuildPublishInput_Has_Payload_Topic_Qos_Retain_Fields()
    {
        var msg = InvokeMessageBuilder("BuildPublishInput", "sensors/temperature");

        Assert.Equal("MqttPublishRequest", msg.Name);
        var fieldNames = msg.Fields.Select(f => f.Name).ToHashSet();
        Assert.Contains("payload", fieldNames);
    }

    [Fact]
    public void BuildSubscribeInput_Names_The_Topic_In_FullName()
    {
        var msg = InvokeMessageBuilder("BuildSubscribeInput", "lights/kitchen");

        Assert.Equal("MqttSubscribeRequest", msg.Name);
        Assert.Contains("lights/kitchen", msg.FullName, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPublishOutput_Is_Empty_Ack()
    {
        var msg = InvokeStaticBuilder<BowireMessageInfo>("BuildPublishOutput");

        Assert.Equal("MqttPublishResponse", msg.Name);
    }

    [Fact]
    public void BuildMessageOutput_Has_Topic_Payload_Qos_Retain_Bytes()
    {
        var msg = InvokeStaticBuilder<BowireMessageInfo>("BuildMessageOutput");

        Assert.Equal("MqttMessage", msg.Name);
        var fieldNames = msg.Fields.Select(f => f.Name).ToHashSet();
        Assert.Contains("topic", fieldNames);
        Assert.Contains("payload", fieldNames);
    }

    // ---- MqttBowireChannel.ParseOutgoingFrame (private static) ----

    [Fact]
    public void ParseOutgoingFrame_Whitespace_Returns_Empty_Text()
    {
        var (isBinary, text, bytes) = InvokeParseOutgoingFrame("   ");

        Assert.False(isBinary);
        Assert.Equal("", text);
        Assert.Null(bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_Type_Text_Returns_Unwrapped_Text()
    {
        var (isBinary, text, bytes) = InvokeParseOutgoingFrame(
            "{\"type\":\"text\",\"text\":\"hello\"}");

        Assert.False(isBinary);
        Assert.Equal("hello", text);
        Assert.Null(bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_Type_Binary_Decodes_Base64()
    {
        // base64 of "ABC" is "QUJD".
        var (isBinary, text, bytes) = InvokeParseOutgoingFrame(
            "{\"type\":\"binary\",\"base64\":\"QUJD\"}");

        Assert.True(isBinary);
        Assert.Null(text);
        Assert.NotNull(bytes);
        Assert.Equal([0x41, 0x42, 0x43], bytes!);
    }

    [Fact]
    public void ParseOutgoingFrame_Data_Shorthand_Requires_Type_Field()
    {
        // The `data` shorthand sits inside the `type`-gated block, so an
        // unrecognised `type` plus a `data` string still routes through the
        // shorthand. Without the `type` field the helper drops out of the
        // block entirely and treats the input as a raw payload.
        var (isBinary, text, bytes) = InvokeParseOutgoingFrame(
            "{\"type\":\"unknown\",\"data\":\"plain message\"}");

        Assert.False(isBinary);
        Assert.Equal("plain message", text);
        Assert.Null(bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_Object_Without_Type_Falls_Back_To_Raw()
    {
        // `{ "data": "..." }` with no `type` — the helper doesn't enter the
        // type-gated block and returns the original JSON as raw text so the
        // publish path still surfaces the user's input untouched.
        var raw = "{\"data\":\"plain message\"}";
        var (isBinary, text, _) = InvokeParseOutgoingFrame(raw);

        Assert.False(isBinary);
        Assert.Equal(raw, text);
    }

    [Fact]
    public void ParseOutgoingFrame_Malformed_Json_Returns_Raw()
    {
        // Unparseable input is treated as a raw text payload — keeps the
        // publish path lossless when the user sends a literal protobuf-like
        // blob without the JSON envelope.
        var raw = "{ broken";
        var (isBinary, text, bytes) = InvokeParseOutgoingFrame(raw);

        Assert.False(isBinary);
        Assert.Equal(raw, text);
        Assert.Null(bytes);
    }

    [Fact]
    public void ParseOutgoingFrame_Plain_String_Treated_As_Text()
    {
        // A bare JSON string like `"hello"` — root-element is a string,
        // not an object, so the "type" check skips and the helper falls
        // through to the default-text branch.
        var (isBinary, text, _) = InvokeParseOutgoingFrame("\"hello\"");

        Assert.False(isBinary);
        Assert.Equal("\"hello\"", text);
    }

    // ---- Reflection plumbing ----

    private static (string host, int port)? InvokeParseBrokerUrl(string url)
    {
        var t = s_pluginAsm.GetType("Kuestenlogik.Bowire.Protocol.Mqtt.MqttConnectionHelper");
        Assert.NotNull(t);
        var method = t!.GetMethod("ParseBrokerUrl", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, [url]);
        if (result is null) return null;
        var tuple = (System.Runtime.CompilerServices.ITuple)result;
        return ((string)tuple[0]!, (int)tuple[1]!);
    }

    private static string InvokePayloadToDisplayString(byte[] payload)
    {
        var t = s_pluginAsm.GetType("Kuestenlogik.Bowire.Protocol.Mqtt.MqttPayloadHelper");
        Assert.NotNull(t);
        return (string)t!.GetMethod("PayloadToDisplayString", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [payload])!;
    }

    private static List<BowireServiceInfo> InvokeBuildServices(IEnumerable<string> topics, string originUrl)
    {
        var t = s_pluginAsm.GetType("Kuestenlogik.Bowire.Protocol.Mqtt.MqttDiscovery");
        Assert.NotNull(t);
        var hashSet = new HashSet<string>(topics, StringComparer.Ordinal);
        return (List<BowireServiceInfo>)t!.GetMethod("BuildServices", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [hashSet, originUrl])!;
    }

    private static BowireMessageInfo InvokeMessageBuilder(string methodName, string topic)
    {
        var t = s_pluginAsm.GetType("Kuestenlogik.Bowire.Protocol.Mqtt.MqttDiscovery");
        Assert.NotNull(t);
        return (BowireMessageInfo)t!.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [topic])!;
    }

    private static T InvokeStaticBuilder<T>(string methodName)
    {
        var t = s_pluginAsm.GetType("Kuestenlogik.Bowire.Protocol.Mqtt.MqttDiscovery");
        Assert.NotNull(t);
        return (T)t!.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;
    }

    private static (bool IsBinary, string? Text, byte[]? Bytes) InvokeParseOutgoingFrame(string jsonMessage)
    {
        var t = s_pluginAsm.GetType("Kuestenlogik.Bowire.Protocol.Mqtt.MqttBowireChannel");
        Assert.NotNull(t);
        var method = t!.GetMethod("ParseOutgoingFrame", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, [jsonMessage])!;
        var tuple = (System.Runtime.CompilerServices.ITuple)result;
        return ((bool)tuple[0]!, (string?)tuple[1], (byte[]?)tuple[2]);
    }
}
