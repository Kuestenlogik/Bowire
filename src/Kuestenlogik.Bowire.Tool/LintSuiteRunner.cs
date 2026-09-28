// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Linting;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// <c>bowire test --suite=lint</c> — the design-time rules, run through the
/// test runner's front door (#583).
/// </summary>
/// <remarks>
/// <para>
/// <c>bowire lint --fail-on high</c> already gates a pipeline on findings. What
/// this adds is not a second spelling of that but the test runner's report
/// sinks: a CI job that already collects <c>--junit</c> and <c>--sarif</c> from
/// <c>bowire test</c> gets its lint findings in the same places, next to its
/// test failures, without a second step that knows a second set of flags.
/// </para>
/// <para>
/// The gate reads <c>--fail-on</c> the way <c>bowire test</c> does —
/// <c>any</c> (the default) fails on any finding, <c>never</c> reports and
/// exits 0 — and also takes <c>bowire lint</c>'s severities, so
/// <c>--fail-on high</c> means the same in both commands.
/// </para>
/// </remarks>
internal static class LintSuiteRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(
        TestCliOptions cli, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var source = cli.CollectionPath;
        if (string.IsNullOrWhiteSpace(source))
        {
            await stderr.WriteLineAsync(
                "Usage: bowire test --suite=lint <snapshot|url> [--fail-on any|never|info|low|medium|high] [--junit path.xml] [--sarif path.sarif] [--annotations]")
                .ConfigureAwait(false);
            return 2;
        }

        if (!TryGate(cli.FailOn, out var gate))
        {
            await stderr.WriteLineAsync(
                $"bowire test --suite=lint: --fail-on '{cli.FailOn}' is not one of any, never, info, low, medium, high.")
                .ConfigureAwait(false);
            return 2;
        }

        var (config, configFailed) = await LintCommand.LoadConfigAsync(null, stderr).ConfigureAwait(false);
        if (configFailed) return 2;

        var services = await CliSchemaSnapshot.ResolveAsync(source, null, stderr, ct).ConfigureAwait(false);
        if (services is null) return 2;

        var linter = BowireSchemaLinter.CreateWithDiscoveredRules();
        var findings = linter.Lint(services, config);
        var rules = BowireSchemaLinter.DiscoverRules()
            .Where(r => config is null || config.IsEnabled(r.Id))
            .ToList();

        await stdout.WriteAsync(LintCommand.ToText(findings, LintCommand.ResponseCoverageNote(services)))
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(cli.JUnitPath))
        {
            await File.WriteAllTextAsync(cli.JUnitPath, RenderJUnit(rules, findings, gate, source), ct)
                .ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(cli.SarifPath))
        {
            await File.WriteAllTextAsync(cli.SarifPath, RenderSarif(rules, findings, source), ct)
                .ConfigureAwait(false);
        }
        if (cli.Annotations)
        {
            foreach (var f in findings)
                await stdout.WriteLineAsync(Annotation(f, gate)).ConfigureAwait(false);
        }

        return gate is { } g && findings.Any(f => f.Severity >= g) ? 1 : 0;
    }

    /// <summary>
    /// The threshold a finding has to reach to fail the run, or null for
    /// "never". False when the value is neither a test nor a lint word — an
    /// unrecognised gate is refused rather than read as "never", which would
    /// quietly turn a misspelt CI step green.
    /// </summary>
    internal static bool TryGate(string? failOn, out BowireLintSeverity? gate)
    {
        gate = null;
        var value = string.IsNullOrWhiteSpace(failOn) ? "any" : failOn.Trim();
        if (string.Equals(value, "any", StringComparison.OrdinalIgnoreCase))
        {
            gate = BowireLintSeverity.Info;
            return true;
        }
        if (string.Equals(value, "never", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        gate = LintCommand.ParseThreshold(value);
        return gate is not null;
    }

    // ---- JUnit ----------------------------------------------------------

    /// <summary>
    /// One test case per enabled rule: it fails when that rule found anything
    /// at or above the gate, and passes otherwise. Per rule rather than per
    /// finding, so the case count is stable from run to run and a CI view that
    /// tracks "newly failing tests" tracks rules going red, not an ever-changing
    /// list of field names. Findings below the gate are listed in the case's
    /// output, where they can be read without failing anything.
    /// </summary>
    internal static string RenderJUnit(
        IReadOnlyList<IBowireLintRule> rules, IReadOnlyList<BowireLintFinding> findings,
        BowireLintSeverity? gate, string source)
    {
        var failures = 0;
        var cases = new StringBuilder();
        foreach (var rule in rules)
        {
            var own = findings.Where(f => string.Equals(f.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var failing = own.Where(f => gate is { } g && f.Severity >= g).ToList();
            var name = SecurityElement.Escape($"{rule.Id} — {rule.Title}");
            cases.Append(CultureInfo.InvariantCulture,
                $"    <testcase classname=\"bowire.lint\" name=\"{name}\">\n");
            if (failing.Count > 0)
            {
                failures++;
                var message = SecurityElement.Escape(
                    $"{failing.Count} finding{(failing.Count == 1 ? "" : "s")} at or above {gate}");
                cases.Append(CultureInfo.InvariantCulture, $"      <failure message=\"{message}\">");
                cases.Append(SecurityElement.Escape(string.Join("\n", failing.Select(Line))));
                cases.Append("</failure>\n");
            }
            var below = own.Except(failing).ToList();
            if (below.Count > 0)
            {
                cases.Append("      <system-out>");
                cases.Append(SecurityElement.Escape(string.Join("\n", below.Select(Line))));
                cases.Append("</system-out>\n");
            }
            cases.Append("    </testcase>\n");
        }

        var suite = SecurityElement.Escape(source);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<testsuites>\n"
            + string.Create(CultureInfo.InvariantCulture,
                $"  <testsuite name=\"bowire-lint: {suite}\" tests=\"{rules.Count}\" failures=\"{failures}\" errors=\"0\">\n")
            + cases
            + "  </testsuite>\n"
            + "</testsuites>\n";
    }

    // ---- SARIF ----------------------------------------------------------

    /// <summary>
    /// Every finding, at the SARIF level its severity maps to — High is an
    /// error, Medium a warning, Low and Info notes. The gate does not filter
    /// here: Code Scanning shows levels on its own, and a finding hidden from
    /// the report because it would not fail the build is a finding nobody
    /// sees. Where it is in the API travels as a logical location; the
    /// physical one is the snapshot file when there is one, the same
    /// placeholder <c>bowire scan</c> uses when the source was a URL.
    /// </summary>
    internal static string RenderSarif(
        IReadOnlyList<IBowireLintRule> rules, IReadOnlyList<BowireLintFinding> findings, string source)
    {
        var uri = File.Exists(source) ? TestSarifReport.ToArtifactUri(source) : PlaceholderPath();
        var results = findings.Select(f => new TestSarifResult
        {
            RuleId = f.RuleId,
            Level = f.Severity switch
            {
                BowireLintSeverity.High => "error",
                BowireLintSeverity.Medium => "warning",
                _ => "note",
            },
            Message = new TestSarifMessage { Text = f.Message },
            Locations =
            [
                new TestSarifLocation
                {
                    PhysicalLocation = new TestSarifPhysicalLocation
                    {
                        ArtifactLocation = new TestSarifArtifactLocation { Uri = uri },
                    },
                    LogicalLocations =
                    [
                        new TestSarifLogicalLocation
                        {
                            Name = f.Field ?? f.Method ?? f.Service,
                            FullyQualifiedName = Where(f),
                            Kind = f.Field is not null ? "member" : f.Method is not null ? "function" : "type",
                        },
                    ],
                },
            ],
            PartialFingerprints = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bowireLint"] = $"{f.RuleId}@{Where(f)}",
            },
        }).ToList();

        var log = new TestSarifLog
        {
            Runs =
            [
                new TestSarifRun
                {
                    Tool = new TestSarifTool
                    {
                        Driver = new TestSarifDriver
                        {
                            Name = "bowire-lint",
                            InformationUri = "https://github.com/Kuestenlogik/Bowire",
                            Rules = [.. rules.Select(r => new TestSarifRule
                            {
                                Id = r.Id,
                                Name = r.Id,
                                ShortDescription = new TestSarifMessage { Text = r.Title },
                            })],
                        },
                    },
                    Results = results,
                },
            ],
        };
        return JsonSerializer.Serialize(log, JsonOpts);
    }

    /// <summary>The workflow file on Actions, a bare token elsewhere — as <c>bowire scan</c> does.</summary>
    private static string PlaceholderPath()
    {
        var workflowRef = Environment.GetEnvironmentVariable("GITHUB_WORKFLOW_REF");
        if (string.IsNullOrWhiteSpace(workflowRef)) return "bowire-lint";
        var parts = workflowRef.Split('@', 2)[0].Split('/');
        if (parts.Length <= 2) return "bowire-lint";
        var relative = string.Join('/', parts.Skip(2));
        return string.IsNullOrWhiteSpace(relative) ? "bowire-lint" : relative;
    }

    // ---- annotations ----------------------------------------------------

    /// <summary>A finding that would fail the run is an error; one below the gate a warning.</summary>
    internal static string Annotation(BowireLintFinding f, BowireLintSeverity? gate)
    {
        var level = gate is { } g && f.Severity >= g ? "error" : "warning";
        return $"::{level} title={Escape(f.RuleId, property: true)}::{Escape(Where(f) + ": " + f.Message, property: false)}";
    }

    private static string Escape(string s, bool property)
    {
        var data = s.Replace("%", "%25", StringComparison.Ordinal)
                    .Replace("\r", "%0D", StringComparison.Ordinal)
                    .Replace("\n", "%0A", StringComparison.Ordinal);
        return property
            ? data.Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal)
            : data;
    }

    private static string Where(BowireLintFinding f)
        => f.Service
            + (f.Method is null ? "" : "/" + f.Method)
            + (f.Field is null ? "" : "." + f.Field);

    private static string Line(BowireLintFinding f)
        => $"[{f.Severity}] {Where(f)}: {f.Message}";
}
