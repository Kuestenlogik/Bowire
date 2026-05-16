// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Implementation of <c>bowire scan</c> — the Tier-1 anchor of the
/// security-testing lane (<c>docs/architecture/security-testing.md</c>).
/// Loads vulnerability templates (recordings flagged
/// <see cref="BowireRecording.Attack"/>), replays each one's probe
/// against a target URL, evaluates <see cref="AttackPredicate"/> against
/// the response, and emits findings.
/// </summary>
/// <remarks>
/// <para>
/// v1 scope: HTTP-class probes only (<c>rest</c>, <c>graphql</c>,
/// <c>odata</c>, <c>http</c>). gRPC / SignalR / WebSocket / MQTT probes
/// surface as <see cref="ScanFindingStatus.Skipped"/> with a
/// "transport not yet supported by scanner" message — the templates
/// still load, they just don't run yet. Later iterations route
/// non-HTTP probes through the corresponding protocol plugin's
/// invoke path.
/// </para>
/// </remarks>
internal static class ScanCommand
{
    /// <summary>Loaded JSON options shared across template parse + finding write.</summary>
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One scan-subcommand invocation. Returns the process exit code (0 = clean, 1 = at least one finding above the severity threshold, 2 = usage error).</summary>
    public static async Task<int> RunAsync(ScanOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Target))
        {
            await Console.Error.WriteLineAsync("  Usage: bowire scan --target <url> --corpus <dir> [--template <file>] [--out <sarif>]").ConfigureAwait(false);
            return 2;
        }

        // Collect templates from --corpus (directory) and/or --template (single file).
        var templates = new List<LoadedTemplate>();
        foreach (var path in EnumerateTemplatePaths(options))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var rec = JsonSerializer.Deserialize<BowireRecording>(raw, s_jsonOpts);
                if (rec is null) continue;
                if (!rec.Attack) continue;
                if (rec.Vulnerability is null || rec.VulnerableWhen is null) continue;
                if (rec.Steps.Count == 0) continue;
                templates.Add(new LoadedTemplate(path, rec));
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"  Skipping {Path.GetFileName(path)}: {ex.Message}").ConfigureAwait(false);
            }
        }

        if (templates.Count == 0 && !options.RunBuiltins)
        {
            await Console.Error.WriteLineAsync("  No vulnerability templates found and built-ins disabled. Provide --corpus <dir> or --template <file>, OR drop --no-builtins.").ConfigureAwait(false);
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"  Scanning {options.Target}");
        var pieces = $"{templates.Count} template(s) loaded";
        if (options.RunBuiltins) pieces += " + built-in checks (TLS / banner / verbose-errors)";
        Console.WriteLine($"  {pieces}; min severity = {options.MinSeverity ?? "any"}");
        Console.WriteLine();

        var minRank = SeverityRank(options.MinSeverity ?? "");
        var findings = new List<ScanFinding>();
        using var http = BuildHttpClient(options);

        foreach (var tmpl in templates)
        {
            var severity = tmpl.Recording.Vulnerability?.Severity ?? "medium";
            if (SeverityRank(severity) < minRank)
            {
                findings.Add(ScanFinding.Skipped(tmpl, "below severity threshold"));
                continue;
            }

            var probe = tmpl.Recording.Steps[0];
            var protocol = (probe.Protocol ?? string.Empty).ToUpperInvariant();
            if (!IsHttpClassProtocol(protocol))
            {
                findings.Add(ScanFinding.Skipped(tmpl, $"transport {probe.Protocol} not yet supported by scanner (v1 covers HTTP-class only)"));
                continue;
            }

            try
            {
                var response = await SendHttpProbeAsync(http, options.Target, probe, ct).ConfigureAwait(false);
                var matched = AttackPredicateEvaluator.Evaluate(tmpl.Recording.VulnerableWhen!, response);
                findings.Add(matched
                    ? ScanFinding.Vulnerable(tmpl, response)
                    : ScanFinding.Safe(tmpl, response));
            }
            catch (Exception ex)
            {
                findings.Add(ScanFinding.Error(tmpl, ex.Message));
            }
        }

        if (options.RunBuiltins)
        {
            var builtinResults = await SecurityBuiltins.RunAllAsync(options.Target, http, ct).ConfigureAwait(false);
            foreach (var f in builtinResults)
            {
                var sev = f.Template.Recording.Vulnerability?.Severity ?? "info";
                if (SeverityRank(sev) < minRank && f.Status == ScanFindingStatus.Vulnerable)
                {
                    findings.Add(new ScanFinding
                    {
                        Template = f.Template,
                        Status = ScanFindingStatus.Skipped,
                        Detail = "below severity threshold",
                    });
                }
                else
                {
                    findings.Add(f);
                }
            }
        }

        WriteConsoleReport(findings);

        if (!string.IsNullOrEmpty(options.OutSarif))
        {
            await WriteSarifAsync(options.OutSarif, options.Target, findings, ct).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine($"  SARIF report → {options.OutSarif}");
        }

        var anyVulnerable = findings.Exists(f => f.Status == ScanFindingStatus.Vulnerable);
        return anyVulnerable ? 1 : 0;
    }

    private static IEnumerable<string> EnumerateTemplatePaths(ScanOptions options)
    {
        if (!string.IsNullOrEmpty(options.Template) && File.Exists(options.Template))
            yield return options.Template;
        if (!string.IsNullOrEmpty(options.Corpus) && Directory.Exists(options.Corpus))
        {
            foreach (var p in Directory.EnumerateFiles(options.Corpus, "*.json", SearchOption.AllDirectories))
                yield return p;
        }
    }

    private static int SeverityRank(string severity) => severity.ToUpperInvariant() switch
    {
        "CRITICAL" => 4,
        "HIGH" => 3,
        "MEDIUM" => 2,
        "LOW" => 1,
        _ => 0,
    };

    private static bool IsHttpClassProtocol(string protocol) => protocol switch
    {
        "REST" or "GRAPHQL" or "ODATA" or "HTTP" or "SSE" => true,
        _ => false,
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient(handler, disposeHandler: true) takes ownership — the handler is disposed when the HttpClient is.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5400:HttpClient may be created without enabling CheckCertificateRevocationList",
        Justification = "CheckCertificateRevocationList is set explicitly below when the operator hasn't opted into self-signed certs.")]
    private static HttpClient BuildHttpClient(ScanOptions options)
    {
        var handler = new HttpClientHandler();
        if (options.AllowSelfSignedCerts)
        {
            // Operator explicitly opted into accepting self-signed
            // certs (typically for dev/staging probes). Revocation
            // check is moot since the chain isn't trusted anyway.
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        else
        {
            handler.CheckCertificateRevocationList = true;
        }
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
    }

    private static async Task<AttackProbeResponse> SendHttpProbeAsync(HttpClient http, string target, BowireRecordingStep probe, CancellationToken ct)
    {
        var basePath = probe.HttpPath ?? "/";
        var url = CombineUrl(target, basePath);
        var verb = (probe.HttpVerb ?? "GET").ToUpperInvariant();
        using var req = new HttpRequestMessage(new HttpMethod(verb), url);

        if (probe.Metadata is { Count: > 0 } md)
        {
            foreach (var (k, v) in md)
            {
                if (!req.Headers.TryAddWithoutValidation(k, v))
                {
                    // Headers like Content-Type ride on the content, not the request — handled below.
                }
            }
        }

        if (!string.IsNullOrEmpty(probe.Body) && verb is "POST" or "PUT" or "PATCH" or "DELETE")
        {
            req.Content = new StringContent(probe.Body, Encoding.UTF8, "application/json");
        }

        var sw = Stopwatch.StartNew();
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        sw.Stop();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers) headers[h.Key] = string.Join(",", h.Value);
        foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(",", h.Value);

        return new AttackProbeResponse
        {
            Status = (int)resp.StatusCode,
            Headers = headers,
            Body = body,
            LatencyMs = (int)sw.ElapsedMilliseconds,
        };
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = string.IsNullOrEmpty(path) ? "/" : (path.StartsWith('/') ? path : "/" + path);
        return b + p;
    }

    private static void WriteConsoleReport(List<ScanFinding> findings)
    {
        Console.WriteLine($"  {findings.Count} template(s) processed:");
        foreach (var f in findings)
        {
            var marker = f.Status switch
            {
                ScanFindingStatus.Vulnerable => "[VULN]",
                ScanFindingStatus.Safe => "[ok]  ",
                ScanFindingStatus.Skipped => "[skip]",
                ScanFindingStatus.Error => "[err] ",
                _ => "[?]   ",
            };
            var sev = f.Template.Recording.Vulnerability?.Severity ?? "-";
            var id = f.Template.Recording.Vulnerability?.Id ?? "(no-id)";
            var title = f.Template.Recording.Vulnerability is { } v && !string.IsNullOrEmpty(v.Id)
                ? f.Template.Recording.Name
                : Path.GetFileNameWithoutExtension(f.Template.Path);
            Console.WriteLine($"  {marker} {id,-22} {sev,-8} {title}");
            if (!string.IsNullOrEmpty(f.Detail)) Console.WriteLine($"          {f.Detail}");
        }
        Console.WriteLine();
        var vulnCount = findings.FindAll(f => f.Status == ScanFindingStatus.Vulnerable).Count;
        if (vulnCount > 0)
        {
            Console.WriteLine($"  {vulnCount} vulnerability finding(s).");
        }
        else
        {
            Console.WriteLine("  No vulnerabilities matched. (Negative results don't prove security — just that this corpus didn't catch anything.)");
        }
    }

    private static async Task WriteSarifAsync(string path, string target, List<ScanFinding> findings, CancellationToken ct)
    {
        // Minimal SARIF 2.1.0 envelope, only the fields GitHub Code
        // Scanning / GitLab Security Dashboard / Azure DevOps Security
        // need to ingest the findings. Tool-component is "bowire scan";
        // every finding becomes a result with the template id as the
        // ruleId so the CI dashboard groups them.
        var sarif = new SarifLog
        {
            Runs =
            [
                new SarifRun
                {
                    Tool = new SarifTool
                    {
                        Driver = new SarifDriver
                        {
                            Name = "bowire-scan",
                            InformationUri = "https://github.com/Kuestenlogik/Bowire",
                            Rules = ExtractRules(findings),
                        },
                    },
                    Results = findings
                        .Where(f => f.Status == ScanFindingStatus.Vulnerable)
                        .Select(f => new SarifResult
                        {
                            RuleId = f.Template.Recording.Vulnerability?.Id ?? "unknown",
                            Level = f.Template.Recording.Vulnerability?.Severity switch
                            {
                                "critical" or "high" => "error",
                                "medium" => "warning",
                                _ => "note",
                            },
                            Message = new SarifMessage { Text = f.Template.Recording.Name },
                            Locations =
                            [
                                new SarifLocation
                                {
                                    PhysicalLocation = new SarifPhysicalLocation
                                    {
                                        ArtifactLocation = new SarifArtifactLocation { Uri = target },
                                    },
                                },
                            ],
                        })
                        .ToList(),
                },
            ],
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(sarif, s_jsonOpts), ct).ConfigureAwait(false);
    }

    private static List<SarifRule> ExtractRules(List<ScanFinding> findings) => findings
        .Where(f => f.Template.Recording.Vulnerability is not null)
        .Select(f => f.Template.Recording.Vulnerability!)
        .GroupBy(v => v.Id)
        .Select(g => new SarifRule
        {
            Id = g.Key,
            Name = g.First().Id,
            ShortDescription = new SarifMessage { Text = g.First().OwaspApi ?? g.First().Cwe ?? g.First().Id },
            FullDescription = new SarifMessage { Text = g.First().Remediation ?? "" },
            HelpUri = g.First().References.Count > 0 ? g.First().References[0] : null,
            Properties = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["security-severity"] = g.First().Cvss?.ToString("F1", CultureInfo.InvariantCulture)
                    ?? g.First().Severity,
                ["cwe"] = g.First().Cwe ?? "",
                ["owaspApi"] = g.First().OwaspApi ?? "",
            },
        })
        .ToList();

}

