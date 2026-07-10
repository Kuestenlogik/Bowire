// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Implementation of <c>bowire jwt</c> — the Tier-2 JWT toolkit from
/// the cyber-security ADR. Two sub-actions:
/// <list type="bullet">
///   <item><c>decode</c> — split a JWT into header / payload / signature,
///   pretty-print the JSON portions and the signature metadata.</item>
///   <item><c>tamper</c> — produce a modified token. Common flavours:
///   <c>--alg=none</c> (strip the signature, set header alg to
///   <c>none</c>); <c>--set claim=value</c> to mutate one or more
///   payload claims; <c>--secret &lt;key&gt;</c> to re-sign with HMAC-SHA256
///   instead of dropping the signature.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// JWTs are three URL-safe-Base64 segments joined by dots: header,
/// payload, signature. The signature segment is empty for <c>alg:none</c>
/// and a Base64URL-encoded HMAC / RSA / ECDSA blob otherwise. The
/// toolkit only writes HMAC-SHA256 signatures — RSA / ECDSA tampering
/// requires the private key and isn't typically what a tester reaches
/// for; the <c>--alg=none</c> path covers the classic JWT downgrade
/// attack without it.
/// </para>
/// </remarks>
internal static class JwtCommand
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Decode the token and print the header + payload as
    /// pretty-printed JSON. Returns exit code 0 on success, 2 on
    /// argument error, 1 on parse failure.
    /// </summary>
    public static async Task<int> RunDecodeAsync(string token, TextWriter? stdout = null, TextWriter? stderr = null, CancellationToken ct = default)
    {
        var io = CommandIo.Resolve(stdout, stderr);
        if (string.IsNullOrWhiteSpace(token))
        {
            await io.Err.WriteLineAsync("  Usage: bowire jwt decode <token>").ConfigureAwait(false);
            return 2;
        }

        if (!TrySplitJwt(token, out var headerSeg, out var payloadSeg, out var signatureSeg, out var err))
        {
            await io.Err.WriteLineAsync($"  Could not parse token: {err}").ConfigureAwait(false);
            return 1;
        }

        io.OutLine();
        io.OutLine("  Header:");
        io.OutLine(IndentJson(DecodeJsonSegment(headerSeg)));
        io.OutLine();
        io.OutLine("  Payload:");
        io.OutLine(IndentJson(DecodeJsonSegment(payloadSeg)));
        io.OutLine();
        io.OutLine($"  Signature: {(string.IsNullOrEmpty(signatureSeg) ? "(empty — alg:none)" : signatureSeg.Length + " base64url chars")}");
        io.OutLine();

        if (TryReadAlg(headerSeg, out var alg))
        {
            io.OutLine($"  Declared alg: {alg}");
            if (string.Equals(alg, "none", StringComparison.OrdinalIgnoreCase))
            {
                io.OutLine("  Warning: alg:none — token claims to be unsigned. If the server accepts this without rejecting, the JWT layer is bypassable (CVE-2015-9235 class).");
            }
        }

        return 0;
    }

    /// <summary>
    /// Run the deterministic JWT security analysis (#105) and print the header +
    /// payload plus the rule-based findings (alg=none, symmetric HMAC, missing /
    /// expired / long-lived exp, missing nbf, scope creep, audience binding, …).
    /// Findings are output, not a failure, so a clean run still returns 0.
    /// </summary>
    public static async Task<int> RunAnalyzeAsync(string token, string? audience,
        TextWriter? stdout = null, TextWriter? stderr = null, CancellationToken ct = default)
    {
        var io = CommandIo.Resolve(stdout, stderr);
        if (string.IsNullOrWhiteSpace(token))
        {
            await io.Err.WriteLineAsync("  Usage: bowire jwt analyze <token> [--audience <aud>]").ConfigureAwait(false);
            return 2;
        }

        var analysis = JwtSecurityAnalyzer.Analyze(token, string.IsNullOrWhiteSpace(audience) ? null : audience);
        if (!analysis.Parsed)
        {
            await io.Err.WriteLineAsync($"  Could not analyze token: {analysis.ParseError}").ConfigureAwait(false);
            return 1;
        }

        io.OutLine();
        io.OutLine("  Header:");
        io.OutLine(IndentJson(analysis.HeaderJson));
        io.OutLine();
        io.OutLine("  Payload:");
        io.OutLine(IndentJson(analysis.PayloadJson));
        io.OutLine();
        io.OutLine("  Security analysis:");
        foreach (var f in analysis.Flags)
            io.OutLine($"    {Marker(f.Level)} [{f.Claim}] {f.Message}");
        io.OutLine();
        return 0;
    }

    private static string Marker(JwtFlagLevel level) => level switch
    {
        JwtFlagLevel.High => "[HIGH]",
        JwtFlagLevel.Medium => "[MED] ",
        JwtFlagLevel.Low => "[low] ",
        _ => "[info]",
    };

    /// <summary>
    /// Produce a tampered token. <paramref name="algNone"/> downgrades
    /// to the <c>alg:none</c> attack; <paramref name="setClaims"/>
    /// replace or add payload claims; <paramref name="hmacSecret"/>
    /// re-signs the result with HMAC-SHA256 (overrides <paramref name="algNone"/>
    /// if both are set).
    /// </summary>
    public static async Task<int> RunTamperAsync(string token, bool algNone, IReadOnlyList<string> setClaims,
        string? hmacSecret, TextWriter? stdout = null, TextWriter? stderr = null, CancellationToken _ct = default)
    {
        var io = CommandIo.Resolve(stdout, stderr);
        if (string.IsNullOrWhiteSpace(token))
        {
            await io.Err.WriteLineAsync("  Usage: bowire jwt tamper <token> [--alg=none] [--set claim=value] [--secret <hmac>]").ConfigureAwait(false);
            return 2;
        }

        if (!TrySplitJwt(token, out var headerSeg, out var payloadSeg, out _, out var err))
        {
            await io.Err.WriteLineAsync($"  Could not parse token: {err}").ConfigureAwait(false);
            return 1;
        }

        // Header rewrite: only the `alg` field; everything else
        // round-trips. Templates that need a kid / typ tweak can be
        // added to the toolkit later; alg-tampering covers the
        // overwhelming majority of JWT attacks worth shipping in v1.
        Dictionary<string, JsonElement> headerObj;
        Dictionary<string, JsonElement> payloadObj;
        try
        {
            headerObj = ParseJsonObject(DecodeJsonSegment(headerSeg));
            payloadObj = ParseJsonObject(DecodeJsonSegment(payloadSeg));
        }
        catch (JsonException ex)
        {
            await io.Err.WriteLineAsync($"  Token segments are not valid JSON: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        // Apply --set claim=value pairs. Values are taken as-is for
        // strings; integers + booleans + null are detected when the
        // entire value parses cleanly as JSON. Catches the common
        // tester move of bumping `role` from `user` to `admin` or
        // setting `exp` to a far-future int.
        foreach (var pair in setClaims)
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                await io.Err.WriteLineAsync($"  --set must be in `claim=value` form (got '{pair}').").ConfigureAwait(false);
                return 2;
            }
            var claim = pair[..eq];
            var rawValue = pair[(eq + 1)..];
            payloadObj[claim] = ParseClaimValue(rawValue);
        }

        // Re-sign or strip signature.
        if (!string.IsNullOrEmpty(hmacSecret))
        {
            headerObj["alg"] = JsonElement("HS256");
            headerObj["typ"] = JsonElement("JWT");
            var newHeaderSeg = Base64Url(SerializeObject(headerObj));
            var newPayloadSeg = Base64Url(SerializeObject(payloadObj));
            var signatureSegOut = HmacSign(newHeaderSeg + "." + newPayloadSeg, hmacSecret);
            io.OutLine();
            io.OutLine(newHeaderSeg + "." + newPayloadSeg + "." + signatureSegOut);
            io.OutLine();
            io.OutLine($"  Tampered with HS256 (key: {hmacSecret.Length} chars). Claims: {setClaims.Count} mutation(s).");
        }
        else if (algNone)
        {
            headerObj["alg"] = JsonElement("none");
            headerObj["typ"] = JsonElement("JWT");
            var newHeaderSeg = Base64Url(SerializeObject(headerObj));
            var newPayloadSeg = Base64Url(SerializeObject(payloadObj));
            // alg:none ⇒ empty signature (no signatureSegOut needed).
            io.OutLine();
            io.OutLine(newHeaderSeg + "." + newPayloadSeg + ".");
            io.OutLine();
            io.OutLine($"  Tampered with alg:none. Claims: {setClaims.Count} mutation(s).");
            io.OutLine("  Send this token instead of the original. If the server still validates the response, the JWT layer is bypassable.");
        }
        else
        {
            await io.Err.WriteLineAsync("  No tamper mode selected. Use --alg=none OR --secret <key>.").ConfigureAwait(false);
            return 2;
        }
        return 0;
    }

    // ----- helpers -----

    private static bool TrySplitJwt(string token, out string h, out string p, out string s, out string err)
    {
        h = p = s = "";
        err = "";
        var parts = token.Split('.');
        if (parts.Length < 2 || parts.Length > 3)
        {
            err = $"expected 2-3 dot-separated segments, got {parts.Length}";
            return false;
        }
        h = parts[0];
        p = parts[1];
        s = parts.Length == 3 ? parts[2] : "";
        return true;
    }

    private static string DecodeJsonSegment(string segment)
    {
        var bytes = Base64UrlDecode(segment);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var pad = (4 - (segment.Length & 3)) & 3;
        var b64 = segment.Replace('-', '+').Replace('_', '/') + new string('=', pad);
        return Convert.FromBase64String(b64);
    }

    private static string Base64Url(string utf8)
    {
        var b = Convert.ToBase64String(Encoding.UTF8.GetBytes(utf8));
        return b.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string IndentJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return "  " + JsonSerializer.Serialize(doc.RootElement, s_jsonOpts).Replace("\n", "\n  ");
        }
        catch (JsonException)
        {
            return "  " + raw;
        }
    }

    private static bool TryReadAlg(string headerSeg, out string alg)
    {
        alg = "";
        try
        {
            using var doc = JsonDocument.Parse(DecodeJsonSegment(headerSeg));
            if (doc.RootElement.TryGetProperty("alg", out var v) && v.ValueKind == JsonValueKind.String)
            {
                alg = v.GetString() ?? "";
                return alg.Length > 0;
            }
        }
        catch (JsonException) { /* fall through */ }
        catch (FormatException) { /* fall through */ }
        return false;
    }

    private static Dictionary<string, JsonElement> ParseJsonObject(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }
        return dict;
    }

    private static string SerializeObject(IDictionary<string, JsonElement> obj)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in obj)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static JsonElement ParseClaimValue(string raw)
    {
        // Try as JSON literal first (so `--set exp=9999999999` lands as
        // a number, `--set isAdmin=true` as bool, etc.); fall back to
        // a plain string. This keeps the CLI ergonomic — operators
        // don't have to remember to quote-escape every primitive.
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { /* fall through */ }

        // Plain string — serialize through JsonDocument so escaping is
        // handled correctly.
        var quoted = JsonSerializer.Serialize(raw);
        using var docStr = JsonDocument.Parse(quoted);
        return docStr.RootElement.Clone();
    }

    private static JsonElement JsonElement(string value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }

    private static string HmacSign(string signingInput, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return Convert.ToBase64String(sig).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
