// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Protocol.Nats;

namespace Kuestenlogik.Bowire.Protocol.Nats.Tests;

/// <summary>
/// Additional coverage-gap tests that go beyond <c>NatsCoverageGapsTests</c>:
/// URL-normalisation edge cases the parameterised theory in
/// <c>NatsHelperTests</c> doesn't reach, the Services-PING fallback
/// branches in <c>InvokeServiceAsync</c> that the existing Phase-2
/// tests skip (subject == "ping" and subject == ""), the JetStream
/// publish/consume routes against an unreachable server, plus
/// pre-cancelled-token paths through <c>DiscoverAsync</c> /
/// <c>InvokeAsync</c> / <c>InvokeStreamAsync</c>.
/// </summary>
public sealed class NatsAdditionalGapsTests
{
    private static readonly Assembly s_pluginAsm = typeof(BowireNatsProtocol).Assembly;

    // ---- NatsConnectionHelper.NormaliseServerUrl edge cases -----------

    [Fact]
    public void NormaliseServerUrl_MixedCaseScheme_PreservedVerbatim()
    {
        // The case-insensitive StartsWith branch returns the raw input
        // unchanged when the user types an uppercase / mixed-case
        // NATS scheme. The downstream NATS client lower-cases it.
        var s = Invoke<string?>("NatsConnectionHelper", "NormaliseServerUrl", "NATS://foo:4222");
        Assert.Equal("NATS://foo:4222", s);
    }

    [Fact]
    public void NormaliseServerUrl_MixedCaseHttp_RewrittenAsNats()
    {
        // Same OrdinalIgnoreCase branch on the http rewrite — keeps
        // pasted browser URLs working even when the scheme casing is
        // inconsistent.
        var s = Invoke<string?>("NatsConnectionHelper", "NormaliseServerUrl", "HTTP://foo:4222");
        Assert.Equal("nats://foo:4222", s);
    }

    [Fact]
    public void NormaliseServerUrl_MixedCaseHttps_RewrittenAsTls()
    {
        var s = Invoke<string?>("NatsConnectionHelper", "NormaliseServerUrl", "HTTPS://foo:4222");
        Assert.Equal("tls://foo:4222", s);
    }

    [Fact]
    public void NormaliseServerUrl_TrimsLeadingAndTrailingWhitespace()
    {
        // The Trim() at the top of the method is otherwise only
        // exercised indirectly — pin it explicitly.
        var s = Invoke<string?>("NatsConnectionHelper", "NormaliseServerUrl", "  nats://foo:4222  ");
        Assert.Equal("nats://foo:4222", s);
    }

    [Fact]
    public void NormaliseServerUrl_HostWithPortNoScheme_GetsNatsPrefix()
    {
        var s = Invoke<string?>("NatsConnectionHelper", "NormaliseServerUrl", "10.0.0.5:6222");
        Assert.Equal("nats://10.0.0.5:6222", s);
    }

    [Fact]
    public void BuildOptions_GeneratesUniqueClientNamesAcrossInstances()
    {
        // The 24-char truncated GUID-based Name must differ between
        // back-to-back BuildOptions calls so the broker can tell two
        // concurrent Bowire clients apart in $SYS observation.
        var a = Invoke<NATS.Client.Core.NatsOpts>("NatsConnectionHelper", "BuildOptions", "nats://a:4222");
        var b = Invoke<NATS.Client.Core.NatsOpts>("NatsConnectionHelper", "BuildOptions", "nats://b:4222");
        Assert.NotEqual(a.Name, b.Name);
        Assert.Equal(24, a.Name!.Length);
    }

    // ---- NatsPayloadHelper LooksLikeText branches ---------------------

