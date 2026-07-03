// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Renders a test run as a SARIF 2.1.0 document so failures light up the
/// GitHub Code Scanning tab (and any other SARIF ingester — GitLab, Azure
/// DevOps). Same minimal-envelope approach as <c>bowire scan</c>'s SARIF
/// writer: only the fields the ingesters actually validate.
/// <para>
/// Location semantics: unlike <c>bowire scan</c> (DAST — no source file),
/// a test failure has a natural checkout-relative anchor — the collection
/// / flow JSON file the operator passed in. GitHub rejects absolute and
/// https URIs, so the path is relativised against the working directory
/// and falls back to the bare file name when the file lives outside it.
/// </para>
/// </summary>
internal static class TestSarifReport
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string RuleAssertionFailed = "assertion-failed";
    private const string RuleInvocationError = "invocation-error";
    private const string RuleExpectationFailed = "expectation-failed";
    private const string RuleStepError = "step-error";

    /// <summary>Render the legacy recording / test-collection run.</summary>
    public static string Render(RunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var uri = ToArtifactUri(report.CollectionPath);
        var results = new List<TestSarifResult>();

        foreach (var test in report.Tests)
        {
            if (!string.IsNullOrEmpty(test.Error))
            {
                results.Add(MakeResult(
                    RuleInvocationError, uri,
                    $"{test.Name}: invocation failed — {test.Error}",
                    fingerprint: $"{RuleInvocationError}@{report.CollectionPath}#{test.Name}"));
                continue;
            }
            foreach (var a in test.Assertions.Where(a => !a.Passed))
            {
                var detail = a.Error is not null
                    ? $"{a.Path} {a.Op} {a.Expected} — error: {a.Error}"
                    : $"{a.Path} {a.Op} {a.Expected} — actual: {a.ActualText}";
                results.Add(MakeResult(
                    RuleAssertionFailed, uri,
                    $"{test.Name}: {detail}",
                    fingerprint: $"{RuleAssertionFailed}@{report.CollectionPath}#{test.Name}/{a.Path} {a.Op}"));
            }
        }

        return Serialize(results);
    }

    /// <summary>Render the v2.2 Flow run.</summary>
    public static string Render(FlowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var uri = ToArtifactUri(report.FlowPath);
        var results = new List<TestSarifResult>();

        foreach (var step in report.Steps)
        {
            if (step.Skipped) continue;
            if (!string.IsNullOrEmpty(step.Error))
            {
                results.Add(MakeResult(
                    RuleStepError, uri,
                    $"{report.FlowName} / {step.StepId}: step errored — {step.Error}",
                    fingerprint: $"{RuleStepError}@{report.FlowPath}#{step.StepId}"));
                continue;
            }
            foreach (var e in step.Expectations.Where(e => !e.Passed))
            {
                results.Add(MakeResult(
                    RuleExpectationFailed, uri,
                    $"{report.FlowName} / {step.StepId}: {e.Message}",
                    fingerprint: $"{RuleExpectationFailed}@{report.FlowPath}#{step.StepId}/{e.Message}"));
            }
        }

        return Serialize(results);
    }

    /// <summary>
    /// Relativise a collection / flow path into a SARIF-safe artifact URI.
    /// GitHub's ingest rejects absolute paths and non-file URI schemes, so:
    /// under the working directory → relative with forward slashes;
    /// elsewhere → bare file name.
    /// </summary>
    internal static string ToArtifactUri(string path)
    {
        if (string.IsNullOrEmpty(path)) return "unknown";
        try
        {
            var full = Path.GetFullPath(path);
            var cwd = Directory.GetCurrentDirectory();
            var rel = Path.GetRelativePath(cwd, full);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                return Path.GetFileName(full);
            return rel.Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return Path.GetFileName(path);
        }
    }

    private static TestSarifResult MakeResult(string ruleId, string uri, string message, string fingerprint)
        => new()
        {
            RuleId = ruleId,
            Level = "error",
            Message = new TestSarifMessage { Text = message },
            Locations =
            [
                new TestSarifLocation
                {
                    PhysicalLocation = new TestSarifPhysicalLocation
                    {
                        ArtifactLocation = new TestSarifArtifactLocation { Uri = uri },
                    },
                },
            ],
            PartialFingerprints = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bowireTestCase"] = fingerprint,
            },
        };

    private static string Serialize(List<TestSarifResult> results)
    {
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
                            Name = "bowire-test",
                            InformationUri = "https://github.com/Kuestenlogik/Bowire",
                            Rules =
                            [
                                Rule(RuleAssertionFailed, "An assertion on a response did not hold."),
                                Rule(RuleInvocationError, "The call under test failed before assertions ran."),
                                Rule(RuleExpectationFailed, "A Flow expectation on a step's response did not hold."),
                                Rule(RuleStepError, "A Flow step errored before its expectations were evaluated."),
                            ],
                        },
                    },
                    Results = results,
                },
            ],
        };
        return JsonSerializer.Serialize(log, JsonOpts);
    }

    private static TestSarifRule Rule(string id, string description) => new()
    {
        Id = id,
        Name = id,
        ShortDescription = new TestSarifMessage { Text = description },
    };
}

