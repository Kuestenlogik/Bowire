// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The "was I explicitly asked for?" marker.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BowireServerUrl.Parse"/> consumes the <c>hint@</c> prefix
/// before any plugin is reached, so a plugin cannot see it. Plugins that
/// must only act when pinned — a bundled-schema discovery, an ad-hoc
/// separate-target fallback — need that bit delivered some other way.
/// </para>
/// <para>
/// It is delivered as discovery <em>metadata</em>, merged in one place:
/// <see cref="BowireDiscoveryProbe.RunAsync"/>, which every surface goes
/// through. Two earlier arrangements failed, and both failures are pinned
/// below. A marker appended to the URL leaks to the operator's own server
/// unless the receiving plugin strips it, and only the plugins that owned a
/// marker did. Merging at the call site instead left four of five callers —
/// the CLI and the MCP tool among them — silently without the hint.
/// </para>
/// </remarks>
public sealed class PluginHintMarkerTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    [Fact]
    public void ParseStripsTheHint_WhichIsWhyTheMarkerExists()
    {
        // The premise. If this ever changed, a plugin could read the
        // prefix and none of the rest would be needed.
        var (hint, url) = BowireServerUrl.Parse("tacticalapi@http://localhost:5191");

        Assert.Equal("tacticalapi", hint);
        Assert.Equal("http://localhost:5191", url);
        Assert.DoesNotContain("tacticalapi@", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinnedPluginIsToldItWasNamed()
    {
        var registry = new BowireProtocolRegistry();
        var sse = new RecordingProtocol("sse");
        registry.Register(sse);

        await BowireDiscoveryProbe.RunAsync(
            registry, "http://host/stream", pluginHint: "sse",
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("sse", sse.SeenMetadata?[BowireMetadataKeys.PluginHint]);
    }

    [Fact]
    public async Task TheMarkerNeverTouchesTheUrl()
    {
        // The regression this file exists to prevent: with the marker on the
        // URL, a `rest@`/`graphql@`/`odata@` discovery dialled the operator's
        // own server with `?__bowirePluginHint=…` appended, because no
        // stripper stood between the endpoint and the wire.
        var registry = new BowireProtocolRegistry();
        var rest = new RecordingProtocol("rest");
        registry.Register(rest);

        await BowireDiscoveryProbe.RunAsync(
            registry, "http://host/api?page=2", pluginHint: "rest",
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("http://host/api?page=2", rest.SeenUrl);
        Assert.DoesNotContain(
            BowireMetadataKeys.PluginHint, rest.SeenUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnpinnedProbeCarriesNoMarkerAtAll()
    {
        // Absent, not empty-string: a plugin asks `TryGetValue`, and a key
        // that is always present would make every fan-out look pinned.
        var registry = new BowireProtocolRegistry();
        var sse = new RecordingProtocol("sse");
        registry.Register(sse);

        await BowireDiscoveryProbe.RunAsync(
            registry, "http://host/stream", pluginHint: null,
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        Assert.False(sse.SeenMetadata?.ContainsKey(BowireMetadataKeys.PluginHint) ?? false);
    }

    [Fact]
    public async Task TheMarkerJoinsExistingMetadataRatherThanReplacingIt()
    {
        // A pinned gRPC discovery still needs its descriptor set: the two
        // ride the same bag, and the merge must not drop the other.
        var registry = new BowireProtocolRegistry();
        var grpc = new RecordingProtocol("grpc");
        registry.Register(grpc);
        var caller = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BowireMetadataKeys.GrpcDescriptorSet] = "/tmp/set.binpb",
        };

        await BowireDiscoveryProbe.RunAsync(
            registry, "http://host", pluginHint: "grpc",
            showInternalServices: false, perProbeCeiling: Ceiling,
            metadata: caller,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("/tmp/set.binpb", grpc.SeenMetadata?[BowireMetadataKeys.GrpcDescriptorSet]);
        Assert.Equal("grpc", grpc.SeenMetadata?[BowireMetadataKeys.PluginHint]);
        // The caller's own dictionary is not mutated — it may be a shared
        // or frozen bag, and a probe that edited it would leak the hint
        // into the next, unrelated, discovery.
        Assert.False(caller.ContainsKey(BowireMetadataKeys.PluginHint));
    }

    [Fact]
    public async Task TheHintReachesThePluginWithTheCaseTheCallerTyped()
    {
        // Plugins compare case-insensitively (the router that matched the
        // hint does too), so the probe forwards the id verbatim rather
        // than normalising it and inventing a second convention.
        var registry = new BowireProtocolRegistry();
        var signalr = new RecordingProtocol("signalr");
        registry.Register(signalr);

        await BowireDiscoveryProbe.RunAsync(
            registry, "http://host/hub", pluginHint: "SignalR",
            showInternalServices: false, perProbeCeiling: Ceiling,
            ct: TestContext.Current.CancellationToken);

        var seen = signalr.SeenMetadata?[BowireMetadataKeys.PluginHint];
        Assert.Equal("SignalR", seen);
        Assert.Equal("signalr", seen, ignoreCase: true);
    }

    /// <summary>
    /// Records what the probe handed it and finds nothing — the questions
    /// here are all about what reaches the plugin, not what comes back.
    /// </summary>
    /// <remarks>
    /// Overrides the metadata overload deliberately. The interface default
    /// forwards to the three-argument one and drops the bag, so a stub that
    /// took the default would record <c>null</c> for every case and pass
    /// this file whatever the probe did.
    /// </remarks>
    private sealed class RecordingProtocol(string id) : IBowireProtocol
    {
        public string Id { get; } = id;
        public string Name { get; } = id;
        public string IconSvg => "<svg/>";

        public string? SeenUrl { get; private set; }
        public IReadOnlyDictionary<string, string>? SeenMetadata { get; private set; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => DiscoverAsync(serverUrl, showInternalServices, null, ct);

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices,
            IReadOnlyDictionary<string, string>? metadata, CancellationToken ct = default)
        {
            SeenUrl = serverUrl;
            SeenMetadata = metadata;
            return Task.FromResult(new List<BowireServiceInfo>());
        }

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