    [Fact]
    public void PayloadToDisplayString_ControlByte_BellIsRejected_AsBinary()
    {
        // BEL (0x07) is char.IsControl == true and not in the
        // \n/\r/\t allow-list — LooksLikeText returns false and the
        // payload drops into the hex branch even though it's a single
        // printable-looking byte.
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString",
            new byte[] { 0x07 });
        Assert.Contains("[binary: 1 bytes]", s, StringComparison.Ordinal);
        Assert.Contains("07", s, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadToDisplayString_HexBoundary257Bytes_AnnotatesOneMore()
    {
        // 257 bytes is one past the 256-byte cap → "... (1 more bytes)".
        // Pins the boundary arithmetic in FormatHexDump's trailing
        // annotation.
        var bytes = new byte[257];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = 0x07;
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString", bytes);
        Assert.Contains("[binary: 257 bytes]", s, StringComparison.Ordinal);
        Assert.Contains("... (1 more bytes)", s, StringComparison.Ordinal);
    }

    // ---- BowireNatsProtocol Services PING fallback subjects -----------

    [Fact]
    public async Task InvokeAsync_Services_With_Empty_Subject_Synthesises_Srv_Ping_Subject()
    {
        // Route = (Services, "", "request", null, "echo") would
        // synthesise the subject "$SRV.PING.echo" because route.Subject
        // is empty. We can't observe the synthesised subject directly
        // (connect fails first) — but to actually reach that line the
        // route must be constructed as a Services route. The only way
        // to get an empty subject is a method that ends right after
        // the service name. ParseRoute requires firstSlash > 0; with
        // "nats/services/echo/" we get serviceName="echo", endpoint="".
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "Service:echo",
            method: "nats/services/echo/",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
        Assert.Null(result.Response);
        // The metadata bag on error is empty (the success branch fills
        // it). Pin the error contract.
        Assert.Empty(result.Metadata);
    }

    [Fact]
    public async Task InvokeAsync_Services_With_Ping_Subject_HitsSrv_Ping_Synthesis()
    {
        // route.Subject == "ping" is the second branch into the
        // $SRV.PING.<name> synthesis (see InvokeServiceAsync line ~450).
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "Service:echo",
            method: "nats/services/echo/ping",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
    }

    // ---- BowireNatsProtocol JetStream invocation error paths ----------

    [Fact]
    public async Task InvokeAsync_JetStream_Publish_On_Unreachable_Returns_Error()
    {
        // Route is JetStream/publish with a subject inside the stream —
        // the publish branch on InvokeJetStreamAsync (lines 376-396) is
        // distinct from the info branch and exercises a separate set
        // of statements.
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "JetStream:orders",
            method: "nats/jetstream/orders/publish/orders.created",
            jsonMessages: ["""{"id":"x"}"""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task InvokeAsync_JetStream_Publish_With_Empty_Json_DefaultsTo_BraceBrace()
    {
        // jsonMessages is empty → payload defaults to "{}". The branch
        // on FirstOrDefault() ?? "{}" is the same as the core publish
        // branch but inside the JetStream dispatcher.
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "JetStream:orders",
            method: "nats/jetstream/orders/publish/orders.created",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
    }

    // ---- BowireNatsProtocol JetStream consume streaming path ----------

    [Fact]
    public async Task InvokeStreamAsync_JetStream_Consume_On_Unreachable_Throws_Or_Yields_Nothing()
    {
        // The consume branch enters StreamJetStreamConsumerAsync which
        // awaits ConnectAsync().WaitAsync(ct). Against port 1 the
        // connect will throw (or the ct will trip first) — propagated
        // out of the iterator. Either way, we observe zero yielded
        // messages.
        var p = new BowireNatsProtocol();
        var collected = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var msg in p.InvokeStreamAsync(
                "nats://127.0.0.1:1",
                service: "JetStream:orders",
                method: "nats/jetstream/orders/consume",
                jsonMessages: [],
                showInternalServices: false,
                metadata: null,
                ct: cts.Token))
            {
                collected.Add(msg);
            }
        }
        catch
        {
            // Connection-refused / TaskCanceledException are both fine
            // — the test only pins that no envelopes get produced.
        }
        Assert.Empty(collected);
    }

    [Fact]
    public async Task InvokeStreamAsync_JetStream_Consume_With_EmptyUrl_YieldsNothing()
    {
        // Empty URL → NormaliseServerUrl returns null → method yield-
        // breaks immediately before hitting the JetStream branch.
        var p = new BowireNatsProtocol();
        var collected = new List<string>();
        await foreach (var msg in p.InvokeStreamAsync(
            "",
            service: "JetStream:orders",
            method: "nats/jetstream/orders/consume",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            collected.Add(msg);
        }
        Assert.Empty(collected);
    }

    // ---- Pre-cancelled token paths ------------------------------------

    [Fact]
    public async Task DiscoverAsync_PreCancelledToken_Returns_Empty_Without_Throwing()
    {
        // ct is already cancelled when DiscoverAsync starts — the
        // WaitAsync on ConnectAsync trips immediately, the catch
        // swallows it, and we return []. Pins that cancellation
        // doesn't bubble out as an exception (the workbench depends
        // on this contract).
        var p = new BowireNatsProtocol();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await p.DiscoverAsync(
            "nats://127.0.0.1:1",
            showInternalServices: false,
            ct: cts.Token);

        Assert.Empty(result);
    }

    [Fact]
    public async Task InvokeAsync_PreCancelledToken_PropagatesOrReturnsError()
    {
        // Same shape on InvokeAsync — but here the catch block wraps
        // the exception into an InvokeResult with the message, so we
        // get back a non-OK status rather than an OperationCanceled
        // bubbling out.
        var p = new BowireNatsProtocol();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "(root)",
            method: "nats/health/publish",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: cts.Token);

        Assert.NotEqual("OK", result.Status);
        Assert.False(string.IsNullOrEmpty(result.Status));
        // The duration field is set from a stopwatch — even on the
        // cancelled-fast path it should be non-negative.
        Assert.True(result.DurationMs >= 0);
    }

    [Fact]
    public async Task InvokeAsync_PreCancelledToken_JetStream_ReturnsError()
    {
        // Same pre-cancel guarantee for the JetStream dispatcher.
        var p = new BowireNatsProtocol();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "JetStream:orders",
            method: "nats/jetstream/orders/info",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: cts.Token);

        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_PreCancelledToken_Services_ReturnsError()
    {
        var p = new BowireNatsProtocol();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "Service:echo",
            method: "nats/services/echo/say",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: cts.Token);

        Assert.NotEqual("OK", result.Status);
    }

