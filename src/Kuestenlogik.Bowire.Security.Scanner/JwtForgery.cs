// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// The JWT handling the authentication probes share: finding the bearer token
/// among the <c>--auth-header</c> values, telling a JWT from an opaque token,
/// and building the two forgeries a server must refuse — the token re-headed to
/// <c>alg:none</c>, and the token with a corrupted signature.
/// </summary>
/// <remarks>
/// <see cref="Api2AuthProbe"/> sends the forgeries over HTTP and
/// <see cref="WebSocketAuthProbe"/> on the WebSocket upgrade. One copy, so both
/// surfaces are tested with the same tokens.
/// </remarks>
internal static class JwtForgery
{
    /// <summary>The <c>Authorization: Bearer …</c> header among <paramref name="authHeaders"/>, if any.</summary>
    public static (string HeaderName, string Token)? FindBearer(IList<string> authHeaders)
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

    /// <summary>Three dot-separated parts with a header and a payload: shaped like a JWT.</summary>
    public static bool LooksLikeJwt(string token)
    {
        var parts = token.Split('.');
        return parts.Length == 3 && parts[0].Length > 0 && parts[1].Length > 0;
    }

    /// <summary>The <c>exp</c> claim of <paramref name="token"/>, when it has a readable one.</summary>
    public static (bool HasExp, long Exp) ReadExp(string token)
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

    /// <summary>The same claims under an <c>alg:none</c> header and with no signature.</summary>
    public static string ForgeNone(string token)
    {
        var parts = token.Split('.');
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        // header . payload . (empty signature)
        return header + "." + parts[1] + ".";
    }

    /// <summary>The same header and claims with a signature in the right charset that cannot verify.</summary>
    public static string TamperSignature(string token)
    {
        var parts = token.Split('.');
        return parts[0] + "." + parts[1] + ".bowireTamperedSignature";
    }

    /// <summary><paramref name="authHeaders"/> with the bearer token in <paramref name="headerName"/> replaced.</summary>
    public static List<string> WithBearer(IList<string> authHeaders, string headerName, string newToken)
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
}
