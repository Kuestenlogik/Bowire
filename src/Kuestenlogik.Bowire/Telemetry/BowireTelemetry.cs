// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Kuestenlogik.Bowire.Telemetry;

/// <summary>
/// Canonical OpenTelemetry surface for Bowire (issue #29). A single
/// <see cref="ActivitySource"/> and a single <see cref="Meter"/> with
/// the stable name <c>Kuestenlogik.Bowire</c> so every consumer --
/// host pipelines that pick up Bowire's traces / metrics via
/// <c>WithTracing().AddSource(...)</c> and
/// <c>WithMetrics().AddMeter(...)</c> -- references one name and
/// receives every Bowire-domain signal we ever emit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Names are stable, even when Bowire isn't.</b> The
/// <see cref="ActivityName"/> / <see cref="MeterName"/> constants
/// don't change between minor releases -- if they ever do we'll cut
/// a major. Embedded hosts and Grafana dashboards can pin against
/// them without breakage.
/// </para>
/// <para>
/// <b>Always-on, always-cheap.</b> The instruments are created at
/// module-load time regardless of whether OTel is wired up. With no
/// listener, the SDK's no-op fast path keeps the per-call cost at a
/// single virtual call + a null check. Opt-in happens at the host's
/// <c>AddBowireTelemetry</c> call: the SDK starts collecting when a
/// listener attaches.
/// </para>
/// </remarks>
public static class BowireTelemetry
{
    /// <summary>Source name used for both the <see cref="ActivitySource"/> and the <see cref="Meter"/>.</summary>
    public const string SourceName = "Kuestenlogik.Bowire";

    // Renamed so the ActivitySource / Meter properties below can stay
    // short while the constant still tells callers where to look.
    public const string ActivityName = SourceName;
    public const string MeterName = SourceName;

    private static readonly string Version = typeof(BowireTelemetry).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BowireTelemetry).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>
    /// Shared <see cref="ActivitySource"/> Bowire emits all its spans
    /// through. Embedded hosts add <c>AddSource(<see cref="SourceName"/>)</c>
    /// to their existing <c>WithTracing()</c> configuration and the
    /// Bowire-side spans flow into the host's exporter without further
    /// wiring.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivityName, Version);

    /// <summary>
    /// Shared <see cref="Meter"/> Bowire emits all its metrics
    /// through. Embedded hosts add <c>AddMeter(<see cref="SourceName"/>)</c>
    /// to their existing <c>WithMetrics()</c> configuration.
    /// </summary>
    public static readonly Meter Meter = new(MeterName, Version);

    // ----- domain instruments -----
    // Add new ones here; consumers reference them through this class
    // so the metric names stay grouped and discoverable in one place.

    /// <summary><c>bowire.invoke.count</c> -- one increment per workbench invoke.</summary>
    public static readonly Counter<long> InvokeCount =
        Meter.CreateCounter<long>("bowire.invoke.count",
            unit: "{invoke}",
            description: "Total workbench invocations across every protocol.");

    /// <summary><c>bowire.invoke.duration</c> -- millisecond histogram of invoke latency.</summary>
    public static readonly Histogram<double> InvokeDuration =
        Meter.CreateHistogram<double>("bowire.invoke.duration",
            unit: "ms",
            description: "Wall-clock duration of a workbench invocation, from request build to response render.");

    /// <summary><c>bowire.discover.count</c> -- discovery passes against backends.</summary>
    public static readonly Counter<long> DiscoverCount =
        Meter.CreateCounter<long>("bowire.discover.count",
            unit: "{discover}",
            description: "Discovery passes Bowire ran against a target URL.");

    /// <summary><c>bowire.plugin.load</c> -- one increment per protocol-plugin load attempt at startup.</summary>
    public static readonly Counter<long> PluginLoad =
        Meter.CreateCounter<long>("bowire.plugin.load",
            unit: "{load}",
            description: "Plugin load attempts -- success or failure encoded on the `outcome` dimension.");

    /// <summary><c>bowire.mock.requests</c> -- request entries against UI-started mocks.</summary>
    public static readonly Counter<long> MockRequests =
        Meter.CreateCounter<long>("bowire.mock.requests",
            unit: "{request}",
            description: "Inbound requests against UI-started mock instances. Pairs with #57's per-mock log.");
}
