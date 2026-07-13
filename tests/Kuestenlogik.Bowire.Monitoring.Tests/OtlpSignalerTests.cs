// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Monitoring.Otlp;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for the OTLP signaler package — factory validation + the no-op
/// transition path. The exporter targets a dead loopback port so no collector
/// is needed (nothing is exported during the test; dispose flushes to a refused
/// connection).
/// </summary>
public sealed class OtlpSignalerTests
{
    // Nothing listens here — the exporter builds fine and fails fast on flush.
    private const string DeadEndpoint = "http://127.0.0.1:4317";

    [Fact]
    public void Registry_discovers_the_otlp_scheme()
    {
        Assert.Contains("otlp", SignalerRegistry.Discover().Schemes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_rejects_a_non_url_argument()
    {
        Assert.Throws<SignalerConfigException>(() => new OtlpSignalerFactory().Create("not-a-url"));
    }

    [Fact]
    public void Factory_builds_an_otlp_signaler_for_an_endpoint()
    {
        using var signaler = (OtlpSignaler)new OtlpSignalerFactory().Create(DeadEndpoint);
        Assert.IsType<OtlpSignaler>(signaler);
    }

    [Fact]
    public async Task Deliver_is_a_noop_metrics_carry_the_transition()
    {
        using var signaler = (OtlpSignaler)new OtlpSignalerFactory().Create(DeadEndpoint);
        // The transition is already in the outcome counter; delivering is a no-op
        // and must not throw.
        await signaler.DeliverAsync(TestSignals.Event(ProbeTransition.ToFailing, ProbeResult.Fail), TestContext.Current.CancellationToken);
    }
}
