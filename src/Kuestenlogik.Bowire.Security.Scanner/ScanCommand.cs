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
/// <c>odata</c>, <c>http</c>, <c>sse</c>, <c>signalr</c>, <c>socketio</c>,
/// <c>mcp</c>). These are HTTP-class because the request the template
/// probes is a plain HTTP call: SignalR's negotiate, Socket.IO's
/// Engine.IO polling handshake, and MCP's Streamable-HTTP JSON-RPC POST.
/// WebSocket / MQTT / raw-gRPC probes still surface as
/// <see cref="ScanFindingStatus.Skipped"/> with a "transport not yet
/// supported by scanner" message — the templates still load, they just
/// don't run yet. Later iterations route non-HTTP probes through the
/// corresponding protocol plugin's invoke path.
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

        // Default template source: when the operator gave no explicit
        // template source at all (no --template/--templates, no --nuclei, no
        // --suite), fall back to the local cache at ~/.bowire/vulndb that
        // `bowire vulndb update` populates. This closes the loop — update
        // once, then scan without repeating --templates. An explicit
        // --templates always wins; the cache is a fallback, never an
        // override, and it never fetches (scan stays offline).
        var effectiveTemplatesDir = options.Templates;
        var cacheDir = ResolveCacheTemplatesDir(options);
        if (cacheDir is not null)
        {
            effectiveTemplatesDir = cacheDir;
            var cacheRoot = string.IsNullOrWhiteSpace(options.VulnDbCacheRoot)
                ? VulnDbCache.DefaultRoot()
                : options.VulnDbCacheRoot;
            await stdout.WriteLineAsync(
                $"  Using template cache at {cacheDir} ({VulnDbCache.CountTemplates(cacheRoot)} templates). Run `bowire vulndb update` to refresh.").ConfigureAwait(false);
        }

        // Collect templates from --templates (directory) and/or --template (single file).
        var templates = new List<LoadedTemplate>();
        foreach (var path in EnumerateTemplatePaths(options.Template, effectiveTemplatesDir))
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

        // Named suites (#184): resolve which suite view is active up front — a
        // requested suite is itself a source of work, so an empty-template scan
        // with built-ins disabled must NOT bail when a suite will run probes.
        //   owasp-api → HTTP OWASP probes (if http target) + protocol probes + table
        //   protocol  → ONLY the protocol probes + table — makes non-HTTP targets
        //               (mqtt://, ws://, …) first-class by skipping the HTTP OWASP
        //               probes by design, not by accident.
        //   all       → superset alias: HTTP OWASP (if http) + protocol + table.
        var suite = options.Suite ?? "";
        var runHttpOwasp = suite.Equals("owasp-api", StringComparison.OrdinalIgnoreCase)
            || suite.Equals("all", StringComparison.OrdinalIgnoreCase);
        var runProtocol = runHttpOwasp || suite.Equals("protocol", StringComparison.OrdinalIgnoreCase);
        var writeSummary = runProtocol;   // any of the three suites prints the coverage table

        if (templates.Count == 0 && !options.RunBuiltins && !runProtocol)
        {
            await stderr.WriteLineAsync("  No vulnerability templates found and built-ins disabled. Provide --templates <dir> or --template <file>, drop --no-builtins, OR run a named suite (--suite owasp-api|protocol|all).").ConfigureAwait(false);
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

        // #190: headless auth flow. Run the recorded login → token chain once
        // (over the same HttpClient, so TLS / timeout settings match the scan),
        // then inject the captured token as an auth header ahead of every probe.
        // Fail closed — if the flow can't produce a token we abort rather than
        // scan an authenticated API unauthenticated and report misleading
        // "endpoint missing" findings.
        var effectiveAuth = new List<string>(options.AuthHeaders);
        if (!string.IsNullOrWhiteSpace(options.AuthFlowPath))
        {
            try
            {
                var flow = AuthFlowRunner.Load(options.AuthFlowPath);
                var result = await AuthFlowRunner.RunAsync(flow, http, ct).ConfigureAwait(false);
                effectiveAuth.Insert(0, result.HeaderLine);
                await stdout.WriteLineAsync(
                    $"  Auth flow: {flow.Steps.Count} step(s) ran; injecting '{result.HeaderLine[..result.HeaderLine.IndexOf(':', StringComparison.Ordinal)]}' header into every probe.").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthFlowException or IOException or HttpRequestException)
            {
                await stderr.WriteLineAsync($"  Auth flow failed: {ex.Message}").ConfigureAwait(false);
                return 2;
            }
        }

        // The passive built-ins and the HTTP-class OWASP probes talk HTTP to
        // the target directly. A non-HTTP target (mqtt://, tcp://, ws://, …)
        // is only meaningful to the protocol-specific probes, so the HTTP-only
        // work is skipped rather than letting HttpClient throw on the
        // unsupported scheme (which used to sink the whole scan).
        var isHttpTarget = IsHttpScheme(options.Target);

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
            if (!isHttpTarget)
            {
                findings.Add(ScanFinding.Skipped(tmpl, "HTTP-class template skipped — the target is not an http/https URL"));
                continue;
            }

            try
            {
                var response = await SendHttpProbeAsync(http, options.Target, probe, effectiveAuth, ct).ConfigureAwait(false);
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

        if (options.RunBuiltins && !isHttpTarget)
        {
            await stdout.WriteLineAsync("  Built-in passive checks skipped — the target is not an http/https URL.").ConfigureAwait(false);
        }
        else if (options.RunBuiltins)
        {
            void FoldBuiltin(IReadOnlyList<ScanFinding> probeFindings)
            {
                foreach (var f in probeFindings)
                {
                    var sev = f.Template.Recording.Vulnerability?.Severity ?? "info";
                    findings.Add(SeverityRank(sev) < minRank && f.Status == ScanFindingStatus.Vulnerable
                        ? new ScanFinding { Template = f.Template, Status = ScanFindingStatus.Skipped, Detail = "below severity threshold" }
                        : f);
                }
            }

            FoldBuiltin(await SecurityBuiltins.RunAllAsync(options.Target, http, effectiveAuth, ct).ConfigureAwait(false));

            // #187: CVE lookup — match the Server/X-Powered-By banner against a
            // VulnDb corpus (loaded from --cve-db, or the built-in seed).
            var cveDb = LoadCveDatabase(options, stderr);
            FoldBuiltin(await ServerCveProbe.RunAsync(options.Target, http, effectiveAuth, cveDb, ct).ConfigureAwait(false));
        }

        // Named suites (#184): fold the dedicated per-entry probes into the
        // same list BEFORE the report / SARIF / roll-up, so they surface
        // everywhere the template + built-in findings do. The suite view was
        // resolved up front (runHttpOwasp / runProtocol / writeSummary).
        if (runProtocol)
        {
            void Fold(IReadOnlyList<ScanFinding> probeFindings)
            {
                foreach (var f in probeFindings)
                {
                    var sev = f.Template.Recording.Vulnerability?.Severity ?? "info";
                    findings.Add(SeverityRank(sev) < minRank && f.Status == ScanFindingStatus.Vulnerable
                        ? new ScanFinding { Template = f.Template, Status = ScanFindingStatus.Skipped, Detail = "below severity threshold" }
                        : f);
                }
            }

            if (runHttpOwasp)
            {
                if (isHttpTarget)
                {
                    Fold(await OwaspApiSuite.RunProbesAsync(options.Target, http, effectiveAuth, options.AuthHeadersB, ct).ConfigureAwait(false));
                }
                else
                {
                    await stdout.WriteLineAsync("  HTTP OWASP probes skipped — non-HTTP target; running protocol-specific probes only.").ConfigureAwait(false);
                }
            }

            // Protocol-specific probes (GraphQL introspection, gRPC reflection)
            // drive the corresponding protocol plugin's invoke path — only the
            // plugins deployed next to the host are available; absent ones skip.
            // Bounded well under the per-probe HTTP timeout so a target that
            // doesn't speak the protocol can't stall the whole scan.
            var registry = BowireProtocolRegistry.Discover();
            var protocolTimeout = TimeSpan.FromSeconds(Math.Min(options.TimeoutSeconds, 12));
            Fold(await OwaspApiSuite.RunProtocolProbesAsync(options.Target, registry, effectiveAuth, protocolTimeout, ct).ConfigureAwait(false));
        }

        // Active (mutating / aggressive) probes — #395–#400. Opt-in only, and
        // independent of the suite selection: an operator running `--active`
        // has explicitly asked for the mutating tier. Loud banner first, then
        // run; per-probe timeout leaves headroom for the duration budget so a
        // connection-holding probe isn't cut off early.
        if (options.Active)
        {
            await stdout.WriteLineAsync(
                "  ⚠ ACTIVE MODE — mutating/aggressive probes enabled. This scan may publish messages, hold connections open, or open many streams against the target. Run only against systems you are authorised to test.").ConfigureAwait(false);

            var activeRegistry = BowireProtocolRegistry.Discover();
            var activeOptions = new ActiveScanOptions
            {
                DurationSeconds = options.ActiveDurationSeconds > 0 ? options.ActiveDurationSeconds : 15,
                Concurrency = options.ActiveConcurrency > 0 ? options.ActiveConcurrency : 100,
                ExpectedTopics = options.ActiveExpectedTopics.ToArray(),
            };
            var activeTimeout = TimeSpan.FromSeconds(activeOptions.DurationSeconds + 10);
            var activeFindings = await OwaspApiSuite.RunActiveProtocolProbesAsync(
                options.Target, activeRegistry, effectiveAuth, activeOptions, activeTimeout, ct).ConfigureAwait(false);
            foreach (var f in activeFindings)
            {
                var sev = f.Template.Recording.Vulnerability?.Severity ?? "info";
                findings.Add(SeverityRank(sev) < minRank && f.Status == ScanFindingStatus.Vulnerable
                    ? new ScanFinding { Template = f.Template, Status = ScanFindingStatus.Skipped, Detail = "below severity threshold" }
                    : f);
            }
        }

        await WriteConsoleReportAsync(findings, stdout).ConfigureAwait(false);

        // Per-entry covered / clean / vulnerable table for the named suites.
        if (writeSummary)
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

    /// <summary>
    /// The local template-cache fallback gate. Returns the cache's
    /// <c>templates/</c> dir when the operator gave no explicit template
    /// source at all (no <c>--template</c>/<c>--templates</c>/<c>--nuclei</c>/
    /// <c>--suite</c>) and the cache holds at least one template; otherwise
    /// <c>null</c> (an explicit source always wins, and the cache is never an
    /// override). Pure + offline — split out so the resolution is unit-tested
    /// without running a scan.
    /// </summary>
    internal static string? ResolveCacheTemplatesDir(ScanOptions options)
    {
        if (!string.IsNullOrEmpty(options.Template)
            || !string.IsNullOrEmpty(options.Templates)
            || !string.IsNullOrEmpty(options.Nuclei)
            || !string.IsNullOrEmpty(options.Suite))
        {
            return null;
        }
        var cacheRoot = string.IsNullOrWhiteSpace(options.VulnDbCacheRoot)
            ? VulnDbCache.DefaultRoot()
            : options.VulnDbCacheRoot;
        return VulnDbCache.HasTemplates(cacheRoot) ? VulnDbCache.TemplatesDir(cacheRoot) : null;
    }

    private static IEnumerable<string> EnumerateTemplatePaths(string? templateFile, string? templatesDir)
    {
        if (!string.IsNullOrEmpty(templateFile) && File.Exists(templateFile))
            yield return templateFile;
        if (!string.IsNullOrEmpty(templatesDir) && Directory.Exists(templatesDir))
        {
            foreach (var p in Directory.EnumerateFiles(templatesDir, "*.json", SearchOption.AllDirectories))
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
        // SignalR / Socket.IO / MCP are HTTP-class for the request the
        // template probes: SignalR's negotiate + Socket.IO's Engine.IO
        // polling handshake are HTTP GET/POSTs, and MCP's Streamable-HTTP
        // transport carries its JSON-RPC calls over HTTP POST. The scanner
        // replays these like any REST probe. The live upgraded connection
        // (WebSocket/long-poll/SSE stream) is out of template scope, but the
        // handshake/initial-request misconfiguration is fully HTTP-detectable.
        "REST" or "GRAPHQL" or "ODATA" or "HTTP" or "SSE"
            or "SIGNALR" or "SOCKETIO" or "MCP" => true,
        _ => false,
    };

    /// <summary>
    /// Whether the target is an http/https URL the HTTP-only checks (passive
    /// built-ins + HTTP-class OWASP probes) can talk to. A scheme HttpClient
    /// can't handle (mqtt://, tcp://, ws://, …) returns false so that work is
    /// skipped; a bare host or relative target (no scheme) is treated as HTTP
    /// to preserve the pre-existing behaviour.
    /// </summary>
    private static bool IsHttpScheme(string target)
        => !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is "http" or "https";

    // #187: load the CVE corpus for the banner lookup — a --cve-db VulnDb file,
    // or the built-in seed. A bad file degrades to the seed with a warning
    // rather than sinking the scan.
    private static CveDatabase LoadCveDatabase(ScanOptions options, TextWriter stderr)
    {
        if (string.IsNullOrEmpty(options.CveDbPath)) return CveDatabase.Seed();
        try
        {
            return CveDatabase.Load(options.CveDbPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            stderr.WriteLine($"  --cve-db could not be loaded ({ex.Message}); using the built-in CVE seed.");
            return CveDatabase.Seed();
        }
    }

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
    /// Override the local template-cache root the scan falls back to when no
    /// explicit template source (<see cref="Template"/> / <see cref="Templates"/>
    /// / <see cref="Nuclei"/> / <see cref="Suite"/>) is given. Null → the
    /// default <c>~/.bowire/vulndb</c> the <c>bowire vulndb update</c> command
    /// populates. Exposed so embedded hosts (and tests) can pin the cache
    /// without depending on the process's home directory. Never fetches — the
    /// scan only ever reads this cache.
    /// </summary>
    public string? VulnDbCacheRoot { get; init; }

    /// <summary>
    /// Named test-suite to run instead of / alongside the flat template
    /// report (case-insensitive). One of:
    /// <list type="bullet">
    /// <item><c>owasp-api</c> — HTTP OWASP probes (when the target is http/https)
    /// + the protocol-specific probes, rolled up against the OWASP API Security
    /// Top 10 (2023) with a per-entry coverage table (see <see cref="OwaspApiSuite"/>).</item>
    /// <item><c>protocol</c> — ONLY the protocol-specific probes
    /// (gRPC/GraphQL/WS/MQTT/SSE/MCP) + the table; the HTTP OWASP probes are
    /// skipped by design so non-HTTP targets (mqtt://, ws://) are first-class.</item>
    /// <item><c>all</c> — superset alias: everything <c>owasp-api</c> runs.</item>
    /// </list>
    /// Null = flat report only.
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

    /// <summary>
    /// #187: path to a CVE / VulnDb JSON file used by the banner CVE-lookup
    /// (Server / X-Powered-By → known CVEs). Null = use the built-in seed set.
    /// </summary>
    public string? CveDbPath { get; init; }

    /// <summary>
    /// #190: path to a headless auth-flow JSON file. When set, the recorded
    /// login → token chain runs once before the scan and the captured token is
    /// injected as an auth header ahead of <see cref="AuthHeaders"/>. Null =
    /// no flow (use <see cref="AuthHeaders"/> directly).
    /// </summary>
    public string? AuthFlowPath { get; init; }

    /// <summary>
    /// #395–#400: opt into the active (mutating / aggressive) scan tier. Off by
    /// default. When set, active protocol probes run — they may PUBLISH to the
    /// target, hold connections open, or open many streams. Each namespaces +
    /// cleans up its side effects, but this is a deliberate mutation of the
    /// target, so it must never be implicit.
    /// </summary>
    public bool Active { get; init; }

    /// <summary>Wall-clock budget (seconds) for time-based active probes. Default 15.</summary>
    public int ActiveDurationSeconds { get; init; } = 15;

    /// <summary>Concurrency budget for fan-out active probes. Default 100.</summary>
    public int ActiveConcurrency { get; init; } = 100;

    /// <summary>Operator-supplied expected-topic scope for the MQTT wildcard-subscribe active probe (#396).</summary>
    public IList<string> ActiveExpectedTopics { get; init; } = new List<string>();

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
