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

        Assert.Equal(5, detectors.Count);
        Assert.Contains(detectors, d => d is Wgs84CoordinateDetector);
        Assert.Contains(detectors, d => d is GeoJsonPointDetector);
        Assert.Contains(detectors, d => d is ImageBytesDetector);
        Assert.Contains(detectors, d => d is AudioBytesDetector);
        Assert.Contains(detectors, d => d is TimestampDetector);
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

        Assert.Empty(detectors);
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
