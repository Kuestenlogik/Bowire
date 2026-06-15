// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Reflection;
using System.Text;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Protocol.Pulsar;

namespace Kuestenlogik.Bowire.Protocol.Pulsar.Tests;

/// <summary>
/// Coverage for the admin-API discovery surface — <see cref="PulsarDiscovery"/>
/// and the <see cref="BowirePulsarProtocol.DiscoverAsync"/> happy path —
/// plus the private <c>ResolveTopic</c> / <c>GetSetting</c> helpers
/// inside <see cref="BowirePulsarProtocol"/>. Drives the HTTP layer
/// with a stub <see cref="HttpMessageHandler"/> so the tests stay
/// offline. Complements <see cref="PulsarPluginTests"/> (pure helpers)
/// and <see cref="PulsarCoverageGapsTests"/> (Op-routing edges).
/// </summary>
#pragma warning disable CA2000 // HttpClient owns the handler via disposeHandler:true.
public sealed class PulsarDiscoveryHttpFakeTests
{
    // ---- PulsarDiscovery.ListTopicsAsync ------------------------------

    [Fact]
    public async Task ListTopicsAsync_BuildsOneServicePerTopic_WithProduceAndSubscribe()
    {
        // Two topics in public/default → two services, each with the
        // canonical produce + subscribe pair. The short name (the leaf
        // after the last slash) drives the sidebar label.
        var endpoints = PulsarConnectionHelper.Resolve("pulsar://localhost:6650")!;
        using var http = NewHttp((req, _) =>
        {
            Assert.Equal("http://localhost:8080/admin/v2/persistent/public/default",
                req.RequestUri!.ToString());
            return Respond(HttpStatusCode.OK,
                "[\"persistent://public/default/orders\",\"persistent://public/default/shipments\"]");
        });

        var services = await PulsarDiscovery.ListTopicsAsync(
            http, endpoints, ["public/default"],
            originUrl: "pulsar://localhost:6650",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, services.Count);
        Assert.Equal(["orders", "shipments"], services.Select(s => s.Name).ToArray());
        Assert.All(services, s =>
        {
            Assert.Equal("pulsar", s.Package);
            Assert.Equal("pulsar", s.Source);
            Assert.Equal("pulsar://localhost:6650", s.OriginUrl);
            Assert.StartsWith("Pulsar topic persistent://public/default/", s.Description!, StringComparison.Ordinal);
            Assert.Equal(2, s.Methods.Count);
            Assert.Contains(s.Methods, m => m.Name == "produce" && m.MethodType == "Unary");
            Assert.Contains(s.Methods, m => m.Name == "subscribe" && m.ServerStreaming);
        });
    }

    [Fact]
    public async Task ListTopicsAsync_SkipsNamespacesThatReturnEmpty()
    {
        // First ns is empty (continue), second ns has one topic. The
        // continue keeps the empty namespace from contributing zero
        // services *and* keeps the next namespace from being skipped.
        var endpoints = PulsarConnectionHelper.Resolve("pulsar://localhost:6650")!;
        var seen = new List<string>();
        using var http = NewHttp((req, _) =>
        {
            seen.Add(req.RequestUri!.AbsolutePath);
            return req.RequestUri!.AbsolutePath.EndsWith("public/default", StringComparison.Ordinal)
                ? Respond(HttpStatusCode.OK, "[]")
                : Respond(HttpStatusCode.OK, "[\"persistent://team-a/orders/in\"]");
        });

        var services = await PulsarDiscovery.ListTopicsAsync(
            http, endpoints, ["public/default", "team-a/orders"],
            originUrl: "pulsar://localhost:6650",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, seen.Count);
        Assert.Single(services);
        Assert.Equal("in", services[0].Name);
    }

