// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp.Tasks;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// #811 — a finished run as <c>bowire.test.run</c> answers it step by step:
/// the collection's or the flow's report, with every text that could carry a
/// secret masked the way the JUnit report masks it.
/// </summary>
internal static class McpTestReports
{
    public static BowireTestRunReport From(RunReport report, SecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(redactor);
        return new BowireTestRunReport("collection", report.CollectionName, report.DurationMs, Math.Max(1, report.Iterations),
            [.. report.Tests.Select(t => new BowireTestStepOutcome(
                string.IsNullOrEmpty(t.TestName) ? t.Name : t.TestName, t.Service, t.Method, t.Status, t.DurationMs,
                Passed: string.IsNullOrEmpty(t.Error) && t.Assertions.All(a => a.Passed),
                Skipped: false,
                redactor.Redact(t.Error), t.Iteration, t.Source,
                [.. t.Assertions.Select(a => new BowireTestCheckOutcome(
                    a.Path, a.Op, redactor.Redact(a.Expected), redactor.Redact(a.ActualText), a.Passed, redactor.Redact(a.Error)))]))]);
    }

    public static BowireTestRunReport From(FlowRunReport report, SecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(redactor);
        return new BowireTestRunReport("flow", report.FlowName, report.DurationMs, Math.Max(1, report.Iterations),
            [.. report.Steps.Select(s => new BowireTestStepOutcome(
                s.StepId, s.Service, s.Method, s.Status, s.LatencyMs,
                Passed: !s.Skipped && string.IsNullOrEmpty(s.Error) && s.Expectations.All(e => e.Passed),
                s.Skipped,
                redactor.Redact(s.Error), s.Iteration, s.Source,
                [.. s.Expectations.Select(e => new BowireTestCheckOutcome(
                    e.Id ?? e.Kind.ToString(), e.Operator.ToString(), redactor.Redact(e.Expected), redactor.Redact(e.Actual), e.Passed,
                    e.Passed ? null : redactor.Redact(e.Message)))]))]);
    }
}
