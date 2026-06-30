// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Xml;
using Kuestenlogik.Bowire.Flows.Expectations;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Render a <see cref="FlowRunReport"/> as a JUnit Surefire XML document.
/// One <c>&lt;testcase&gt;</c> per (step, expectation) pair so a CI's test
/// reporter shows individual expectation rows rather than collapsed
/// step-level pass/fail. Step-level invocation errors collapse to a
/// single error case so the rest of the rollup stays addressable.
/// </summary>
/// <remarks>
/// Schema follows the de-facto Surefire shape Jenkins / GitLab CI / Azure
/// DevOps / GitHub Actions test-reporter all accept:
/// <code>
/// &lt;testsuites name=… tests=N failures=K errors=M time=s&gt;
///   &lt;testsuite name=… tests=N failures=K errors=M skipped=0 time=s timestamp=…&gt;
///     &lt;testcase classname=&lt;flow.name&gt; name=&lt;step.id&gt;::&lt;exp.kind&gt;:&lt;op&gt;:&lt;target&gt; time=ms&gt;
///       &lt;failure message=… type="AssertionFailed"&gt;…&lt;/failure&gt;  ← only on fail
///       &lt;error   message=… type="StepError"&gt;…&lt;/error&gt;          ← only on step error
///     &lt;/testcase&gt;
///   &lt;/testsuite&gt;
/// &lt;/testsuites&gt;
/// </code>
/// </remarks>
internal static class FlowJUnitReport
{
    public static string Render(FlowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false,
        };

        var totalSeconds = (report.DurationMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
        var totalTests = report.Steps.Sum(s => Math.Max(s.Expectations.Count, s.Skipped ? 1 : (string.IsNullOrEmpty(s.Error) ? 0 : 1)));
        if (totalTests == 0) totalTests = report.Steps.Count;

        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
        using (var w = XmlWriter.Create(sw, settings))
        {
            w.WriteStartElement("testsuites");
            w.WriteAttributeString("name", report.FlowName);
            w.WriteAttributeString("tests", totalTests.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("failures", report.FailedExpectations.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("errors", report.StepErrors.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("time", totalSeconds);

            w.WriteStartElement("testsuite");
            w.WriteAttributeString("name", report.FlowName);
            w.WriteAttributeString("tests", totalTests.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("failures", report.FailedExpectations.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("errors", report.StepErrors.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("skipped", report.Steps.Count(s => s.Skipped).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("time", totalSeconds);
            w.WriteAttributeString("timestamp", report.StartedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));

            foreach (var step in report.Steps)
            {
                WriteStepCases(w, step, report.FlowName);
            }

            w.WriteEndElement(); // testsuite
            w.WriteEndElement(); // testsuites
        }
        return sb.ToString();
    }

    private static void WriteStepCases(XmlWriter w, FlowStepRunResult step, string flowName)
    {
        var stepSeconds = (step.LatencyMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);

        // Step error → single <testcase> wrapping a single <error>.
        // Expectation-level cases would be misleading because the
        // invocation never happened.
        if (!string.IsNullOrEmpty(step.Error))
        {
            w.WriteStartElement("testcase");
            w.WriteAttributeString("classname", flowName);
            w.WriteAttributeString("name", step.StepId + " :: invocation");
            w.WriteAttributeString("time", stepSeconds);
            w.WriteStartElement("error");
            w.WriteAttributeString("message", step.Error);
            w.WriteAttributeString("type", "StepError");
            w.WriteString(step.Error);
            w.WriteEndElement();
            w.WriteEndElement();
            return;
        }

        // Skipped control-flow step — emit a marker case so the suite
        // count stays consistent with the run summary.
        if (step.Skipped)
        {
            w.WriteStartElement("testcase");
            w.WriteAttributeString("classname", flowName);
            w.WriteAttributeString("name", step.StepId + " :: " + step.StepType);
            w.WriteAttributeString("time", "0");
            w.WriteStartElement("skipped");
            w.WriteAttributeString("message", "Control-flow node — not replayed by T2 v0.");
            w.WriteEndElement();
            w.WriteEndElement();
            return;
        }

        // No expectations on a successful step → emit one synthetic
        // pass-case so CI reporters can see the step ran.
        if (step.Expectations.Count == 0)
        {
            w.WriteStartElement("testcase");
            w.WriteAttributeString("classname", flowName);
            w.WriteAttributeString("name", step.StepId + " :: invocation");
            w.WriteAttributeString("time", stepSeconds);
            w.WriteEndElement();
            return;
        }

        foreach (var exp in step.Expectations)
        {
            var label = $"{step.StepId} :: {KindLabel(exp.Kind)}:{OperatorLabel(exp.Operator)}";
            w.WriteStartElement("testcase");
            w.WriteAttributeString("classname", flowName);
            w.WriteAttributeString("name", label);
            // Per-expectation we don't have a per-row clock; carry the
            // step's wall-clock so the suite total stays consistent.
            w.WriteAttributeString("time", "0");
            if (!exp.Passed)
            {
                w.WriteStartElement("failure");
                w.WriteAttributeString("message", exp.Message);
                w.WriteAttributeString("type", "AssertionFailed");
                var detail = new StringBuilder()
                    .Append(exp.Message).Append('\n')
                    .Append("  expected: ").Append(exp.Expected ?? "<null>").Append('\n')
                    .Append("  actual:   ").Append(exp.Actual ?? "<null>")
                    .ToString();
                w.WriteString(detail);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
    }

    private static string KindLabel(FlowExpectationKind kind) => kind switch
    {
        FlowExpectationKind.Status => "status",
        FlowExpectationKind.Header => "header",
        FlowExpectationKind.BodyPath => "body-path",
        FlowExpectationKind.BodyText => "body-text",
        FlowExpectationKind.Latency => "latency",
        _ => "?",
    };

    private static string OperatorLabel(FlowExpectationOperator op) => op switch
    {
        FlowExpectationOperator.Equals => "equals",
        FlowExpectationOperator.NotEquals => "not-equals",
        FlowExpectationOperator.Contains => "contains",
        FlowExpectationOperator.GreaterThan => "gt",
        FlowExpectationOperator.GreaterThanOrEquals => "gte",
        FlowExpectationOperator.LessThan => "lt",
        FlowExpectationOperator.LessThanOrEquals => "lte",
        FlowExpectationOperator.Exists => "exists",
        FlowExpectationOperator.NotExists => "not-exists",
        FlowExpectationOperator.Regex => "regex",
        _ => "?",
    };
}