/// <summary>
/// Emits GitHub Actions workflow-command annotations (<c>::error …</c>)
/// for every failure in a run, so failing tests surface inline on the PR
/// without any reporter action. Opt-in via <c>--annotations</c>; the
/// escaping rules follow the workflow-command spec (data: % → %25,
/// \r → %0D, \n → %0A; properties additionally : → %3A, , → %2C).
/// </summary>
internal static class GitHubAnnotations
{
    public static async Task WriteAsync(TextWriter stdout, RunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var file = TestSarifReport.ToArtifactUri(report.CollectionPath);
        foreach (var test in report.Tests)
        {
            if (!string.IsNullOrEmpty(test.Error))
            {
                await WriteErrorAsync(stdout, file, test.Name, $"invocation failed — {test.Error}").ConfigureAwait(false);
                continue;
            }
            foreach (var a in test.Assertions.Where(a => !a.Passed))
            {
                var detail = a.Error is not null
                    ? $"{a.Path} {a.Op} {a.Expected} — error: {a.Error}"
                    : $"{a.Path} {a.Op} {a.Expected} — actual: {a.ActualText}";
                await WriteErrorAsync(stdout, file, test.Name, detail).ConfigureAwait(false);
            }
        }
    }

    public static async Task WriteAsync(TextWriter stdout, FlowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var file = TestSarifReport.ToArtifactUri(report.FlowPath);
        foreach (var step in report.Steps)
        {
            if (step.Skipped) continue;
            if (!string.IsNullOrEmpty(step.Error))
            {
                await WriteErrorAsync(stdout, file, step.StepId, $"step errored — {step.Error}").ConfigureAwait(false);
                continue;
            }
            foreach (var e in step.Expectations.Where(e => !e.Passed))
            {
                await WriteErrorAsync(stdout, file, step.StepId, e.Message).ConfigureAwait(false);
            }
        }
    }

    private static Task WriteErrorAsync(TextWriter stdout, string file, string title, string message)
        => stdout.WriteLineAsync(
            $"::error file={EscapeProperty(file)},title={EscapeProperty(title)}::{EscapeData(message)}");

    private static string EscapeData(string s) => s
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);

    private static string EscapeProperty(string s) => EscapeData(s)
        .Replace(":", "%3A", StringComparison.Ordinal)
        .Replace(",", "%2C", StringComparison.Ordinal);
}

// ---- Minimal SARIF 2.1.0 POCOs (test-runner flavour) ----
// The scan-side POCOs live internal to Kuestenlogik.Bowire.Security.Scanner;
// duplicating the handful of envelope records here keeps the Tool free of a
// hard scanner dependency (third-party-deps-stay-optional rule).

internal sealed class TestSarifLog
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json";
    [JsonPropertyName("version")] public string Version { get; init; } = "2.1.0";
    [JsonPropertyName("runs")] public List<TestSarifRun> Runs { get; init; } = [];
}

internal sealed class TestSarifRun
{
    [JsonPropertyName("tool")] public TestSarifTool Tool { get; init; } = new();
    [JsonPropertyName("results")] public List<TestSarifResult> Results { get; init; } = [];
}

internal sealed class TestSarifTool { [JsonPropertyName("driver")] public TestSarifDriver Driver { get; init; } = new(); }

internal sealed class TestSarifDriver
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("informationUri")] public string InformationUri { get; init; } = "";
    [JsonPropertyName("rules")] public List<TestSarifRule> Rules { get; init; } = [];
}

internal sealed class TestSarifRule
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("shortDescription")] public TestSarifMessage ShortDescription { get; init; } = new();
}

internal sealed class TestSarifResult
{
    [JsonPropertyName("ruleId")] public string RuleId { get; init; } = "";
    [JsonPropertyName("level")] public string Level { get; init; } = "error";
    [JsonPropertyName("message")] public TestSarifMessage Message { get; init; } = new();
    [JsonPropertyName("locations")] public List<TestSarifLocation> Locations { get; init; } = [];
    [JsonPropertyName("partialFingerprints")] public Dictionary<string, string>? PartialFingerprints { get; init; }
}

internal sealed class TestSarifMessage { [JsonPropertyName("text")] public string Text { get; init; } = ""; }

internal sealed class TestSarifLocation
{
    [JsonPropertyName("physicalLocation")] public TestSarifPhysicalLocation PhysicalLocation { get; init; } = new();
}

internal sealed class TestSarifPhysicalLocation
{
    [JsonPropertyName("artifactLocation")] public TestSarifArtifactLocation ArtifactLocation { get; init; } = new();
}

internal sealed class TestSarifArtifactLocation { [JsonPropertyName("uri")] public string Uri { get; init; } = ""; }
