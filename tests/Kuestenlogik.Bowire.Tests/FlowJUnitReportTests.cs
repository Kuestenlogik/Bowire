// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Flows.Expectations;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The JUnit XML <c>bowire test</c> hands to a CI's test reporter.
/// </summary>
/// <remarks>
/// <para>
/// Nobody reads this file; Jenkins, GitLab, Azure DevOps and the GitHub
/// Actions reporter do, and they all fail quietly. A miscounted
/// <c>failures</c> attribute or a case emitted in the wrong shape produces a
/// green check over a failed run — the one outcome a test report must never
/// produce.
/// </para>
/// <para>
/// The document is parsed rather than string-matched here, for the same
/// reason: what matters is what a reporter would read out of it.
/// </para>
/// </remarks>
public sealed class FlowJUnitReportTests
{
    private static FlowStepRunResult Step(
        string id, string? error = null, bool skipped = false, long latencyMs = 120,
        params FlowExpectationResult[] expectations)
    {
        var step = new FlowStepRunResult
        {
            StepId = id,
            StepType = "request",
            Service = "orders.v1.OrderService",
            Method = "GetOrder",
            LatencyMs = latencyMs,
            Error = error,
            Skipped = skipped,
        };
        step.Expectations.AddRange(expectations);
        return step;
    }

    private static FlowExpectationResult Expectation(bool passed, string message = "status equals 200")
        => new()
        {
            Passed = passed,
            Message = message,
            Kind = FlowExpectationKind.Status,
            Operator = FlowExpectationOperator.Equals,
            Actual = passed ? "200" : "404",
            Expected = "200",
        };

