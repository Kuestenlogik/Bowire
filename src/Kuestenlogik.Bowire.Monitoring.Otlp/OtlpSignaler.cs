// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Kuestenlogik.Bowire.Monitoring.Otlp;

/// <summary>
/// The OTLP "signal" channel — exports the <c>bowire.monitoring.*</c> probe
/// metrics (per-run duration + outcome counter) to an OpenTelemetry collector.
/// Unlike Slack / PagerDuty, the work happens at construction: a
/// <see cref="MeterProvider"/> subscribing to <see cref="MonitoringTelemetry.Meter"/>
/// with the OTLP exporter is stood up and kept alive for the process, so metrics
/// flow continuously. A pass↔fail transition is already visible in the outcome
/// counter, so <see cref="DeliverAsync"/> is a no-op (per-transition OTLP log
/// records are a follow-up).
/// </summary>
public sealed class OtlpSignaler : ISignaler, IDisposable
{
    private readonly MeterProvider _meterProvider;

    public OtlpSignaler(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(MonitoringTelemetry.MeterName)
            .AddOtlpExporter(options => options.Endpoint = endpoint)
            .Build();
    }

    /// <inheritdoc/>
    /// <remarks>No-op: the transition is already carried by the outcome counter
    /// the metrics pipeline exports.</remarks>
    public Task DeliverAsync(SignalEvent signal, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Flush + tear down the exporter.</summary>
    public void Dispose() => _meterProvider.Dispose();
}

/// <summary>Contributes the <c>otlp</c> <c>--signal</c> scheme.</summary>
public sealed class OtlpSignalerFactory : ISignalerFactory
{
    /// <inheritdoc/>
    public string Scheme => "otlp";

    /// <inheritdoc/>
    public ISignaler Create(string argument)
    {
        if (!Uri.TryCreate(argument, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new SignalerConfigException(
                "otlp signal needs a collector endpoint URL: --signal otlp:http://collector:4317");
        }
        return new OtlpSignaler(endpoint);
    }
}