/// <summary>Internal record pairing the on-disk template path with its parsed recording.</summary>
internal sealed record LoadedTemplate(string Path, BowireRecording Recording);

/// <summary>Bag of <c>bowire scan</c> CLI options resolved from System.CommandLine.</summary>
internal sealed class ScanOptions
{
    public string Target { get; init; } = "";
    public string? Corpus { get; init; }
    public string? Template { get; init; }
    public string? OutSarif { get; init; }
    public string? MinSeverity { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public bool AllowSelfSignedCerts { get; init; }
    public bool RunBuiltins { get; init; } = true;
}

/// <summary>One scan-result row — what happened when the template was run against the target.</summary>
internal enum ScanFindingStatus { Vulnerable, Safe, Skipped, Error }

/// <summary>One finding emitted by <see cref="ScanCommand"/>.</summary>
internal sealed class ScanFinding
{
    internal LoadedTemplate Template { get; init; } = null!;
    public ScanFindingStatus Status { get; init; }
    public string Detail { get; init; } = "";
    public AttackProbeResponse? Response { get; init; }

    internal static ScanFinding Vulnerable(LoadedTemplate t, AttackProbeResponse r) => new()
    { Template = t, Status = ScanFindingStatus.Vulnerable, Response = r, Detail = $"status={r.Status} latency={r.LatencyMs}ms — predicate matched" };

