// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Kuestenlogik.Bowire.Flows.Expectations;

namespace Kuestenlogik.Bowire.Flows.Tests;

/// <summary>
/// Exercises the per-kind × per-operator evaluation matrix from
/// <see cref="FlowExpectationEvaluator"/>. Each kind appears with at
/// least one happy + one failing case per operator it meaningfully
/// supports, so a regression in either dispatch arm shows up as a
/// failing row instead of as silent always-passing assertions.
/// </summary>
public class FlowExpectationEvaluatorTests
{
    private static FlowRequestEnvelope Envelope(
        string? status = "200",
        string? body = "{\"user\":{\"id\":42,\"name\":\"Ada\"},\"tags\":[\"alpha\",\"beta\"]}",
        long latencyMs = 120,
        IReadOnlyDictionary<string, string>? headers = null,
        string? error = null)
        => new()
        {
            Status = status,
            Body = body,
            LatencyMs = latencyMs,
            Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["X-Trace-Id"] = "abc-123",
            },
            Error = error,
        };

    // ---- Status × every operator that applies ----

    [Fact]
    public void Status_Equals_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Status, Operator = FlowExpectationOperator.Equals, Expected = "200" },
            Envelope());
        Assert.True(r.Passed);
        Assert.Equal("200", r.Actual);
    }

    [Fact]
    public void Status_Equals_Fail()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Status, Operator = FlowExpectationOperator.Equals, Expected = "500" },
            Envelope());
        Assert.False(r.Passed);
        Assert.Contains("failed", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_NotEquals_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Status, Operator = FlowExpectationOperator.NotEquals, Expected = "500" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void Status_NotEquals_Fail()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Status, Operator = FlowExpectationOperator.NotEquals, Expected = "200" },
            Envelope());
        Assert.False(r.Passed);
    }

    [Fact]
    public void Status_Contains_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Status, Operator = FlowExpectationOperator.Contains, Expected = "20" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void Status_Regex_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Status, Operator = FlowExpectationOperator.Regex, Expected = "^2\\d\\d$" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void Status_Regex_Fail_OnBadPattern()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Status, Operator = FlowExpectationOperator.Regex, Expected = "[" },
            Envelope());
        Assert.False(r.Passed);
    }

    // ---- Header ----

    [Fact]
    public void Header_Equals_CaseInsensitive_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Header, Operator = FlowExpectationOperator.Equals, Target = "content-type", Expected = "application/json" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void Header_Contains_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Header, Operator = FlowExpectationOperator.Contains, Target = "Content-Type", Expected = "json" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void Header_Exists_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Header, Operator = FlowExpectationOperator.Exists, Target = "X-Trace-Id" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void Header_Exists_FailOnMissing()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Header, Operator = FlowExpectationOperator.Exists, Target = "X-Missing" },
            Envelope());
        Assert.False(r.Passed);
    }

    [Fact]
    public void Header_NotExists_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Header, Operator = FlowExpectationOperator.NotExists, Target = "X-Missing" },
            Envelope());
        Assert.True(r.Passed);
    }

    // ---- BodyPath ----

    [Fact]
    public void BodyPath_Equals_DottedPath_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.Equals, Target = "$.user.id", Expected = "42" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void BodyPath_Equals_StringField_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.Equals, Target = "$.user.name", Expected = "Ada" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void BodyPath_Equals_Fail()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.Equals, Target = "$.user.id", Expected = "99" },
            Envelope());
        Assert.False(r.Passed);
    }

    [Fact]
    public void BodyPath_Exists_OnArrayElement_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.Exists, Target = "$.tags.0" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void BodyPath_NotExists_OnInvalidPath_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.NotExists, Target = "$.user.unknown" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void BodyPath_Equals_OnUnparseableBody_Fails()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.Equals, Target = "$.x", Expected = "y" },
            Envelope(body: "this is not json"));
        Assert.False(r.Passed);
    }

    [Fact]
    public void BodyPath_Contains_OnArray_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.Contains, Target = "$.tags", Expected = "beta" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void BodyPath_GreaterThan_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.GreaterThan, Target = "$.user.id", Expected = "10" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void BodyPath_GreaterThanOrEquals_BoundaryPass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.GreaterThanOrEquals, Target = "$.user.id", Expected = "42" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void BodyPath_LessThan_Fail()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.LessThan, Target = "$.user.id", Expected = "10" },
            Envelope());
        Assert.False(r.Passed);
    }

    [Fact]
    public void BodyPath_Regex_OnString_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.Regex, Target = "$.user.name", Expected = "^A.*a$" },
            Envelope());
        Assert.True(r.Passed);
    }

    // ---- BodyText ----

    [Fact]
    public void BodyText_Contains_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyText, Operator = FlowExpectationOperator.Contains, Expected = "\"name\":\"Ada\"" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void BodyText_Regex_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyText, Operator = FlowExpectationOperator.Regex, Expected = "\"id\":\\s*\\d+" },
            Envelope());
        Assert.True(r.Passed);
    }

    [Fact]
    public void BodyText_Contains_Fail()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.BodyText, Operator = FlowExpectationOperator.Contains, Expected = "needle-not-in-haystack" },
            Envelope());
        Assert.False(r.Passed);
    }

    // ---- Latency × numeric operators ----

    [Fact]
    public void Latency_LessThan_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Latency, Operator = FlowExpectationOperator.LessThan, Expected = "500" },
            Envelope(latencyMs: 120));
        Assert.True(r.Passed);
    }

    [Fact]
    public void Latency_LessThanOrEquals_BoundaryPass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Latency, Operator = FlowExpectationOperator.LessThanOrEquals, Expected = "120" },
            Envelope(latencyMs: 120));
        Assert.True(r.Passed);
    }

    [Fact]
    public void Latency_LessThan_Fail()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Latency, Operator = FlowExpectationOperator.LessThan, Expected = "100" },
            Envelope(latencyMs: 120));
        Assert.False(r.Passed);
    }

    [Fact]
    public void Latency_GreaterThan_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Latency, Operator = FlowExpectationOperator.GreaterThan, Expected = "50" },
            Envelope(latencyMs: 120));
        Assert.True(r.Passed);
    }

    [Fact]
    public void Latency_Equals_Pass()
    {
        var r = FlowExpectationEvaluator.Evaluate(
            new FlowExpectation { Kind = FlowExpectationKind.Latency, Operator = FlowExpectationOperator.Equals, Expected = "120" },
            Envelope(latencyMs: 120));
        Assert.True(r.Passed);
    }

    // ---- Step rollup ----

    [Fact]
    public void EvaluateStep_MixedResults_TalliesPassAndFailCounts()
    {
        var step = FlowExpectationEvaluator.EvaluateStep(
            "node_x",
            new[]
            {
                new FlowExpectation { Kind = FlowExpectationKind.Status, Operator = FlowExpectationOperator.Equals, Expected = "200" },
                new FlowExpectation { Kind = FlowExpectationKind.BodyPath, Operator = FlowExpectationOperator.Equals, Target = "$.user.id", Expected = "99" },
                new FlowExpectation { Kind = FlowExpectationKind.Latency, Operator = FlowExpectationOperator.LessThan, Expected = "500" },
            },
            Envelope());

        Assert.Equal("node_x", step.StepId);
        Assert.Equal(2, step.Passed);
        Assert.Equal(1, step.Failed);
        Assert.False(step.AllPassed);
        Assert.Equal(3, step.Evaluations.Count);
    }

    [Fact]
    public void EvaluateStep_NoExpectations_VacuouslyPasses()
    {
        var step = FlowExpectationEvaluator.EvaluateStep("node_x", Array.Empty<FlowExpectation>(), Envelope());
        Assert.True(step.AllPassed);
        Assert.Empty(step.Evaluations);
    }
}
