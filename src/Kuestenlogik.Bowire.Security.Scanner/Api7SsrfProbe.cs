// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Specialized;
using System.Diagnostics;
using System.Web;
using Kuestenlogik.Bowire.Security;

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

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(OwaspApiProbeContext ctx, CancellationToken ct)
    {
        var (target, http, authHeaders, authHeadersB) =
            (ctx.Target, ctx.Http, ctx.AuthHeaders, ctx.AuthHeadersB);
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

        // Plant a callback host in every candidate before timing any of
        // them, so one round of waiting covers the lot rather than one per
        // parameter. Nothing is asserted yet -- a callback is evidence when
        // it arrives, and its absence is not evidence of anything.
        var planted = await PlantCallbacksAsync(ctx, uri, query, urlParams, ct).ConfigureAwait(false);

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

        // Out-of-band evidence, if any arrived. Added after the timing
        // findings and never merged into them: a callback proves the fetch
        // happened where a latency stall infers it, and a reader should be
        // able to tell which they are looking at.
        findings.AddRange(await CollectCallbacksAsync(ctx, planted, ct).ConfigureAwait(false));

        if (findings.Count == 0)
        {
            findings.Add(Marker(ScanFindingStatus.Safe, "API7-CLEAN", "No SSRF timing signal found",
                $"Probed {urlParams.Count} URL-input parameter(s); none showed the latency stall that indicates a server-side fetch."
                + (planted.Count > 0
                    ? " No out-of-band callback arrived either, which is not the same as proof there is none: a target with no outbound network reaches nothing."
                    : string.Empty)));
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

    /// <summary>
    /// Put a fresh callback host in each candidate parameter and fire the
    /// request, mapping each host back to the parameter it went into.
    /// </summary>
    /// <returns>Empty when the scan has no interaction server.</returns>
    private static async Task<IReadOnlyDictionary<string, string>> PlantCallbacksAsync(
        OwaspApiProbeContext ctx, Uri uri, NameValueCollection query,
        IReadOnlyList<string> urlParams, CancellationToken ct)
    {
        if (ctx.Oast is null) return new Dictionary<string, string>();

        // One host per parameter, so an arriving callback names which input
        // reached the network. That is the difference between "this endpoint
        // is vulnerable" and a report somebody has to re-test by hand.
        var planted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var param in urlParams)
        {
            ct.ThrowIfCancellationRequested();

            var callbackHost = await ctx.Oast.AllocateAsync(ct).ConfigureAwait(false);
            planted[callbackHost] = param;

            try
            {
                await TimeAsync(
                    ctx.Http,
                    SwapParam(uri, query, param, $"http://{callbackHost}/"),
                    ctx.AuthHeaders, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                          or InvalidOperationException or UriFormatException)
            {
                // Our request failing says nothing about whether the server
                // made its own. A callback may still arrive.
                _ = ex;
            }
        }
        return planted;
    }

    /// <summary>
    /// Wait briefly, then turn the callbacks that arrived into findings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A callback lands after the target has made its own request, so there
    /// is nothing to read at the moment our request returns. The wait is
    /// short and bounded: a scan that blocked until a callback that may never
    /// come would hang on every target that simply does not fetch.
    /// </para>
    /// <para>
    /// Anything slower than this still reaches the workbench live feed, which
    /// polls the same session. Losing it from the finding costs less than
    /// making every clean scan wait.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ScanFinding>> CollectCallbacksAsync(
        OwaspApiProbeContext ctx, IReadOnlyDictionary<string, string> planted, CancellationToken ct)
    {
        if (ctx.Oast is null || planted.Count == 0) return [];

        var matched = new Dictionary<string, List<ProbeInteraction>>(StringComparer.OrdinalIgnoreCase);
        var deadline = DateTimeOffset.UtcNow + ctx.OastGrace;

        while (DateTimeOffset.UtcNow < deadline && matched.Count < planted.Count)
        {
            try { await Task.Delay(ctx.OastPollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            IReadOnlyList<OastCallback> seen;
            try { seen = await ctx.Oast.PollAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // The interaction server went away mid-scan. The timing
                // findings stand; this pass contributes nothing.
                _ = ex;
                break;
            }

            // The feed is cumulative, so this rebuilds the match set each
            // pass rather than appending -- otherwise a callback seen in two
            // polls would be counted twice.
            matched.Clear();
            foreach (var callback in seen)
            {
                var host = planted.Keys.FirstOrDefault(h =>
                    callback.Id?.Contains(h, StringComparison.OrdinalIgnoreCase) == true);
                if (host is null) continue;

                if (!matched.TryGetValue(host, out var list))
                {
                    list = [];
                    matched[host] = list;
                }
                list.Add(new ProbeInteraction
                {
                    Protocol = callback.Protocol,
                    Id = callback.Id,
                    RemoteAddress = callback.RemoteAddress,
                    RawRequest = callback.RawRequest,
                });
            }
        }

        return [.. matched.Select(pair => Confirmed(planted[pair.Key], pair.Value))];
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    /// <summary>
    /// An SSRF the target proved by reaching out. Separate from the timing
    /// finding on purpose: this one carries the callback as evidence and does
    /// not rest on a latency threshold anyone has to trust.
    /// </summary>
    private ScanFinding Confirmed(string param, IReadOnlyList<ProbeInteraction> interactions) => new()
    {
        Template = SyntheticTemplate.Build(
            id: $"BWR-OWASP-API7-OOB-{param.ToUpperInvariant()}",
            name: $"Confirmed SSRF via '{param}' parameter",
            cwe: null, owaspApi: Entry.Tag, severity: "critical", cvss: 9.1,
            remediation: "Validate and allow-list outbound URLs server-side: reject internal / link-local / metadata ranges (169.254.0.0/16, 127.0.0.0/8, 10/8, 172.16/12, 192.168/16), pin schemes/hosts, and disable redirects on the fetch."),
        Status = ScanFindingStatus.Vulnerable,
        Detail = $"A callback host planted in '{param}' was contacted from "
            + string.Join(", ", interactions.Select(i => i.RemoteAddress ?? "an unrecorded address").Distinct(StringComparer.Ordinal))
            + " over "
            + string.Join("/", interactions.Select(i => i.Protocol).Distinct(StringComparer.Ordinal))
            + ". The server fetched the supplied URL. This is the request arriving, not an inference from timing.",
        Response = new AttackProbeResponse { Interactions = interactions },
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the API7 suite row."),
        Status = status,
        Detail = detail,
    };
}
