// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using TestResult = Kuestenlogik.Bowire.App.TestResult;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Snapshot-style assertions on the standalone-HTML test-report renderer.
/// HTML structure isn't compared verbatim (CSS gets reformatted on style
/// tweaks); instead each scenario asserts the fragments that downstream
/// CI artifact viewers depend on — pass / fail icons, the assertion list,
/// the summary class, and basic HTML well-formedness.
/// </summary>
public sealed class HtmlReportTests
{
    [Fact]
    public void Render_EmptyReport_ProducesValidShell()
    {
        var report = new RunReport
        {
            CollectionName = "no-tests",
            CollectionPath = "empty.json",
            StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DurationMs = 0,
        };

        var html = HtmlReport.Render(report);

        // Doctype + title + body close — minimum well-formedness.
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("<title>Bowire Test Report", html, StringComparison.Ordinal);
        Assert.Contains("no-tests", html, StringComparison.Ordinal);
        Assert.Contains("empty.json", html, StringComparison.Ordinal);
        Assert.EndsWith("</html>", html, StringComparison.Ordinal);
        // Empty run → 0/0 summary, summary marked pass (no failures).
        Assert.Contains("0/0", html, StringComparison.Ordinal);
        Assert.Contains("class=\"summary pass\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SinglePassedTest_EmitsCheckMarkAndPassClass()
    {
        var report = new RunReport
        {
            CollectionName = "happy",
            CollectionPath = "happy.json",
            StartedAt = DateTime.UtcNow,
            DurationMs = 100,
            TotalAssertions = 1,
            PassedAssertions = 1,
            FailedTests = 0,
        };
        var test = new TestResult
        {
            Name = "Login",
            Service = "users.UserService",
            Method = "Login",
            Status = "OK",
            DurationMs = 42,
        };
        test.Assertions.Add(new AssertionResult
        {
            Path = "status",
            Op = "eq",
            Expected = "OK",
            ActualText = "OK",
            Passed = true,
        });
        report.Tests.Add(test);

        var html = HtmlReport.Render(report);

        Assert.Contains("class=\"test pass\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"summary pass\"", html, StringComparison.Ordinal);
        // Pass icon and the assertion's path/op/expected triple.
        Assert.Contains("✓", html, StringComparison.Ordinal);
        Assert.Contains("status", html, StringComparison.Ordinal);
        Assert.Contains("eq", html, StringComparison.Ordinal);
        // No error block on a green test.
        Assert.DoesNotContain("class=\"error\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_FailedAssertion_EmitsCrossAndActualDiff()
    {
        var report = new RunReport
        {
            CollectionName = "regression",
            CollectionPath = "regression.json",
            StartedAt = DateTime.UtcNow,
            DurationMs = 200,
            TotalAssertions = 1,
            PassedAssertions = 0,
            FailedTests = 1,
        };
        var test = new TestResult
        {
            Name = "BadStatus",
            Service = "users.UserService",
            Method = "Login",
            Status = "Unauthorized",
            DurationMs = 50,
        };
        test.Assertions.Add(new AssertionResult
        {
            Path = "status",
            Op = "eq",
            Expected = "OK",
            ActualText = "Unauthorized",
            Passed = false,
        });
        report.Tests.Add(test);

        var html = HtmlReport.Render(report);

        Assert.Contains("class=\"summary fail\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"test fail\"", html, StringComparison.Ordinal);
        Assert.Contains("✗", html, StringComparison.Ordinal);
        // Actual block carries the observed value when an assertion fails
        // (passed assertions just show path/op/expected).
        Assert.Contains("Unauthorized", html, StringComparison.Ordinal);
        Assert.Contains("class=\"actual\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AssertionWithError_EmitsErrorActualBlock()
    {
        // When the assertion engine itself blew up (regex parse, etc.)
        // the rendered HTML carries the error message in a styled block,
        // distinct from the plain "actual: ..." diff.
        var report = new RunReport
        {
            CollectionName = "engine",
            CollectionPath = "engine.json",
            StartedAt = DateTime.UtcNow,
            DurationMs = 5,
            FailedTests = 1,
        };
        var test = new TestResult
        {
            Name = "BadRegex",
            Service = "x",
            Method = "y",
            Status = "OK",
            DurationMs = 1,
        };
        test.Assertions.Add(new AssertionResult
        {
            Path = "response.body",
            Op = "matches",
            Expected = "(unclosed",
            ActualText = "",
            Passed = false,
            Error = "Invalid pattern",
        });
        report.Tests.Add(test);

        var html = HtmlReport.Render(report);

        Assert.Contains("class=\"actual error\"", html, StringComparison.Ordinal);
        Assert.Contains("Invalid pattern", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_InvocationError_EmitsTestErrorBlock()
    {
        // No assertions at all — just an Error string on the test result.
        // The renderer should emit the standalone error <div> *and* mark
        // the testcase as failing, even with zero <li> assertion rows.
        var report = new RunReport
        {
            CollectionName = "infra",
            CollectionPath = "infra.json",
            StartedAt = DateTime.UtcNow,
            DurationMs = 100,
            FailedTests = 1,
        };
        report.Tests.Add(new TestResult
        {
            Name = "Unreachable",
            Service = "users.UserService",
            Method = "Login",
            Status = "FAIL",
            DurationMs = 100,
            Error = "Connection refused",
        });

        var html = HtmlReport.Render(report);

        Assert.Contains("class=\"test fail\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"error\"", html, StringComparison.Ordinal);
        Assert.Contains("Connection refused", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HtmlMetacharacters_AreEscaped()
    {
        // Names/paths can come from user-controlled JSON, so the renderer
        // has to escape HTML reserved characters. <, >, &, " in any
        // string-bearing field must surface as their entity references.
        var report = new RunReport
        {
            CollectionName = "<script>",
            CollectionPath = "a&b.json",
            StartedAt = DateTime.UtcNow,
            DurationMs = 1,
        };
        report.Tests.Add(new TestResult
        {
            Name = "alert(\"xss\")",
            Service = "x>",
            Method = "y<",
            Status = "OK",
            DurationMs = 1,
        });

        var html = HtmlReport.Render(report);

        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("a&amp;b.json", html, StringComparison.Ordinal);
        Assert.Contains("&quot;xss&quot;", html, StringComparison.Ordinal);
        // Raw <script> tag must NOT survive into the output.
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }
}
