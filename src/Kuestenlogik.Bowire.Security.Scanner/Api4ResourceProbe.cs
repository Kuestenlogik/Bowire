// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Default probe for <c>API4:2023 — Unrestricted Resource Consumption</c>.
/// Two HTTP-class checks:
/// <list type="number">
///   <item><b>Rate limiting</b> — fire a small burst of rapid requests at
///   the base; if none come back <c>429</c> and no response advertises a
///   rate-limit header (<c>RateLimit-*</c> / <c>X-RateLimit-*</c> /
///   <c>Retry-After</c>), the API shows no throttling.</item>
///   <item><b>Request-size cap</b> — POST a 1 MB body; a <c>2xx</c>
///   (rather than <c>413 Payload Too Large</c>) means no obvious max-body
///   limit is enforced on that path.</item>
/// </list>
/// The burst is deliberately modest (see <see cref="BurstCount"/>) — enough
/// to trip a throttle, not a load test.
/// </summary>
internal sealed class Api4ResourceProbe : IOwaspApiProbe
{
    /// <summary>Requests fired in the rate-limit burst. Modest on purpose.</summary>
    private const int BurstCount = 20;

    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API4:2023");

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct)
    {
        var findings = new List<ScanFinding>();

        // 1. Rate-limit burst.
        var burst = await RunBurstAsync(http, target, authHeaders, ct).ConfigureAwait(false);
        if (burst is null)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-UNREACHABLE", "API4 probe skipped",
                "No request in the burst reached the target — rate-limit / resource checks skipped.")];
        }
        var (sawThrottle, sawRateHeaders, completed) = burst.Value;
        if (!sawThrottle && !sawRateHeaders)
        {
            findings.Add(Finding("BWR-OWASP-API4-RATELIMIT", "No rate limiting observed",
                $"{completed} rapid requests to the base returned no 429 and no rate-limit headers (RateLimit-* / X-RateLimit-* / Retry-After). An unthrottled API is exposed to resource-exhaustion and cost-amplification abuse.",
                "Enforce per-client rate limits (token bucket / sliding window) at the gateway or application. Return 429 with Retry-After and advertise the budget via RateLimit-* response headers.",
                "medium", 5.3));
        }

        // 2. Request-size cap.
        try
        {
            var status = await PostLargeBodyAsync(http, target, authHeaders, ct).ConfigureAwait(false);
            if (status is >= 200 and < 300)
            {
                findings.Add(Finding("BWR-OWASP-API4-BODYSIZE", "Large request body accepted",
                    $"A 1 MB POST body returned HTTP {status} (no 413 Payload Too Large). Without a maximum request-size cap, oversized bodies can exhaust memory / CPU.",
                    "Set an explicit max request-body size (ASP.NET Core MaxRequestBodySize, nginx client_max_body_size, gateway body-limit) and reject oversize with 413.",
                    "low", 3.7));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            // best-effort — a base that refuses POST (404/405/connect error) is inconclusive, not a finding
        }

        if (findings.Count == 0)
        {
            findings.Add(Marker(ScanFindingStatus.Safe, "API4-CLEAN", "No resource-consumption issues found",
                $"Throttling observed within {completed} rapid requests, and the 1 MB body probe did not indicate an unbounded request size."));
        }
        return findings;
    }

    // ---- checks ----

    private static async Task<(bool Throttle, bool RateHeaders, int Completed)?> RunBurstAsync(
        HttpClient http, string target, IList<string> authHeaders, CancellationToken ct)
    {
        var tasks = new List<Task<(bool Ok, bool Throttle, bool RateHeaders)>>(BurstCount);
        for (var i = 0; i < BurstCount; i++)
            tasks.Add(OneAsync(http, target, authHeaders, ct));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var completed = results.Count(r => r.Ok);
        if (completed == 0) return null;
        return (results.Any(r => r.Throttle), results.Any(r => r.RateHeaders), completed);
    }

    private static async Task<(bool Ok, bool Throttle, bool RateHeaders)> OneAsync(
        HttpClient http, string url, IList<string> authHeaders, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ScanCommand.ApplyAuthHeaders(req, authHeaders);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (true, (int)resp.StatusCode == 429, HasRateLimitHeader(resp));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return (false, false, false);
        }
    }

    private static bool HasRateLimitHeader(HttpResponseMessage resp)
    {
        foreach (var header in resp.Headers)
        {
            var normalized = header.Key.Replace("-", "", StringComparison.Ordinal);
            if (normalized.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<int> PostLargeBodyAsync(HttpClient http, string url, IList<string> authHeaders, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        ScanCommand.ApplyAuthHeaders(req, authHeaders);
        var payload = new byte[1024 * 1024]; // 1 MB of zero bytes — modest, just enough to trip a small default cap
        req.Content = new ByteArrayContent(payload);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return (int)resp.StatusCode;
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
            remediation: "Diagnostic marker for the API4 suite row."),
        Status = status,
        Detail = detail,
    };
}
