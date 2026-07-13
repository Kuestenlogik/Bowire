// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for <see cref="ProbeAssertion"/> — the status / latency / body
/// predicates and their pass/fail verdicts.
/// </summary>
public sealed class ProbeAssertionTests
{
    private static ProbeExecutionResult Result(int status = 200, double latency = 50, string? body = "ok")
        => new(status, latency, body);

    [Theory]
    [InlineData(200, "200", true)]
    [InlineData(500, "200", false)]
    [InlineData(200, "not-a-number", false)]
    public void Status_assertion(int actual, string expected, bool passes)
    {
        var v = new ProbeAssertion { Kind = ProbeAssertionKind.Status, Expected = expected }.Evaluate(Result(status: actual));
        Assert.Equal(passes, v.Passed);
    }

    [Theory]
    [InlineData(50, "100", true)]   // under budget
    [InlineData(100, "100", true)]  // exactly at budget
    [InlineData(150, "100", false)] // over budget
    public void Latency_assertion(double actual, string budget, bool passes)
    {
        var v = new ProbeAssertion { Kind = ProbeAssertionKind.LatencyBelowMs, Expected = budget }.Evaluate(Result(latency: actual));
        Assert.Equal(passes, v.Passed);
    }

    [Theory]
    [InlineData("healthy", true)]
    [InlineData("missing", false)]
    public void Body_contains_assertion(string needle, bool passes)
    {
        var v = new ProbeAssertion { Kind = ProbeAssertionKind.BodyContains, Expected = needle }
            .Evaluate(Result(body: "status: healthy"));
        Assert.Equal(passes, v.Passed);
    }

    [Fact]
    public void Body_contains_against_null_body_fails()
    {
        var v = new ProbeAssertion { Kind = ProbeAssertionKind.BodyContains, Expected = "x" }.Evaluate(Result(body: null));
        Assert.False(v.Passed);
    }

    [Fact]
    public void Verdict_carries_a_human_readable_description()
    {
        var v = new ProbeAssertion { Kind = ProbeAssertionKind.Status, Expected = "200" }.Evaluate(Result(status: 200));
        Assert.Contains("status", v.Description, StringComparison.Ordinal);
    }
}
