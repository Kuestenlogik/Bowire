// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Kuestenlogik.Bowire.Flows.Expectations;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Render a <see cref="FlowRunReport"/> as a self-contained HTML document.
/// All CSS is inlined — no external assets — so the file works as a CI
/// artifact when the operator downloads it from Jenkins / GitHub-Actions
/// and double-clicks it. Layout intentionally minimal: header → summary
/// counts → per-step section with request/response excerpts + expectation
/// rows → footer with the exit code so the page is also useful via
/// view-source for grepping in a terminal.
/// </summary>
internal static class FlowHtmlReport
{
    public static string Render(FlowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder(8 * 1024);

        var summaryClass = report.FailedExpectations > 0 || report.StepErrors > 0 ? "fail" : "pass";

        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<title>Bowire Flow Report — ").Append(EscapeHtml(report.FlowName)).Append("</title>");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<style>").Append(Css).Append("</style></head><body>");

        sb.Append("<header><div class=\"title\"><span class=\"logo\">⚓</span> Bowire Flow Report</div>");
        sb.Append("<div class=\"meta\">");
        sb.Append("<div><span class=\"label\">Flow</span> ").Append(EscapeHtml(report.FlowName)).Append("</div>");
        sb.Append("<div><span class=\"label\">Source</span> ").Append(EscapeHtml(report.FlowPath)).Append("</div>");
        sb.Append("<div><span class=\"label\">Started</span> ").Append(report.StartedAt.ToString("u", CultureInfo.InvariantCulture)).Append("</div>");
        sb.Append("<div><span class=\"label\">Duration</span> ").Append(report.DurationMs).Append(" ms</div>");
        sb.Append("</div></header>");

        sb.Append("<section class=\"summary ").Append(summaryClass).Append("\">");
        sb.Append("<div class=\"stat\"><div class=\"stat-value\">").Append(report.PassedExpectations).Append('/').Append(report.TotalExpectations).Append("</div><div class=\"stat-label\">Expectations passed</div></div>");
        sb.Append("<div class=\"stat\"><div class=\"stat-value\">").Append(report.Steps.Count - report.StepErrors).Append('/').Append(report.Steps.Count).Append("</div><div class=\"stat-label\">Steps invoked</div></div>");
        sb.Append("<div class=\"stat\"><div class=\"stat-value\">").Append(report.FailedExpectations).Append("</div><div class=\"stat-label\">Failed expectations</div></div>");
        sb.Append("</section>");

        sb.Append("<main>");
        foreach (var step in report.Steps)
        {
            var failed = !string.IsNullOrEmpty(step.Error) || step.Expectations.Any(e => !e.Passed);
            var stepClass = step.Skipped ? "skip" : (failed ? "fail" : "pass");
            sb.Append("<article class=\"step ").Append(stepClass).Append("\">");
            sb.Append("<header class=\"step-header\">");
            sb.Append("<span class=\"icon\">").Append(step.Skipped ? "−" : failed ? "✗" : "✓").Append("</span>");
            sb.Append("<span class=\"name\">").Append(EscapeHtml(step.StepId)).Append("</span>");
            if (!step.Skipped)
            {
                sb.Append("<span class=\"endpoint\">").Append(EscapeHtml(step.Service)).Append(" / ").Append(EscapeHtml(step.Method)).Append("</span>");
                sb.Append("<span class=\"status\">").Append(EscapeHtml(step.Status ?? "")).Append("</span>");
                sb.Append("<span class=\"duration\">").Append(step.LatencyMs).Append(" ms</span>");
            }
            else
            {
                sb.Append("<span class=\"endpoint\">").Append(EscapeHtml(step.StepType)).Append(" — skipped (control-flow node)</span>");
            }
            sb.Append("</header>");

            if (!string.IsNullOrEmpty(step.Error))
            {
                sb.Append("<div class=\"error\">").Append(EscapeHtml(step.Error)).Append("</div>");
            }

            // Response excerpt — first 800 chars, monospaced. Lets the
            // operator eyeball the actual payload without opening a
            // second tool.
            if (!step.Skipped && !string.IsNullOrEmpty(step.ResponseBody))
            {
                sb.Append("<details class=\"response\"><summary>Response excerpt</summary><pre><code>")
                  .Append(EscapeHtml(Trunc(step.ResponseBody, 800)))
                  .Append("</code></pre></details>");
            }

            if (step.Expectations.Count > 0)
            {
                sb.Append("<ul class=\"expectations\">");
                foreach (var exp in step.Expectations)
                {
                    var cls = exp.Passed ? "pass" : "fail";
                    sb.Append("<li class=\"").Append(cls).Append("\">");
                    sb.Append("<span class=\"icon\">").Append(exp.Passed ? "✓" : "✗").Append("</span>");
                    sb.Append("<code>").Append(EscapeHtml(exp.Message)).Append("</code>");
                    if (!exp.Passed)
                    {
                        sb.Append("<div class=\"actual\">expected: <code>").Append(EscapeHtml(exp.Expected ?? "")).Append("</code> · actual: <code>").Append(EscapeHtml(Trunc(exp.Actual ?? "", 200))).Append("</code></div>");
                    }
                    sb.Append("</li>");
                }
                sb.Append("</ul>");
            }

            sb.Append("</article>");
        }
        sb.Append("</main>");

