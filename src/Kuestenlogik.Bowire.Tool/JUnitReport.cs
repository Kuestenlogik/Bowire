// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Xml;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Renders a <see cref="RunReport"/> as a JUnit XML document. The format
/// is the de-facto CI standard — Jenkins, GitLab CI, Azure DevOps, GitHub
/// Actions test reporters all consume it natively. Schema follows the
/// surface-area Apache Ant emits and that downstream tools agree on
/// (testsuites → testsuite → testcase → failure | error).
/// </summary>
internal static class JUnitReport
{
    /// <summary>
    /// Render the run report as a JUnit XML document. Each Bowire test
    /// becomes one <c>&lt;testcase&gt;</c>; assertion failures and
    /// invocation errors both map to <c>&lt;failure&gt;</c> children with
    /// a human-readable message attribute and the diff details inside.
    /// </summary>
    public static string Render(RunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false
        };

        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
        using (var w = XmlWriter.Create(sw, settings))
        {
            // <testsuites> aggregate — single suite for one collection,
            // but the wrapper is required by some tools (Azure DevOps).
            var suiteName = report.CollectionName;
            var totalSeconds = (report.DurationMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
            w.WriteStartElement("testsuites");
            w.WriteAttributeString("name", suiteName);
            w.WriteAttributeString("tests", report.Tests.Count.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("failures", report.FailedTests.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("time", totalSeconds);

            // <testsuite> — one per collection.
            w.WriteStartElement("testsuite");
            w.WriteAttributeString("name", suiteName);
            w.WriteAttributeString("tests", report.Tests.Count.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("failures", report.FailedTests.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("errors", "0");
            w.WriteAttributeString("skipped", "0");
            w.WriteAttributeString("time", totalSeconds);
            w.WriteAttributeString("timestamp", report.StartedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));

            foreach (var test in report.Tests)
            {
                WriteTestCase(w, test, suiteName);
            }

            w.WriteEndElement(); // testsuite
            w.WriteEndElement(); // testsuites
        }

        return sb.ToString();
    }

    private static void WriteTestCase(XmlWriter w, TestResult test, string suiteName)
    {
        // classname doubles as the service name (Jenkins / GitLab group
        // tests by classname in their UI). Method goes into the test name.
        var className = string.IsNullOrEmpty(test.Service) ? suiteName : test.Service;
        var caseName = string.IsNullOrEmpty(test.Name) ? test.Method : test.Name;
        var seconds = (test.DurationMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);

        w.WriteStartElement("testcase");
        w.WriteAttributeString("classname", className);
        w.WriteAttributeString("name", caseName);
        w.WriteAttributeString("time", seconds);

        // Invocation error → single <error> (the call itself blew up,
        // distinct from an assertion failure).
        if (!string.IsNullOrEmpty(test.Error))
        {
            w.WriteStartElement("error");
            w.WriteAttributeString("message", test.Error);
            w.WriteAttributeString("type", "InvocationError");
            w.WriteString(test.Error);
            w.WriteEndElement();
        }
        else
        {
            // Each failed assertion → one <failure>. Multiple failures per
            // testcase are valid per the schema and many tools render them.
            foreach (var a in test.Assertions)
            {
                if (a.Passed) continue;
                var msg = $"{a.Path} {a.Op} {Truncate(a.Expected, 80)}";
                w.WriteStartElement("failure");
                w.WriteAttributeString("message", msg);
                w.WriteAttributeString("type", "AssertionFailed");
                var detail = a.Error is not null
                    ? $"{msg}\n  error: {a.Error}"
                    : $"{msg}\n  expected: {a.Expected}\n  actual:   {a.ActualText}";
                w.WriteString(detail);
                w.WriteEndElement();
            }
        }

        w.WriteEndElement(); // testcase
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return string.Concat(s.AsSpan(0, max), "…");
    }
}
