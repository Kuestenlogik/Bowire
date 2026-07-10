// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Security;

/// <summary>Severity of a <see cref="JwtFlag"/>.</summary>
public enum JwtFlagLevel { Info, Low, Medium, High }

/// <summary>One deterministic observation about a JWT's security posture.</summary>
/// <param name="Level">How much it matters.</param>
/// <param name="Claim">The header/payload field it's about (e.g. <c>alg</c>, <c>exp</c>), or a pseudo-name like <c>signature</c>.</param>
/// <param name="Message">Human-readable finding.</param>
public sealed record JwtFlag(JwtFlagLevel Level, string Claim, string Message);

/// <summary>Result of <see cref="JwtSecurityAnalyzer.Analyze"/>.</summary>
public sealed record JwtAnalysis(
    bool Parsed,
    string? Algorithm,
    string? KeyId,
    string HeaderJson,
    string PayloadJson,
    IReadOnlyList<JwtFlag> Flags,
    string? ParseError = null);

/// <summary>
/// Deterministic JWT security analysis (#105): decodes a token's header + payload
/// and runs rule-based checks — <c>alg=none</c>, symmetric-HMAC crackability,
/// missing / expired / long-lived <c>exp</c>, missing <c>nbf</c>, scope creep,
/// audience binding, future <c>iat</c>, <c>kid</c> injection surface. No crypto,
/// no network, no AI — the substance a tester would otherwise eyeball on jwt.io.
/// An AI layer can narrate these; this is the ground truth it narrates.
/// </summary>
public static class JwtSecurityAnalyzer
{
    private const long OneDaySeconds = 24 * 60 * 60;

    // Scope tokens that read as high- vs low-privilege for the scope-creep check.
    private static readonly string[] s_highPrivMarkers = ["admin", "write", "delete", "root", "superuser", "*", "manage"];
    private static readonly string[] s_lowPrivMarkers = ["read", "readonly", "view", "list", "get"];

    /// <summary>
    /// Analyze <paramref name="token"/>. <paramref name="expectedAudience"/>, when
    /// given, is cross-checked against the <c>aud</c> claim. <paramref name="nowUnixSeconds"/>
    /// overrides "now" for deterministic time checks (defaults to the current time).
    /// </summary>
    public static JwtAnalysis Analyze(string token, string? expectedAudience = null, long? nowUnixSeconds = null)
    {
        var now = nowUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (string.IsNullOrWhiteSpace(token))
            return new JwtAnalysis(false, null, null, "", "", [], "Empty token.");

        var parts = token.Split('.');
        if (parts.Length is not (2 or 3))
            return new JwtAnalysis(false, null, null, "", "", [], "Not a JWT — expected 2 or 3 dot-separated segments.");

        string headerJson, payloadJson;
        JsonElement header, payload;
        try
        {
            headerJson = DecodeSegment(parts[0]);
            payloadJson = DecodeSegment(parts[1]);
            using var hDoc = JsonDocument.Parse(headerJson);
            using var pDoc = JsonDocument.Parse(payloadJson);
            header = hDoc.RootElement.Clone();
            payload = pDoc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return new JwtAnalysis(false, null, null, "", "", [], "Could not decode header/payload: " + ex.Message);
        }

        var flags = new List<JwtFlag>();
        var alg = GetString(header, "alg");
        var kid = GetString(header, "kid");
        var signaturePresent = parts.Length == 3 && parts[2].Length > 0;

        AnalyzeAlgorithm(flags, alg, signaturePresent);
        if (kid is not null)
            flags.Add(new(JwtFlagLevel.Info, "kid", $"kid=\"{kid}\" — key selection is server-controlled; ensure the key id can't be pointed at an attacker-supplied key (kid injection / path traversal)."));

        AnalyzeExpiry(flags, payload, now);
        AnalyzeNotBefore(flags, payload);
        AnalyzeIssuedAt(flags, payload, now);
        AnalyzeAudience(flags, payload, expectedAudience);
        AnalyzeScope(flags, payload);

        if (flags.Count == 0)
            flags.Add(new(JwtFlagLevel.Info, "-", "No deterministic issues flagged — the token's structure looks conventional."));

        return new JwtAnalysis(true, alg, kid, headerJson, payloadJson, flags);
    }

