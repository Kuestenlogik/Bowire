// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Default probe for <c>API2:2023 — Broken Authentication</c>. Needs a
/// credential (<c>--auth-header Authorization: Bearer …</c>) to test against;
/// with one it establishes an authenticated 2xx baseline at the target and
/// then checks:
/// <list type="bullet">
///   <item><b>Authentication not enforced</b> — the same request succeeds
///   with no credential at all.</item>
///   <item><b>JWT forgery</b> — a token re-headed to <c>alg:none</c> or with a
///   corrupted signature is still accepted. Only asserted when anonymous
///   access is actually blocked, so a public API isn't misreported.</item>
///   <item><b>Token lifetime</b> — an already-expired token is still accepted
///   (reuse after expiry), or the JWT carries no <c>exp</c> claim at all.</item>
/// </list>
/// </summary>
internal sealed class Api2AuthProbe : IOwaspApiProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API2:2023");

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, CancellationToken ct)
    {
        if (authHeaders is null || authHeaders.Count == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API2-NO-CRED", "API2 checks need a credential",
                "No --auth-header supplied. Authentication checks (unauthenticated access, JWT forgery, token expiry) need a credential to test against — re-run with e.g. --auth-header \"Authorization: Bearer <token>\".")];
        }

        int authed;
        try { authed = await StatusAsync(http, target, authHeaders, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return [Marker(ScanFindingStatus.Skipped, "API2-UNREACHABLE", "API2 probe skipped", $"Target could not be reached ({ex.GetType().Name}).")];
        }
        if (authed is < 200 or >= 300)
        {
            return [Marker(ScanFindingStatus.Skipped, "API2-NO-BASELINE", "No authenticated 2xx baseline",
                $"The credential did not yield a 2xx at {target} (got HTTP {authed}). Point --target at an endpoint the credential authorizes so API2 can test authentication there.")];
        }

        var findings = new List<ScanFinding>();

        // Is anonymous access blocked? Everything about forgery hinges on this:
        // if the endpoint answers 2xx with no credential, it isn't enforcing
        // auth at all — forged-token "successes" would be meaningless.
        var anon = await SafeStatusAsync(http, target, Array.Empty<string>(), ct).ConfigureAwait(false);
        var authEnforced = !(anon is >= 200 and < 300);
        if (!authEnforced)
        {
            findings.Add(Finding("BWR-OWASP-API2-NOAUTH", "Endpoint returns success without authentication",
                $"GET returned HTTP {anon} with no credential (and {authed} with one). The endpoint doesn't require authentication — confirm this isn't an intentionally public resource.",
                "Require authentication on protected endpoints and return 401 when no valid credential is presented.",
                "medium", 5.3));
        }

        var authorization = FindBearer(authHeaders);
        if (authorization is { } bearer && LooksLikeJwt(bearer.Token))
        {
            // Token lifetime (properties of the token itself).
            var (hasExp, exp) = ReadExp(bearer.Token);
            if (hasExp && exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() && authEnforced)
            {
                findings.Add(Finding("BWR-OWASP-API2-EXPIRED", "Expired token still accepted",
                    "The supplied token's exp is in the past, yet the authenticated request succeeded — the server accepts expired tokens (reuse after expiry).",
                    "Validate the exp claim on every request and reject expired tokens with 401.",
                    "high", 7.5));
            }
            else if (!hasExp)
            {
                findings.Add(Finding("BWR-OWASP-API2-NOEXP", "JWT carries no expiry (exp) claim",
                    "The supplied JWT has no exp claim — it never expires, so a leaked token stays valid forever.",
                    "Issue short-lived tokens with an exp claim and validate it server-side.",
                    "medium", 5.3));
            }

            // Signature forgery — only meaningful when anonymous access is blocked.
            if (authEnforced)
            {
                var noneStatus = await SafeStatusAsync(http, target, WithBearer(authHeaders, bearer.HeaderName, ForgeNone(bearer.Token)), ct).ConfigureAwait(false);
                if (noneStatus is >= 200 and < 300)
                {
                    findings.Add(Finding("BWR-OWASP-API2-ALG-NONE", "Unsigned JWT (alg:none) accepted",
                        $"A token re-headed to alg:none returned HTTP {noneStatus} while anonymous access is blocked ({anon}). The server accepts unsigned tokens — anyone can forge any identity.",
                        "Reject alg:none. Pin the expected algorithm(s) and verify the signature against the issuer key on every request.",
                        "critical", 9.8));
                }

                var tamperStatus = await SafeStatusAsync(http, target, WithBearer(authHeaders, bearer.HeaderName, TamperSignature(bearer.Token)), ct).ConfigureAwait(false);
                if (tamperStatus is >= 200 and < 300)
                {
                    findings.Add(Finding("BWR-OWASP-API2-SIG", "JWT signature not verified",
                        $"A token with a corrupted signature returned HTTP {tamperStatus} while anonymous access is blocked ({anon}). The server isn't verifying the signature.",
                        "Verify the JWT signature against the issuer's key on every request; reject any token that fails.",
                        "critical", 9.1));
                }
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(Marker(ScanFindingStatus.Safe, "API2-CLEAN", "No broken-authentication issues found",
                "Authentication is enforced, the token carries an expiry, and forged (alg:none / tampered-signature) variants were rejected."));
        }
        return findings;
    }

    // ---- JWT helpers ----

    private static (string HeaderName, string Token)? FindBearer(IList<string> authHeaders)
    {
        foreach (var raw in authHeaders.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = raw[..colon].Trim();
            var value = raw[(colon + 1)..].Trim();
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return (name, value["Bearer ".Length..].Trim());
            }
        }
        return null;
    }

    private static bool LooksLikeJwt(string token)
    {
        var parts = token.Split('.');
        return parts.Length == 3 && parts[0].Length > 0 && parts[1].Length > 0;
    }

    private static (bool HasExp, long Exp) ReadExp(string token)
    {
        try
        {
            var payload = Base64UrlDecode(token.Split('.')[1]);
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("exp", out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var exp))
                return (true, exp);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException)
        {
            // malformed token — treat as no readable exp
        }
        return (false, 0);
    }

    private static string ForgeNone(string token)
    {
        var parts = token.Split('.');
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        // header . payload . (empty signature)
        return header + "." + parts[1] + ".";
    }

    private static string TamperSignature(string token)
    {
        var parts = token.Split('.');
        // Keep header + payload, swap the signature for a valid-charset but wrong value.
        return parts[0] + "." + parts[1] + ".bowireTamperedSignature";
    }

    private static List<string> WithBearer(IList<string> authHeaders, string headerName, string newToken)
    {
        var result = new List<string>(authHeaders.Count);
        foreach (var raw in authHeaders)
        {
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            var name = colon > 0 ? raw[..colon].Trim() : raw;
            if (name.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                result.Add($"{headerName}: Bearer {newToken}");
            else
                result.Add(raw);
        }
        return result;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- http ----

    private static async Task<int> StatusAsync(HttpClient http, string url, IList<string> authHeaders, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ScanCommand.ApplyAuthHeaders(req, authHeaders);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return (int)resp.StatusCode;
    }

    private static async Task<int> SafeStatusAsync(HttpClient http, string url, IList<string> authHeaders, CancellationToken ct)
    {
        try { return await StatusAsync(http, url, authHeaders, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException) { return -1; }
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
            remediation: "Diagnostic marker for the API2 suite row."),
        Status = status,
        Detail = detail,
    };
}