    private static FlowRunReport Report(params FlowStepRunResult[] steps)
    {
        var report = new FlowRunReport
        {
            FlowId = "flow-1",
            FlowName = "checkout",
            FlowPath = "flows/checkout.json",
            StartedAt = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc),
            DurationMs = 1500,
        };
        report.Steps.AddRange(steps);
        report.TotalExpectations = steps.Sum(s => s.Expectations.Count);
        report.PassedExpectations = steps.Sum(s => s.Expectations.Count(e => e.Passed));
        report.FailedExpectations = steps.Sum(s => s.Expectations.Count(e => !e.Passed));
        report.StepErrors = steps.Count(s => !string.IsNullOrEmpty(s.Error));
        return report;
    }

    private static XElement Render(FlowRunReport report, SecretRedactor? redactor = null)
        => XDocument.Parse(FlowJUnitReport.Render(report, redactor)).Root!;

    [Fact]
    public void The_Document_Is_A_Testsuites_Wrapping_One_Testsuite()
    {
        // The shape every reporter keys off; a bare <testsuite> root is
        // accepted by some and silently ignored by others.
        var root = Render(Report(Step("s1", expectations: Expectation(true))));

        Assert.Equal("testsuites", root.Name.LocalName);
        var suite = Assert.Single(root.Elements("testsuite"));
        Assert.Equal("checkout", suite.Attribute("name")?.Value);
    }

    [Fact]
    public void One_Testcase_Per_Expectation_Not_Per_Step()
    {
        // The whole reason for this renderer: a reporter should show the
        // individual expectation rows, not one collapsed pass/fail per step.
        var root = Render(Report(Step("s1", expectations: [Expectation(true), Expectation(false)])));

        Assert.Equal(2, root.Descendants("testcase").Count());
    }

    [Fact]
    public void A_Failed_Expectation_Becomes_A_Failure_Element_Carrying_Its_Message()
    {
        var root = Render(Report(Step("s1",
            expectations: Expectation(false, "status equals 200 — got 404"))));

        var failure = Assert.Single(root.Descendants("failure"));
        Assert.Equal("AssertionFailed", failure.Attribute("type")?.Value);
        Assert.Contains("got 404", failure.Attribute("message")?.Value!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Passing_Expectation_Carries_No_Failure_Element()
    {
        var root = Render(Report(Step("s1", expectations: Expectation(true))));

        Assert.Empty(root.Descendants("failure"));
        Assert.Single(root.Descendants("testcase"));
    }

    [Fact]
    public void The_Counts_On_The_Suite_Match_The_Cases_Inside_It()
    {
        // A reporter reads the attributes, not the children. If the two
        // disagree, the build badge and the detail view tell different
        // stories — and the badge is what people act on.
        var root = Render(Report(
            Step("s1", expectations: [Expectation(true), Expectation(false)]),
            Step("s2", expectations: Expectation(false))));

        var suite = root.Element("testsuite")!;
        Assert.Equal("3", suite.Attribute("tests")?.Value);
        Assert.Equal("2", suite.Attribute("failures")?.Value);
        Assert.Equal(2, root.Descendants("failure").Count());
    }

    [Fact]
    public void A_Step_That_Never_Ran_Is_One_Error_Case_Rather_Than_Expectation_Rows()
    {
        // Expectation-level rows would be misleading: the invocation did not
        // happen, so nothing was actually asserted.
        var root = Render(Report(Step("s1", error: "connection refused",
            expectations: [Expectation(true), Expectation(true)])));

        var testcase = Assert.Single(root.Descendants("testcase"));
        Assert.Contains("invocation", testcase.Attribute("name")?.Value!, StringComparison.Ordinal);
        var error = Assert.Single(root.Descendants("error"));
        Assert.Equal("StepError", error.Attribute("type")?.Value);
        Assert.Contains("connection refused", error.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Skipped_Control_Flow_Step_Is_Marked_Skipped()
    {
        // It keeps the suite count consistent with the run summary; dropping
        // it would make the two disagree by one for every conditional node.
        var root = Render(Report(Step("branch-1", skipped: true)));

        Assert.Single(root.Descendants("skipped"));
        Assert.Equal("1", root.Element("testsuite")?.Attribute("skipped")?.Value);
    }

    [Fact]
    public void A_Successful_Step_With_No_Expectations_Still_Shows_That_It_Ran()
    {
        // Otherwise a flow of pure invocations reports zero tests, which reads
        // as "nothing ran" rather than "nothing was asserted".
        var root = Render(Report(Step("s1")));

        var testcase = Assert.Single(root.Descendants("testcase"));
        Assert.Contains("invocation", testcase.Attribute("name")?.Value!, StringComparison.Ordinal);
        Assert.Empty(testcase.Elements("failure"));
    }

    [Fact]
    public void Every_Case_Is_Classed_Under_The_Flow_Name()
    {
        // That is what groups the run in a reporter's tree.
        var root = Render(Report(
            Step("s1", expectations: Expectation(true)),
            Step("s2", expectations: Expectation(false))));

        Assert.All(root.Descendants("testcase"),
            c => Assert.Equal("checkout", c.Attribute("classname")?.Value));
    }

    [Fact]
    public void The_Case_Name_Says_Which_Step_And_Which_Expectation()
    {
        // Two expectations on one step have to be distinguishable in a list
        // that shows nothing but the name.
        var root = Render(Report(Step("fetch-order", expectations: Expectation(false))));

        var name = root.Descendants("testcase").Single().Attribute("name")!.Value;
        Assert.StartsWith("fetch-order", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Times_Are_Invariant_Culture_Seconds()
    {
        // A German-locale runner writing "1,500" produces a file every
        // reporter rejects — and the run then has no report at all.
        var root = Render(Report(Step("s1", latencyMs: 1234, expectations: Expectation(true))));

        Assert.Equal("1.500", root.Attribute("time")?.Value);
    }

    [Fact]
    public void An_Empty_Run_Still_Renders_A_Valid_Document()
    {
        var root = Render(Report());

        Assert.Equal("testsuites", root.Name.LocalName);
        Assert.Empty(root.Descendants("testcase"));
    }

    [Fact]
    public void A_Secret_In_A_Step_Error_Is_Redacted_Before_It_Reaches_The_Report()
    {
        // JUnit XML is uploaded as a CI artefact and often rendered publicly.
        // A token that leaked into an error message must not survive the trip.
        var redactor = new SecretRedactor(["s3cret-token"]);

        var root = Render(
            Report(Step("s1", error: "401 from https://api.example.com (token s3cret-token)")),
            redactor);

        var xml = root.ToString();
        Assert.DoesNotContain("s3cret-token", xml, StringComparison.Ordinal);
        Assert.Contains("401", xml, StringComparison.Ordinal);
    }
}
