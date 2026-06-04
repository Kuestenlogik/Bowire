// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Telemetry;

/// <summary>
/// Configuration knobs for Bowire's self-telemetry seam (#29). Bound
/// from <c>Bowire:Telemetry</c> by
/// <see cref="BowireTelemetryServiceCollectionExtensions.AddBowireTelemetry"/>;
/// the CLI's <c>--telemetry</c> / <c>--telemetry-strip-method-labels</c>
/// flags feed the same keys via an in-memory configuration overlay.
/// </summary>
/// <remarks>
/// <para>
/// Off by default: a laptop user running <c>bowire --url ...</c>
/// shouldn't see any OTel exporter activity. Operators of a shared
/// multi-tenant install opt in by setting
/// <c>Bowire:Telemetry:Enabled=true</c> (or passing
/// <c>--telemetry</c>) and pointing <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
/// at their collector.
/// </para>
/// <para>
/// Wire endpoint, headers, protocol, &amp;c. come from the standard
/// <c>OTEL_*</c> environment variables -- Bowire doesn't introduce
/// its own re-naming of OTel's vocabulary. That keeps existing
/// Prometheus / Loki / Tempo deploys plug-and-play.
/// </para>
/// </remarks>
public sealed class BowireTelemetryOptions
{
    /// <summary>
    /// Master switch. <c>false</c> (default) skips OTel registration
    /// entirely -- no exporter threads, no listeners. <c>true</c>
    /// wires <c>AddOpenTelemetry().WithMetrics().WithTracing()</c>
    /// against <see cref="BowireTelemetry"/>'s Source and Meter.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Drop the high-cardinality <c>service</c> + <c>method</c>
    /// dimensions from emitted metrics when <c>true</c>. Shared
    /// multi-tenant installs (or anything covered by GDPR / HIPAA /
    /// SOX) almost always want this on -- the per-method histogram
    /// bin's value is way too high a price for the privacy-leakage
    /// risk in regulated environments. Defaults <c>false</c> to keep
    /// the per-method breakdown that operators on private networks
    /// want.
    /// </summary>
    public bool StripMethodLabels { get; set; }
}
