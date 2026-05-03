// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Mock.Replay;

/// <summary>
/// Substitutes dynamic placeholders inside a recorded response body at
/// replay time. Matches the Bowire UI's auth-helper variable syntax so
/// recording authors don't have to learn a new dialect per layer.
/// </summary>
/// <remarks>
/// <para>Supported placeholders:</para>
/// <list type="bullet">
/// <item><c>${uuid}</c> — fresh UUID v4 per substitution.</item>
/// <item><c>${now}</c> — current Unix timestamp in seconds.</item>
/// <item><c>${nowMs}</c> — current Unix timestamp in milliseconds.</item>
/// <item><c>${now+N}</c> / <c>${now-N}</c> — <c>${now}</c> shifted by <c>N</c> seconds.</item>
/// <item><c>${timestamp}</c> — current UTC time as ISO 8601 with millisecond precision.</item>
/// <item><c>${random}</c> — random <c>uint32</c>, rendered in decimal.</item>
/// </list>
/// <para>
/// Unknown tokens are left verbatim (including the braces) so a literal
/// <c>${foo}</c> in a recorded body survives as-is — useful when the
/// recorded API happens to return template-shaped strings.
/// </para>
/// </remarks>
public static class ResponseBodySubstitutor
{
    // Permissive capture: anything between ${ and } that doesn't contain }.
    // Lets us keep the lookup logic in one place.
    private static readonly Regex PlaceholderPattern = new(
        @"\$\{([^}]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Apply placeholder substitution to <paramref name="body"/>. Returns the
    /// input unchanged when no placeholders are present.
    /// </summary>
    public static string Substitute(string body) => Substitute(body, request: null, extraBindings: null);

    /// <summary>
    /// Like <see cref="Substitute(string)"/> but resolves
    /// <paramref name="extraBindings"/> first — used by the MQTT
    /// reactive matcher to expose topic-wildcard captures as
    /// <c>${topic.0}</c>/<c>${topic.rest}</c> inside recorded response
    /// bodies and topics. The built-in placeholders (<c>${uuid}</c>,
    /// <c>${now}</c>, …) still win over any extra binding that
    /// accidentally shadows them — prevents a hostile recording from
    /// overriding the generator semantics via a spoofed binding.
    /// </summary>
    public static string Substitute(string body, IReadOnlyDictionary<string, string>? extraBindings) =>
        Substitute(body, request: null, extraBindings);

    /// <summary>
    /// Full-featured overload with access to the inbound request for
    /// <c>${request.*}</c> tokens (see <see cref="RequestTemplate"/>).
    /// Precedence: built-ins (uuid/now/…) → request tokens → extra
    /// bindings → literal fallback. Request tokens come before extra
    /// bindings so a hostile extra-binding can't spoof them, mirroring
    /// the existing built-in-wins policy.
    /// </summary>
    public static string Substitute(
        string body,
        RequestTemplate? request,
        IReadOnlyDictionary<string, string>? extraBindings)
    {
        if (string.IsNullOrEmpty(body)) return body;
        if (body.IndexOf("${", StringComparison.Ordinal) < 0) return body;
        return PlaceholderPattern.Replace(body, match => Resolve(match.Groups[1].Value, request, extraBindings));
    }

    private static string Resolve(
        string token,
        RequestTemplate? request,
        IReadOnlyDictionary<string, string>? extraBindings)
    {
        return token switch
        {
            "uuid" => Guid.NewGuid().ToString(),
            "now" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "nowMs" => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            "timestamp" => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            "random" => RandomUInt32().ToString(CultureInfo.InvariantCulture),
            _ when token.StartsWith("now+", StringComparison.Ordinal) => NowOffset(token[4..], sign: 1),
            _ when token.StartsWith("now-", StringComparison.Ordinal) => NowOffset(token[4..], sign: -1),
            _ when token.StartsWith("request.", StringComparison.Ordinal) && request is not null
                => request.Resolve(token[8..]) ?? "${" + token + "}",
            _ when extraBindings is not null && extraBindings.TryGetValue(token, out var bound) => bound,
            // Unknown token — keep the literal so the substitution is
            // idempotent on content that happens to look like a placeholder.
            _ => "${" + token + "}"
        };
    }

    private static string NowOffset(string numberPart, int sign)
    {
        if (!int.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return "${now" + (sign > 0 ? "+" : "-") + numberPart + "}";
        var shifted = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (sign * (long)seconds);
        return shifted.ToString(CultureInfo.InvariantCulture);
    }

#pragma warning disable CA5394 // Non-crypto Random is fine for a ${random} token
                              // used in mock-response bodies — the token is
                              // documented as "random uint32", not a security
                              // primitive.
    private static uint RandomUInt32()
    {
        // Random.Shared.Next(int.MaxValue) caps at 2^31-1; combine two calls
        // to get a full uint32 range. Not cryptographically random, but
        // matches what users expect from ${random}.
        var hi = (uint)Random.Shared.Next() << 1;
        var lo = (uint)Random.Shared.Next(0, 2);
        return hi | lo;
    }
#pragma warning restore CA5394
}
