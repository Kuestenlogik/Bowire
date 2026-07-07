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

    // ---------- fakes ----------

    private sealed class FakeProtocol : IBowireProtocol
    {
        public required string Id { get; init; }
        public string Name => Id;
        public string IconSvg => "";
        public Func<string, bool, List<BowireServiceInfo>>? Discover { get; init; }
        public Func<string, string, InvokeResult>? Invoke { get; init; }
        public Func<string, string, IBowireChannel?>? Open { get; init; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Discover is null ? throw new InvalidOperationException("Discover not configured") : Task.FromResult(Discover(serverUrl, showInternalServices));

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Invoke is null ? throw new InvalidOperationException("Invoke not configured") : Task.FromResult(Invoke(service, method));

        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Open is null ? throw new InvalidOperationException("Open not configured") : Task.FromResult(Open(service, method));
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
