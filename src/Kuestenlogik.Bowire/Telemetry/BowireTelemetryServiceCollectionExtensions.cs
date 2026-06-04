// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Kuestenlogik.Bowire.Telemetry;

/// <summary>
/// DI helpers for Bowire's self-telemetry seam (#29). Idempotent --
/// re-calling <see cref="AddBowireTelemetry"/> is a no-op rather than
/// registering a duplicate. Embedded hosts that already wire OTel
/// themselves can skip these helpers entirely and just add Bowire's
/// Source / Meter to their existing pipeline:
/// <code>
///   builder.Services.AddOpenTelemetry()
///     .WithMetrics(m => m.AddMeter(BowireTelemetry.MeterName))
///     .WithTracing(t  => t.AddSource(BowireTelemetry.ActivityName));
/// </code>
/// </summary>
public static class BowireTelemetryServiceCollectionExtensions
{
    // Static readonly array so the view registration doesn't allocate
    // a fresh array per call -- CA1861 wants this for repeated paths.
    private static readonly string[] StrippedTagKeys = ["protocol", "outcome"];

    /// <summary>
    /// Bind <see cref="BowireTelemetryOptions"/> from
    /// <paramref name="configuration"/> (section <c>Bowire:Telemetry</c>)
    /// and wire the OTel SDK against
    /// <see cref="BowireTelemetry.MeterName"/> +
    /// <see cref="BowireTelemetry.ActivityName"/> when
    /// <see cref="BowireTelemetryOptions.Enabled"/> resolves to
    /// <c>true</c>. The OTLP exporter reads <c>OTEL_EXPORTER_OTLP_*</c>
    /// from the environment -- Bowire doesn't paper over OTel's own
    /// configuration vocabulary.
    /// </summary>
    public static IServiceCollection AddBowireTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<BowireTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var opts = new BowireTelemetryOptions();
        configuration.GetSection("Bowire:Telemetry").Bind(opts);
        configure?.Invoke(opts);

        services.TryAddSingleton(opts);

        if (!opts.Enabled)
        {
            // Telemetry off: the static ActivitySource + Meter still
            // exist (their no-op fast path costs near-zero) but no
            // SDK pipeline is wired up. Operators that flip the
            // switch later need a host restart -- that's the standard
            // OTel registration contract.
            return services;
        }

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: BowireTelemetry.SourceName,
                serviceVersion: typeof(BowireTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                serviceInstanceId: Environment.MachineName);

        services.AddOpenTelemetry()
            .ConfigureResource(rb => rb.AddService(BowireTelemetry.SourceName))
            .WithMetrics(builder =>
            {
                builder
                    .SetResourceBuilder(resource)
                    .AddMeter(BowireTelemetry.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter();
                if (opts.StripMethodLabels)
                {
                    // Drop the per-service / per-method dimensions before
                    // export so a leak via the wire never carries them.
                    builder.AddView("bowire.invoke.duration", new MetricStreamConfiguration
                    {
                        TagKeys = StrippedTagKeys,
                    });
                    builder.AddView("bowire.invoke.count", new MetricStreamConfiguration
                    {
                        TagKeys = StrippedTagKeys,
                    });
                }
            })
            .WithTracing(builder => builder
                .SetResourceBuilder(resource)
                .AddSource(BowireTelemetry.ActivityName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());

        return services;
    }
}
