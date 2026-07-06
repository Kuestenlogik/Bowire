// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Specialized;
using System.Diagnostics;
using System.Web;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Default probe for <c>API7:2023 — Server Side Request Forgery</c>. SSRF is a
/// server-side effect, so without an out-of-band callback channel this probe
/// uses a <b>timing differential</b>: it finds query parameters on the target
/// whose name suggests a URL / host input (<c>url</c>, <c>uri</c>,
/// <c>callback</c>, <c>webhook</c>, <c>dest</c>, <c>image</c>, …), then swaps
/// each in turn for a non-routable blackhole address and compares the response
/// latency against a fast-failing baseline. A server that fetches the supplied
/// URL stalls on the blackhole (connect timeout) — a large latency delta is
/// strong evidence the parameter is resolved server-side.
/// </summary>
/// <remarks>
/// Gated hard on the target actually carrying a URL-input parameter: a base
/// URL with no query string is reported as <see cref="ScanFindingStatus.Skipped"/>
/// with guidance to point <c>--target</c> at a request that includes the
/// URL-taking parameter, rather than a misleading "clean".
/// </remarks>
internal sealed class Api7SsrfProbe : IOwaspApiProbe
{
    // Parameter-name fragments that commonly carry a server-fetched URL/host.
    private static readonly string[] s_urlParamHints =
    [
        "url", "uri", "link", "dest", "redirect", "callback", "webhook",
        "feed", "proxy", "image", "img", "host", "domain", "site", "fetch", "load", "next",
    ];

    // Non-routable blackhole (TEST-NET-3 / reserved) — a server that tries to
    // connect stalls until the connect timeout; nothing legitimate resolves here.
    private const string Blackhole = "http://10.255.255.1/bowire-ssrf-probe";

    // Latency delta (ms) above which we treat the blackhole request as
    // "the server tried to fetch it". Connect timeouts dwarf this; set well
    // above normal jitter.
    private const int LatencyDeltaMs = 1500;

    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API7:2023");

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, CancellationToken ct)
    {
        Uri uri;
        try { uri = new Uri(target); }
        catch (UriFormatException)
        {
            return [Marker(ScanFindingStatus.Error, "API7-INVALID-TARGET", "API7 probe skipped", $"Could not parse target '{target}' as a URL.")];
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        var urlParams = FindUrlParams(query);
        if (urlParams.Count == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API7-NO-URL-PARAM", "API7 needs a URL-input parameter",
                "The target has no query parameter that looks like a server-fetched URL. Point --target at a request that includes the URL-taking parameter (e.g. ?url=… or ?webhook=…) so SSRF can be tested.")];
        }

        // Baseline latency for the unmodified target.
        double baseline;
        try { baseline = await TimeAsync(http, target, authHeaders, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return [Marker(ScanFindingStatus.Skipped, "API7-UNREACHABLE", "API7 probe skipped", $"Baseline request failed ({ex.GetType().Name}).")];
        }

        var findings = new List<ScanFinding>();
        foreach (var param in urlParams)
        {
            var mutated = SwapParam(uri, query, param, Blackhole);
            double stalled;
            try { stalled = await TimeAsync(http, mutated, authHeaders, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { stalled = double.MaxValue; } // timed out = stalled fetching
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException) { continue; }

            if (stalled - baseline >= LatencyDeltaMs)
            {
                findings.Add(Finding($"BWR-OWASP-API7-{param.ToUpperInvariant()}",
                    $"Possible SSRF via '{param}' parameter",
                    $"Swapping '{param}' for a non-routable blackhole URL stalled the response (~{Fmt(stalled)} vs ~{Fmt(baseline)} baseline). The server appears to fetch the supplied URL — a classic SSRF surface.",
                    "Validate and allow-list outbound URLs server-side: reject internal / link-local / metadata ranges (169.254.0.0/16, 127.0.0.0/8, 10/8, 172.16/12, 192.168/16), pin schemes/hosts, and disable redirects on the fetch.",
                    "high", 8.6));
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(Marker(ScanFindingStatus.Safe, "API7-CLEAN", "No SSRF timing signal found",
                $"Probed {urlParams.Count} URL-input parameter(s); none showed the latency stall that indicates a server-side fetch."));
        }
        return findings;
    }

    // ---- helpers ----

    private static List<string> FindUrlParams(NameValueCollection query)
    {
        var hits = new List<string>();
        foreach (var key in query.AllKeys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            var value = query[key] ?? "";
            var looksUrl = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (looksUrl || s_urlParamHints.Any(h => key.Contains(h, StringComparison.OrdinalIgnoreCase)))
                hits.Add(key);
        }
        return hits;
    }

    private static string SwapParam(Uri uri, NameValueCollection original, string param, string newValue)
    {
        var copy = HttpUtility.ParseQueryString(uri.Query);
        copy[param] = newValue;
        var builder = new UriBuilder(uri) { Query = copy.ToString() };
        return builder.Uri.ToString();
    }

    private static async Task<double> TimeAsync(HttpClient http, string url, IList<string> authHeaders, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ScanCommand.ApplyAuthHeaders(req, authHeaders);
        var sw = Stopwatch.StartNew();
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static string Fmt(double ms) => ms >= double.MaxValue / 2 ? "timeout" : $"{ms:F0}ms";

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
            remediation: "Diagnostic marker for the API7 suite row."),
        Status = status,
        Detail = detail,
    };
}
