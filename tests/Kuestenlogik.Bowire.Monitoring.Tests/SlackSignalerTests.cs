// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using Kuestenlogik.Bowire.Monitoring.Slack;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for the Slack signaler package — posts to the webhook on a
/// transition; HTTP failures wrap into <see cref="SignalerException"/>.
/// </summary>
public sealed class SlackSignalerTests
{
    [Fact]
    public async Task Posts_an_alert_on_a_failing_transition()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var signaler = new SlackSignaler(new Uri("https://hooks.test/x"), http);

        await signaler.DeliverAsync(TestSignals.Event(ProbeTransition.ToFailing, ProbeResult.Fail), TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://hooks.test/x"), handler.LastUri);
        Assert.Contains("payments", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("failing", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Posts_a_recovery_on_a_passing_transition()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var signaler = new SlackSignaler(new Uri("https://hooks.test/x"), http);

        await signaler.DeliverAsync(TestSignals.Event(ProbeTransition.ToPassing, ProbeResult.Pass), TestContext.Current.CancellationToken);
        Assert.Contains("recovered", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_failure_becomes_a_signaler_exception()
    {
        using var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler);
        var signaler = new SlackSignaler(new Uri("https://hooks.test/x"), http);

        await Assert.ThrowsAsync<SignalerException>(
            () => signaler.DeliverAsync(TestSignals.Event(ProbeTransition.ToFailing, ProbeResult.Fail), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Factory_rejects_a_non_url_argument()
    {
        Assert.Throws<SignalerConfigException>(() => new SlackSignalerFactory().Create("not-a-url"));
    }

    [Fact]
    public void Factory_builds_a_slack_signaler_for_a_webhook_url()
    {
        var signaler = new SlackSignalerFactory().Create("https://hooks.slack.com/services/T/B/x");
        Assert.IsType<SlackSignaler>(signaler);
    }
}
