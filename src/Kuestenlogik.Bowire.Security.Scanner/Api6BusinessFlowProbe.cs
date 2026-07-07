// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Default probe for <c>API6:2023 — Unrestricted Access to Sensitive Business
/// Flows</c>. API6 is the one Top-10 entry with no clean generic black-box
/// check: whether a flow is <em>sensitive</em> (purchase, invite, vote,
/// scrape) is business context Bowire can't infer, and the raw rate-limit
/// facet already belongs to <see cref="Api4ResourceProbe"/>. What this probe
/// <em>can</em> assess black-box is the property that makes a sensitive flow
/// abusable once you've found one: <b>the same state-changing request replayed
/// verbatim, accepted every time, with no anti-automation friction</b>.
///
/// <para>It fires a modest burst of identical POSTs at the target and looks
/// for <em>any</em> control that would slow an attacker scripting the flow:
/// a CAPTCHA / challenge in the body, a bot-mitigation cookie / header
/// (Cloudflare, DataDome, PerimeterX, Imperva…), a per-request anti-replay
/// token (CSRF / nonce / <c>Idempotency-Key</c> echo), or throttling
/// (<c>429</c> / <c>Retry-After</c>). N identical POSTs all accepted with
/// none of those is the API6 smell — the flow is trivially scriptable.</para>
///
/// <para>The verdict is deliberately conservative: a flag only fires when the
/// endpoint actually accepts the repeated POST (<c>2xx</c>); a base that
/// rejects POST (<c>404</c> / <c>405</c> / auth-gated) or is unreachable is
/// reported <see cref="ScanFindingStatus.Skipped"/> — API6 needs a real
/// sensitive-flow endpoint to assess, so point <c>--url</c> at one (or replay
/// a stored workspace flow) rather than the service root.</para>
/// </summary>
internal sealed class Api6BusinessFlowProbe : IOwaspApiProbe
{
    /// <summary>
    /// Identical POSTs fired in the friction burst. Small on purpose — we're
    /// testing repeatability / anti-automation, not rate (that's API4) — but
    /// enough that a per-request nonce or single-use token would break by now.
    /// </summary>
    private const int BurstCount = 8;

    /// <summary>Bytes of each response body scanned for challenge markers.</summary>
    private const int BodyScanCap = 4096;

    // Markers that a response is an anti-automation challenge rather than the
    // flow's real result. Lower-cased substring match against the body prefix.
    private static readonly string[] s_challengeMarkers =
    [
        "captcha", "recaptcha", "hcaptcha", "turnstile", "cf-challenge",
        "just a moment", "are you human", "verify you are human",
        "attention required", "px-captcha", "please enable javascript and cookies",
    ];

    // Set-Cookie / header names that betray a bot-mitigation layer in front of
    // the app. A single-cookie match is enough — these are never set by an app
    // that isn't sitting behind the corresponding WAF / anti-bot product.
    private static readonly string[] s_botMitigationTokens =
    [
        "__cf_bm", "cf_clearance", "cf-mitigated", "datadome", "x-datadome",
        "_px", "_pxhd", "pxvid", "incap_ses", "visid_incap", "ak_bmsc", "bm_sz",
    ];

