// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Default probe for <c>API9:2023 — Improper Inventory Management</c>. Checks
/// the three classic inventory smells over HTTP:
/// <list type="number">
///   <item>older API versions still routed alongside the target's version
///   (an <c>/v2</c> target with a live <c>/v1</c>);</item>
///   <item>machine-readable API inventory / documentation surfaces
///   (<c>openapi.json</c>, <c>swagger.json</c>, <c>actuator</c>, …) publicly
///   readable;</item>
///   <item>endpoints that advertise <c>Deprecation</c> / <c>Sunset</c> yet
///   still serve.</item>
/// </list>
/// A random-path baseline suppresses the reachability checks when the target
/// answers 2xx for unknown paths (catch-all route) to avoid false positives.
/// </summary>
internal sealed partial class Api9InventoryProbe : IOwaspApiProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API9:2023");

    // Root-level inventory / doc surfaces. A public 2xx here leaks the whole
    // endpoint catalogue — the essence of improper inventory management.
    private static readonly string[] s_inventorySurfaces =
    [
        "/openapi.json", "/swagger.json", "/swagger/v1/swagger.json",
        "/api-docs", "/v3/api-docs", "/actuator",
    ];

    [GeneratedRegex(@"/v(\d+)(?=/|$)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex VersionSegment();

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct)
    {
        var uri = TryUri(target);
        if (uri is null)
        {
            return [Marker(ScanFindingStatus.Error, "API9-INVALID-TARGET", "API9 probe skipped", $"Could not parse target '{target}' as a URL.")];
        }
        var authority = uri.GetLeftPart(UriPartial.Authority);

        // Baseline: 2xx for a definitely-bogus path ⇒ catch-all route ⇒
        // reachability can't be trusted, so suppress the path checks.
        int baseline;
        try
        {
            baseline = await StatusOfAsync(http, CombineUrl(authority, $"/bowire-api9-{Guid.NewGuid():N}"), authHeaders, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return [Marker(ScanFindingStatus.Skipped, "API9-UNREACHABLE", "API9 probe skipped", $"Target could not be reached ({ex.GetType().Name}).")];
        }
        var catchAll = baseline is >= 200 and < 300;

        var findings = new List<ScanFinding>();

        if (!catchAll)
        {
            // 1. Older-version reachability.
            var match = VersionSegment().Match(uri.AbsolutePath);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var current) && current > 1)
            {
                for (var older = current - 1; older >= 1; older--)
                {
                    var olderPath = uri.AbsolutePath.Remove(match.Groups[1].Index, match.Groups[1].Length)
                        .Insert(match.Groups[1].Index, older.ToString(CultureInfo.InvariantCulture));
                    var status = await SafeStatusAsync(http, CombineUrl(authority, olderPath), authHeaders, ct).ConfigureAwait(false);
                    if (Routed(status))
                    {
                        findings.Add(Vuln($"BWR-OWASP-API9-VER-V{older}",
                            $"Older API version v{older} still reachable",
                            $"Probed {olderPath} → HTTP {status}. Target is v{current}; the older v{older} still answers. Unretired versions widen the attack surface and lag on security fixes.",
                            $"Retire or auth-gate deprecated versions. Publish a Sunset header + deprecation timeline, then return 410 Gone after the sunset date."));
                    }
                }
            }

            // 2. Inventory / documentation surfaces (public read only).
            foreach (var surface in s_inventorySurfaces)
            {
                var status = await SafeStatusAsync(http, CombineUrl(authority, surface), authHeaders, ct).ConfigureAwait(false);
                if (Public(status))
                {
                    findings.Add(Vuln($"BWR-OWASP-API9-DOC-{surface.Trim('/').Replace('/', '-').ToUpperInvariant()}",
                        $"API inventory surface publicly readable: {surface}",
                        $"Probed {surface} → HTTP {status}. A public machine-readable inventory / doc surface lets anyone enumerate every endpoint and payload shape.",
                        $"Gate {surface} behind auth or restrict it to non-production. Keep a private record of which environments expose which API versions."));
                }
            }
        }

        // 3. Deprecation / Sunset still served on the base.
        try
        {
            var (status, headerNames) = await HeadersOfAsync(http, target, authHeaders, ct).ConfigureAwait(false);
            if (Routed(status) && (headerNames.Contains("Deprecation", StringComparer.OrdinalIgnoreCase)
                || headerNames.Contains("Sunset", StringComparer.OrdinalIgnoreCase)))
            {
                findings.Add(Vuln("BWR-OWASP-API9-DEPRECATED",
                    "Deprecated endpoint still served",
                    "The base endpoint advertises a Deprecation / Sunset header yet still answers requests.",
                    "Deprecation is a migration signal, not a control. Enforce the sunset — return 410 Gone after the date — so consumers actually move off."));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            // best-effort — a failed header read shouldn't sink the probe
        }

        if (catchAll)
        {
            findings.Add(Marker(ScanFindingStatus.Skipped, "API9-CATCHALL", "API9 path checks suppressed",
                "Target returns 2xx for unknown paths (catch-all route) — version / inventory reachability checks suppressed to avoid false positives."));
        }
        else if (findings.Count == 0)
        {
            findings.Add(Marker(ScanFindingStatus.Safe, "API9-CLEAN", "No inventory-management issues found",
                "No older API versions routed, no public inventory / doc surfaces, no active deprecated endpoints."));
        }
        return findings;
    }

    // ---- finding factories ----

    private ScanFinding Vuln(string id, string name, string detail, string remediation) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "medium", cvss: 5.3, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the API9 suite row."),
        Status = status,
        Detail = detail,
    };

    // ---- http helpers ----

    private static Uri? TryUri(string target)
    {
        try { return new Uri(target); }
        catch (UriFormatException) { return null; }
    }

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

    private static async Task<(int Status, IReadOnlyCollection<string> HeaderNames)> HeadersOfAsync(HttpClient http, string url, IList<string> authHeaders, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ScanCommand.ApplyAuthHeaders(req, authHeaders);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var names = new List<string>();
        foreach (var h in resp.Headers) names.Add(h.Key);
        foreach (var h in resp.Content.Headers) names.Add(h.Key);
        return ((int)resp.StatusCode, names);
    }

    // A path is "routed" if the server answers with anything other than a
    // not-found — 401/403 still means the route exists (an unretired version).
    private static bool Routed(int status) => status > 0 && status != 404 && status != 410;

    // A surface is "publicly readable" only on a 2xx — 401/403 means it
    // exists but is gated, which is the desired state, not a finding.
    private static bool Public(int status) => status is >= 200 and < 300;

    private static string CombineUrl(string authority, string path)
    {
        var b = authority.TrimEnd('/');
        var p = string.IsNullOrEmpty(path) ? "/" : (path.StartsWith('/') ? path : "/" + path);
        return b + p;
    }
}
