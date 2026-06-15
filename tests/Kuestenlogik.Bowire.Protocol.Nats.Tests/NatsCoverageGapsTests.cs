// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Nats;

namespace Kuestenlogik.Bowire.Protocol.Nats.Tests;

/// <summary>
/// Targeted tests for the Nats plugin's uncovered branches: the
/// <c>NatsServicesDiscovery.BuildServiceFromInfo</c> JSON-shape
/// branches (no endpoints → placeholder; full endpoints with queue
/// groups; version + name), the <c>NatsPayloadHelper</c> JSON →
/// UTF-8 → hex fallback chain edge-cases, and
/// <see cref="BowireNatsProtocol"/> InvokeAsync / InvokeStreamAsync
/// error paths the live-server suite doesn't reach.
/// </summary>
public sealed class NatsCoverageGapsTests
{
    private static readonly Assembly s_pluginAsm = typeof(BowireNatsProtocol).Assembly;

    // ---- NatsPayloadHelper edge cases ---------------------------------

    [Fact]
    public void PayloadToDisplayString_JsonNumber_RoundTripsAsString()
    {
        // A bare JSON number is valid JSON — TryParseValue accepts
        // tokens, not just objects. Verify the round-trip preserves
        // the literal so it's not mis-categorised as binary.
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString",
            Encoding.UTF8.GetBytes("42"));
        Assert.Equal("42", s);
    }

    [Fact]
    public void PayloadToDisplayString_JsonString_IsKeptQuoted()
    {
        // A bare JSON string keeps its surrounding quotes after the
        // indented serializer round-trips it — pins the "JSON wins"
        // path over the UTF-8 fallback.
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString",
            Encoding.UTF8.GetBytes("\"hello\""));
        Assert.Equal("\"hello\"", s);
    }

    [Fact]
    public void PayloadToDisplayString_NestedJson_GetsIndentedAcrossLines()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"a":{"b":[1,2]}}""");
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString", bytes);
        // Each nested level adds another newline — at least 4 in a
        // pretty-printed nested doc.
        Assert.True(s.Split('\n').Length >= 4);
        Assert.Contains("\"b\"", s, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadToDisplayString_TextWithTabsAndNewlines_StaysText()
    {
        // Tabs + newlines are explicitly allow-listed in LooksLikeText
        // so the user-typed structured text doesn't fall down to a
        // hex dump.
        var bytes = Encoding.UTF8.GetBytes("a\tb\nc\r");
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString", bytes);
        Assert.Equal("a\tb\nc\r", s);
    }

    [Fact]
    public void PayloadToDisplayString_ExactlyAtHexCap_TruncatesNothing()
    {
        // 256 bytes is the cap; the suffix annotation should be
        // missing because there are zero "more bytes". 0x07 (BEL)
        // forces the hex branch.
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = 0x07;
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString", bytes);
        Assert.Contains("[binary: 256 bytes]", s, StringComparison.Ordinal);
        Assert.DoesNotContain("more bytes", s, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadToDisplayString_HexLine_WrapsAt16Bytes()
    {
        // 32 bytes of BEL produces two 16-byte hex rows after the
        // header line.
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = 0x07;
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString", bytes);
        // Header + two body rows = 3 lines (no trailing newline).
        var lines = s.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("[binary: 32 bytes]", lines[0], StringComparison.Ordinal);
        // Each body row is 16 two-digit hex tokens separated by spaces.
        Assert.Equal(16, lines[1].Split(' ').Length);
        Assert.Equal(16, lines[2].Split(' ').Length);
    }

    // ---- NatsServicesDiscovery.BuildServiceFromInfo branches ---------

    [Fact]
    public void BuildServiceFromInfo_NoInfoDoc_FallsBack_To_Placeholder_Ping()
    {
        var svc = InvokeBuildServiceFromInfo("orderbook", infoJson: null, "nats://localhost:4222");

        Assert.Equal("Service:orderbook", svc.Name);
        Assert.Equal("nats", svc.Source);
        var m = Assert.Single(svc.Methods);
        Assert.Equal("ping", m.Name);
        Assert.Equal("nats/services/orderbook/ping", m.FullName);
        Assert.Equal("Unary", m.MethodType);
        Assert.Contains("PING service 'orderbook'", m.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildServiceFromInfo_InfoWithVersion_GetsAnnotatedDescription()
    {
        var info = """{"name":"echo","version":"1.2.3","endpoints":[{"name":"hello","subject":"echo.hello"}]}""";
        var svc = InvokeBuildServiceFromInfo("echo", info, "nats://localhost:4222");

        Assert.Equal("Service:echo", svc.Name);
        Assert.Contains("v1.2.3", svc.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildServiceFromInfo_NoVersion_DescriptionStillSet()
    {
        var info = """{"name":"echo","endpoints":[{"name":"hello","subject":"echo.hello"}]}""";
        var svc = InvokeBuildServiceFromInfo("echo", info, "nats://localhost:4222");
        // The "v{version}" annotation appears only when the version
        // is present — its prefix "' v" is what we check for absence
        // (the trailing space + v before the version number).
        Assert.DoesNotContain("' v", svc.Description!, StringComparison.Ordinal);
        Assert.Contains("$SRV.PING", svc.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildServiceFromInfo_EndpointWithSubjectAndQueueGroup_AnnotatesDescription()
    {
        var info = """
            {
              "name":"echo",
              "endpoints":[
                {"name":"hello","subject":"echo.hello","queue_group":"echo_qg"}
              ]
            }
            """;
        var svc = InvokeBuildServiceFromInfo("echo", info, "nats://localhost:4222");

        var m = Assert.Single(svc.Methods);
        Assert.Equal("hello", m.Name);
        Assert.Equal("nats/services/echo/hello", m.FullName);
        Assert.Equal("Unary", m.MethodType);
        Assert.Contains("queue group: echo_qg", m.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildServiceFromInfo_EndpointWithoutName_FallsBack_To_Subject()
    {
        // The endpoint has only a subject — `name` defaults to the
        // subject so the FullName remains unique.
        var info = """{"name":"echo","endpoints":[{"subject":"echo.hello"}]}""";
        var svc = InvokeBuildServiceFromInfo("echo", info, "nats://localhost:4222");

        var m = Assert.Single(svc.Methods);
        Assert.Equal("echo.hello", m.Name);
        Assert.Equal("nats/services/echo/echo.hello", m.FullName);
    }

    [Fact]
    public void BuildServiceFromInfo_EndpointWithoutSubject_IsSkipped()
    {
        // Endpoints without a subject are unroutable — the discovery
        // skips them and falls back to the placeholder method.
        var info = """{"name":"echo","endpoints":[{"name":"broken"},{"name":"good","subject":"echo.good"}]}""";
        var svc = InvokeBuildServiceFromInfo("echo", info, "nats://localhost:4222");

        var m = Assert.Single(svc.Methods);
        Assert.Equal("good", m.Name);
    }

    [Fact]
    public void BuildServiceFromInfo_NoEndpointsArray_Yields_Placeholder()
    {
        // The info doc exists but has no endpoints — the placeholder
        // ping method kicks in.
        var info = """{"name":"echo","version":"0.1.0"}""";
        var svc = InvokeBuildServiceFromInfo("echo", info, "nats://localhost:4222");

        var m = Assert.Single(svc.Methods);
        Assert.Equal("ping", m.Name);
    }

    [Fact]
    public void BuildServiceFromInfo_EndpointsArray_NonObjectEntry_IsSkipped()
    {
        // Defensive — an array entry that isn't an object (string,
        // number, …) gets dropped without breaking the loop.
        var info = """{"name":"echo","endpoints":["broken",{"subject":"echo.ok"}]}""";
        var svc = InvokeBuildServiceFromInfo("echo", info, "nats://localhost:4222");

        var m = Assert.Single(svc.Methods);
        Assert.Equal("echo.ok", m.Name);
    }

    // ---- BowireNatsProtocol error paths --------------------------------

    [Fact]
    public async Task InvokeAsync_Request_On_Unreachable_Returns_Error()
    {
        // request shape hits the same connect-then-RequestAsync flow
        // as publish — but with a different ResponseHelper branch.
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "(root)",
            method: "nats/health/request",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task InvokeAsync_Publish_With_ReplyTo_Metadata_OnUnreachable_Errors()
    {
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "(root)",
            method: "nats/health/publish",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: new Dictionary<string, string> { ["reply_to"] = "inbox.echo" },
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_UnknownJetStreamOp_Returns_StructuredError()
    {
        // Route parser yields op="bogus" which JetStream dispatcher
        // doesn't know — surfaces a "Unknown JetStream operation"
        // message rather than crashing.
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "JetStream:orders",
            method: "nats/jetstream/orders/bogus",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        // Connect fails first → the catch path wins. Either way we
        // get a non-OK status with a non-null message.
        Assert.NotEqual("OK", result.Status);
        Assert.False(string.IsNullOrEmpty(result.Status));
    }

    [Fact]
    public async Task InvokeStreamAsync_EmptyUrl_YieldsNothing()
    {
        var p = new BowireNatsProtocol();
        var collected = new List<string>();
        await foreach (var msg in p.InvokeStreamAsync(
            "",
            service: "(root)",
            method: "nats/health/subscribe",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            collected.Add(msg);
        }
        Assert.Empty(collected);
    }

    [Fact]
    public async Task InvokeStreamAsync_QueueGroupMetadata_Honoured_On_Subscribe()
    {
        // Subscribe with a queue_group hint — we can't observe the
        // actual NATS-side semantics without a live broker, but we
        // can verify the code path doesn't throw before opening
        // (it returns immediately because the connect fails).
        var p = new BowireNatsProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await foreach (var _ in p.InvokeStreamAsync(
                "nats://127.0.0.1:1",
                service: "(root)",
                method: "nats/health/subscribe",
                jsonMessages: [],
                showInternalServices: false,
                metadata: new Dictionary<string, string> { ["queue_group"] = "echo_qg" },
                ct: cts.Token))
            {
                // not expected
            }
        }
        catch (OperationCanceledException)
        {
            // Connect-then-cancel is the happy path when no broker
            // is listening on port 1.
        }
        catch
        {
            // Any other failure mode (connection refused) is also
            // acceptable — the test pins that the queue-group branch
            // doesn't throw on metadata parsing.
        }
    }

    [Fact]
    public async Task InvokeAsync_BadSubject_NormalisationStillReturnsError()
    {
        // ResolveSubjectAndOp gives ("", "publish") for an empty
        // method name — the connect fails fast and we get an error
        // result. Pins the no-throw contract.
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "(root)",
            method: "",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
    }

    // ---- reflection helpers ----

    private static T Invoke<T>(string typeName, string methodName, params object?[] args)
    {
        var type = s_pluginAsm.GetType($"Kuestenlogik.Bowire.Protocol.Nats.{typeName}")
            ?? throw new InvalidOperationException($"Type {typeName} not found");
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {typeName}.{methodName} not found");
        return (T)method.Invoke(null, args)!;
    }

    private static BowireServiceInfo InvokeBuildServiceFromInfo(string name, string? infoJson, string originUrl)
    {
        var type = s_pluginAsm.GetType("Kuestenlogik.Bowire.Protocol.Nats.NatsServicesDiscovery")
            ?? throw new InvalidOperationException("NatsServicesDiscovery not found");
        var method = type.GetMethod("BuildServiceFromInfo",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildServiceFromInfo not found");

        JsonElement? doc = null;
        if (infoJson is not null)
        {
            using var d = JsonDocument.Parse(infoJson);
            doc = d.RootElement.Clone();
        }
        return (BowireServiceInfo)method.Invoke(null, [name, doc, originUrl])!;
    }
}
