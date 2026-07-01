// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;
using Kuestenlogik.Bowire.Semantics.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests.Semantics.Extensions;

/// <summary>
/// Coverage for the field-detector auto-discovery arm of
/// <see cref="BowireExtensionRegistry.Discover"/>. The test project is
/// itself a <c>Kuestenlogik.Bowire.*</c> assembly, so any
/// <see cref="BowireExtensionAttribute"/>-marked detector defined in
/// this file is picked up by the same sweep the framework runs at
/// startup — no synthetic assembly load ceremony needed.
/// </summary>
/// <remarks>
/// <para>
/// Pins three properties: (1) a third-party marker-tagged detector
/// gets discovered, (2) the built-in
/// <see cref="Wgs84CoordinateDetector"/> is discovered through the
/// same mechanism (dogfood proof for the reference implementation),
/// (3) <see cref="BowireOptions.DisableBuiltInDetectors"/> still opts
/// Core built-ins out even though they carry the marker, while
/// third-party marker-tagged detectors remain auto-registered
/// through the DI pipeline.
/// </para>
/// </remarks>
/// <summary>
/// Sample <see cref="IBowireFieldDetector"/> that ships in the test
/// assembly with the <c>[BowireExtension]</c> marker. The registry
/// sweep should discover it exactly like a third-party detector would
/// be discovered in a plugin nupkg dropped next to a host.
/// </summary>
[BowireExtension]
internal sealed class TestMarkerDetector : IBowireFieldDetector
{
    public string Id => "test.marker-detector";
    public IEnumerable<DetectionResult> Detect(in DetectionContext ctx) => [];
}

public sealed class BowireExtensionRegistryDetectorDiscoveryTests
{
    [Fact]
    public void Discover_Finds_Marker_Tagged_Detector_From_Test_Assembly()
    {
        _ = typeof(TestMarkerDetector).Assembly.FullName;
        var registry = BowireExtensionRegistry.Discover();

        var detector = registry.GetFieldDetector("test.marker-detector");
        Assert.NotNull(detector);
        Assert.IsType<TestMarkerDetector>(detector);
    }

    [Fact]
    public void Discover_Finds_BuiltIn_Wgs84_Detector_Through_Marker_Path()
    {
        // Dogfood — the Core built-in carries [BowireExtension] as the
        // reference implementation, so the sweep sees it too.
        _ = typeof(Wgs84CoordinateDetector).Assembly.FullName;
        var registry = BowireExtensionRegistry.Discover();

        var detector = registry.GetFieldDetector("kuestenlogik.wgs84-coordinate");
        Assert.NotNull(detector);
        Assert.IsType<Wgs84CoordinateDetector>(detector);
    }

    [Fact]
    public void FieldDetectors_List_Includes_Both_BuiltIn_And_ThirdParty_Marker_Tagged()
    {
        _ = typeof(TestMarkerDetector).Assembly.FullName;
        _ = typeof(Wgs84CoordinateDetector).Assembly.FullName;
        var registry = BowireExtensionRegistry.Discover();

        Assert.Contains(registry.FieldDetectors, d => d is Wgs84CoordinateDetector);
        Assert.Contains(registry.FieldDetectors, d => d is TestMarkerDetector);
    }

    [Fact]
    public void AddBowire_Registers_Third_Party_Marker_Tagged_Detector_Via_Sweep()
    {
        _ = typeof(TestMarkerDetector).Assembly.FullName;

        var services = new ServiceCollection();
        services.AddBowire();

        using var sp = services.BuildServiceProvider();
        var detectors = sp.GetServices<IBowireFieldDetector>().ToList();

        Assert.Contains(detectors, d => d is TestMarkerDetector);
    }

    [Fact]
    public void AddBowire_Does_Not_Double_Register_BuiltIn_Detector_Marked_And_Hand_Wired()
    {
        // Wgs84 carries the marker AND is explicitly registered by
        // RegisterFrameDetectors. The de-dup guard must keep exactly
        // one instance in the IBowireFieldDetector enumeration.
        var services = new ServiceCollection();
        services.AddBowire();

        using var sp = services.BuildServiceProvider();
        var detectors = sp.GetServices<IBowireFieldDetector>().ToList();

        var wgs84Count = detectors.Count(d => d is Wgs84CoordinateDetector);
        Assert.Equal(1, wgs84Count);
    }

    [Fact]
    public void AddBowire_Preserves_Hand_Registered_Detector_When_Sweep_Would_Also_Add_It()
    {
        // Backward-compat: a host that manually registers a
        // marker-tagged detector before AddBowire keeps its instance —
        // the sweep must skip the type on the second pass.
        var services = new ServiceCollection();
        var handRegistered = new TestMarkerDetector();
        services.AddSingleton<IBowireFieldDetector>(handRegistered);
        services.AddBowire();

        using var sp = services.BuildServiceProvider();
        var detectors = sp.GetServices<IBowireFieldDetector>()
            .OfType<TestMarkerDetector>()
            .ToList();

        Assert.Single(detectors);
        Assert.Same(handRegistered, detectors[0]);
    }

    [Fact]
    public void DisableBuiltInDetectors_Skips_Core_Wgs84_Even_When_Marker_Present()
    {
        _ = typeof(TestMarkerDetector).Assembly.FullName;

        var services = new ServiceCollection();
        services.AddBowire(options => options.DisableBuiltInDetectors = true);

        using var sp = services.BuildServiceProvider();
        var detectors = sp.GetServices<IBowireFieldDetector>().ToList();

        Assert.DoesNotContain(detectors, d => d is Wgs84CoordinateDetector);
        // Third-party marker-tagged detectors are still auto-registered.
        Assert.Contains(detectors, d => d is TestMarkerDetector);
    }
}