        sb.Append("<footer>Duration: ").Append(report.DurationMs).Append(" ms · Exit code: ").Append(report.ExitCode).Append(" · Generated by Bowire</footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string EscapeHtml(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string Trunc(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return string.Concat(s.AsSpan(0, max), "…");
    }

    private const string Css = """
        :root {
            --bg:#0f0f17;--bg-elev:#161621;--surface:#1a1a2e;
            --text:#e8e8f0;--text-2:#9898b0;--text-3:#6a6a82;
            --border:#2a2a3d;
            --success:#34d399;--success-bg:rgba(52,211,153,.12);
            --error:#f87171;--error-bg:rgba(248,113,113,.12);
            --skip:#a5b4fc;--skip-bg:rgba(165,180,252,.12);
            --accent:#6366f1;--accent-2:#a5b4fc;
            --mono:'JetBrains Mono','Fira Code','Cascadia Code',Consolas,monospace;
            --sans:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;
        }
        *{box-sizing:border-box;}
        body{margin:0;padding:24px;font-family:var(--sans);font-size:14px;color:var(--text);background:var(--bg);}
        header{max-width:1100px;margin:0 auto 18px;}
        header .title{font-size:22px;font-weight:700;margin-bottom:14px;}
        header .logo{display:inline-block;margin-right:8px;color:var(--accent-2);}
        header .meta{display:grid;grid-template-columns:repeat(2,1fr);gap:6px 24px;font-size:12px;color:var(--text-2);}
        header .meta .label{display:inline-block;width:80px;color:var(--text-3);text-transform:uppercase;font-size:10px;letter-spacing:.04em;font-weight:600;}
        section.summary{max-width:1100px;margin:0 auto 24px;display:grid;grid-template-columns:repeat(3,1fr);gap:12px;padding:16px;background:var(--bg-elev);border:1px solid var(--border);border-radius:8px;}
        section.summary.pass{border-left:3px solid var(--success);}
        section.summary.fail{border-left:3px solid var(--error);}
        section.summary .stat{text-align:center;}
        section.summary .stat-value{font-size:28px;font-weight:700;font-family:var(--mono);}
        section.summary.pass .stat-value{color:var(--success);}
        section.summary.fail .stat-value{color:var(--error);}
        section.summary .stat-label{font-size:11px;color:var(--text-3);text-transform:uppercase;letter-spacing:.04em;margin-top:4px;}
        main{max-width:1100px;margin:0 auto;}
        article.step{background:var(--bg-elev);border:1px solid var(--border);border-left-width:3px;border-radius:8px;padding:12px 16px;margin-bottom:10px;}
        article.step.pass{border-left-color:var(--success);}
        article.step.fail{border-left-color:var(--error);}
        article.step.skip{border-left-color:var(--skip);}
        article.step .step-header{display:flex;align-items:center;gap:12px;flex-wrap:wrap;}
        article.step .step-header .icon{font-size:16px;font-weight:700;}
        article.step.pass .icon{color:var(--success);}
        article.step.fail .icon{color:var(--error);}
        article.step.skip .icon{color:var(--skip);}
        article.step .step-header .name{font-weight:600;flex:1;min-width:200px;}
        article.step .step-header .endpoint{font-family:var(--mono);font-size:11px;color:var(--text-2);}
        article.step .step-header .status{font-family:var(--mono);font-size:11px;padding:2px 8px;background:var(--surface);border-radius:4px;color:var(--text-2);}
        article.step .step-header .duration{font-family:var(--mono);font-size:11px;color:var(--text-3);}
        article.step .error{color:var(--error);background:var(--error-bg);padding:8px 12px;border-radius:4px;margin-top:8px;font-family:var(--mono);font-size:11px;}
        article.step .response{margin-top:8px;}
        article.step .response summary{cursor:pointer;font-size:11px;color:var(--text-3);text-transform:uppercase;letter-spacing:.04em;}
        article.step .response pre{margin:6px 0 0;padding:8px 10px;background:var(--surface);border-radius:4px;overflow:auto;}
        article.step .response code{font-family:var(--mono);font-size:11px;color:var(--text-2);white-space:pre-wrap;}
        article.step ul.expectations{list-style:none;padding:0;margin:8px 0 0;display:flex;flex-direction:column;gap:4px;}
        article.step ul.expectations li{padding:6px 10px;border-radius:4px;display:flex;align-items:center;flex-wrap:wrap;gap:8px;font-size:12px;}
        article.step ul.expectations li.pass{background:var(--success-bg);}
        article.step ul.expectations li.fail{background:var(--error-bg);}
        article.step ul.expectations li .icon{font-weight:700;}
        article.step ul.expectations li.pass .icon{color:var(--success);}
        article.step ul.expectations li.fail .icon{color:var(--error);}
        article.step ul.expectations li code{font-family:var(--mono);color:var(--text);background:rgba(0,0,0,.25);padding:1px 6px;border-radius:3px;font-size:11px;}
        article.step ul.expectations li .actual{width:100%;font-family:var(--mono);font-size:11px;color:var(--text-2);margin-left:24px;}
        footer{max-width:1100px;margin:24px auto 0;text-align:center;font-size:11px;color:var(--text-3);}
    """;
}
