// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for <see cref="BowireDiscoveryProbe"/> (#534) — the shared
/// registry fan-out behind <c>/api/services</c>, <c>bowire discover</c>
/// and the <c>bowire.discover</c> MCP tool.
/// <para>
/// The behaviour worth pinning is the <em>attempt</em> bookkeeping, not
/// the service merge: the bug this replaced only reported plugins that
/// threw, so "the plugin ran and found nothing" — by far the most common
/// outcome, and the one that explains an empty sidebar — was invisible.
/// </para>
/// </summary>
public class BowireDiscoveryProbeTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunAsync_Records_One_Attempt_Per_Plugin_Even_When_All_Succeed()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new FakeProtocol("a", "Alpha", services: 2));
        registry.Register(new FakeProtocol("b", "Beta", services: 1));
        registry.Register(new FakeProtocol("c", "Gamma", services: 3));

        var result = await BowireDiscoveryProbe.RunAsync(
            registry, "https://api.example.com", pluginHint: null,
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Attempts.Count);
        Assert.All(result.Attempts, a => Assert.Equal(BowireDiscoveryAttempt.OutcomeOk, a.Outcome));
        Assert.Equal(6, result.Services.Count);
    }

    [Fact]
    public async Task RunAsync_Reports_Empty_Not_Ok_For_A_Zero_Result_Plugin()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new FakeProtocol("quiet", "Quiet", services: 0));

        var result = await BowireDiscoveryProbe.RunAsync(
            registry, "https://api.example.com", pluginHint: null,
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        var attempt = Assert.Single(result.Attempts);
        Assert.Equal(BowireDiscoveryAttempt.OutcomeEmpty, attempt.Outcome);
        Assert.Equal(0, attempt.ServicesFound);
        Assert.Equal("returned no services", attempt.Message);
    }

    [Fact]
    public async Task RunAsync_Reports_Error_With_The_Raw_Exception_Message()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new ThrowingProtocol("grpc", "gRPC", "connection refused"));
        registry.Register(new FakeProtocol("rest", "REST", services: 0));

        var result = await BowireDiscoveryProbe.RunAsync(
            registry, "https://api.example.com", pluginHint: null,
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Attempts.Count);
        var failed = Assert.Single(result.Attempts, a => a.Outcome == BowireDiscoveryAttempt.OutcomeError);
        Assert.Equal("gRPC", failed.Plugin);
        Assert.Equal("grpc", failed.PluginId);
        // No "gRPC: " prefix — the record already carries the plugin name,
        // and a prefixed message renders as "gRPC — gRPC: connection refused".
        Assert.Equal("connection refused", failed.Message);
        // The plugin that ran cleanly is still listed. That is the whole
        // point: before #534 only the throwing one appeared.
        Assert.Contains(result.Attempts, a => a.Outcome == BowireDiscoveryAttempt.OutcomeEmpty);
    }

    [Fact]
    public async Task RunAsync_Maps_A_Probe_That_Blows_The_Ceiling_To_Timeout()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new HangingProtocol("slow", "Slow"));

        var result = await BowireDiscoveryProbe.RunAsync(
            registry, "https://api.example.com", pluginHint: null,
            showInternalServices: false,
            perProbeCeiling: TimeSpan.FromMilliseconds(120),
            ct: TestContext.Current.CancellationToken);

        var attempt = Assert.Single(result.Attempts);
        Assert.Equal(BowireDiscoveryAttempt.OutcomeTimeout, attempt.Outcome);
        Assert.Contains("ceiling", attempt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Narrows_The_Fanout_To_The_Hinted_Plugin()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new FakeProtocol("rest", "REST", services: 1));
        registry.Register(new ThrowingProtocol("grpc", "gRPC", "boom"));

        var result = await BowireDiscoveryProbe.RunAsync(
            registry, "https://api.example.com", pluginHint: "REST",
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        var attempt = Assert.Single(result.Attempts);
        Assert.Equal("rest", attempt.PluginId);
    }

    [Fact]
    public async Task RunAsync_Returns_No_Attempts_When_The_Hint_Matches_Nothing()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new FakeProtocol("rest", "REST", services: 1));

        var result = await BowireDiscoveryProbe.RunAsync(
            registry, "https://api.example.com", pluginHint: "nosuchplugin",
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Attempts);
        Assert.Empty(result.Services);
    }

    [Fact]
    public async Task RunAsync_Populates_DurationMs()
    {
        // Guards the discarded-elapsedMs bug the old inline fanout had:
        // it computed the elapsed time and then threw it away with
        // `_ = elapsedMs;`, so nothing downstream could show a probe cost.
        var registry = new BowireProtocolRegistry();
        registry.Register(new SlowishProtocol("rest", "REST"));

        var result = await BowireDiscoveryProbe.RunAsync(
            registry, "https://api.example.com", pluginHint: null,
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        var attempt = Assert.Single(result.Attempts);
        Assert.True(attempt.DurationMs >= 10,
            $"expected a measured duration, got {attempt.DurationMs} ms");
    }

    [Fact]
    public async Task RunAsync_Tags_Discovered_Services_With_Source_And_Origin()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new FakeProtocol("rest", "REST", services: 1));

        var result = await BowireDiscoveryProbe.RunAsync(
            registry, "https://api.example.com", pluginHint: null,
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        var svc = Assert.Single(result.Services);
        Assert.Equal("rest", svc.Source);
        Assert.Equal("https://api.example.com", svc.OriginUrl);
    }

    [Fact]
    public async Task RunAsync_Rejects_A_Null_Registry()
        => await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await BowireDiscoveryProbe.RunAsync(
                null!, "https://api.example.com", pluginHint: null,
                showInternalServices: false, perProbeCeiling: Ceiling,
                ct: TestContext.Current.CancellationToken));

    // ---- stubs ----

    private sealed class FakeProtocol(string id, string name, int services) : StubProtocolBase(id, name)
    {
        public override Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(Enumerable.Range(0, services)
                .Select(i => new BowireServiceInfo($"{Id}.Service{i}", Id, []))
                .ToList());
    }

    private sealed class ThrowingProtocol(string id, string name, string message)
        : StubProtocolBase(id, name)
    {
        public override Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class HangingProtocol(string id, string name) : StubProtocolBase(id, name)
    {
        public override async Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }
    }

    private sealed class SlowishProtocol(string id, string name) : StubProtocolBase(id, name)
    {
        public override async Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
        {
            await Task.Delay(25, ct);
            return [];
        }
    }

    private abstract class StubProtocolBase(string id, string name) : IBowireProtocol
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string IconSvg => "<svg/>";

        public abstract Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default);

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));

        // Non-async so there is no CS1998 to suppress — the repo bans
        // pragma suppressions, and an empty sequence needs no iterator.
        public IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default) => Task.FromResult<IBowireChannel?>(null);
    }
}