    // Header / cookie names that indicate a per-request anti-replay token —
    // if these rotate across the burst the flow is not blindly replayable.
    private static readonly string[] s_antiReplayTokens =
    [
        "x-csrf-token", "x-xsrf-token", "csrf-token", "xsrf-token",
        "request-id-signature", "x-nonce", "idempotency-key",
    ];

    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API6:2023");

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct)
    {
        var tasks = new List<Task<Attempt>>(BurstCount);
        for (var i = 0; i < BurstCount; i++)
            tasks.Add(OnePostAsync(http, target, authHeaders, ct));
        var attempts = await Task.WhenAll(tasks).ConfigureAwait(false);

        var reached = attempts.Count(a => a.Reached);
        if (reached == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API6-UNREACHABLE", "API6 probe skipped",
                "No request in the burst reached the target — business-flow friction check skipped.")];
        }

        var accepted = attempts.Count(a => a.Accepted);
        if (accepted == 0)
        {
            // The endpoint exists but doesn't accept a repeated POST as a flow
            // (405 / 404 / auth-gated). API6 needs a real sensitive-flow
            // endpoint — assessing the service root is not meaningful.
            return [Marker(ScanFindingStatus.Skipped, "API6-NO-FLOW",
                "API6 probe skipped — no acceptable business flow at the target",
                "Repeated POSTs to the target were not accepted (no 2xx) — point --url at a sensitive-flow endpoint (checkout, invite, vote, signup) or replay a stored workspace flow to assess API6.")];
        }

        var sawChallenge = attempts.Any(a => a.Challenge);
        var sawBotMitigation = attempts.Any(a => a.BotMitigation);
        var sawThrottle = attempts.Any(a => a.Throttle);
        var sawAntiReplay = attempts.Any(a => a.AntiReplay);

        if (!sawChallenge && !sawBotMitigation && !sawThrottle && !sawAntiReplay)
        {
            return [Finding("BWR-OWASP-API6-NO-FRICTION", "Sensitive flow scriptable without anti-automation controls",
                $"{accepted} identical POSTs to the target were all accepted with no anti-automation friction — no CAPTCHA / challenge, no bot-mitigation layer (Cloudflare / DataDome / PerimeterX / Imperva), no per-request anti-replay token (CSRF / nonce / Idempotency-Key), and no throttling (429 / Retry-After). If this endpoint drives a sensitive business flow (checkout, invite, vote, signup, password-reset), it can be scripted end-to-end at scale.",
                "Protect sensitive flows with anti-automation controls proportionate to the risk: device fingerprinting / bot detection, a CAPTCHA or proof-of-work step, per-request one-time tokens (CSRF / nonce / Idempotency-Key), and per-identity flow-rate limits. See OWASP API6:2023.",
                "medium", 5.3)];
        }

        var seen = new List<string>(4);
        if (sawChallenge) seen.Add("a CAPTCHA / challenge");
        if (sawBotMitigation) seen.Add("a bot-mitigation layer");
        if (sawAntiReplay) seen.Add("a per-request anti-replay token");
        if (sawThrottle) seen.Add("throttling");
        return [Marker(ScanFindingStatus.Safe, "API6-FRICTION", "Anti-automation friction observed on the flow",
            $"The repeated-POST burst hit {string.Join(" / ", seen)} — the flow is not trivially scriptable end-to-end.")];
    }

    // ---- one attempt ----

    private readonly record struct Attempt(
        bool Reached, bool Accepted, bool Challenge, bool BotMitigation, bool Throttle, bool AntiReplay);

    private static async Task<Attempt> OnePostAsync(HttpClient http, string url, IList<string> authHeaders, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            ScanCommand.ApplyAuthHeaders(req, authHeaders);
            // A minimal, verbatim-repeatable JSON body — the shape a flow
            // trigger expects. Identical every time so a single-use nonce or
            // idempotency guard would reject the replay.
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

            var status = (int)resp.StatusCode;
            var accepted = status is >= 200 and < 300;
            var throttle = status == 429 || resp.Headers.Contains("Retry-After");

            var headerNames = new List<string>();
            foreach (var h in resp.Headers) headerNames.Add(h.Key);
            foreach (var h in resp.Content.Headers) headerNames.Add(h.Key);
            var cookieBlob = resp.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? string.Join(";", cookies) : "";
            var headerBlob = string.Join(";", headerNames) + ";" + cookieBlob;

            var botMitigation = MatchesAny(headerBlob, s_botMitigationTokens);
            var antiReplay = MatchesAny(headerBlob, s_antiReplayTokens);

            var challenge = status is 403 or 429 or 503 && await BodyHasChallengeAsync(resp, ct).ConfigureAwait(false);

            return new Attempt(true, accepted, challenge, botMitigation, throttle, antiReplay);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return default; // unreached
        }
    }

    private static async Task<bool> BodyHasChallengeAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var prefix = body.Length > BodyScanCap ? body[..BodyScanCap] : body;
            return MatchesAny(prefix, s_challengeMarkers);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool MatchesAny(string haystack, string[] needles)
    {
        foreach (var needle in needles)
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-799", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the API6 business-flow probe."),
        Status = status,
        Detail = detail,
    };
}
