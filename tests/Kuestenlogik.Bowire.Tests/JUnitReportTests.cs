// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Xml;
using Kuestenlogik.Bowire.App;
using TestResult = Kuestenlogik.Bowire.App.TestResult;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the JUnit-XML report rendering. Asserts the generated XML
/// is well-formed, that pass/fail counts and per-testcase failure
/// children land on the right elements, and that error invocations
/// (the call itself blew up) emit &lt;error&gt; rather than &lt;failure&gt;.
/// </summary>
public class JUnitReportTests
{
    [Fact]
    public void Render_AllPassing_EmitsZeroFailures()
    {
        var report = new RunReport
        {
            CollectionName = "happy-path",
            CollectionPath = "happy.json",
            StartedAt = new DateTime(2026, 4, 25, 12, 0, 0, DateTimeKind.Utc),
            DurationMs = 1500,
            FailedTests = 0,
            TotalAssertions = 2,
            PassedAssertions = 2
        };
        report.Tests.Add(MakeTest("login", "/users/Login", durationMs: 250, status: "OK",
            assertion: ("status", "eq", "OK", true)));
        report.Tests.Add(MakeTest("listUsers", "/users/List", durationMs: 300, status: "OK",
            assertion: ("response.0.id", "eq", "42", true)));

        var xml = JUnitReport.Render(report);

        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var root = doc.DocumentElement!;
        Assert.Equal("testsuites", root.Name);
        Assert.Equal("happy-path", root.GetAttribute("name"));
        Assert.Equal("2", root.GetAttribute("tests"));
        Assert.Equal("0", root.GetAttribute("failures"));
        Assert.Equal("1.500", root.GetAttribute("time"));

        var suite = (XmlElement)root.GetElementsByTagName("testsuite")[0]!;
        Assert.Equal("0", suite.GetAttribute("failures"));
        Assert.Equal("0", suite.GetAttribute("errors"));

        var cases = doc.GetElementsByTagName("testcase");
        Assert.Equal(2, cases.Count);
        // No <failure> children when everything passed
        Assert.Equal(0, doc.GetElementsByTagName("failure").Count);
        Assert.Equal(0, doc.GetElementsByTagName("error").Count);
    }

    [Fact]
    public void Render_FailedAssertion_EmitsFailureElementWithDiff()
    {
        var report = new RunReport
        {
            CollectionName = "regression",
            CollectionPath = "regression.json",
            StartedAt = DateTime.UtcNow,
            DurationMs = 800,
            FailedTests = 1,
            TotalAssertions = 1,
            PassedAssertions = 0
        };
        report.Tests.Add(MakeTest("statusCheck", "/users/Login", durationMs: 250, status: "Unauthorized",
            assertion: ("status", "eq", "OK", false, "OK", "Unauthorized")));

        var xml = JUnitReport.Render(report);

        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var failure = (XmlElement)doc.GetElementsByTagName("failure")[0]!;
        Assert.NotNull(failure);
        Assert.Equal("AssertionFailed", failure.GetAttribute("type"));
        Assert.Contains("status eq OK", failure.GetAttribute("message"), StringComparison.Ordinal);
        // Diff body has expected + actual
        Assert.Contains("expected: OK", failure.InnerText, StringComparison.Ordinal);
        Assert.Contains("actual:   Unauthorized", failure.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_InvocationError_EmitsErrorElement()
    {
        // When the invocation itself blows up (network, missing protocol,
        // ...) the testcase carries a single <error> rather than <failure>
        // children — distinguishing infrastructure failures from
        // assertion failures is the whole reason JUnit has both elements.
        var report = new RunReport
        {
            CollectionName = "regression",
            CollectionPath = "x.json",
            StartedAt = DateTime.UtcNow,
            DurationMs = 100,
            FailedTests = 1
        };
        var t = new TestResult
        {
            Name = "broken",
            Service = "/users/Login",
            Method = "Login",
            DurationMs = 50,
            Error = "Connection refused"
        };
        report.Tests.Add(t);

        var xml = JUnitReport.Render(report);

        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var err = (XmlElement)doc.GetElementsByTagName("error")[0]!;
        Assert.NotNull(err);
        Assert.Equal("InvocationError", err.GetAttribute("type"));
        Assert.Equal("Connection refused", err.GetAttribute("message"));
        Assert.Equal(0, doc.GetElementsByTagName("failure").Count);
    }

    [Fact]
    public void Render_TestcaseClassname_DerivedFromService()
    {
        // CI test reporters (GitLab, Azure DevOps, GitHub Actions) group
        // testcases by classname in their UI, so the service name has to
        // land on that attribute — not in the test name where it would
        // get truncated.
        var report = new RunReport
        {
            CollectionName = "test",
            CollectionPath = "x.json",
            StartedAt = DateTime.UtcNow,
            DurationMs = 100
        };
        report.Tests.Add(MakeTest("login", "/users/Login", durationMs: 50, status: "OK",
            assertion: ("status", "eq", "OK", true)));

        var xml = JUnitReport.Render(report);
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var tc = (XmlElement)doc.GetElementsByTagName("testcase")[0]!;
        Assert.Equal("/users/Login", tc.GetAttribute("classname"));
        Assert.Equal("login", tc.GetAttribute("name"));
        Assert.Equal("0.050", tc.GetAttribute("time"));
    }

    private static TestResult MakeTest(
        string name,
        string service,
        long durationMs,
        string status,
        (string path, string op, string expected, bool passed, string expected2, string actual) assertion)
    {
        var r = new TestResult
        {
            Name = name,
            Service = service,
            Method = name,
            DurationMs = durationMs,
            Status = status
        };
        r.Assertions.Add(new AssertionResult
        {
            Path = assertion.path,
            Op = assertion.op,
            Expected = assertion.expected2,
            ActualText = assertion.actual,
            Passed = assertion.passed
        });
        return r;
    }

    private static TestResult MakeTest(
        string name,
        string service,
        long durationMs,
        string status,
        (string path, string op, string expected, bool passed) assertion)
        => MakeTest(name, service, durationMs, status,
            (assertion.path, assertion.op, assertion.expected, assertion.passed, assertion.expected, assertion.expected));
}
