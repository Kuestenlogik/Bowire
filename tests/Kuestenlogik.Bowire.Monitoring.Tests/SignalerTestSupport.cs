// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>Captures the outgoing request + body and returns a canned status.</summary>
internal sealed class CapturingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public string? LastBody { get; private set; }
    public Uri? LastUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUri = request.RequestUri;
        if (request.Content is not null)
        {
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        return new HttpResponseMessage(status);
    }
}

/// <summary>Shared probe / signal-event builders for the signaler tests.</summary>
internal static class TestSignals
{
    public static Probe Probe(ProbeSeverity severity = ProbeSeverity.Crit, string name = "payments")
    {
        Assert.True(ProbeSchedule.TryParse("every 60s", out var s, out _));
        return new Probe { Name = name, Schedule = s!, Severity = severity, Recording = new BowireRecording { Id = "r", Name = name } };
    }

    public static SignalEvent Event(ProbeTransition transition, ProbeResult result, ProbeSeverity severity = ProbeSeverity.Crit)
        => new(Probe(severity), transition, new ProbeOutcome { TimestampUnixMs = 1, Result = result });
}
