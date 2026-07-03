// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Flows.Expectations;
using TestResult = Kuestenlogik.Bowire.App.TestResult;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the SARIF 2.1.0 renderer + GitHub ::error annotations behind
/// <c>bowire test --sarif / --annotations</c> (#181). Asserts the envelope
/// fields GitHub Code Scanning validates (version, driver, physical
/// location with a non-absolute URI, partialFingerprints), the
/// per-failure result mapping for both runner shapes, and the
/// workflow-command escaping rules.
/// </summary>
public class TestSarifReportTests
{
    // ---- recording / test-collection shape ----

    [Fact]
    public void Render_RunReport_AllPassing_EmitsNoResults()
    {
        var report = MakeRunReport();
        report.Tests.Add(MakeTest("login", assertionPassed: true));

        using var doc = JsonDocument.Parse(TestSarifReport.Render(report));
        var root = doc.RootElement;

        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        var run = root.GetProperty("runs")[0];
        Assert.Equal("bowire-test", run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());
        Assert.Equal(0, run.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void Render_RunReport_FailedAssertion_MapsToAssertionFailedResult()
    {
        var report = MakeRunReport();
        report.Tests.Add(MakeTest("login", assertionPassed: false));

        using var doc = JsonDocument.Parse(TestSarifReport.Render(report));
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");

        Assert.Equal(1, results.GetArrayLength());
        var r = results[0];
        Assert.Equal("assertion-failed", r.GetProperty("ruleId").GetString());
        Assert.Equal("error", r.GetProperty("level").GetString());
        Assert.Contains("login", r.GetProperty("message").GetProperty("text").GetString());
        Assert.Contains("actual", r.GetProperty("message").GetProperty("text").GetString());

        var uri = r.GetProperty("locations")[0].GetProperty("physicalLocation")
            .GetProperty("artifactLocation").GetProperty("uri").GetString();
        Assert.False(string.IsNullOrEmpty(uri));
        Assert.DoesNotContain(":\\", uri);      // never an absolute Windows path
        Assert.DoesNotContain("https://", uri); // never a URL scheme

        Assert.True(r.GetProperty("partialFingerprints").TryGetProperty("bowireTestCase", out _));
    }

    [Fact]
    public void Render_RunReport_InvocationError_MapsToInvocationErrorResult()
    {
        var report = MakeRunReport();
        var test = new TestResult { Name = "boom", Service = "svc", Method = "M" };
        test.Error = "connection refused";
        report.Tests.Add(test);

        using var doc = JsonDocument.Parse(TestSarifReport.Render(report));
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");

        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("invocation-error", results[0].GetProperty("ruleId").GetString());
        Assert.Contains("connection refused", results[0].GetProperty("message").GetProperty("text").GetString());
    }

    // ---- flow shape ----

    [Fact]
    public void Render_FlowReport_FailedExpectationAndStepError_MapToDistinctRules()
    {
        var report = new FlowRunReport { FlowName = "checkout", FlowPath = "flows/checkout.json" };

        var failing = new FlowStepRunResult { StepId = "step-1" };
        failing.Expectations.Add(new FlowExpectationResult { Passed = false, Message = "status equals 200 — got 500" });
        report.Steps.Add(failing);

        var errored = new FlowStepRunResult { StepId = "step-2", Error = "backend unreachable" };
        report.Steps.Add(errored);

        var skipped = new FlowStepRunResult { StepId = "step-3", Skipped = true };
        report.Steps.Add(skipped);

        using var doc = JsonDocument.Parse(TestSarifReport.Render(report));
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");

        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("expectation-failed", results[0].GetProperty("ruleId").GetString());
        Assert.Contains("got 500", results[0].GetProperty("message").GetProperty("text").GetString());
        Assert.Equal("step-error", results[1].GetProperty("ruleId").GetString());
    }

    // ---- artifact URI relativisation ----

    [Fact]
    public void ToArtifactUri_PathOutsideCwd_FallsBackToFileName()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "suite.json");
        Assert.Equal("suite.json", TestSarifReport.ToArtifactUri(outside));
    }

    [Fact]
    public void ToArtifactUri_RelativePath_UsesForwardSlashes()
    {
        var uri = TestSarifReport.ToArtifactUri(Path.Combine("flows", "checkout.json"));
        Assert.Equal("flows/checkout.json", uri);
    }

    // ---- GitHub annotations ----

    [Fact]
    public async Task Annotations_RunReport_EmitOneErrorPerFailure_WithEscaping()
    {
        var report = MakeRunReport();
        report.Tests.Add(MakeTest("login", assertionPassed: false));
        var errored = new TestResult { Name = "multi\nline: test", Service = "svc", Method = "M" };
        errored.Error = "line1\nline2";
        report.Tests.Add(errored);

        await using var sw = new StringWriter();
        await GitHubAnnotations.WriteAsync(sw, report);
        var lines = sw.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("::error file=", l, StringComparison.Ordinal));
        // data newlines escaped, property colons/commas escaped
        Assert.Contains("line1%0Aline2", lines[1]);
        Assert.Contains("multi%0Aline%3A test", lines[1]);
    }

    [Fact]
    public async Task Annotations_FlowReport_SkippedStepsEmitNothing()
    {
        var report = new FlowRunReport { FlowName = "f", FlowPath = "f.json" };
        report.Steps.Add(new FlowStepRunResult { StepId = "s1", Skipped = true });
        var pass = new FlowStepRunResult { StepId = "s2" };
        pass.Expectations.Add(new FlowExpectationResult { Passed = true, Message = "ok" });
        report.Steps.Add(pass);

        await using var sw = new StringWriter();
        await GitHubAnnotations.WriteAsync(sw, report);

        Assert.Equal(string.Empty, sw.ToString());
    }

    // ---- helpers ----

    private static RunReport MakeRunReport() => new()
    {
        CollectionName = "suite",
        CollectionPath = "suite.json",
        StartedAt = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc),
    };

    private static TestResult MakeTest(string name, bool assertionPassed)
    {
        var t = new TestResult { Name = name, Service = "svc", Method = "M", Status = "OK" };
        t.Assertions.Add(new AssertionResult
        {
            Path = "status",
            Op = "eq",
            Expected = "OK",
            ActualText = assertionPassed ? "OK" : "ERROR",
            Passed = assertionPassed,
        });
        return t;
    }
}
