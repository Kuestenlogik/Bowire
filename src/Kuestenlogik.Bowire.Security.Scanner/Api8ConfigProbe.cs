// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Dedicated probe for <c>API8:2023 — Security Misconfiguration</c>, enriching
/// the passive built-ins (TLS enumeration, banner disclosure, verbose errors)
/// with active checks over a single base request that carries an <c>Origin</c>:
/// <list type="bullet">
///   <item><b>CORS</b> — does the response reflect an arbitrary Origin, use
///   <c>*</c>, or accept <c>null</c>, and is it combined with
///   <c>Access-Control-Allow-Credentials: true</c> (the dangerous case)?</item>
///   <item><b>Security headers</b> — HSTS on https, <c>X-Content-Type-Options:
///   nosniff</c>, and (for HTML responses only, to stay false-positive-free on
///   JSON APIs) <c>Content-Security-Policy</c> + clickjacking protection.</item>
/// </list>
/// </summary>
internal sealed class Api8ConfigProbe : IOwaspApiProbe
{
    // A synthetic cross-origin value. If it comes back in Access-Control-Allow-
    // Origin, the server is reflecting whatever Origin it's handed.
    private const string ProbeOrigin = "https://bowire-cors-probe.example";

    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API8:2023");

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct)
    {
        int status;
        Dictionary<string, string> headers;
        bool isHtml;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            ScanCommand.ApplyAuthHeaders(req, authHeaders);
            req.Headers.TryAddWithoutValidation("Origin", ProbeOrigin);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            status = (int)resp.StatusCode;
            headers = CollectHeaders(resp);
            isHtml = (resp.Content.Headers.ContentType?.MediaType ?? "").Contains("html", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return [Marker(ScanFindingStatus.Skipped, "API8-CONFIG-UNREACHABLE", "API8 config probe skipped",
                $"Base request failed ({ex.GetType().Name}) — CORS / security-header checks skipped.")];
        }

        var findings = new List<ScanFinding>();
        var isHttps = TryIsHttps(target);

        // --- CORS ---
        if (headers.TryGetValue("Access-Control-Allow-Origin", out var acao) && !string.IsNullOrWhiteSpace(acao))
        {
            var value = acao.Trim();
            var reflected = value.Equals(ProbeOrigin, StringComparison.OrdinalIgnoreCase);
            var wildcard = value == "*";
            var nullOrigin = value.Equals("null", StringComparison.OrdinalIgnoreCase);
            var credentials = headers.TryGetValue("Access-Control-Allow-Credentials", out var acac)
                && acac.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

            if (reflected && credentials)
            {
                findings.Add(Finding("BWR-OWASP-API8-CORS-REFLECT-CREDS", "CORS reflects arbitrary Origin with credentials",
                    $"Access-Control-Allow-Origin echoed the probe Origin ({ProbeOrigin}) and Access-Control-Allow-Credentials is true. Any website a victim visits can make credentialed cross-origin calls and read the responses.",
                    "Never reflect the Origin together with credentials. Allow-list exact trusted origins; never combine Access-Control-Allow-Credentials: true with a reflected or '*' origin.",
                    "high", 8.1));
            }
            else if (reflected || wildcard || nullOrigin)
            {
                var which = wildcard ? "uses the '*' wildcard" : reflected ? "reflects an arbitrary Origin" : "accepts the 'null' Origin";
                findings.Add(Finding("BWR-OWASP-API8-CORS-PERMISSIVE", "Permissive CORS policy",
                    $"Access-Control-Allow-Origin = '{value}' — the policy {which}. Over-broad CORS lets untrusted sites read responses.",
                    "Restrict Access-Control-Allow-Origin to an explicit allow-list of trusted origins; avoid '*', Origin reflection, and 'null'.",
                    "medium", 5.3));
            }
        }

        // --- Security headers ---
        if (isHttps && !headers.ContainsKey("Strict-Transport-Security"))
        {
            findings.Add(Finding("BWR-OWASP-API8-HSTS", "Missing Strict-Transport-Security (HSTS)",
                "No HSTS header on an https endpoint — a window remains for SSL-strip / protocol-downgrade on the first request before TLS is pinned.",
                "Add Strict-Transport-Security: max-age=31536000; includeSubDomains once https is reliable; consider the preload list.",
                "low", 3.7));
        }
        if (!headers.ContainsKey("X-Content-Type-Options"))
        {
            findings.Add(Finding("BWR-OWASP-API8-XCTO", "Missing X-Content-Type-Options: nosniff",
                "Responses don't set X-Content-Type-Options: nosniff — a browser may MIME-sniff the body into an unintended content type (e.g. treating an upload as HTML).",
                "Set X-Content-Type-Options: nosniff on every response.",
                "low", 3.1));
        }
        if (isHtml && !headers.ContainsKey("Content-Security-Policy"))
        {
            findings.Add(Finding("BWR-OWASP-API8-CSP", "Missing Content-Security-Policy",
                "HTML response without a Content-Security-Policy — no defence-in-depth against injected/inline scripts.",
                "Add a Content-Security-Policy tuned to the app; start from default-src 'self' and tighten.",
                "low", 3.1));
        }
        if (isHtml && !headers.ContainsKey("X-Frame-Options") && !CspHasFrameAncestors(headers))
        {
            findings.Add(Finding("BWR-OWASP-API8-XFO", "Missing clickjacking protection",
                "HTML response without X-Frame-Options or a CSP frame-ancestors directive — the page can be embedded in a hostile frame (clickjacking).",
                "Add X-Frame-Options: DENY (or SAMEORIGIN), or a CSP frame-ancestors 'none' / 'self' directive.",
                "low", 3.7));
        }

        if (findings.Count == 0)
        {
            findings.Add(Marker(ScanFindingStatus.Safe, "API8-CONFIG-CLEAN", "No CORS / security-header issues found",
                $"Base returned HTTP {status}; CORS is not over-permissive and the checked security headers are present."));
        }
        return findings;
    }

    // ---- helpers ----

    private static Dictionary<string, string> CollectHeaders(HttpResponseMessage resp)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers) headers[h.Key] = string.Join(",", h.Value);
        foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        return headers;
    }

    private static bool CspHasFrameAncestors(Dictionary<string, string> headers)
        => headers.TryGetValue("Content-Security-Policy", out var csp)
            && csp.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase);

    private static bool TryIsHttps(string target)
    {
        try { return string.Equals(new Uri(target).Scheme, "https", StringComparison.OrdinalIgnoreCase); }
        catch (UriFormatException) { return false; }
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
            remediation: "Diagnostic marker for the API8 config probe."),
        Status = status,
        Detail = detail,
    };
}