    // ---- NatsDiscovery.BuildServices wider grouping --------------------

    [Fact]
    public void BuildServices_DeeplyNestedSubject_GroupedByFirstToken()
    {
        // Subjects with many dots still group by the leading token —
        // pin the IndexOf('.') split rather than a LastIndexOf or split-
        // on-every-dot mistake.
        var services = BuildServices(["alpha.beta.gamma.delta"], "nats://h:4222");
        var alpha = Assert.Single(services);
        Assert.Equal("alpha", alpha.Name);
        // Methods keep the full subject as Name.
        Assert.All(alpha.Methods, m => Assert.Equal("alpha.beta.gamma.delta", m.Name));
    }

    [Fact]
    public void BuildServices_MultipleRootSubjects_StackInRootService()
    {
        var services = BuildServices(["health", "ping", "status"], "nats://h:4222");
        var root = Assert.Single(services);
        Assert.Equal("(root)", root.Name);
        // 3 subjects * 3 method shapes = 9.
        Assert.Equal(9, root.Methods.Count);
    }

    [Fact]
    public void BuildServices_OneSubject_DescriptionUsesSingular()
    {
        // Plural vs singular description hinges on subjectList.Count != 1.
        var services = BuildServices(["x.y"], "nats://h:4222");
        var svc = Assert.Single(services);
        Assert.Contains("1 subject)", svc.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("1 subjects)", svc.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildServices_TwoSubjectsInSameGroup_DescriptionUsesPlural()
    {
        var services = BuildServices(["x.a", "x.b"], "nats://h:4222");
        var svc = Assert.Single(services);
        Assert.Contains("2 subjects)", svc.Description, StringComparison.Ordinal);
    }

    // ---- Protocol surface stable contract -----------------------------

    [Fact]
    public void Settings_Include_ExpectedKeysAndDefaultValues()
    {
        var p = new BowireNatsProtocol();
        var auto = Assert.Single(p.Settings, s => s.Key == "autoInterpretJson");
        Assert.Equal("bool", auto.Type);
        Assert.Equal(true, auto.DefaultValue);

        var dur = Assert.Single(p.Settings, s => s.Key == "scanDuration");
        Assert.Equal("number", dur.Type);
        Assert.Equal(3, dur.DefaultValue);
    }

    [Fact]
    public void Description_MentionsPubSubAndRequestReply()
    {
        // The plugin's user-facing description is what the workbench
        // shows under the protocol picker — keep it stable.
        var p = new BowireNatsProtocol();
        Assert.Contains("publish/subscribe", p.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("request/reply", p.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initialize_With_Null_ServiceProvider_Is_NoOp()
    {
        // The current Initialize method intentionally does nothing
        // (services come from the host via constructor params on
        // sister plugins). Pin the no-op contract — a future side
        // effect should force a deliberate test update.
        var p = new BowireNatsProtocol();
        p.Initialize(null);
        // Re-call should also be safe (idempotent).
        p.Initialize(null);
        Assert.Equal("nats", p.Id);
    }

    // ---- ParseRoute additional malformed inputs -----------------------

    [Fact]
    public void ParseRoute_JetStream_Stream_Without_Op_Tail_Yields_EmptyOp()
    {
        // "nats/jetstream/orders/" has stream="orders", tail="" — the
        // method returns NatsRoute with Op="" rather than crashing.
        var route = InvokeParseRoute("nats/jetstream/orders/");
        Assert.Equal("JetStream", route.GetType().GetProperty("Family")!.GetValue(route)!.ToString());
        Assert.Equal("", route.GetType().GetProperty("Op")!.GetValue(route));
        Assert.Equal("orders", route.GetType().GetProperty("StreamName")!.GetValue(route));
    }

    [Fact]
    public void ParseRoute_Services_Trailing_Slash_Yields_EmptySubject()
    {
        // Pre-condition for the Empty_Subject test above — confirm the
        // ParseRoute output explicitly so the synthesis-branch test
        // doesn't depend on undocumented behaviour.
        var route = InvokeParseRoute("nats/services/echo/");
        Assert.Equal("Services", route.GetType().GetProperty("Family")!.GetValue(route)!.ToString());
        Assert.Equal("", route.GetType().GetProperty("Subject")!.GetValue(route));
        Assert.Equal("echo", route.GetType().GetProperty("ServiceName")!.GetValue(route));
    }

    // ---- reflection helpers -------------------------------------------

    private static T Invoke<T>(string typeName, string methodName, params object?[] args)
    {
        var type = s_pluginAsm.GetType($"Kuestenlogik.Bowire.Protocol.Nats.{typeName}")
            ?? throw new InvalidOperationException($"Type {typeName} not found");
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {typeName}.{methodName} not found");
        return (T)method.Invoke(null, args)!;
    }

    private static List<Kuestenlogik.Bowire.Models.BowireServiceInfo> BuildServices(string[] subjects, string originUrl)
    {
        var set = new HashSet<string>(subjects, StringComparer.Ordinal);
        return Invoke<List<Kuestenlogik.Bowire.Models.BowireServiceInfo>>(
            "NatsDiscovery", "BuildServices", set, originUrl);
    }

    private static object InvokeParseRoute(string method)
    {
        var mi = typeof(BowireNatsProtocol).GetMethod(
            "ParseRoute", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ParseRoute not found");
        return mi.Invoke(null, [method])
            ?? throw new InvalidOperationException("ParseRoute returned null");
    }
}
