// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using Kuestenlogik.Bowire.Monitoring.PagerDuty;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for the PagerDuty signaler package — triggers on a failing edge,
/// resolves on recovery (Events API v2), with the same dedup key.
/// </summary>
public sealed class PagerDutySignalerTests
{
    private static readonly Uri Endpoint = new("https://pd.test/enqueue");

    private static PagerDutySignaler NewSignaler(HttpClient http) => new("routing-key", http, Endpoint);

    [Fact]
    public async Task Failing_transition_triggers_with_severity_and_dedup_key()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler);

        await NewSignaler(http).DeliverAsync(TestSignals.Event(ProbeTransition.ToFailing, ProbeResult.Fail, ProbeSeverity.Crit), TestContext.Current.CancellationToken);

        Assert.Equal(Endpoint, handler.LastUri);
        Assert.Contains("\"event_action\":\"trigger\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"critical\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("bowire-monitor/payments", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("routing-key", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passing_transition_resolves_with_the_same_dedup_key()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler);

        await NewSignaler(http).DeliverAsync(TestSignals.Event(ProbeTransition.ToPassing, ProbeResult.Pass), TestContext.Current.CancellationToken);

        Assert.Contains("\"event_action\":\"resolve\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("bowire-monitor/payments", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_failure_becomes_a_signaler_exception()
    {
        using var handler = new CapturingHandler(HttpStatusCode.BadGateway);
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<SignalerException>(
            () => NewSignaler(http).DeliverAsync(TestSignals.Event(ProbeTransition.ToFailing, ProbeResult.Fail), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Factory_rejects_an_empty_routing_key()
    {
        Assert.Throws<SignalerConfigException>(() => new PagerDutySignalerFactory().Create("   "));
    }

    [Fact]
    public void Factory_builds_a_pagerduty_signaler_for_a_routing_key()
    {
        Assert.IsType<PagerDutySignaler>(new PagerDutySignalerFactory().Create("R0UT1NGK3Y"));
    }
}
