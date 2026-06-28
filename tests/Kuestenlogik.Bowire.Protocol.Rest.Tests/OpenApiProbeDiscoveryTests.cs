// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// Tests for the well-known OpenAPI path auto-probe added to
/// <see cref="BowireRestProtocol"/>'s URL-discovery path. The operator
/// can now point Bowire at <c>http://localhost:5181</c> (origin only)
/// and the plugin sweeps the canonical <c>/openapi.json</c> /
/// <c>/swagger/v1/swagger.json</c> / … paths until something parses,
/// instead of demanding the exact spec URL up front.
/// </summary>
/// <remarks>
/// Each test uses a unique fake origin (e.g. <c>http://probe-test-1.invalid</c>)
/// so the seen-URL assertions ignore stray adapter calls from sibling test
/// classes running in parallel — <see cref="BowireOpenApiAdapterRegistry"/>
/// is process-wide, and an unrelated <see cref="BowireRestProtocol.DiscoverAsync"/>
/// call elsewhere routes through whatever adapter we just registered.
/// </remarks>
[Collection(nameof(OpenApiUploadStoreTestGroup))]
public sealed class OpenApiProbeDiscoveryTests : IDisposable
{
    public OpenApiProbeDiscoveryTests()
    {
        OpenApiUploadStore.Clear();
        BowireOpenApiAdapterRegistry.ResetForTests();
        RestProbeLog.Clear();
    }

    public void Dispose()
    {
        OpenApiUploadStore.Clear();
        BowireOpenApiAdapterRegistry.ResetForTests();
        RestProbeLog.Clear();
    }

