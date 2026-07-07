// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Default probe for <c>API10:2023 — Unsafe Consumption of APIs</c>. API10 is
/// a <em>server-side</em> weakness — an API trusting the data it consumes from
/// upstream third-party APIs without validation — so it is largely untestable
/// by black-box DAST: the vulnerable code path runs between the target and its
/// upstream, out of the scanner's reach. This probe therefore takes the
/// documented <b>passive-heuristic</b> route: it inspects a single base
/// response for the one thing black-box <em>can</em> observe — the target
/// leaking raw, un-sanitised upstream data back to the client, which is direct
/// evidence it does not treat upstream responses as untrusted:
/// <list type="number">
///   <item><b>Reflected upstream error.</b> The body carries a raw upstream /
///   gateway error (<c>upstream connect error</c>, <c>no healthy upstream</c>,
///   <c>502 Bad Gateway</c> naming a backend, <c>ECONNREFUSED</c> /
///   <c>getaddrinfo</c> from an outbound call) — the target forwards upstream
///   failures verbatim instead of sanitising them.</item>
///   <item><b>Off-host redirect.</b> A <c>3xx</c> <c>Location</c> pointing at a
///   different host — the API may be following / forwarding an upstream
///   redirect without validating its destination.</item>
/// </list>
/// When neither signal fires the probe does <em>not</em> claim a clean pass:
/// black-box can't prove upstream-consumption safety, so it emits a
/// <see cref="ScanFindingStatus.Skipped"/> review-only marker pointing the
/// operator at a code / config review of the target's outbound integrations.
/// </summary>
internal sealed class Api10UnsafeConsumptionProbe : IOwaspApiProbe
{
    /// <summary>Bytes of the base response body scanned for upstream-error markers.</summary>
    private const int BodyScanCap = 8192;

    // Phrases that only appear when a target forwards a raw upstream / gateway
    // failure to the client — the observable tell of unsafe consumption.
    private static readonly string[] s_upstreamErrorMarkers =
    [
        "upstream connect error", "no healthy upstream", "upstream request timeout",
        "502 bad gateway", "504 gateway", "bad gateway", "gateway timeout",
        "econnrefused", "ehostunreach", "etimedout", "getaddrinfo",
        "socket hang up", "upstreamerror", "failed to fetch upstream",
        "error connecting to upstream",
    ];

    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API10:2023");

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct)
    {
        int status;
        string? location;
        string bodyPrefix;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            ScanCommand.ApplyAuthHeaders(req, authHeaders);
            // Don't let HttpClient swallow the 3xx — we want to see the Location.
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            status = (int)resp.StatusCode;
            location = resp.Headers.Location?.ToString();
            bodyPrefix = await ReadPrefixAsync(resp, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return [Marker(ScanFindingStatus.Skipped, "API10-UNREACHABLE", "API10 probe skipped",
                $"Base request failed ({ex.GetType().Name}) — unsafe-consumption heuristics skipped.")];
        }

        var findings = new List<ScanFinding>();

        if (MatchesUpstreamError(bodyPrefix))
        {
            findings.Add(Finding("BWR-OWASP-API10-UPSTREAM-ERROR", "Raw upstream error forwarded to the client",
                "The response body carries a raw upstream / gateway error verbatim. Forwarding upstream failures unsanitised shows the target does not treat responses from the APIs it consumes as untrusted — the essence of API10 — and leaks the topology of its outbound dependencies.",
                "Treat every upstream response as untrusted input: validate status / schema / content-type before use, and map upstream failures to a generic, sanitised error for the client rather than forwarding them verbatim."));
        }

        if (status is >= 300 and < 400 && IsOffHost(target, location))
        {
            findings.Add(Finding("BWR-OWASP-API10-OFFHOST-REDIRECT", "Redirect to a different host",
                $"The base returned HTTP {status} redirecting to a different host ({HostOf(location)}). If that destination is derived from an upstream / third-party response, following it without validating the target is an unsafe-consumption vector (SSRF-adjacent, open-redirect, data exfiltration).",
                "Validate redirect destinations against an allow-list before following or forwarding them. Never follow a Location built from upstream / user-controlled data without checking its host."));
        }

        if (findings.Count == 0)
        {
            // No passive signal — but black-box can't prove upstream-consumption
            // safety, so this is honestly "not assessed", not a clean pass.
            findings.Add(Marker(ScanFindingStatus.Skipped, "API10-REVIEW-ONLY",
                "API10 requires review — no black-box signal",
                "API10 (Unsafe Consumption of APIs) is a server-side concern between the target and its upstream dependencies; black-box DAST can only surface a target that leaks raw upstream data. None observed here — assess how the service validates data it consumes from third-party / internal APIs via code / config review of its outbound integrations."));
        }
        return findings;
    }

    // ---- helpers ----

    private static async Task<string> ReadPrefixAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > BodyScanCap ? body[..BodyScanCap] : body;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return "";
        }
    }

    private static bool MatchesUpstreamError(string body)
    {
        foreach (var marker in s_upstreamErrorMarkers)
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsOffHost(string target, string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;
        try
        {
            var baseUri = new Uri(target);
            // Location may be relative (same host) or absolute (possibly off-host).
            if (!Uri.TryCreate(baseUri, location, out var dest)) return false;
            return !string.Equals(dest.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static string HostOf(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return "unknown";
        return Uri.TryCreate(location, UriKind.Absolute, out var u) ? u.Host : location;
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-20", owaspApi: Entry.Tag, severity: "low", cvss: 3.7, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the API10 unsafe-consumption probe."),
        Status = status,
        Detail = detail,
    };
}
