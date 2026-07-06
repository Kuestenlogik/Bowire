// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Default probe for <c>API5:2023 — Broken Function Level Authorization</c>.
/// Probes a curated set of privileged / management-plane endpoints that must
/// never answer to an unelevated caller. A public <c>2xx</c> is a finding
/// (the privileged function is reachable without proper authorization); a
/// <c>401 / 403</c> is the healthy state (the route exists but is gated) and a
/// <c>404</c> means it's simply absent. A random-path baseline suppresses the
/// checks on catch-all routes to avoid false positives.
/// </summary>
/// <remarks>
/// The scanner probes with whatever <c>--auth-header</c>s the operator supplied:
/// with none, a <c>2xx</c> means anonymous access; with a regular-user token,
/// a <c>2xx</c> on an admin function is textbook BFLA (a low-privilege identity
/// reaching a high-privilege function).
/// </remarks>
internal sealed class Api5AuthorizationProbe : IOwaspApiProbe
{
    // Privileged endpoints and how bad an anonymous 2xx on each is. Kept
    // high-signal on purpose — these are management-plane / diagnostic
    // surfaces, not generic "/admin" pages that legitimately serve a 200
    // login screen (which would be false-positive-prone).
    private static readonly (string Path, string Severity, double Cvss, string Why)[] s_privileged =
    [
        ("/actuator/env", "high", 8.2, "Spring Boot env actuator — dumps the full configuration, routinely including secrets, connection strings and tokens."),
        ("/actuator/heapdump", "high", 8.6, "Heap-dump download — raw process memory, typically containing credentials and in-flight tokens."),
        ("/actuator/configprops", "high", 7.5, "Configuration-properties actuator — exposes bound config incl. sensitive values."),
        ("/actuator/threaddump", "medium", 5.3, "Thread-dump actuator — leaks internal stack traces and timing."),
        ("/actuator/mappings", "medium", 5.3, "Route-table actuator — discloses every internal endpoint."),
        ("/_cat/indices", "high", 7.5, "Elasticsearch _cat API — enumerates cluster indices without authentication."),
        ("/_cluster/health", "medium", 5.3, "Elasticsearch cluster-health API reachable without authentication."),
        ("/debug/pprof/", "high", 7.5, "Go pprof profiling endpoint — profiles + goroutine dumps expose internals and enable resource abuse."),
        ("/metrics", "low", 3.7, "Prometheus metrics endpoint publicly readable — leaks internal route/cardinality/host detail."),
    ];

    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API5:2023");

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct)
    {
        Uri uri;
        try { uri = new Uri(target); }
        catch (UriFormatException)
        {
            return [Marker(ScanFindingStatus.Error, "API5-INVALID-TARGET", "API5 probe skipped", $"Could not parse target '{target}' as a URL.")];
        }
        var authority = uri.GetLeftPart(UriPartial.Authority);

        int baseline;
        try
        {
            baseline = await StatusOfAsync(http, CombineUrl(authority, $"/bowire-api5-{Guid.NewGuid():N}"), authHeaders, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return [Marker(ScanFindingStatus.Skipped, "API5-UNREACHABLE", "API5 probe skipped", $"Target could not be reached ({ex.GetType().Name}).")];
        }
        if (baseline is >= 200 and < 300)
        {
            return [Marker(ScanFindingStatus.Skipped, "API5-CATCHALL", "API5 checks suppressed",
                "Target returns 2xx for unknown paths (catch-all route) — privileged-endpoint checks suppressed to avoid false positives.")];
        }

        var findings = new List<ScanFinding>();
        foreach (var (path, severity, cvss, why) in s_privileged)
        {
            var status = await SafeStatusAsync(http, CombineUrl(authority, path), authHeaders, ct).ConfigureAwait(false);
            if (status is >= 200 and < 300)
            {
                findings.Add(Finding($"BWR-OWASP-API5-{path.Trim('/').Replace('/', '-').ToUpperInvariant()}",
                    $"Privileged endpoint reachable without authorization: {path}",
                    $"Probed {path} → HTTP {status}. {why}",
                    "Enforce function-level authorization on management / diagnostic endpoints: require an authenticated privileged role, bind them to an internal interface, or disable them in production.",
                    severity, cvss));
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(Marker(ScanFindingStatus.Safe, "API5-CLEAN", "No exposed privileged endpoints found",
                "None of the probed management / diagnostic endpoints answered without authorization."));
        }
        return findings;
    }

    // ---- helpers ----

    private static async Task<int> StatusOfAsync(HttpClient http, string url, IList<string> authHeaders, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ScanCommand.ApplyAuthHeaders(req, authHeaders);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return (int)resp.StatusCode;
    }

    private static async Task<int> SafeStatusAsync(HttpClient http, string url, IList<string> authHeaders, CancellationToken ct)
    {
        try { return await StatusOfAsync(http, url, authHeaders, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException) { return -1; }
    }

    private static string CombineUrl(string authority, string path)
    {
        var b = authority.TrimEnd('/');
        var p = string.IsNullOrEmpty(path) ? "/" : (path.StartsWith('/') ? path : "/" + path);
        return b + p;
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the API5 suite row."),
        Status = status,
        Detail = detail,
    };
}