    [Fact]
    public async Task Origin_Url_Triggers_Probe_Sweep_And_Resolves_First_Well_Known_Path()
    {
        // Operator types `http://probe-test-1.invalid` — not a spec URL itself.
        // /openapi.json wins on the first probe.
        const string origin = "http://probe-test-1.invalid";
        var adapter = new ProgrammableAdapter
        {
            Responses =
            {
                [origin] = null,
                [origin + "/openapi.json"] = StubResult("hits-on-openapi-json"),
            }
        };
        BowireOpenApiAdapterRegistry.Register(adapter);

        using var protocol = new BowireRestProtocol();
        var services = await protocol.DiscoverAsync(
            origin, showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal("hits-on-openapi-json", svc.Name);
    }

    [Fact]
    public async Task Probe_Skipped_When_Url_Already_Looks_Like_Spec()
    {
        // The operator already supplied /foo.json — it came back as
        // non-OpenAPI. Probing would be 8 wasted round-trips against the
        // same origin; the LooksLikeSpecUrl guard short-circuits.
        const string origin = "http://probe-test-2.invalid";
        var adapter = new ProgrammableAdapter
        {
            Responses =
            {
                [origin + "/foo.json"] = null,
                // These would otherwise hit if probing wasn't skipped;
                // we assert the adapter never sees them.
                [origin + "/openapi.json"] = StubResult("should-not-be-reached"),
            }
        };
        BowireOpenApiAdapterRegistry.Register(adapter);

        using var protocol = new BowireRestProtocol();
        var services = await protocol.DiscoverAsync(
            origin + "/foo.json", showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
        // Adapter saw exactly one call for this origin — the supplied URL — and no probe.
        var seenForOrigin = adapter.SeenUrls.Where(u => u.StartsWith(origin, StringComparison.Ordinal)).ToList();
        Assert.Single(seenForOrigin);
        Assert.Equal(origin + "/foo.json", seenForOrigin[0]);
    }

    [Fact]
    public async Task Probe_Skipped_When_Supplied_Url_Already_Returns_OpenApi_Doc()
    {
        // First call hits — no probe needed.
        const string origin = "http://probe-test-3.invalid";
        var adapter = new ProgrammableAdapter
        {
            Responses =
            {
                [origin + "/api"] = StubResult("direct"),
            }
        };
        BowireOpenApiAdapterRegistry.Register(adapter);

        using var protocol = new BowireRestProtocol();
        var services = await protocol.DiscoverAsync(
            origin + "/api", showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Single(services);
        var seenForOrigin = adapter.SeenUrls.Where(u => u.StartsWith(origin, StringComparison.Ordinal)).ToList();
        Assert.Single(seenForOrigin);
    }

    [Fact]
    public async Task Probe_Stops_At_First_Hit_Walking_Past_Earlier_Misses()
    {
        // /openapi.json + /openapi/v1.json miss; /swagger/v1/swagger.json wins.
        // The 4th, 5th, … probes must not fire.
        const string origin = "http://probe-test-4.invalid";
        var adapter = new ProgrammableAdapter
        {
            Responses =
            {
                [origin] = null,
                [origin + "/openapi.json"] = null,
                [origin + "/openapi/v1.json"] = null,
                [origin + "/swagger/v1/swagger.json"] = StubResult("via-swagger"),
                // Later probes would steal the win if the sweep didn't stop:
                [origin + "/swagger.json"] = StubResult("should-not-be-reached"),
            }
        };
        BowireOpenApiAdapterRegistry.Register(adapter);

        using var protocol = new BowireRestProtocol();
        var services = await protocol.DiscoverAsync(
            origin, showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal("via-swagger", svc.Name);

        // Sweep stopped after the winning probe — `/swagger.json` was never
        // attempted even though it would have matched too.
        Assert.DoesNotContain(origin + "/swagger.json", adapter.SeenUrls);
    }

    [Fact]
    public async Task All_Probes_Failing_Returns_Empty_And_Does_Not_Throw()
    {
        // Every well-known path returns null; the origin call also returned
        // null. The plugin yields zero services without an exception so
        // sibling protocol plugins can still try the URL.
        const string origin = "http://probe-test-5.invalid";
        var adapter = new ProgrammableAdapter(); // every Response → null
        BowireOpenApiAdapterRegistry.Register(adapter);

        using var protocol = new BowireRestProtocol();
        var services = await protocol.DiscoverAsync(
            origin, showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
        // Initial URL + 8 well-known probes attempted against this origin.
        var seenForOrigin = adapter.SeenUrls.Where(u => u.StartsWith(origin, StringComparison.Ordinal)).ToList();
        Assert.Equal(9, seenForOrigin.Count);
    }

    [Fact]
    public async Task Probe_Sweep_Survives_Adapter_Throwing_On_Individual_Probe()
    {
        // Defensive — a probe path that throws (e.g. server returns a 500
        // that the adapter surfaces as an exception, or the URL trips a
        // YAML parse crash) must not kill the sweep. The next probe still
        // gets a chance.
        const string origin = "http://probe-test-6.invalid";
        var adapter = new ProgrammableAdapter
        {
            Responses =
            {
                [origin] = null,
                [origin + "/openapi.json"] = null,
            },
            ThrowOnUrls = { origin + "/openapi/v1.json" },
            Responses_After_Throw =
            {
                [origin + "/swagger/v1/swagger.json"] = StubResult("after-throw"),
            }
        };
        BowireOpenApiAdapterRegistry.Register(adapter);

        using var protocol = new BowireRestProtocol();
        var services = await protocol.DiscoverAsync(
            origin, showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal("after-throw", svc.Name);
    }

    [Fact]
    public async Task Winning_Probe_Writes_Info_Log_Entry()
    {
        const string origin = "http://probe-test-7.invalid";
        var adapter = new ProgrammableAdapter
        {
            Responses =
            {
                [origin] = null,
                [origin + "/openapi.json"] = StubResult("logs-here"),
            }
        };
        BowireOpenApiAdapterRegistry.Register(adapter);

        using var protocol = new BowireRestProtocol();
        await protocol.DiscoverAsync(
            origin, showInternalServices: false,
            TestContext.Current.CancellationToken);

        var entries = RestProbeLog.Snapshot();
        Assert.Contains(entries, e =>
            e.Level == RestProbeLogLevel.Info
            && e.Message.Contains(origin, StringComparison.Ordinal)
            && e.Message.Contains("/openapi.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_Probes_Failing_Writes_Debug_Log_Entry()
    {
        const string origin = "http://probe-test-8.invalid";
        var adapter = new ProgrammableAdapter(); // everything is null
        BowireOpenApiAdapterRegistry.Register(adapter);

        using var protocol = new BowireRestProtocol();
        await protocol.DiscoverAsync(
            origin, showInternalServices: false,
            TestContext.Current.CancellationToken);

        var entries = RestProbeLog.Snapshot();
        Assert.Contains(entries, e =>
            e.Level == RestProbeLogLevel.Debug
            && e.Message.Contains("no OpenAPI document found", StringComparison.Ordinal)
            && e.Message.Contains(origin, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Subsequent_Discover_Calls_Use_Cached_Resolved_Url()
    {
        // First Discover: probes 4 paths, the 4th hits. Second Discover:
        // the cached resolution should fire the winning URL directly
        // instead of probing again.
        const string origin = "http://probe-test-9.invalid";
        var adapter = new ProgrammableAdapter
        {
            Responses =
            {
                [origin] = null,
                [origin + "/openapi.json"] = null,
                [origin + "/openapi/v1.json"] = null,
                [origin + "/swagger/v1/swagger.json"] = StubResult("via-swagger"),
            }
        };
        BowireOpenApiAdapterRegistry.Register(adapter);

        using var protocol = new BowireRestProtocol();

        // Pass 1 — full probe sweep happens.
        await protocol.DiscoverAsync(
            origin, showInternalServices: false,
            TestContext.Current.CancellationToken);
        var firstPassCount = adapter.SeenUrls
            .Count(u => u.StartsWith(origin, StringComparison.Ordinal));

        // Snapshot the URLs seen before pass 2.
        var before = adapter.SeenUrls.Count;

        // Pass 2 — same origin → fast path kicks in.
        var second = await protocol.DiscoverAsync(
            origin, showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Single(second);
        // Exactly one *new* adapter call on the second pass — the cached well-
        // known URL, not the user-typed origin and not the 8-probe sweep.
        var addedThisPass = adapter.SeenUrls.Skip(before)
            .Where(u => u.StartsWith(origin, StringComparison.Ordinal)).ToList();
        Assert.Single(addedThisPass);
        Assert.Equal(origin + "/swagger/v1/swagger.json", addedThisPass[0]);
        Assert.True(firstPassCount > 1, "First pass should have probed multiple paths.");
    }

    [Fact]
    public async Task Https_Origin_Probes_Only_Https_Urls()
    {
        // Probe sweep must inherit the supplied URL's scheme so an HTTPS-only
        // origin isn't pinged with http:// (and the other way round).
        const string origin = "https://probe-test-10.invalid";
        var adapter = new ProgrammableAdapter
        {
            Responses =
            {
                [origin] = null,
                [origin + "/openapi.json"] = StubResult("https-hit"),
            }
        };
        BowireOpenApiAdapterRegistry.Register(adapter);

        using var protocol = new BowireRestProtocol();
        await protocol.DiscoverAsync(
            origin, showInternalServices: false,
            TestContext.Current.CancellationToken);

        // Every URL targeted at this origin must keep the https scheme.
        var seenForOrigin = adapter.SeenUrls
            .Where(u => u.Contains("probe-test-10.invalid", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(seenForOrigin);
        Assert.All(seenForOrigin, url =>
            Assert.StartsWith("https://", url, StringComparison.Ordinal));
    }

    private static BowireOpenApiDiscoveryResult StubResult(string serviceName)
    {
        var method = new BowireMethodInfo(
            Name: "Get",
            FullName: "GET /",
            ClientStreaming: false, ServerStreaming: false,
            InputType: new BowireMessageInfo("In", "In", []),
            OutputType: new BowireMessageInfo("Out", "Out", []),
            MethodType: "Unary")
        { HttpMethod = "GET", HttpPath = "/" };

        var svc = new BowireServiceInfo(
            Name: serviceName,
            Package: "Stub",
            Methods: [method]);
        return new BowireOpenApiDiscoveryResult(
            SourceUrl: "stub",
            ApiBaseUrl: null,
            Services: [svc],
            RawContent: null);
    }

    /// <summary>
    /// Per-test adapter stub. Maps URL → response so each test can script
    /// the exact probe-sweep outcome it wants to assert on. Also records
    /// every URL the adapter was asked about so tests can verify the
    /// sweep stopped at the right point.
    /// </summary>
    private sealed class ProgrammableAdapter : IBowireOpenApiAdapter
    {
        public int OpenApiLibraryMajorVersion => 99;
        public Dictionary<string, BowireOpenApiDiscoveryResult?> Responses { get; }
            = new(StringComparer.Ordinal);
        public Dictionary<string, BowireOpenApiDiscoveryResult?> Responses_After_Throw { get; }
            = new(StringComparer.Ordinal);
        public HashSet<string> ThrowOnUrls { get; } = new(StringComparer.Ordinal);
        public List<string> SeenUrls { get; } = [];

        private readonly System.Threading.Lock _gate = new();
        private bool _hasThrown;

        public Task<BowireOpenApiDiscoveryResult?> FetchAndDiscoverAsync(
            string docUrl, HttpClient http, CancellationToken ct)
        {
            lock (_gate)
            {
                SeenUrls.Add(docUrl);

                if (ThrowOnUrls.Contains(docUrl))
                {
                    _hasThrown = true;
                    throw new HttpRequestException($"stub-throw at {docUrl}");
                }

                if (_hasThrown && Responses_After_Throw.TryGetValue(docUrl, out var post))
                    return Task.FromResult(post);

                Responses.TryGetValue(docUrl, out var result);
                return Task.FromResult(result);
            }
        }

        public Task<BowireOpenApiDiscoveryResult?> ParseAndDiscoverAsync(
            string content, string sourceLabel, CancellationToken ct) =>
            Task.FromResult<BowireOpenApiDiscoveryResult?>(null);

        public Task<BowireRecording> BuildMockRecordingFromFileAsync(
            string path, CancellationToken ct) =>
            Task.FromResult(new BowireRecording { Id = "stub", Name = "stub" });
    }
}