    internal static ScanFinding Safe(LoadedTemplate t, AttackProbeResponse r) => new()
    { Template = t, Status = ScanFindingStatus.Safe, Response = r };

    internal static ScanFinding Skipped(LoadedTemplate t, string reason) => new()
    { Template = t, Status = ScanFindingStatus.Skipped, Detail = reason };

    internal static ScanFinding Error(LoadedTemplate t, string msg) => new()
    { Template = t, Status = ScanFindingStatus.Error, Detail = msg };
}

// ---- SARIF 2.1.0 minimal envelope ----

internal sealed class SarifLog
{
    [JsonPropertyName("$schema")] public string Schema { get; init; } = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/Schemata/sarif-schema-2.1.0.json";
    [JsonPropertyName("version")] public string Version { get; init; } = "2.1.0";
    [JsonPropertyName("runs")] public IList<SarifRun> Runs { get; init; } = new List<SarifRun>();
}

internal sealed class SarifRun
{
    [JsonPropertyName("tool")] public SarifTool Tool { get; init; } = new();
    [JsonPropertyName("results")] public IList<SarifResult> Results { get; init; } = new List<SarifResult>();
}

internal sealed class SarifTool { [JsonPropertyName("driver")] public SarifDriver Driver { get; init; } = new(); }

internal sealed class SarifDriver
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("informationUri")] public string? InformationUri { get; init; }
    [JsonPropertyName("rules")] public IList<SarifRule> Rules { get; init; } = new List<SarifRule>();
}

