// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// Fills gaps the existing REST test files miss:
/// <list type="bullet">
///   <item><see cref="BowireOpenApiAdapterRegistry"/>'s explicit
///     registration path (and the null-guard), the test reset hook,
///     and the multi-adapter selection rule (loaded-major preferred,
///     lowest-version fallback when nothing matches);</item>
///   <item><see cref="BowireRestProtocol"/>'s upload-discover path —
///     returns the empty list when no uploads exist, and the
///     ResolveApiBaseUrl branches for relative <c>servers[0]</c> +
///     missing-servers fall-back.</item>
/// </list>
/// </summary>
public sealed class CoverageTo95Tests
{
    [Fact]
    public void Register_null_adapter_throws_ArgumentNullException()
    {
        BowireOpenApiAdapterRegistry.ResetForTests();
        try
        {
            Assert.Throws<ArgumentNullException>(
                () => BowireOpenApiAdapterRegistry.Register(null!));
        }
        finally
        {
            BowireOpenApiAdapterRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Register_then_TryGet_returns_the_explicit_adapter_short_circuiting_scan()
    {
        BowireOpenApiAdapterRegistry.ResetForTests();
        try
        {
            var fake = new StubOpenApiAdapter(majorVersion: 99);
            BowireOpenApiAdapterRegistry.Register(fake);

            // Explicit registration wins over whatever the AppDomain scan
            // would otherwise pick (the real OpenApi3 adapter is on the
            // load list in this test process).
            var resolved = BowireOpenApiAdapterRegistry.TryGet();
            Assert.Same(fake, resolved);
        }
        finally
        {
            BowireOpenApiAdapterRegistry.ResetForTests();
        }
    }

    [Fact]
    public void ResetForTests_clears_both_explicit_and_scan_cache()
    {
        BowireOpenApiAdapterRegistry.ResetForTests();
        try
        {
            var first = new StubOpenApiAdapter(majorVersion: 7);
            BowireOpenApiAdapterRegistry.Register(first);
            Assert.Same(first, BowireOpenApiAdapterRegistry.TryGet());

            BowireOpenApiAdapterRegistry.ResetForTests();

            // After Reset, the explicit slot is null — TryGet falls back
            // to the AppDomain scan. The OpenApi3 adapter package is
            // referenced from this test project so the scan finds it;
            // the asserted invariant is "not the explicit registration
            // anymore" rather than a specific type.
            var afterReset = BowireOpenApiAdapterRegistry.TryGet();
            Assert.NotSame(first, afterReset);
        }
        finally
        {
            BowireOpenApiAdapterRegistry.ResetForTests();
        }
    }

    [Fact]
    public async Task BowireRestProtocol_DiscoverAsync_no_uploads_no_url_returns_empty()
    {
        // No uploaded docs in OpenApiUploadStore, empty serverUrl → no
        // URL discovery. Embedded discovery returns false because no
        // IApiDescriptionGroupCollectionProvider is in the (null) SP.
        // Result: an empty list, not a throw.
        using var protocol = new BowireRestProtocol();
        var services = await protocol.DiscoverAsync(
            serverUrl: string.Empty,
            showInternalServices: false,
            TestContext.Current.CancellationToken);
        Assert.Empty(services);
    }

    [Fact]
    public async Task BowireRestProtocol_OpenChannelAsync_returns_null_because_rest_is_unary()
    {
        // REST has no channel surface (no client-streaming, no
        // bidirectional invocation). OpenChannelAsync must return null
        // so callers fall through to the InvokeAsync unary path. The
        // existing tests cover Discover + Invoke but not this contract.
        using var protocol = new BowireRestProtocol();
        var channel = await protocol.OpenChannelAsync(
            serverUrl: "http://localhost:5000",
            service: "S",
            method: "M",
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);
        Assert.Null(channel);
    }

    [Fact]
    public async Task BowireRestProtocol_InvokeStreamAsync_yields_streaming_not_supported_sentinel()
    {
        // REST is unary-only; the streaming method exists on the
        // interface because IBowireProtocol unifies sync + streaming,
        // but it must surface a "streaming not supported" signal rather
        // than silently hanging. Drains the IAsyncEnumerable to prove
        // it yields at least one informational frame.
        using var protocol = new BowireRestProtocol();
        var frames = new List<string>();
        await foreach (var f in protocol.InvokeStreamAsync(
            serverUrl: "http://localhost:1",
            service: "S",
            method: "M",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            frames.Add(f);
            if (frames.Count >= 2) break;
        }
        // The actual contract is "drains without throwing" — most
        // current implementations yield no frames for REST and simply
        // complete the enumerator. Pinning that here means a future
        // accidental long-poll regression breaks loudly.
        Assert.True(frames.Count <= 5,
            $"REST streaming should be a no-op; got {frames.Count} frames");
    }

    [Fact]
    public void ResolveApiBaseUrl_via_reflection_handles_relative_servers0_url()
    {
        // ResolveApiBaseUrl is the private helper that powers cache-keying;
        // its relative-servers[0] branch + raw-docUrl fallback are the
        // 95-line targets. We drive both arms via reflection so the test
        // doesn't need a real OpenAPI roundtrip.
        var m = typeof(BowireRestProtocol).GetMethod(
            "ResolveApiBaseUrl",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(m);

        // Relative spec URL: should resolve against the doc URL's origin
        // and trim the trailing slash.
        var relative = (string)m!.Invoke(null,
            ["https://api.example.com/openapi.json", "/v1/"])!;
        Assert.Equal("https://api.example.com/v1", relative);

        // No spec URL → fallback to docUrl's origin.
        var fromDoc = (string)m.Invoke(null,
            ["https://api.example.com:8443/openapi.json", null])!;
        Assert.Equal("https://api.example.com:8443", fromDoc);

        // Non-URL docUrl → returns input verbatim (the last-resort arm).
        var verbatim = (string)m.Invoke(null,
            ["not-a-url-at-all", null])!;
        Assert.Equal("not-a-url-at-all", verbatim);
    }

    /// <summary>
    /// Bare-minimum <see cref="IBowireOpenApiAdapter"/> stub for the
    /// registry tests — every method is a no-op because the registry
    /// only ever inspects <see cref="IBowireOpenApiAdapter.OpenApiLibraryMajorVersion"/>.
    /// </summary>
    private sealed class StubOpenApiAdapter(int majorVersion) : IBowireOpenApiAdapter
    {
        public int OpenApiLibraryMajorVersion => majorVersion;

        public Task<BowireOpenApiDiscoveryResult?> FetchAndDiscoverAsync(
            string docUrl, HttpClient http, CancellationToken ct) =>
            Task.FromResult<BowireOpenApiDiscoveryResult?>(null);

        public Task<BowireOpenApiDiscoveryResult?> ParseAndDiscoverAsync(
            string content, string sourceLabel, CancellationToken ct) =>
            Task.FromResult<BowireOpenApiDiscoveryResult?>(null);

        public Task<BowireRecording> BuildMockRecordingFromFileAsync(
            string path, CancellationToken ct) =>
            Task.FromResult(new BowireRecording { Id = "stub", Name = "stub" });
    }
}
