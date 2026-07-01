// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics.Detectors;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests.Semantics.Detectors;

/// <summary>
/// DI-surface coverage for the Phase-2 detector wiring inside
/// <see cref="BowireServiceCollectionExtensions"/>: the five built-in
/// detectors registered as <see cref="IBowireFieldDetector"/>, the
/// <see cref="IFrameProber"/> singleton, and the
/// <see cref="BowireOptions.DisableBuiltInDetectors"/> opt-out.
/// </summary>
public sealed class BowireServiceCollectionDetectorsTests
{
    [Fact]
    public void AddBowire_Registers_Five_Built_In_Detectors()
    {
        var services = new ServiceCollection();
        services.AddBowire();

        using var sp = services.BuildServiceProvider();
        var detectors = sp.GetServices<IBowireFieldDetector>().ToList();

        // The five built-ins are all present. Total count may exceed
        // five once auto-discovery picks up marker-tagged detectors
        // from sibling test assemblies — this test pins the built-in
        // set specifically, not the enumeration size.
        var coreAssembly = typeof(Wgs84CoordinateDetector).Assembly;
        var builtIns = detectors.Where(d => d.GetType().Assembly == coreAssembly).ToList();
        Assert.Equal(5, builtIns.Count);
        Assert.Contains(builtIns, d => d is Wgs84CoordinateDetector);
        Assert.Contains(builtIns, d => d is GeoJsonPointDetector);
        Assert.Contains(builtIns, d => d is ImageBytesDetector);
        Assert.Contains(builtIns, d => d is AudioBytesDetector);
        Assert.Contains(builtIns, d => d is TimestampDetector);
    }

    [Fact]
    public void AddBowire_Registers_FrameProber_As_Singleton()
    {
        var services = new ServiceCollection();
        services.AddBowire();

        using var sp = services.BuildServiceProvider();
        var a = sp.GetRequiredService<IFrameProber>();
        var b = sp.GetRequiredService<IFrameProber>();

        Assert.Same(a, b);
    }

    [Fact]
    public void DisableBuiltInDetectors_Skips_The_Five_Registrations()
    {
        var services = new ServiceCollection();
        services.AddBowire(options => options.DisableBuiltInDetectors = true);

        using var sp = services.BuildServiceProvider();
        var detectors = sp.GetServices<IBowireFieldDetector>().ToList();

        // Third-party [BowireExtension]-marker-tagged detectors from
        // sibling assemblies still auto-discover through the sweep
        // (that's the point of the extension-registry integration);
        // this assertion pins the narrow contract that
        // DisableBuiltInDetectors owns — Core's five built-ins are
        // absent, even the ones that carry the marker as reference
        // implementations for the auto-discovery mechanism.
        var coreAssembly = typeof(Wgs84CoordinateDetector).Assembly;
        var fromCore = detectors.Where(d => d.GetType().Assembly == coreAssembly).ToList();
        Assert.Empty(fromCore);
    }

    [Fact]
    public void DisableBuiltInDetectors_Still_Registers_FrameProber()
    {
        // Even with no detectors the prober is registered — the host
        // can layer its own detector set on later.
        var services = new ServiceCollection();
        services.AddBowire(options => options.DisableBuiltInDetectors = true);

        using var sp = services.BuildServiceProvider();
        var prober = sp.GetRequiredService<IFrameProber>();

        Assert.NotNull(prober);
    }

    [Fact]
    public void Built_In_Detectors_Have_Stable_Ids()
    {
        var services = new ServiceCollection();
        services.AddBowire();

        using var sp = services.BuildServiceProvider();
        var detectors = sp.GetServices<IBowireFieldDetector>().ToList();
        var ids = detectors.Select(d => d.Id).ToHashSet();

        Assert.Contains("kuestenlogik.wgs84-coordinate", ids);
        Assert.Contains("kuestenlogik.geojson-point", ids);
        Assert.Contains("kuestenlogik.image-bytes", ids);
        Assert.Contains("kuestenlogik.audio-bytes", ids);
        Assert.Contains("kuestenlogik.timestamp", ids);
    }
}
