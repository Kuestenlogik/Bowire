// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for <see cref="ProbeFile"/> — parsing a probe definition (recording
/// + schedule + assertions + severity) and its error surface.
/// </summary>
public sealed class ProbeFileTests
{
    private const string Valid = """
        {
          "name": "payments-health",
          "schedule": "every 60s, 09:00-17:00 UTC, Mon-Fri",
          "severity": "crit",
          "assertions": [
            { "kind": "status", "expected": "200" },
            { "kind": "latencyBelowMs", "expected": "500" },
            { "kind": "bodyContains", "expected": "healthy" }
          ],
          "recording": {
            "id": "rec_1",
            "name": "payments health",
            "steps": [ { "id": "s1", "protocol": "rest", "service": "root", "method": "GET /health", "httpVerb": "GET", "httpPath": "/health" } ]
          }
        }
        """;

    [Fact]
    public void Parses_a_full_probe()
    {
        var probe = ProbeFile.Parse(Valid);
        Assert.Equal("payments-health", probe.Name);
        Assert.Equal(ProbeSeverity.Crit, probe.Severity);
        Assert.Equal(TimeSpan.FromSeconds(60), probe.Schedule.Interval);
        Assert.NotNull(probe.Schedule.Window);
        Assert.Equal(3, probe.Assertions.Count);
        Assert.Equal(ProbeAssertionKind.Status, probe.Assertions[0].Kind);
        Assert.Equal(ProbeAssertionKind.LatencyBelowMs, probe.Assertions[1].Kind);
        Assert.Equal(ProbeAssertionKind.BodyContains, probe.Assertions[2].Kind);
        Assert.Single(probe.Recording.Steps);
    }

    [Fact]
    public void Severity_defaults_to_warn()
    {
        var probe = ProbeFile.Parse("""
            { "name": "x", "schedule": "every 30s", "recording": { "id": "r", "name": "n", "steps": [] } }
            """);
        Assert.Equal(ProbeSeverity.Warn, probe.Severity);
        Assert.Empty(probe.Assertions);
    }

    [Theory]
    [InlineData("{ \"schedule\": \"every 60s\", \"recording\": {} }", "name")]        // missing name
    [InlineData("{ \"name\": \"x\", \"schedule\": \"hourly\", \"recording\": {} }", "schedule")] // bad schedule
    [InlineData("{ \"name\": \"x\", \"schedule\": \"every 60s\" }", "recording")]      // missing recording
    public void Rejects_invalid_files(string json, string reasonFragment)
    {
        var ex = Assert.Throws<ProbeFileException>(() => ProbeFile.Parse(json));
        Assert.Contains(reasonFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_unknown_assertion_kind()
    {
        var ex = Assert.Throws<ProbeFileException>(() => ProbeFile.Parse("""
            { "name": "x", "schedule": "every 60s", "assertions": [ { "kind": "teapot", "expected": "418" } ],
              "recording": { "id": "r", "name": "n", "steps": [] } }
            """));
        Assert.Contains("teapot", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_malformed_json()
        => Assert.Throws<ProbeFileException>(() => ProbeFile.Parse("{ not json"));
}
