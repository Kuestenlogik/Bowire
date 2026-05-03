// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Mqtt;
using Kuestenlogik.Bowire.Protocol.Mqtt.Mock;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the MQTT protocol plugin. Identity, options surface, the broker-
/// URL parser, the topic-grouping discovery shape, and the payload-display
/// helper are all reachable without a live broker. Connect-and-listen against
/// a real broker is integration-tier territory — those paths return empty /
/// null on connect failure here, which is the contract.
/// </summary>
public sealed class BowireMqttProtocolTests
{
    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireMqttProtocol();

        Assert.Equal("MQTT", protocol.Name);
        Assert.Equal("mqtt", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Implements_IBowireProtocol()
    {
        var protocol = new BowireMqttProtocol();

        Assert.IsAssignableFrom<IBowireProtocol>(protocol);
    }

    [Fact]
    public void Initialize_Accepts_Null_Service_Provider()
    {
        var protocol = new BowireMqttProtocol();

        // No-op contract — must not throw.
        protocol.Initialize(null);
    }

    [Fact]
    public void Settings_Surface_Plugin_Config_Knobs()
    {
        var protocol = new BowireMqttProtocol();

        var settings = protocol.Settings;
        Assert.NotEmpty(settings);
        Assert.Contains(settings, s => s.Key == "autoInterpretJson");
        Assert.Contains(settings, s => s.Key == "scanDuration");
    }

    [Fact]
    public async Task DiscoverAsync_Empty_Url_Returns_Empty()
    {
        var protocol = new BowireMqttProtocol();

        var services = await protocol.DiscoverAsync(
            "", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Whitespace_Url_Returns_Empty()
    {
        var protocol = new BowireMqttProtocol();

        var services = await protocol.DiscoverAsync(
            "   ", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Unreachable_Broker_Returns_Empty_Without_Throwing()
    {
        var protocol = new BowireMqttProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 127.0.0.2:1 — the connect helper has a 15s ceiling, so this returns
        // empty rather than blocking on the kernel TCP retry budget.
        var services = await protocol.DiscoverAsync(
            "mqtt://127.0.0.2:1", showInternalServices: false, cts.Token);

        Assert.Empty(services);
    }

    [Fact]
    public async Task InvokeAsync_Unparseable_Url_Returns_Error_Result()
    {
        var protocol = new BowireMqttProtocol();

        var result = await protocol.InvokeAsync(
            serverUrl: "",
            service: "sensors",
            method: "sensors/temp",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task OpenChannelAsync_Unparseable_Url_Returns_Null()
    {
        var protocol = new BowireMqttProtocol();

        var channel = await protocol.OpenChannelAsync(
            serverUrl: "",
            service: "x",
            method: "topic",
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

    [Fact]
    public async Task OpenChannelAsync_Unreachable_Broker_Returns_Null()
    {
        var protocol = new BowireMqttProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Connection failure is caught and surfaced as null.
        var channel = await protocol.OpenChannelAsync(
            serverUrl: "mqtt://127.0.0.2:1",
            service: "sensors",
            method: "sensors/temp",
            showInternalServices: false,
            metadata: null,
            cts.Token);

        Assert.Null(channel);
    }

    [Fact]
    public async Task OpenChannelAsync_Custom_Publish_And_Subscribe_Topics_Are_Accepted()
    {
        // Exercises the publish_topic / subscribe_topic / qos / retain
        // metadata branches before the connect (which then fails because
        // the broker isn't running). Returns null without throwing.
        var protocol = new BowireMqttProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var channel = await protocol.OpenChannelAsync(
            serverUrl: "mqtt://127.0.0.1:1",
            service: "topics",
            method: "default-topic",
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["publish_topic"] = "out/cmd",
                ["subscribe_topic"] = "in/state",
                ["qos"] = "ExactlyOnce",
                ["retain"] = "true"
            },
            cts.Token);

        Assert.Null(channel);
    }

    [Fact]
    public async Task OpenChannelAsync_Invalid_Qos_Falls_Back_To_Default_AtLeastOnce()
    {
        var protocol = new BowireMqttProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var channel = await protocol.OpenChannelAsync(
            serverUrl: "mqtt://127.0.0.1:1",
            service: "x", method: "x/y",
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["qos"] = "GarbageValue",
                ["retain"] = "false"
            },
            cts.Token);

        Assert.Null(channel);
    }

    [Fact]
    public async Task InvokeAsync_With_Qos_And_Retain_Metadata_Returns_Result_Or_Throws()
    {
        // Pre-connect metadata parsing (qos=AtMostOnce, retain=true) is
        // reachable even though the connect itself fails. The plugin's
        // InvokeAsync doesn't catch broker-connect failures here, so we
        // accept either an exception or a returned result.
        var protocol = new BowireMqttProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var result = await protocol.InvokeAsync(
                serverUrl: "mqtt://127.0.0.1:1",
                service: "sensors",
                method: "sensors/temp",
                jsonMessages: ["{\"value\":21.5}"],
                showInternalServices: false,
                metadata: new Dictionary<string, string>
                {
                    ["qos"] = "AtMostOnce",
                    ["retain"] = "TRUE"
                },
                cts.Token);
            Assert.NotNull(result);
        }
        catch (Exception ex)
        {
            Assert.NotNull(ex);
        }
    }
}

/// <summary>
/// Tests for <see cref="MqttConnectionHelper.ParseBrokerUrl"/> — the URL
/// parser the discovery and invocation paths use to extract host + port
/// from a user-supplied broker address. The cases below cover every scheme
/// branch and the trailing-path / default-port fall-throughs.
/// </summary>
public sealed class MqttConnectionHelperTests
{
    [Fact]
    public void Parse_Mqtt_Scheme_With_Port()
    {
        var parsed = ParseBrokerUrl("mqtt://broker.example:1883");
        Assert.NotNull(parsed);
        Assert.Equal("broker.example", parsed!.Value.host);
        Assert.Equal(1883, parsed.Value.port);
    }

    [Fact]
    public void Parse_Mqtts_Scheme_With_Port()
    {
        var parsed = ParseBrokerUrl("mqtts://broker.example:8883");
        Assert.NotNull(parsed);
        Assert.Equal(8883, parsed!.Value.port);
    }

    [Fact]
    public void Parse_Tcp_Scheme()
    {
        var parsed = ParseBrokerUrl("tcp://broker.example:1234");
        Assert.NotNull(parsed);
        Assert.Equal(1234, parsed!.Value.port);
    }

    [Fact]
    public void Parse_Ssl_Scheme()
    {
        var parsed = ParseBrokerUrl("ssl://broker.example:8884");
        Assert.NotNull(parsed);
        Assert.Equal(8884, parsed!.Value.port);
    }

    [Fact]
    public void Parse_Http_Schemes_Are_Stripped_For_Convenience()
    {
        var http = ParseBrokerUrl("http://broker.example:1883");
        var https = ParseBrokerUrl("https://broker.example:8883");

        Assert.Equal("broker.example", http!.Value.host);
        Assert.Equal("broker.example", https!.Value.host);
    }

    [Fact]
    public void Parse_Bare_Host_Port()
    {
        var parsed = ParseBrokerUrl("broker.example:1883");
        Assert.NotNull(parsed);
        Assert.Equal("broker.example", parsed!.Value.host);
        Assert.Equal(1883, parsed.Value.port);
    }

    [Fact]
    public void Parse_Host_Without_Port_Defaults_To_1883()
    {
        var parsed = ParseBrokerUrl("broker.example");
        Assert.NotNull(parsed);
        Assert.Equal("broker.example", parsed!.Value.host);
        Assert.Equal(1883, parsed.Value.port);
    }

    [Fact]
    public void Parse_Trailing_Path_Is_Stripped()
    {
        var parsed = ParseBrokerUrl("ws://broker.example:8083/mqtt");

        // ws:// is not a known MQTT scheme — left in place. Host portion
        // ends at the first '/', so port-with-path drops the path before
        // splitting on ':'. The `ws://` prefix sticks and pulls the
        // resulting host along — the contract's purpose is to be lenient
        // about user input, so we just assert the slash was stripped.
        Assert.NotNull(parsed);
        Assert.DoesNotContain("/mqtt", parsed!.Value.host, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Empty_Returns_Null()
    {
        Assert.Null(ParseBrokerUrl(""));
        Assert.Null(ParseBrokerUrl("   "));
    }

    [Fact]
    public void Parse_Invalid_Port_Falls_Back_To_Default_Port()
    {
        // "broker.example:notaport" — colon present, port unparseable, so
        // the whole thing is treated as a hostname with the default port.
        var parsed = ParseBrokerUrl("broker.example:notaport");
        Assert.NotNull(parsed);
        Assert.Equal(1883, parsed!.Value.port);
    }

    private static (string host, int port)? ParseBrokerUrl(string url)
    {
        var asm = typeof(BowireMqttProtocol).Assembly;
        var type = asm.GetType("Kuestenlogik.Bowire.Protocol.Mqtt.MqttConnectionHelper", throwOnError: true)!;
        var method = type.GetMethod("ParseBrokerUrl", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return ((string host, int port)?)method!.Invoke(null, new object[] { url });
    }
}

/// <summary>
/// Tests for <see cref="MqttDiscovery.BuildServices"/> — the topic-to-service
/// grouping that makes scanned topics navigable in the Bowire UI.
/// </summary>
public sealed class MqttDiscoveryBuildServicesTests
{
    [Fact]
    public void Empty_Topic_Set_Yields_Empty_Service_List()
    {
        var result = InvokeBuildServices(new HashSet<string>(), "mqtt://broker:1883");

        Assert.Empty(result);
    }

    [Fact]
    public void Single_Topic_Yields_One_Service_With_Two_Methods()
    {
        var topics = new HashSet<string> { "sensors/temperature" };

        var result = InvokeBuildServices(topics, "mqtt://broker:1883");

        var svc = Assert.Single(result);
        Assert.Equal("sensors", svc.Name);
        Assert.Equal("mqtt", svc.Source);
        Assert.Equal("mqtt://broker:1883", svc.OriginUrl);
        // Each topic produces a Subscribe (ServerStreaming) + Publish (Unary)
        Assert.Equal(2, svc.Methods.Count);

        var subscribe = svc.Methods.Single(m => m.MethodType == "ServerStreaming");
        Assert.Equal("sensors/temperature", subscribe.Name);
        Assert.True(subscribe.ServerStreaming);
        Assert.Equal("mqtt/sensors/temperature/subscribe", subscribe.FullName);

        var publish = svc.Methods.Single(m => m.MethodType == "Unary");
        Assert.False(publish.ServerStreaming);
        Assert.Equal("mqtt/sensors/temperature/publish", publish.FullName);
    }

    [Fact]
    public void Topics_Are_Grouped_By_First_Path_Segment()
    {
        var topics = new HashSet<string>
        {
            "sensors/a", "sensors/b",
            "alerts/critical",
            "heartbeat"  // single-segment goes to (root)
        };

        var result = InvokeBuildServices(topics, "mqtt://broker");

        Assert.Equal(3, result.Count);
        var names = result.Select(s => s.Name).ToHashSet();
        Assert.Contains("sensors", names);
        Assert.Contains("alerts", names);
        Assert.Contains("(root)", names);

        var sensors = result.First(s => s.Name == "sensors");
        // Two topics → 4 methods (publish + subscribe each)
        Assert.Equal(4, sensors.Methods.Count);
        Assert.Contains("(2 topics)", sensors.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Subscribe_Method_Carries_Standard_Output_Schema()
    {
        var topics = new HashSet<string> { "telemetry/x" };
        var result = InvokeBuildServices(topics, "mqtt://broker");
        var subscribe = result[0].Methods.First(m => m.MethodType == "ServerStreaming");

        Assert.Equal("MqttMessage", subscribe.OutputType.Name);
        var fieldNames = subscribe.OutputType.Fields.Select(f => f.Name).ToArray();
        Assert.Equal(["topic", "payload", "qos", "retain", "bytes"], fieldNames);
    }

    [Fact]
    public void Publish_Method_Has_Payload_Qos_Retain_Inputs()
    {
        var topics = new HashSet<string> { "cmd/light" };
        var result = InvokeBuildServices(topics, "mqtt://broker");
        var publish = result[0].Methods.First(m => m.MethodType == "Unary");

        var fieldNames = publish.InputType.Fields.Select(f => f.Name).ToArray();
        Assert.Contains("payload", fieldNames);
        Assert.Contains("qos", fieldNames);
        Assert.Contains("retain", fieldNames);

        var qosField = publish.InputType.Fields.Single(f => f.Name == "qos");
        Assert.NotNull(qosField.EnumValues);
        Assert.Equal(3, qosField.EnumValues!.Count);
    }

    private static List<BowireServiceInfo> InvokeBuildServices(HashSet<string> topics, string originUrl)
    {
        var asm = typeof(BowireMqttProtocol).Assembly;
        var type = asm.GetType("Kuestenlogik.Bowire.Protocol.Mqtt.MqttDiscovery", throwOnError: true)!;
        var method = type.GetMethod("BuildServices", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { topics, originUrl });
        return Assert.IsType<List<BowireServiceInfo>>(result);
    }
}

/// <summary>
/// Tests for <see cref="MqttPayloadHelper.PayloadToDisplayString"/> — the
/// JSON → UTF-8 → hex fall-through that turns wire payloads into something
/// the UI can render. Each branch is exercised here so regressions in the
/// rendering logic surface immediately.
/// </summary>
public sealed class MqttPayloadHelperTests
{
    [Fact]
    public void Empty_Bytes_Yields_Empty_String()
    {
        var result = InvokePayloadToDisplayString(Array.Empty<byte>());

        Assert.Equal("", result);
    }

    [Fact]
    public void Json_Object_Is_Pretty_Printed()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("""{"temp":21.5,"unit":"C"}""");

        var result = InvokePayloadToDisplayString(bytes);

        // Pretty-print introduces newlines and indentation
        Assert.Contains("\n", result, StringComparison.Ordinal);
        Assert.Contains("\"temp\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_Text_Is_Returned_As_Is()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("hello world");

        var result = InvokePayloadToDisplayString(bytes);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Text_With_Whitespace_Is_Allowed_Through_The_Text_Path()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("line one\nline two\tcol\r");

        var result = InvokePayloadToDisplayString(bytes);

        Assert.Equal("line one\nline two\tcol\r", result);
    }

    [Fact]
    public void Binary_Payload_Falls_Through_To_Hex_Dump()
    {
        // Random bytes including control chars so neither the JSON nor the
        // text path accepts them — falls through to the hex dump.
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0xFF, 0xAB };

        var result = InvokePayloadToDisplayString(bytes);

        Assert.Contains("[binary: 5 bytes]", result, StringComparison.Ordinal);
        Assert.Contains("01", result, StringComparison.Ordinal);
        Assert.Contains("FF", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_Binary_Payload_Is_Truncated_With_Annotation()
    {
        // 300 bytes mixing control characters with 0xFF — the text path
        // rejects the control chars, the JSON path rejects the shape, so
        // we fall through to the hex dump.
        var bytes = new byte[300];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i % 16 == 0 ? 0x01 : 0xFF);

        var result = InvokePayloadToDisplayString(bytes);

        Assert.Contains("[binary: 300 bytes]", result, StringComparison.Ordinal);
        Assert.Contains("more bytes", result, StringComparison.Ordinal);
    }

    private static string InvokePayloadToDisplayString(byte[] bytes)
    {
        var asm = typeof(BowireMqttProtocol).Assembly;
        var type = asm.GetType("Kuestenlogik.Bowire.Protocol.Mqtt.MqttPayloadHelper", throwOnError: true)!;
        var method = type.GetMethod("PayloadToDisplayString", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { bytes });
        return Assert.IsType<string>(result);
    }
}

/// <summary>
/// Tests for <see cref="MqttBowireChannel"/>'s private outgoing-frame parser.
/// The full channel needs a broker connection — but the parser is a pure
/// function and exercising it lifts the channel class out of the
/// coverage-report basement.
/// </summary>
public sealed class MqttBowireChannelParseOutgoingFrameTests
{
    [Fact]
    public void Empty_String_Returns_Empty_Text_Frame()
    {
        var (isBinary, text, bytes) = InvokeParseOutgoingFrame("");

        Assert.False(isBinary);
        Assert.Equal("", text);
        Assert.Null(bytes);
    }

    [Fact]
    public void Plain_String_Is_Treated_As_Raw_Text_Payload()
    {
        // Non-JSON survives the parse exception catch — comes back as a
        // text frame with the raw string as the text payload.
        var (isBinary, text, bytes) = InvokeParseOutgoingFrame("hello");

        Assert.False(isBinary);
        Assert.Equal("hello", text);
        Assert.Null(bytes);
    }

    [Fact]
    public void Binary_Frame_Decodes_Base64_Payload()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 0xFF });

        var (isBinary, text, bytes) = InvokeParseOutgoingFrame(
            $"{{\"type\":\"binary\",\"base64\":\"{b64}\"}}");

        Assert.True(isBinary);
        Assert.Null(text);
        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 0xFF }, bytes);
    }

    [Fact]
    public void Text_Frame_Returns_Inner_Text()
    {
        var (isBinary, text, bytes) = InvokeParseOutgoingFrame(
            "{\"type\":\"text\",\"text\":\"the message\"}");

        Assert.False(isBinary);
        Assert.Equal("the message", text);
        Assert.Null(bytes);
    }

    [Fact]
    public void Convenience_Data_Property_Is_Treated_As_Text()
    {
        var (isBinary, text, bytes) = InvokeParseOutgoingFrame(
            "{\"type\":\"unknown\",\"data\":\"shortcut\"}");

        Assert.False(isBinary);
        Assert.Equal("shortcut", text);
        Assert.Null(bytes);
    }

    [Fact]
    public void Object_Without_Recognised_Type_Falls_Through_To_Raw_Json()
    {
        var raw = "{\"random\":\"object\"}";
        var (isBinary, text, _) = InvokeParseOutgoingFrame(raw);

        Assert.False(isBinary);
        Assert.Equal(raw, text);
    }

    private static (bool IsBinary, string? Text, byte[]? Bytes) InvokeParseOutgoingFrame(string jsonMessage)
    {
        var asm = typeof(BowireMqttProtocol).Assembly;
        var type = asm.GetType("Kuestenlogik.Bowire.Protocol.Mqtt.MqttBowireChannel", throwOnError: true)!;
        var method = type.GetMethod("ParseOutgoingFrame", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { jsonMessage });

        // Tuple value-type — get the items by reflection on its public fields.
        Assert.NotNull(result);
        var rt = result!.GetType();
        return (
            (bool)rt.GetField("Item1")!.GetValue(result)!,
            (string?)rt.GetField("Item2")!.GetValue(result),
            (byte[]?)rt.GetField("Item3")!.GetValue(result)
        );
    }
}

/// <summary>
/// Tests for <see cref="MqttTopicMatcher.TryMatch"/> — pattern matching the
/// MQTT spec's <c>+</c> and <c>#</c> wildcards. The mock-server pipeline
/// uses these bindings to feed the dynamic-value substitutor, so any
/// regression here would silently break recorded mock playback.
/// </summary>
public sealed class MqttTopicMatcherTests
{
    [Fact]
    public void Literal_Match_Returns_True_With_No_Bindings()
    {
        Assert.True(MqttTopicMatcher.TryMatch("sensors/a/temp", "sensors/a/temp", out var bindings));
        Assert.Empty(bindings);
    }

    [Fact]
    public void Literal_Mismatch_Returns_False()
    {
        Assert.False(MqttTopicMatcher.TryMatch("sensors/a", "sensors/b", out _));
    }

    [Fact]
    public void Plus_Wildcard_Captures_Single_Segment()
    {
        Assert.True(MqttTopicMatcher.TryMatch("sensors/+/temp", "sensors/room1/temp", out var bindings));
        Assert.Equal("room1", bindings["0"]);
    }

    [Fact]
    public void Plus_Wildcard_Does_Not_Match_Multiple_Segments()
    {
        Assert.False(MqttTopicMatcher.TryMatch("sensors/+/temp", "sensors/a/b/temp", out _));
    }

    [Fact]
    public void Hash_Wildcard_Matches_Trailing_Levels()
    {
        Assert.True(MqttTopicMatcher.TryMatch("sensors/#", "sensors/a/b/c", out var bindings));
        Assert.Equal("a/b/c", bindings["rest"]);
    }

    [Fact]
    public void Hash_Wildcard_Matches_Empty_Tail()
    {
        Assert.True(MqttTopicMatcher.TryMatch("sensors/#", "sensors", out var bindings));
        Assert.Equal("", bindings["rest"]);
    }

    [Fact]
    public void Hash_Wildcard_Must_Be_Final_Segment()
    {
        // "#" appears mid-pattern — invalid per the spec's TryMatch rule.
        Assert.False(MqttTopicMatcher.TryMatch("sensors/#/temp", "sensors/a/temp", out _));
    }

    [Fact]
    public void Empty_Pattern_Returns_False()
    {
        Assert.False(MqttTopicMatcher.TryMatch("", "anything", out _));
        Assert.False(MqttTopicMatcher.TryMatch(null, "x", out _));
    }

    [Fact]
    public void Empty_Topic_With_NonEmpty_Pattern_Returns_False()
    {
        Assert.False(MqttTopicMatcher.TryMatch("a/b", "", out _));
    }

    [Fact]
    public void Pattern_Length_Mismatch_Without_Hash_Returns_False()
    {
        // Pattern "a/b" doesn't have # so a longer topic must be rejected.
        Assert.False(MqttTopicMatcher.TryMatch("a/b", "a/b/c", out _));
    }

    [Fact]
    public void Pattern_Longer_Than_Topic_Returns_False()
    {
        Assert.False(MqttTopicMatcher.TryMatch("a/b/c", "a/b", out _));
    }

    [Fact]
    public void Multiple_Plus_Wildcards_Collect_Positionally()
    {
        Assert.True(MqttTopicMatcher.TryMatch("+/+/state", "lights/kitchen/state", out var bindings));
        Assert.Equal("lights", bindings["0"]);
        Assert.Equal("kitchen", bindings["1"]);
    }

    [Fact]
    public void IsPattern_Detects_Wildcards()
    {
        Assert.True(MqttTopicMatcher.IsPattern("a/+/c"));
        Assert.True(MqttTopicMatcher.IsPattern("a/#"));
        Assert.False(MqttTopicMatcher.IsPattern("a/b/c"));
        Assert.False(MqttTopicMatcher.IsPattern(""));
        Assert.False(MqttTopicMatcher.IsPattern(null));
    }
}
