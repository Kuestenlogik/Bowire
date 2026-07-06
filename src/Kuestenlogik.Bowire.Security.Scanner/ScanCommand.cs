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
using NucleiTemplates = Kuestenlogik.Bowire.Security.Templates.Nuclei;

namespace Kuestenlogik.Bowire.Security.Scanner;

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
public static class ScanCommand
{
    /// <summary>Loaded JSON options shared across template parse + finding write.</summary>
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One scan-subcommand invocation. Returns the process exit code: 0 = the scanner ran end-to-end (with or without findings; findings are the product, not a failure), 1 = reserved for unhandled tool crashes / unexpected scanner aborts, 2 = usage / configuration error before the scan starts.
    /// <para>
    /// <paramref name="output"/> and <paramref name="error"/> let the
    /// caller redirect stdout / stderr without touching process-global
    /// <see cref="Console.Out"/>. Defaults wire to the real console for
    /// the production CLI path; the System.CommandLine handler hands
    /// the framework's <c>ParseResult.InvocationConfiguration.Output/.Error</c>
    /// through, and tests pass their own <see cref="StringWriter"/> to
    /// capture the run without serialising on a global lock.
    /// </para>
    /// </summary>
    public static async Task<int> RunAsync(
        ScanOptions options,
        CancellationToken ct,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        if (string.IsNullOrWhiteSpace(options.Target))
        {
            await stderr.WriteLineAsync("  Usage: bowire scan --target <url> --templates <dir> [--template <file>] [--out <sarif>]").ConfigureAwait(false);
            return 2;
        }

        // Collect templates from --templates (directory) and/or --template (single file).
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
                await stderr.WriteLineAsync($"  Skipping {Path.GetFileName(path)}: {ex.Message}").ConfigureAwait(false);
            }
        }

        // Nuclei corpus loading — read every *.yaml/*.yml under
        // --nuclei, parse with NucleiTemplateReader, unfold to
        // BowireRecordings via the converter with a target-bound
        // variable context. Each unfolded recording lands on the
        // same templates list the scanner walks, so the scan loop
        // doesn't need to know which corpus contributed which
        // template. Loading errors per file get reported + skipped
        // (matches the native-template behaviour).
        if (!string.IsNullOrEmpty(options.Nuclei) && Directory.Exists(options.Nuclei))
        {
            var nucleiContext = NucleiTemplates.NucleiVariableContext.FromTarget(options.Target);
            var nucleiCount = 0;
            foreach (var path in Directory.EnumerateFiles(options.Nuclei, "*.yaml", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(options.Nuclei, "*.yml", SearchOption.AllDirectories)))
            {
                try
                {
                    var template = NucleiTemplates.NucleiTemplateReader.ReadFile(path);
                    foreach (var rec in NucleiTemplates.NucleiTemplateConverter.ToBowireRecordings(template, nucleiContext)
                        .Where(r => r.VulnerableWhen is not null && r.Steps.Count > 0))
                    {
                        templates.Add(new LoadedTemplate(path, rec));
                        nucleiCount++;
                    }
                }
                catch (Exception ex)
                {
                    await stderr.WriteLineAsync($"  Skipping nuclei {Path.GetFileName(path)}: {ex.Message}").ConfigureAwait(false);
                }
            }
            if (nucleiCount > 0)
            {
                await stdout.WriteLineAsync($"  Loaded {nucleiCount} nuclei template(s) from {options.Nuclei}").ConfigureAwait(false);
            }
        }

        if (templates.Count == 0 && !options.RunBuiltins)
        {
            await stderr.WriteLineAsync("  No vulnerability templates found and built-ins disabled. Provide --templates <dir> or --template <file>, OR drop --no-builtins.").ConfigureAwait(false);
            return 2;
        }

        // Resolve scope and refuse the scan if the target itself
        // falls outside it — typical mistake: pasted the wrong host
        // into --target after typing the scope. The default scope
        // (when --scope is empty) is "the target's own host", so the
        // built-in case always passes; this check fires when an
        // operator widened scope but the target slipped out.
        var inScope = CompileScope(options.Scope, options.Target);
        string targetHost;
        try { targetHost = new Uri(options.Target).Host; }
        catch (UriFormatException)
        {
            await stderr.WriteLineAsync($"  Could not parse --target '{options.Target}' as a URL.").ConfigureAwait(false);
            return 2;
        }
        if (!inScope(targetHost))
        {
            await stderr.WriteLineAsync($"  Refusing to scan: target host '{targetHost}' is outside the configured --scope set. Widen the scope (`--scope {targetHost}`) or change the target.").ConfigureAwait(false);
            return 2;
        }

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync($"  Scanning {options.Target}").ConfigureAwait(false);
        var pieces = $"{templates.Count} template(s) loaded";
        if (options.RunBuiltins) pieces += " + built-in checks (TLS / banner / verbose-errors)";
        await stdout.WriteLineAsync($"  {pieces}; min severity = {options.MinSeverity ?? "any"}").ConfigureAwait(false);
        var scopeDesc = options.Scope is { Count: > 0 }
            ? string.Join(", ", options.Scope)
            : $"{targetHost} (implicit; widen with --scope)";
        await stdout.WriteLineAsync($"  Scope: {scopeDesc}").ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);

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
                var response = await SendHttpProbeAsync(http, options.Target, probe, options.AuthHeaders, ct).ConfigureAwait(false);
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
            var builtinResults = await SecurityBuiltins.RunAllAsync(options.Target, http, options.AuthHeaders, ct).ConfigureAwait(false);
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

        // OWASP API Top 10 suite: run the dedicated per-entry probes and
        // fold their findings into the same list BEFORE the report / SARIF /
        // roll-up, so they surface everywhere the template + built-in
        // findings do.
        var owaspSuite = string.Equals(options.Suite, "owasp-api", StringComparison.OrdinalIgnoreCase);
        if (owaspSuite)
        {
            var probeFindings = await OwaspApiSuite.RunProbesAsync(options.Target, http, options.AuthHeaders, options.AuthHeadersB, ct).ConfigureAwait(false);
            foreach (var f in probeFindings)
            {
                var sev = f.Template.Recording.Vulnerability?.Severity ?? "info";
                findings.Add(SeverityRank(sev) < minRank && f.Status == ScanFindingStatus.Vulnerable
                    ? new ScanFinding { Template = f.Template, Status = ScanFindingStatus.Skipped, Detail = "below severity threshold" }
                    : f);
            }
        }

        await WriteConsoleReportAsync(findings, stdout).ConfigureAwait(false);

        // Per-entry covered / clean / vulnerable table for the OWASP suite.
        if (owaspSuite)
        {
            await OwaspApiSuite.WriteSummaryAsync(findings, stdout).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(options.OutSarif))
        {
            await WriteSarifAsync(options.OutSarif, options.Target, findings, ct).ConfigureAwait(false);
            await stdout.WriteLineAsync().ConfigureAwait(false);
            await stdout.WriteLineAsync($"  SARIF report → {options.OutSarif}").ConfigureAwait(false);
        }

        // A successful scan is "the tool ran end-to-end and produced a
        // report" — findings are the *product*, not a failure. So we
        // exit 0 here regardless of whether the target was vulnerable.
        // Exit 1 stays reserved for actual tool crashes / unhandled
        // exceptions surfacing from the CLI host; exit 2 is the
        // usage-error code returned above. Callers that want the
        // scan-step itself to gate a pipeline should post-process the
        // SARIF (jq on `runs[0].results.length`) — that keeps the
        // gating logic in the CI yaml, not in the tool's exit code.
        return 0;
    }

    private static IEnumerable<string> EnumerateTemplatePaths(ScanOptions options)
    {
        if (!string.IsNullOrEmpty(options.Template) && File.Exists(options.Template))
            yield return options.Template;
        if (!string.IsNullOrEmpty(options.Templates) && Directory.Exists(options.Templates))
        {
            foreach (var p in Directory.EnumerateFiles(options.Templates, "*.json", SearchOption.AllDirectories))
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

    /// <summary>
    /// Map a severity label to a numeric CVSS-band midpoint. GitHub
    /// Code Scanning's SARIF ingest requires the `security-severity`
    /// property to parse as a float; "info" / "low" / etc. as a
    /// string verbatim is rejected with "invalid security severity
    /// value, is not a number". Templates that supply an explicit
    /// CVSS via <c>AttackVulnerability.Cvss</c> still win — this
    /// helper is the fallback when the template only carries a
    /// qualitative severity label.
    /// </summary>
    private static double SeverityToScore(string severity) => severity.ToUpperInvariant() switch
    {
        "CRITICAL" => 9.5,
        "HIGH" => 7.5,
        "MEDIUM" => 5.5,
        "LOW" => 3.5,
        _ => 0.0,
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
        var handler = new HttpClientHandler
        {
            // Scope-awareness: never follow a redirect off the
            // declared scope. A probe that gets 30x'd to a different
            // host should surface AS a redirect — not silently retry
            // against whatever Location says. Operators who want the
            // redirect target tested supply it as an explicit scope
            // entry instead.
            AllowAutoRedirect = false,
        };
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

    /// <summary>
    /// Compile the scope-glob list into a single host-membership
    /// predicate. Patterns: plain hostname (literal match), or a
    /// leading <c>*.</c> wildcard (matches any sub-domain but not
    /// the apex). Empty scope list ⇒ derive from the target's own
    /// host so accidental cross-host probes are blocked by default.
    /// </summary>
    public static Func<string, bool> CompileScope(IList<string> scope, string targetUrl)
    {
        var patterns = new List<string>();
        if (scope is { Count: > 0 })
        {
            foreach (var raw in scope.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                // CLI flags may be comma-separated AND repeated — split each
                // value on `,` so `--scope a.com,b.com` works alongside
                // `--scope a.com --scope b.com`.
                foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    patterns.Add(part);
            }
        }
        if (patterns.Count == 0)
        {
            try
            {
                var u = new Uri(targetUrl);
                patterns.Add(u.Host);
            }
            catch (UriFormatException) { /* leave list empty — scope check passes everything */ }
        }
        if (patterns.Count == 0) return _ => true;

        return host =>
        {
            foreach (var p in patterns)
            {
                if (p.StartsWith("*.", StringComparison.Ordinal))
                {
                    var suffix = p[1..]; // ".example.com"
                    if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        && host.Length > suffix.Length) return true;
                }
                else if (string.Equals(host, p, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        };
    }

    private static async Task<AttackProbeResponse> SendHttpProbeAsync(HttpClient http, string target, BowireRecordingStep probe,
        IList<string> authHeaders, CancellationToken ct)
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

        // Auth-profile headers apply to EVERY probe — they sit on top
        // of any template-supplied metadata so an operator can carry
        // a session token / API key into every scan without rewriting
        // every template's metadata block.
        ApplyAuthHeaders(req, authHeaders);

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

    /// <summary>
    /// Apply the operator-supplied auth headers to a probe request.
    /// Each entry is a <c>Name: Value</c> string; the first colon is
    /// the separator (so a value with embedded colons like a Bearer
    /// token containing dots works). Names that fail validation
    /// (e.g. with spaces) are silently dropped — the scanner is best-
    /// effort here; the operator can debug with --verbose if a header
    /// is misformed.
    /// </summary>
    public static void ApplyAuthHeaders(HttpRequestMessage req, IList<string> headers)
    {
        if (headers is null || headers.Count == 0) return;
        foreach (var raw in headers.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = raw[..colon].Trim();
            var value = raw[(colon + 1)..].TrimStart();
            if (name.Length == 0) continue;
            req.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static async Task WriteConsoleReportAsync(List<ScanFinding> findings, TextWriter stdout)
    {
        await stdout.WriteLineAsync($"  {findings.Count} template(s) processed:").ConfigureAwait(false);
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
            await stdout.WriteLineAsync($"  {marker} {id,-22} {sev,-8} {title}").ConfigureAwait(false);
            if (!string.IsNullOrEmpty(f.Detail)) await stdout.WriteLineAsync($"          {f.Detail}").ConfigureAwait(false);
        }
        await stdout.WriteLineAsync().ConfigureAwait(false);
        var vulnCount = findings.FindAll(f => f.Status == ScanFindingStatus.Vulnerable).Count;
        if (vulnCount > 0)
        {
            await stdout.WriteLineAsync($"  {vulnCount} vulnerability finding(s).").ConfigureAwait(false);
        }
        else
        {
            await stdout.WriteLineAsync("  No vulnerabilities matched. (Negative results don't prove security — just that these templates didn't catch anything.)").ConfigureAwait(false);
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
                            // Include the scan target in the message so the
                            // Code Scanning UI carries the context — physical
                            // locations are omitted on purpose; see PartialFingerprints
                            // below.
                            Message = new SarifMessage { Text = $"{f.Template.Recording.Name} (target: {target})" },
                            // `bowire scan` is DAST — findings don't have a
                            // real source-file location. Code Scanning's
                            // validator is strict about TWO things and they
                            // pull in opposite directions:
                            //   (a) `physicalLocation` is required on every
                            //       result (`expected a physical location`).
                            //   (b) `artifactLocation.uri` must NOT be an
                            //       https:// URL (`SARIF URI scheme "https"
                            //       did not match the checkout URI scheme
                            //       "file"`).
                            // The pragmatic intersection: point the physical
                            // location at the .github/workflows/scan-self.yml
                            // file (a real checkout-relative path that
                            // describes what was scanned) and carry the
                            // actual scan target via `logicalLocations`.
                            Locations =
                            [
                                new SarifLocation
                                {
                                    PhysicalLocation = new SarifPhysicalLocation
                                    {
                                        ArtifactLocation = new SarifArtifactLocation
                                        {
                                            Uri = ".github/workflows/scan-self.yml",
                                        },
                                    },
                                    LogicalLocations =
                                    [
                                        new SarifLogicalLocation
                                        {
                                            Name = target,
                                            FullyQualifiedName = target,
                                            Kind = "resource",
                                        },
                                    ],
                                },
                            ],
                            // Stable per-finding fingerprint = ruleId + target
                            // so re-scans of the same target collapse to one
                            // alert and a fix surfaces as "closed" rather than
                            // a new alert.
                            PartialFingerprints = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["bowireRuleAndTarget"] = (f.Template.Recording.Vulnerability?.Id ?? "unknown") + "@" + target,
                            },
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
                // GitHub Code Scanning requires `security-severity` to
                // be a NUMERIC string parseable as float — it rejects
                // SARIF that carries "info" / "low" / "medium" / etc.
                // verbatim. Map the severity label to a CVSS-band
                // midpoint when the template didn't supply an explicit
                // Cvss score:
                //   critical  → 9.5
                //   high      → 7.5
                //   medium    → 5.5
                //   low       → 3.5
                //   info / *  → 0.0
                ["security-severity"] = g.First().Cvss?.ToString("F1", CultureInfo.InvariantCulture)
                    ?? SeverityToScore(g.First().Severity).ToString("F1", CultureInfo.InvariantCulture),
                ["cwe"] = g.First().Cwe ?? "",
                ["owaspApi"] = g.First().OwaspApi ?? "",
            },
        })
        .ToList();

}

/// <summary>Internal record pairing the on-disk template path with its parsed recording.</summary>
internal sealed record LoadedTemplate(string Path, BowireRecording Recording);

/// <summary>Bag of <c>bowire scan</c> CLI options resolved from System.CommandLine.</summary>
public sealed class ScanOptions
{
    public string Target { get; init; } = "";
    public string? Templates { get; init; }

    /// <summary>
    /// Named test-suite to run instead of / alongside the flat template
    /// report. Currently <c>owasp-api</c> — rolls the scan's findings up
    /// against the OWASP API Security Top 10 (2023) and prints a per-entry
    /// coverage table (see <see cref="OwaspApiSuite"/>). Null = flat report only.
    /// </summary>
    public string? Suite { get; init; }

    /// <summary>
    /// Directory of Nuclei-format YAML templates
    /// (<see href="https://github.com/projectdiscovery/nuclei-templates"/>).
    /// Loaded alongside the Bowire-format <see cref="Templates"/>
    /// directory; each Nuclei template gets unfolded into one or
    /// more BowireRecordings (multi-path + payload matrices) at
    /// load time, with <c>{{BaseURL}}</c> / <c>{{RandStr}}</c>
    /// placeholders resolved against <see cref="Target"/>.
    /// </summary>
    public string? Nuclei { get; init; }
    public string? Template { get; init; }
    public string? OutSarif { get; init; }
    public string? MinSeverity { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public bool AllowSelfSignedCerts { get; init; }
    public bool RunBuiltins { get; init; } = true;
    /// <summary>
    /// Hostname-glob patterns the scanner is allowed to probe. Each
    /// entry is a literal hostname (<c>api.example.com</c>) or a
    /// glob with leading <c>*</c> (<c>*.example.com</c> matches
    /// <c>api.example.com</c> and <c>internal.example.com</c> but
    /// NOT <c>example.com</c> itself). Empty list = the scope is
    /// implicit, derived from the target's own host. Operators who
    /// want explicit cross-host scope (e.g. probing redirect-target
    /// hosts) widen it via repeated <c>--scope</c> CLI flags.
    /// </summary>
    public IList<string> Scope { get; init; } = new List<string>();

    /// <summary>
    /// Auth headers applied to every HTTP-class probe (template +
    /// builtin). Each entry is a `Name: Value` string, e.g.
    /// <c>"Authorization: Bearer &lt;token&gt;"</c>
    /// or <c>"X-Api-Key: &lt;key&gt;"</c>. Repeatable on the CLI. Without
    /// an auth header, scans of authenticated APIs are blind — every
    /// probe lands on the login wall and the scanner reports false
    /// "endpoint missing" findings.
    /// </summary>
    public IList<string> AuthHeaders { get; init; } = new List<string>();

    /// <summary>
    /// A *second* identity's auth headers (<c>--auth-header-b</c>), same shape
    /// as <see cref="AuthHeaders"/>. Used by cross-identity checks — the API1
    /// BOLA probe reads an object as identity A and then tries the same object
    /// as identity B to detect a missing object-level authorization check.
    /// Empty = single-identity scan (BOLA probe skips).
    /// </summary>
    public IList<string> AuthHeadersB { get; init; } = new List<string>();
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
    [JsonPropertyName("partialFingerprints")] public IDictionary<string, string>? PartialFingerprints { get; init; }
}

internal sealed class SarifLocation
{
    // DAST runs use `logicalLocations` instead of `physicalLocation`
    // because the finding's target is a URL, not a file in the
    // checkout — Code Scanning rejects the latter when the scheme
    // isn't `file:`.
    [JsonPropertyName("physicalLocation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SarifPhysicalLocation? PhysicalLocation { get; init; }
    [JsonPropertyName("logicalLocations"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<SarifLogicalLocation>? LogicalLocations { get; init; }
}

internal sealed class SarifLogicalLocation
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("fullyQualifiedName")] public string FullyQualifiedName { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "resource";
}

internal sealed class SarifPhysicalLocation
{
    [JsonPropertyName("artifactLocation")] public SarifArtifactLocation ArtifactLocation { get; init; } = new();
}

internal sealed class SarifArtifactLocation { [JsonPropertyName("uri")] public string Uri { get; init; } = ""; }

internal sealed class SarifMessage { [JsonPropertyName("text")] public string Text { get; init; } = ""; }