internal sealed class SarifRule
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("shortDescription")] public SarifMessage ShortDescription { get; init; } = new();
    [JsonPropertyName("fullDescription")] public SarifMessage FullDescription { get; init; } = new();
    [JsonPropertyName("helpUri")] public string? HelpUri { get; init; }
    [JsonPropertyName("properties")] public IDictionary<string, object>? Properties { get; init; }
}

internal sealed class SarifResult
{
    [JsonPropertyName("ruleId")] public string RuleId { get; init; } = "";
    [JsonPropertyName("level")] public string Level { get; init; } = "note";
    [JsonPropertyName("message")] public SarifMessage Message { get; init; } = new();
    [JsonPropertyName("locations")] public IList<SarifLocation> Locations { get; init; } = new List<SarifLocation>();
}

internal sealed class SarifLocation
{
    [JsonPropertyName("physicalLocation")] public SarifPhysicalLocation PhysicalLocation { get; init; } = new();
}

internal sealed class SarifPhysicalLocation
{
    [JsonPropertyName("artifactLocation")] public SarifArtifactLocation ArtifactLocation { get; init; } = new();
}

internal sealed class SarifArtifactLocation { [JsonPropertyName("uri")] public string Uri { get; init; } = ""; }

internal sealed class SarifMessage { [JsonPropertyName("text")] public string Text { get; init; } = ""; }
