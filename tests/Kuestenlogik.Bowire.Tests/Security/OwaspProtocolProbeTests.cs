// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the protocol-specific OWASP probes (<see cref="IOwaspProtocolProbe"/>
/// implementations) and the suite runner that drives them. Each probe is
/// exercised through a <see cref="FakeProtocol"/> stub of
/// <see cref="IBowireProtocol"/>, so the verdict logic is tested without a live
/// GraphQL / gRPC / MCP / WebSocket / MQTT server.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The probe under test owns the channel's disposal; the test keeps the reference only to assert it was disposed.")]
public sealed class OwaspProtocolProbeTests
{
    private static readonly string[] s_auth = ["Authorization: Bearer x"];
    private static readonly string[] s_noAuth = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BowireServiceInfo Service(string name, params string[] methodNames)
        => new(name, "", methodNames.Select(m => Method(m)).ToList());

    private static BowireMethodInfo Method(string name, bool clientStreaming = false, bool serverStreaming = false)
        => new(name, name, clientStreaming, serverStreaming,
            new BowireMessageInfo("In", "In", []), new BowireMessageInfo("Out", "Out", []), "Unary");

    // ---------- MCP (API9) ----------

    [Fact]
    public async Task Mcp_ListingReturned_FlagsApi9()
    {
        var probe = new McpDiscoveryProbe();
        var proto = new FakeProtocol { Id = "mcp", Discover = (_, _) => [Service("Tools", "search", "delete"), Service("Resources", "file://a")] };

        var findings = await probe.RunAsync("http://x/mcp", proto, s_noAuth, Ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API9-MCP-DISCOVERY", f.Template.Recording.Vulnerability?.Id);
        Assert.Equal("API9-2023-INVENTORY", f.Template.Recording.Vulnerability?.OwaspApi);
    }

    [Fact]
    public async Task Mcp_EmptyListing_Skips()
    {
        var probe = new McpDiscoveryProbe();
        var proto = new FakeProtocol { Id = "mcp", Discover = (_, _) => [] };

        var f = Assert.Single(await probe.RunAsync("http://x", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

    [Fact]
    public async Task Mcp_DiscoverThrows_Skips()
    {
        var probe = new McpDiscoveryProbe();
        var proto = new FakeProtocol { Id = "mcp", Discover = (_, _) => throw new InvalidOperationException("boom") };

        var f = Assert.Single(await probe.RunAsync("http://x", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API9-MCP-UNREACHABLE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    // ---------- GraphQL (API9) ----------

    [Fact]
    public async Task GraphQL_SchemaReturned_FlagsApi9()
    {
        var probe = new GraphQLIntrospectionProbe();
        var proto = new FakeProtocol { Id = "graphql", Discover = (_, _) => [Service("Query", "a", "b", "c")] };

        var f = Assert.Single(await probe.RunAsync("http://x", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API9-GRAPHQL-INTROSPECTION", f.Template.Recording.Vulnerability?.Id);
    }

    [Fact]
    public async Task GraphQL_NoSchema_Skips()
    {
        var probe = new GraphQLIntrospectionProbe();
        var proto = new FakeProtocol { Id = "graphql", Discover = (_, _) => [] };

        Assert.Equal(ScanFindingStatus.Skipped, Assert.Single(await probe.RunAsync("http://x", proto, s_noAuth, Ct)).Status);
    }

    // ---------- GraphQL resource limit (API4) ----------

    private const string GraphQlPreflightOk = "{\"data\":{\"__typename\":\"Query\"}}";

    // A fake that answers the __typename preflight with a GraphQL data envelope
    // and routes the aliasBatch query to the supplied response.
    private static FakeProtocol GraphQlResourceFake(Func<InvokeResult> batchResponse, string preflight = GraphQlPreflightOk)
        => new()
        {
            Id = "graphql",
            InvokeFull = (method, _) => method == "aliasBatch"
                ? batchResponse()
                : new InvokeResult(preflight, 1, "OK", []),
        };

    [Fact]
    public async Task GraphQL_AliasBatchResolvedInFull_FlagsApi4()
    {
        var probe = new GraphQLResourceLimitProbe();
        var proto = GraphQlResourceFake(() => new InvokeResult("{\"data\":{\"a0\":\"Query\",\"a1\":\"Query\"}}", 2, "OK", []));

        var f = Assert.Single(await probe.RunAsync("http://x/graphql", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API4-GRAPHQL-ALIAS-BATCHING", f.Template.Recording.Vulnerability?.Id);
        Assert.Equal("API4-2023-RESOURCE", f.Template.Recording.Vulnerability?.OwaspApi);
        Assert.Equal("CWE-770", f.Template.Recording.Vulnerability?.Cwe);
    }

    [Fact]
    public async Task GraphQL_AliasBatchRejectedWithErrors_ReportsLimitEnforced()
    {
        var probe = new GraphQLResourceLimitProbe();
        var proto = GraphQlResourceFake(() => new InvokeResult(
            "{\"errors\":[{\"message\":\"query is too complex\"}]}", 2, "OK", []));

        var f = Assert.Single(await probe.RunAsync("http://x/graphql", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("API4-GRAPHQL-LIMIT-ENFORCED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphQL_AliasBatchRejectedWith400_ReportsLimitEnforced()
    {
        var probe = new GraphQLResourceLimitProbe();
        // Plugin maps a non-2xx to a null-response InvokeResult carrying the message.
        var proto = GraphQlResourceFake(() => new InvokeResult(
            null, 2, "Response status code does not indicate success: 400 (Bad Request).", []));

        var f = Assert.Single(await probe.RunAsync("http://x/graphql", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("API4-GRAPHQL-LIMIT-ENFORCED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphQL_PreflightNotGraphQl_Skips()
    {
        var probe = new GraphQLResourceLimitProbe();
        var proto = GraphQlResourceFake(
            () => new InvokeResult("{\"data\":{}}", 2, "OK", []),
            preflight: "{\"message\":\"not found\"}");

        var f = Assert.Single(await probe.RunAsync("http://x", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API4-GRAPHQL-NOT-GRAPHQL", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphQL_PreflightThrows_SkipsUnreachable()
    {
        var probe = new GraphQLResourceLimitProbe();
        var proto = new FakeProtocol
        {
            Id = "graphql",
            InvokeFull = (_, _) => throw new HttpRequestException("connection refused"),
        };

        var f = Assert.Single(await probe.RunAsync("http://x/graphql", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API4-GRAPHQL-UNREACHABLE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphQL_AliasBatchNeitherDataNorErrors_Inconclusive()
    {
        var probe = new GraphQLResourceLimitProbe();
        var proto = GraphQlResourceFake(() => new InvokeResult("{}", 2, "OK", []));

        var f = Assert.Single(await probe.RunAsync("http://x/graphql", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API4-GRAPHQL-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphQL_AliasBatchAuthHeader_ForwardedAsMetadata()
    {
        var probe = new GraphQLResourceLimitProbe();
        Dictionary<string, string>? seen = null;
        var proto = new FakeProtocol
        {
            Id = "graphql",
            InvokeFull = (method, _) => new InvokeResult(
                method == "aliasBatch" ? "{\"data\":{\"a0\":\"Query\"}}" : GraphQlPreflightOk, 1, "OK", []),
        };
        // Capture metadata via a wrapping fake: assert the probe threads auth headers through.
        var capturing = new CapturingProtocol { Inner = proto, OnMetadata = md => seen = md };

        var f = Assert.Single(await probe.RunAsync("http://x/graphql", capturing, s_auth, Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.NotNull(seen);
        Assert.True(seen!.ContainsKey("Authorization"));
    }

    // ---------- gRPC reflection (API9) + transport auth (API2) ----------

    [Fact]
    public async Task Grpc_ReflectionOnly_NoAuthHeader_FlagsApi9Only()
    {
        var probe = new GrpcReflectionProbe();
        var proto = new FakeProtocol { Id = "grpc", Discover = (_, _) => [Service("pkg.Svc", "GetThing", "DoThing")] };

        var findings = await probe.RunAsync("https://x", proto, s_noAuth, Ct);

        var f = Assert.Single(findings);
        Assert.Equal("BWR-OWASP-API9-GRPC-REFLECTION", f.Template.Recording.Vulnerability?.Id);
        Assert.DoesNotContain(findings, x => x.Template.Recording.Vulnerability?.OwaspApi == "API2-2023-BROKENAUTH");
    }

    [Fact]
    public async Task Grpc_NoReflection_Skips()
    {
        var probe = new GrpcReflectionProbe();
        var proto = new FakeProtocol { Id = "grpc", Discover = (_, _) => [] };

        var f = Assert.Single(await probe.RunAsync("https://x", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

    [Fact]
    public async Task Grpc_AnonInvokeReachesHandler_FlagsApi2NoAuth()
    {
        var probe = new GrpcReflectionProbe();
        var proto = new FakeProtocol
        {
            Id = "grpc",
            Discover = (_, _) => [Service("pkg.Svc", "GetThing")],
            Invoke = (_, _) => new InvokeResult(null, 1, "OK", []),
        };

        var findings = await probe.RunAsync("https://x", proto, s_auth, Ct);

        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API9-GRPC-REFLECTION");
        var api2 = Assert.Single(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API2-GRPC-NOAUTH");
        Assert.Equal(ScanFindingStatus.Vulnerable, api2.Status);
        Assert.Equal("API2-2023-BROKENAUTH", api2.Template.Recording.Vulnerability?.OwaspApi);
    }

    [Fact]
    public async Task Grpc_AnonInvokeRejected_ReportsAuthEnforced()
    {
        var probe = new GrpcReflectionProbe();
        var proto = new FakeProtocol
        {
            Id = "grpc",
            Discover = (_, _) => [Service("pkg.Svc", "GetThing")],
            Invoke = (_, _) => new InvokeResult(null, 1, "Unauthenticated", []),
        };

        var findings = await probe.RunAsync("https://x", proto, s_auth, Ct);
        var safe = Assert.Single(findings, f => f.Status == ScanFindingStatus.Safe);
        Assert.Contains("API2-GRPC-AUTH-ENFORCED", safe.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grpc_NoReadOnlyMethod_SkipsAuthCheck()
    {
        var probe = new GrpcReflectionProbe();
        var proto = new FakeProtocol
        {
            Id = "grpc",
            Discover = (_, _) => [Service("pkg.Svc", "DeleteThing", "CreateThing")],
            Invoke = (_, _) => throw new InvalidOperationException("should not be called"),
        };

        var findings = await probe.RunAsync("https://x", proto, s_auth, Ct);
        Assert.Contains(findings, f => f.Template.Recording.Id.Contains("API2-GRPC-NO-READONLY", StringComparison.Ordinal));
    }

    // ---------- WebSocket (API2) ----------

    [Fact]
    public async Task WebSocket_NoAuthHeader_Silent()
    {
        var probe = new WebSocketAuthProbe();
        var proto = new FakeProtocol { Id = "websocket", Open = (_, _) => new FakeChannel() };

        Assert.Empty(await probe.RunAsync("wss://x", proto, s_noAuth, Ct));
    }

    [Fact]
    public async Task WebSocket_AnonConnectAccepted_FlagsApi2()
    {
        var probe = new WebSocketAuthProbe();
        var channel = new FakeChannel();
        var proto = new FakeProtocol { Id = "websocket", Open = (_, _) => channel };

        var f = Assert.Single(await probe.RunAsync("wss://x", proto, s_auth, Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API2-WS-NOAUTH", f.Template.Recording.Vulnerability?.Id);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task WebSocket_Rejected401_ReportsAuthEnforced()
    {
        var probe = new WebSocketAuthProbe();
        var proto = new FakeProtocol { Id = "websocket", Open = (_, _) => throw new InvalidOperationException("The server returned status code '401' Unauthorized") };

        var f = Assert.Single(await probe.RunAsync("wss://x", proto, s_auth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
    }

    [Fact]
    public async Task WebSocket_OtherFailure_Inconclusive()
    {
        var probe = new WebSocketAuthProbe();
        var proto = new FakeProtocol { Id = "websocket", Open = (_, _) => throw new HttpRequestException("connection refused") };

        var f = Assert.Single(await probe.RunAsync("wss://x", proto, s_auth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

    // ---------- MQTT (API2) ----------

    [Fact]
    public async Task Mqtt_NonBrokerScheme_Silent()
    {
        var probe = new MqttAuthProbe();
        var proto = new FakeProtocol { Id = "mqtt", Open = (_, _) => new FakeChannel() };

        Assert.Empty(await probe.RunAsync("https://x", proto, s_auth, Ct));
    }

    [Fact]
    public async Task Mqtt_NoAuthHeader_Silent()
    {
        var probe = new MqttAuthProbe();
        var proto = new FakeProtocol { Id = "mqtt", Open = (_, _) => new FakeChannel() };

        Assert.Empty(await probe.RunAsync("mqtt://x:1883", proto, s_noAuth, Ct));
    }

    [Fact]
    public async Task Mqtt_AnonConnectAccepted_FlagsApi2()
    {
        var probe = new MqttAuthProbe();
        var channel = new FakeChannel();
        var proto = new FakeProtocol { Id = "mqtt", Open = (_, _) => channel };

        var f = Assert.Single(await probe.RunAsync("mqtt://x:1883", proto, s_auth, Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API2-MQTT-NOAUTH", f.Template.Recording.Vulnerability?.Id);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task Mqtt_ConnectRefused_Inconclusive()
    {
        var probe = new MqttAuthProbe();
        var proto = new FakeProtocol { Id = "mqtt", Open = (_, _) => null };

        var f = Assert.Single(await probe.RunAsync("mqtt://x:1883", proto, s_auth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

    // ---------- SSE (API2) ----------

    [Fact]
    public async Task Sse_NoAuthHeader_Silent()
    {
        var probe = new SseAuthProbe();
        var proto = new FakeProtocol { Id = "sse", Stream = () => ["{\"data\":\"tick\"}"] };
        Assert.Empty(await probe.RunAsync("https://x/events", proto, s_noAuth, Ct));
    }

    [Fact]
    public async Task Sse_AnonymousStreamEmitsEvent_FlagsApi2()
    {
        var probe = new SseAuthProbe();
        var proto = new FakeProtocol { Id = "sse", Stream = () => ["{\"id\":null,\"event\":null,\"data\":\"tick\",\"retry\":null}"] };

        var f = Assert.Single(await probe.RunAsync("https://x/events", proto, s_auth, Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API2-SSE-NOAUTH", f.Template.Recording.Vulnerability?.Id);
    }

    [Fact]
    public async Task Sse_NotAnEventStream_Skips()
    {
        var probe = new SseAuthProbe();
        var proto = new FakeProtocol { Id = "sse", Stream = () => ["{\"error\":\"not an event-stream (content-type: text/html)\"}"] };

        var f = Assert.Single(await probe.RunAsync("https://x", proto, s_auth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API2-SSE-NOT-STREAM", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sse_Rejected401_ReportsAuthEnforced()
    {
        var probe = new SseAuthProbe();
        var proto = new FakeProtocol { Id = "sse", Stream = () => throw new HttpRequestException("Response status code 401 (Unauthorized)") };

        var f = Assert.Single(await probe.RunAsync("https://x/events", proto, s_auth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
    }

    [Fact]
    public async Task Sse_StreamEndsEmpty_Inconclusive()
    {
        var probe = new SseAuthProbe();
        var proto = new FakeProtocol { Id = "sse", Stream = Array.Empty<string> };

        var f = Assert.Single(await probe.RunAsync("https://x/events", proto, s_auth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

    // ---------- suite runner ----------

    [Fact]
    public async Task RunProtocolProbesAsync_ResolvesPluginsAndFoldsFindings()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new FakeProtocol { Id = "graphql", Discover = (_, _) => [Service("Query", "a")] });
        registry.Register(new FakeProtocol { Id = "grpc", Discover = (_, _) => [] });
        registry.Register(new FakeProtocol { Id = "mcp", Discover = (_, _) => [] });
        // websocket + mqtt plugins intentionally absent → PLUGIN-ABSENT skips.

        var findings = await OwaspApiSuite.RunProtocolProbesAsync("http://x", registry, s_noAuth, TimeSpan.FromSeconds(5), Ct);

        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API9-GRAPHQL-INTROSPECTION");
        Assert.Contains(findings, f => f.Template.Recording.Id.Contains("PLUGIN-ABSENT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunProtocolProbesAsync_ProbeThrows_IsolatedAsError()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new FakeProtocol { Id = "mcp", Discover = (_, _) => throw new TimeoutException("wedged") });

        var findings = await OwaspApiSuite.RunProtocolProbesAsync("http://x", registry, s_noAuth, TimeSpan.FromSeconds(5), Ct);

        // The MCP probe swallows non-cancellation exceptions into a Skipped
        // marker, so the run completes without surfacing an Error here.
        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.OwaspApi == "API9-2023-INVENTORY");
    }

    [Fact]
    public async Task RunProtocolProbesAsync_ReachesProtocolAtSubPath()
    {
        // The GraphQL endpoint is only live at /graphql (empty at the root);
        // the candidate-path loop must still reach it from a root --target so
        // the introspection finding fires instead of a path-blind skip.
        var registry = new BowireProtocolRegistry();
        registry.Register(new FakeProtocol
        {
            Id = "graphql",
            Discover = (url, _) => url.EndsWith("/graphql", StringComparison.Ordinal) ? [Service("Query", "a")] : [],
        });

        var findings = await OwaspApiSuite.RunProtocolProbesAsync("http://x", registry, s_noAuth, TimeSpan.FromSeconds(5), Ct);

        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API9-GRAPHQL-INTROSPECTION"
            && f.Status == ScanFindingStatus.Vulnerable);
    }

    [Fact]
    public async Task RunProtocolProbesAsync_ProtocolNowhere_SkipsNotVulnerable()
    {
        // GraphQL at no candidate path → every candidate skips; the probe
        // reports NO-INTROSPECTION, never a false Vulnerable.
        var registry = new BowireProtocolRegistry();
        registry.Register(new FakeProtocol { Id = "graphql", Discover = (_, _) => [] });

        var findings = await OwaspApiSuite.RunProtocolProbesAsync("http://x", registry, s_noAuth, TimeSpan.FromSeconds(5), Ct);

        Assert.Contains(findings, f => f.Template.Recording.Id.Contains("GRAPHQL-NO-INTROSPECTION", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Status == ScanFindingStatus.Vulnerable);
    }

    [Theory]
    [InlineData("graphql", "/graphql")]
    [InlineData("mcp", "/mcp")]
    [InlineData("websocket", "/ws")]
    [InlineData("sse", "/events")]
    public void CandidateTargets_IncludesWellKnownSubPath_TargetAsIsFirst(string protocolId, string expectedSuffix)
    {
        var candidates = OwaspApiSuite.CandidateTargets("https://h:5140", protocolId);
        Assert.Equal("https://h:5140", candidates[0]); // the operator-supplied target wins
        Assert.Contains("https://h:5140" + expectedSuffix, candidates);
    }

    [Fact]
    public void CandidateTargets_UnknownProtocol_OnlyTargetAsIs()
    {
        // gRPC (host authority) and any protocol without a known sub-path
        // mount only try the target verbatim.
        Assert.Equal(["http://x"], OwaspApiSuite.CandidateTargets("http://x", "grpc"));
    }

    [Fact]
    public void CandidateTargets_TrimsTrailingSlash_NoDuplicates()
    {
        var candidates = OwaspApiSuite.CandidateTargets("http://x/", "graphql");
        Assert.Equal("http://x/", candidates[0]);          // as-is preserved verbatim
        Assert.Contains("http://x/graphql", candidates);   // no double slash
        Assert.Equal(candidates.Count, candidates.Distinct().Count());
    }

    // ---------- fakes ----------

    private sealed class FakeProtocol : IBowireProtocol
    {
        public required string Id { get; init; }
        public string Name => Id;
        public string IconSvg => "";
        public Func<string, bool, List<BowireServiceInfo>>? Discover { get; init; }
        public Func<string, string, InvokeResult>? Invoke { get; init; }

        /// <summary>
        /// Invoke variant that also sees the method name + the raw
        /// <c>jsonMessages</c> (the full <c>{ "query": … }</c> request), so a
        /// probe that sends several distinct queries can be driven per-query.
        /// Takes precedence over <see cref="Invoke"/> when set.
        /// </summary>
        public Func<string, List<string>, InvokeResult>? InvokeFull { get; init; }
        public Func<string, string, IBowireChannel?>? Open { get; init; }
        public Func<IEnumerable<string>>? Stream { get; init; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Discover is null ? throw new InvalidOperationException("Discover not configured") : Task.FromResult(Discover(serverUrl, showInternalServices));

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            if (InvokeFull is not null) return Task.FromResult(InvokeFull(method, jsonMessages));
            return Invoke is null ? throw new InvalidOperationException("Invoke not configured") : Task.FromResult(Invoke(service, method));
        }

        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            if (Stream is null) yield break;
            foreach (var item in Stream())
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Open is null ? throw new InvalidOperationException("Open not configured") : Task.FromResult(Open(service, method));
    }

    // Wraps an inner protocol to capture the metadata dict the probe forwards
    // on InvokeAsync — used to assert --auth-header values are threaded through
    // as request headers.
    private sealed class CapturingProtocol : IBowireProtocol
    {
        public required FakeProtocol Inner { get; init; }
        public Action<Dictionary<string, string>?>? OnMetadata { get; init; }

        public string Id => Inner.Id;
        public string Name => Inner.Name;
        public string IconSvg => Inner.IconSvg;

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Inner.DiscoverAsync(serverUrl, showInternalServices, ct);

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            OnMetadata?.Invoke(metadata);
            return Inner.InvokeAsync(serverUrl, service, method, jsonMessages, showInternalServices, metadata, ct);
        }

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Inner.InvokeStreamAsync(serverUrl, service, method, jsonMessages, showInternalServices, metadata, ct);

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Inner.OpenChannelAsync(serverUrl, service, method, showInternalServices, metadata, ct);
    }

    private sealed class FakeChannel : IBowireChannel
    {
        public bool Disposed { get; private set; }
        public string Id => "fake";
        public bool IsClientStreaming => false;
        public bool IsServerStreaming => false;
        public int SentCount => 0;
        public bool IsClosed { get; private set; }
        public long ElapsedMs => 0;

        public Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default) => Task.FromResult(true);
        public Task CloseAsync(CancellationToken ct = default) { IsClosed = true; return Task.CompletedTask; }

        public async IAsyncEnumerable<string> ReadResponsesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
