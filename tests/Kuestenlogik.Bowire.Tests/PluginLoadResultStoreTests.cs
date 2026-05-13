// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="PluginLoadResultStore"/> — the static bridge
/// between the CLI-side <c>PluginManager.LoadPlugins</c> and the
/// core's <c>/api/plugins/health</c> endpoint. The bridge keeps the
/// endpoint in core (no dependency on Kuestenlogik.Bowire.Tool) while
/// still letting it surface the loader's structured failure rows.
/// </summary>
public sealed class PluginLoadResultStoreTests
{
    [Fact]
    public void Latest_BeforeFirstPublish_IsEmpty()
    {
        // Reset to empty so this test isn't order-dependent against
        // any earlier test that already published.
        PluginLoadResultStore.Publish(Array.Empty<PluginLoadResult>());

        Assert.Empty(PluginLoadResultStore.Latest);
    }

    [Fact]
    public void Publish_ReplacesLatest()
    {
        var first = new[]
        {
            new PluginLoadResult("Pkg.A", "/path/Pkg.A", PluginLoadStatus.Loaded, null),
        };
        PluginLoadResultStore.Publish(first);
        Assert.Single(PluginLoadResultStore.Latest);
        Assert.Equal("Pkg.A", PluginLoadResultStore.Latest[0].PackageId);

        // Second publish replaces — the store keeps only the latest
        // load run, not a history. Important so a follow-up successful
        // load doesn't leave stale failure rows around forever.
        var second = new[]
        {
            new PluginLoadResult("Pkg.B", "/path/Pkg.B", PluginLoadStatus.Loaded, null),
            new PluginLoadResult("Pkg.C", "/path/Pkg.C", PluginLoadStatus.ContractMajorMismatch,
                "Plugin references 1.2 but host is 1.3."),
        };
        PluginLoadResultStore.Publish(second);

        Assert.Equal(2, PluginLoadResultStore.Latest.Count);
        Assert.DoesNotContain(PluginLoadResultStore.Latest, r => r.PackageId == "Pkg.A");
        Assert.Contains(PluginLoadResultStore.Latest, r => r.PackageId == "Pkg.B");
        Assert.Contains(PluginLoadResultStore.Latest, r => r.PackageId == "Pkg.C"
            && r.Status == PluginLoadStatus.ContractMajorMismatch);
    }

    [Fact]
    public void Publish_NullResults_StoredAsEmpty()
    {
        // Defensive: a null hand-over shouldn't NRE the endpoint. The
        // store collapses it to Array.Empty<> so /api/plugins/health
        // returns an empty array instead of 500ing.
        PluginLoadResultStore.Publish(null!);

        Assert.NotNull(PluginLoadResultStore.Latest);
        Assert.Empty(PluginLoadResultStore.Latest);
    }
}