    private static void AnalyzeAlgorithm(List<JwtFlag> flags, string? alg, bool signaturePresent)
    {
        if (string.IsNullOrEmpty(alg))
        {
            flags.Add(new(JwtFlagLevel.Medium, "alg", "No alg in the header — verifiers that infer the algorithm are attackable; pin the expected algorithm server-side."));
            return;
        }
        if (string.Equals(alg, "none", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add(new(JwtFlagLevel.High, "alg", "alg=none — the token claims to be unsigned. If the server accepts it, the whole JWT layer is bypassable (alg:none downgrade, CVE-2015-9235 class)."));
            return;
        }
        if (!signaturePresent)
            flags.Add(new(JwtFlagLevel.Medium, "signature", $"alg={alg} but the signature segment is empty — a stripped signature that the server still accepts is an alg-downgrade bypass."));

        if (alg.StartsWith("HS", StringComparison.OrdinalIgnoreCase))
            flags.Add(new(JwtFlagLevel.Info, "alg", $"alg={alg} is symmetric HMAC — a weak or guessable secret is crackable offline (try `bowire jwt crack`), and if the server also accepts RS*/ES* an HMAC/RSA key-confusion attack may apply."));
    }

    private static void AnalyzeExpiry(List<JwtFlag> flags, JsonElement payload, long now)
    {
        if (!TryGetUnixTime(payload, "exp", out var exp))
        {
            flags.Add(new(JwtFlagLevel.Medium, "exp", "No exp claim — the token never expires; a leaked token is valid forever."));
            return;
        }
        if (exp < now)
        {
            flags.Add(new(JwtFlagLevel.Info, "exp", "exp is in the past — this token is already expired."));
            return;
        }
        var baseTime = TryGetUnixTime(payload, "iat", out var iat) ? iat : now;
        if (exp - baseTime > OneDaySeconds)
            flags.Add(new(JwtFlagLevel.Medium, "exp", $"Long-lived token — valid for {(exp - baseTime) / 3600} h from issue; a leaked token has a wide abuse window. Prefer short-lived access tokens + refresh."));
        else
            flags.Add(new(JwtFlagLevel.Info, "exp", "exp is set and within a sensible window."));
    }

    private static void AnalyzeNotBefore(List<JwtFlag> flags, JsonElement payload)
    {
        if (!TryGetUnixTime(payload, "nbf", out _))
            flags.Add(new(JwtFlagLevel.Low, "nbf", "No nbf claim — the token is valid from issue time with no not-before skew tolerance."));
    }

    private static void AnalyzeIssuedAt(List<JwtFlag> flags, JsonElement payload, long now)
    {
        if (TryGetUnixTime(payload, "iat", out var iat) && iat > now + 60)
            flags.Add(new(JwtFlagLevel.Info, "iat", "iat is in the future — clock skew, or a forged issue time."));
    }

    private static void AnalyzeAudience(List<JwtFlag> flags, JsonElement payload, string? expectedAudience)
    {
        var audValues = GetAudience(payload);
        if (audValues.Count == 0)
        {
            flags.Add(new(JwtFlagLevel.Low, "aud", "No aud claim — the token isn't bound to a specific audience; a service that doesn't check aud may accept tokens minted for another service."));
            return;
        }
        if (!string.IsNullOrEmpty(expectedAudience)
            && !audValues.Contains(expectedAudience, StringComparer.Ordinal))
        {
            flags.Add(new(JwtFlagLevel.Medium, "aud", $"Audience mismatch — aud={string.Join(", ", audValues)} but the configured target expects \"{expectedAudience}\"."));
        }
    }

    private static void AnalyzeScope(List<JwtFlag> flags, JsonElement payload)
    {
        var scopes = GetScopes(payload);
        if (scopes.Count == 0) return;
        var high = scopes.Where(s => s_highPrivMarkers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase))).ToArray();
        var low = scopes.Any(s => s_lowPrivMarkers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase)));
        if (high.Length > 0 && low)
            flags.Add(new(JwtFlagLevel.Medium, "scope", $"Scope creep — the token mixes low- and high-privilege scopes (high: {string.Join(", ", high)}). Confirm this token needs the privileged scopes for the operation at hand (least privilege)."));
    }

    // ---- decode helpers ----

    private static string DecodeSegment(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Invalid base64url length.");
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool TryGetUnixTime(JsonElement obj, string name, out long value)
    {
        value = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value)) return true;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out value)) return true;
        return false;
    }

    private static List<string> GetAudience(JsonElement payload)
    {
        var result = new List<string>();
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("aud", out var aud)) return result;
        if (aud.ValueKind == JsonValueKind.String) { var s = aud.GetString(); if (s is not null) result.Add(s); }
        else if (aud.ValueKind == JsonValueKind.Array)
            foreach (var e in aud.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s) result.Add(s);
        return result;
    }

    private static List<string> GetScopes(JsonElement payload)
    {
        var result = new List<string>();
        if (payload.ValueKind != JsonValueKind.Object) return result;
        // OAuth2 `scope` = space-delimited string; OIDC `scp` = string or array.
        if (payload.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String)
            result.AddRange((scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (payload.TryGetProperty("scp", out var scp))
        {
            if (scp.ValueKind == JsonValueKind.String)
                result.AddRange((scp.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            else if (scp.ValueKind == JsonValueKind.Array)
                foreach (var e in scp.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s) result.Add(s);
        }
        return result;
    }
}