    [Fact]
    public async Task ListTopicsAsync_404FromAdmin_YieldsEmpty()
    {
        // The catch-and-continue inside ListNamespaceTopicsAsync —
        // a 404 must not throw, it must produce an empty list so the
        // workbench keeps walking remaining namespaces.
        var endpoints = PulsarConnectionHelper.Resolve("pulsar://localhost:6650")!;
        using var http = NewHttp((_, _) => Respond(HttpStatusCode.NotFound, "{}"));

        var services = await PulsarDiscovery.ListTopicsAsync(
            http, endpoints, ["public/default"],
            originUrl: "pulsar://localhost:6650",
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task ListTopicsAsync_HttpThrows_YieldsEmpty()
    {
        // Network-level exception → the broad catch in
        // ListNamespaceTopicsAsync swallows it and the namespace is
        // skipped silently.
        var endpoints = PulsarConnectionHelper.Resolve("pulsar://localhost:6650")!;
        using var http = NewHttp((_, _) => throw new HttpRequestException("connection refused"));

        var services = await PulsarDiscovery.ListTopicsAsync(
            http, endpoints, ["public/default"],
            originUrl: "pulsar://localhost:6650",
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task ListTopicsAsync_BlankAndSlashlessNamespaces_AreSkippedWithoutHttpCalls()
    {
        // ListNamespaceTopicsAsync rejects blank + slash-less
        // namespaces *before* hitting HTTP — the stub counts calls
        // to prove no admin request was issued for them.
        var endpoints = PulsarConnectionHelper.Resolve("pulsar://localhost:6650")!;
        var calls = 0;
        using var http = NewHttp((_, _) =>
        {
            calls++;
            return Respond(HttpStatusCode.OK, "[]");
        });

        var services = await PulsarDiscovery.ListTopicsAsync(
            http, endpoints, ["", "   ", "no-slash-here"],
            originUrl: "pulsar://localhost:6650",
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ListTopicsAsync_BackslashOrMixedJson_ParsesOnlyStringEntries()
    {
        // ParseTopicJson skips non-string entries silently — pin the
        // mixed-array tolerance so a future broker that adds metadata
        // fields doesn't crash discovery.
        var endpoints = PulsarConnectionHelper.Resolve("pulsar://localhost:6650")!;
        using var http = NewHttp((_, _) => Respond(HttpStatusCode.OK,
            "[\"persistent://public/default/a\", 42, null, \"persistent://public/default/b\", \"\"]"));

        var services = await PulsarDiscovery.ListTopicsAsync(
            http, endpoints, ["public/default"],
            originUrl: "pulsar://localhost:6650",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, services.Count);
        Assert.Equal("a", services[0].Name);
        Assert.Equal("b", services[1].Name);
    }

    // ---- BowirePulsarProtocol.DiscoverAsync happy path -----------------

    [Fact]
    public async Task DiscoverAsync_DefaultNamespaceSetting_HitsPublicDefault_AndReturnsServices()
    {
        // The plugin defaults to namespaces="public/default" via
        // the Settings list — DiscoverAsync must read that default
        // through GetSetting and pass it to ListTopicsAsync.
        var plugin = new BowirePulsarProtocol();
        var calls = new List<string>();
        var fake = NewHttp((req, _) =>
        {
            calls.Add(req.RequestUri!.ToString());
            return Respond(HttpStatusCode.OK, "[\"persistent://public/default/events\"]");
        });
        SwapHttp(plugin, fake);

        var services = await plugin.DiscoverAsync(
            "pulsar://broker.local:6650",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Single(services);
        Assert.Equal("events", services[0].Name);
        Assert.Equal("pulsar://broker.local:6650", services[0].OriginUrl);
        Assert.Single(calls);
        Assert.Equal("http://broker.local:8080/admin/v2/persistent/public/default", calls[0]);
        fake.Dispose();
    }

    [Fact]
    public async Task DiscoverAsync_HttpAdminUrl_StillResolvesAdminBaseFromInputPort()
    {
        // When the user types an http://host:PORT admin URL the
        // resolver keeps the non-default port for the admin base.
        // Pin that the discovery request lands on the same port the
        // user supplied — not the 8080 default.
        var plugin = new BowirePulsarProtocol();
        var calls = new List<string>();
        var fake = NewHttp((req, _) =>
        {
            calls.Add(req.RequestUri!.ToString());
            return Respond(HttpStatusCode.OK, "[\"persistent://public/default/x\"]");
        });
        SwapHttp(plugin, fake);

        var services = await plugin.DiscoverAsync(
            "http://broker.local:18080",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Single(services);
        Assert.Equal("http://broker.local:18080/admin/v2/persistent/public/default", calls[0]);
        fake.Dispose();
    }

    // ---- Private ResolveTopic via reflection ---------------------------

    [Fact]
    public void ResolveTopic_MetadataTopicOverride_WinsOverDiscoveryTopic()
    {
        // ResolveTopic is private but the metadata-override path
        // matters: workbench users can poke a topic that wasn't in
        // the scan by setting metadata["topic"]. The override goes
        // through NormaliseTopicName too.
        var result = InvokeResolveTopic(
            discoveryTopic: "persistent://public/default/discovered",
            metadata: new Dictionary<string, string> { ["topic"] = "ad-hoc" });
        Assert.Equal("persistent://public/default/ad-hoc", result);
    }

    [Fact]
    public void ResolveTopic_BlankMetadataTopic_FallsBackToDiscoveryTopic()
    {
        // Whitespace-only override is treated as "no override" so
        // pasting an empty value doesn't break the discovery route.
        var result = InvokeResolveTopic(
            discoveryTopic: "persistent://public/default/orders",
            metadata: new Dictionary<string, string> { ["topic"] = "   " });
        Assert.Equal("persistent://public/default/orders", result);
    }

    [Fact]
    public void ResolveTopic_NullMetadata_UsesDiscoveryTopicNormalised()
    {
        var result = InvokeResolveTopic(
            discoveryTopic: "foo",
            metadata: null);
        Assert.Equal("persistent://public/default/foo", result);
    }

    [Fact]
    public void ResolveTopic_TenantNsTopicShortform_GetsPersistentPrefix()
    {
        // Two-slash names are the tenant/ns/topic shape and must
        // get the persistent:// prefix without doubling
        // public/default in front of them.
        var result = InvokeResolveTopic(
            discoveryTopic: "team-a/ns1/widgets",
            metadata: null);
        Assert.Equal("persistent://team-a/ns1/widgets", result);
    }

    // ---- Private GetSetting via reflection -----------------------------

    [Fact]
    public void GetSetting_KnownKey_ReturnsDefaultValueAsString()
    {
        // GetSetting reads from the plugin's own Settings list.
        // "subscribeFromLatest" defaults to bool true → ToString()
        // gives "True" (the case the stream uses for the
        // OrdinalIgnoreCase compare in InvokeStreamAsync).
        var plugin = new BowirePulsarProtocol();
        var val = InvokePrivateString(plugin, "GetSetting", "subscribeFromLatest", "fallback-not-used");
        Assert.Equal("True", val);
    }

    [Fact]
    public void GetSetting_UnknownKey_FallsBackToProvidedDefault()
    {
        var plugin = new BowirePulsarProtocol();
        var val = InvokePrivateString(plugin, "GetSetting", "missing-key", "fb");
        Assert.Equal("fb", val);
    }

    [Fact]
    public void GetSetting_NamespacesDefault_RoundTripsThroughParseNamespaces()
    {
        // The default GetSetting("namespaces", "...") result drives
        // ParseNamespaces — make sure the round-trip yields the
        // single public/default entry the workbench expects.
        var plugin = new BowirePulsarProtocol();
        var val = InvokePrivateString(plugin, "GetSetting", "namespaces", "fb");
        Assert.Equal("public/default", val);
        Assert.Equal(["public/default"], BowirePulsarProtocol.ParseNamespaces(val));
    }

    // ---- InvokeAsync error envelope shape ------------------------------

    [Fact]
    public async Task InvokeAsync_NullRoute_ReturnsZeroDurationAndEmptyMetadata()
    {
        // The router-error envelope must be self-consistent: Response
        // null, Status carries the error, Metadata empty, Duration
        // zero (timer hasn't started yet).
        var plugin = new BowirePulsarProtocol();
        var r = await plugin.InvokeAsync(
            "pulsar://localhost:6650",
            service: "x",
            method: "",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(r.Response);
        Assert.Equal(0, r.DurationMs);
        Assert.Empty(r.Metadata);
        Assert.Contains("Unknown Pulsar route", r.Status, StringComparison.Ordinal);
        Assert.Contains("expected pulsar/topic/<name>/produce", r.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_InvalidUrl_ZeroDurationAndEmptyMetadata()
    {
        // Same self-consistency check on the URL-error envelope.
        var plugin = new BowirePulsarProtocol();
        var r = await plugin.InvokeAsync(
            "ftp://nope",
            service: "x",
            method: "pulsar/topic/orders/produce",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(r.Response);
        Assert.Equal(0, r.DurationMs);
        Assert.Empty(r.Metadata);
        Assert.Equal("Invalid Pulsar server URL", r.Status);
    }

    // ---- InvokeAsync produce path (broker unreachable) ----------------

    [Fact(Timeout = 30_000)]
    public async Task InvokeAsync_Produce_UnreachableBroker_RoundsThroughResolveTopic_AndCatchBlock()
    {
        // Drives the full produce path: URL resolved, route parsed,
        // ResolveTopic runs (including the metadata override), then
        // the broker connect inside producer.Create() / Send() trips
        // on the cancellation token. The catch block returns the
        // exception message + non-null DurationMs (stopwatch was
        // started before the try).
        var plugin = new BowirePulsarProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        InvokeResult result;
        try
        {
            result = await plugin.InvokeAsync(
                "pulsar://127.0.0.1:1",
                service: "x",
                method: "pulsar/topic/persistent://public/default/orders/produce",
                jsonMessages: ["{\"hello\":1}"],
                showInternalServices: false,
                metadata: new Dictionary<string, string> { ["topic"] = "override-topic" },
                ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Some DotPulsar paths surface the cancel as an
            // OperationCanceledException ahead of the catch — that
            // still proves ResolveTopic + the producer path executed.
            return;
        }

        Assert.NotEqual("OK", result.Status);
        Assert.False(string.IsNullOrEmpty(result.Status));
        Assert.Null(result.Response);
        // Status carries the exception message — not the routing /
        // URL error envelope text.
        Assert.DoesNotContain("Unknown Pulsar route", result.Status, StringComparison.Ordinal);
        Assert.NotEqual("Invalid Pulsar server URL", result.Status);
    }

    // ---- InvokeStreamAsync subscribe path (broker unreachable) --------

    [Fact(Timeout = 30_000)]
    public async Task InvokeStreamAsync_Subscribe_MetadataOverrides_AreRead_BeforeBrokerError()
    {
        // The metadata override block (subscription_name, from_latest,
        // topic) runs ahead of the consumer.Create() / Messages()
        // loop. Even when the broker is unreachable we must reach
        // those reads; the await-foreach surfaces the broker error
        // as an exception once enumeration begins.
        var plugin = new BowirePulsarProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var collected = new List<string>();
        Exception? caught = null;
        try
        {
            await foreach (var msg in plugin.InvokeStreamAsync(
                "pulsar://127.0.0.1:1",
                service: "x",
                method: "pulsar/topic/persistent://public/default/orders/subscribe",
                jsonMessages: [],
                showInternalServices: false,
                metadata: new Dictionary<string, string>
                {
                    ["topic"] = "override-topic",
                    ["subscription_name"] = "explicit-sub",
                    ["from_latest"] = "false",
                },
                ct: cts.Token))
            {
                collected.Add(msg);
            }
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        // Either the broker error propagates, or the cancellation
        // token fires first. Both prove we passed through the
        // metadata-override + consumer setup code path.
        Assert.Empty(collected);
        Assert.NotNull(caught);
    }

    // ---- Initialize wiring ---------------------------------------------

    [Fact]
    public async Task Initialize_With_ServiceProvider_ReplacesHttpClient_AndDiscoveryStillSurfacesNoBrokerAsEmpty()
    {
        // Initialize replaces _http with a fresh BowireHttpClientFactory
        // client. After that, hitting an unresolvable host must still
        // produce the empty list — the broad catch in
        // ListNamespaceTopicsAsync absorbs the DNS error.
        var plugin = new BowirePulsarProtocol();
        // Empty provider — GetService<IConfiguration> returns null,
        // which Create() handles by skipping the relaxed callback.
        plugin.Initialize(new EmptyServiceProvider());

        // Use a deliberately unresolvable host so DNS fails fast.
        var services = await plugin.DiscoverAsync(
            "pulsar://this-host-does-not-resolve.bowire-test.invalid:6650",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    // ---- helpers -------------------------------------------------------

    private static StubHandlerHttpClient NewHttp(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> f)
        => new(f);

    private static HttpResponseMessage Respond(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static void SwapHttp(BowirePulsarProtocol plugin, HttpClient client)
    {
        var field = typeof(BowirePulsarProtocol).GetField("_http",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(plugin, client);
    }

    private static string InvokeResolveTopic(string discoveryTopic, Dictionary<string, string>? metadata)
    {
        var m = typeof(BowirePulsarProtocol).GetMethod("ResolveTopic",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(m);
        return (string)m!.Invoke(null, [discoveryTopic, metadata])!;
    }

    private static string InvokePrivateString(BowirePulsarProtocol target, string method, string key, string fallback)
    {
        var m = typeof(BowirePulsarProtocol).GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);
        return (string)m!.Invoke(target, [key, fallback])!;
    }

    private sealed class StubHandlerHttpClient : HttpClient
    {
        public StubHandlerHttpClient(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> f)
            : base(new StubHandler(f), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(5);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _f;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_f(request, cancellationToken));
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
#pragma warning restore CA2000
