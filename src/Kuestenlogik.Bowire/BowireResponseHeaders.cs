// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire;

/// <summary>
/// The baseline headers Bowire puts on everything it serves (#625).
/// </summary>
/// <remarks>
/// <para>
/// Set on Bowire's own responses rather than through host middleware. An
/// embedded host owns its pipeline, and a package that quietly adds headers
/// to every response the host sends has reached outside its mount.
/// </para>
/// </remarks>
public static class BowireResponseHeaders
{
    /// <summary>
    /// The default Content-Security-Policy, with <c>{0}</c> for the nonce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a nonce and not <c>'self'</c>.</b> The workbench ships its whole
    /// JavaScript bundle inline, plus a second inline block carrying the
    /// per-request configuration. <c>script-src 'self'</c> would switch the
    /// product off. <c>'unsafe-inline'</c> would silence a scanner and protect
    /// nothing — worse than no header, because it looks solved. A nonce is the
    /// only form that is both true and useful.
    /// </para>
    /// <para>
    /// <b>Why <c>style-src</c> carries <c>'unsafe-inline'</c> and no nonce.</b>
    /// The DOM-building code sets inline <c>style</c> attributes throughout,
    /// and a nonce does not cover style attributes. Worse, adding one would
    /// make CSP Level 3 browsers <em>ignore</em> <c>'unsafe-inline'</c>
    /// entirely, so the nonce would break the layout it was meant to secure.
    /// </para>
    /// <para>
    /// <b>Why <c>connect-src</c> and <c>img-src</c> are open.</b> The map
    /// widget fetches basemap tiles straight from whatever tile server the
    /// operator configured, and a tool for talking to arbitrary services has
    /// no fixed egress list to write down. This policy buys protection
    /// against script injection, not against egress, and saying so here is
    /// better than a narrow value that breaks a feature on first use.
    /// </para>
    /// </remarks>
    public const string DefaultContentSecurityPolicyFormat =
        "default-src 'self'; "
        + "script-src 'nonce-{0}'; "
        + "style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: blob: https: http:; "
        + "font-src 'self' data:; "
        + "connect-src *; "
        + "object-src 'none'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'self'";

    /// <summary>
    /// A fresh nonce for one response.
    /// </summary>
    /// <remarks>
    /// Base64 of 16 random bytes from the OS generator. Per response, because
    /// a nonce reused across responses is a nonce an injected script can read
    /// from a cached page and replay.
    /// </remarks>
    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Apply the headers that apply to everything — HTML and JSON alike.
    /// </summary>
    /// <summary>
    /// The policy for a response that is not a document: nothing may load,
    /// and nothing may frame it.
    /// </summary>
    /// <remarks>
    /// A JSON body executes nothing, so this is not about the response's own
    /// content — it is about what happens when a browser is talked into
    /// rendering it as a document anyway. Costs nothing and closes that door.
    /// </remarks>
    public const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    public static void ApplyBaseline(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        // The one header that matters for a JSON API: without it a browser is
        // free to sniff a response body and treat it as script.
        response.Headers["X-Content-Type-Options"] = "nosniff";

        // SAMEORIGIN rather than DENY: embedded mode mounts Bowire inside the
        // host's own application, so same-origin framing is a supported shape
        // and DENY would break it.
        response.Headers["X-Frame-Options"] = "SAMEORIGIN";

        // Referrer leakage matters here because a Bowire URL can carry a
        // workspace or service name that names an internal system.
        response.Headers["Referrer-Policy"] = "same-origin";

        // Only over TLS. RFC 6797 §8.1 requires a user agent to ignore the
        // header when it arrives over plaintext, so sending it there would be
        // a header that does nothing, present only to quiet a scanner. A
        // deployment behind TLS gets it; a CI runner without a certificate
        // correctly does not, and that difference is worth keeping visible.
        if (response.HttpContext.Request.IsHttps)
        {
            response.Headers["Strict-Transport-Security"] = "max-age=31536000";
        }

        // Documents overwrite this with the nonce policy a moment later; for
        // everything else — JSON, 404s, the 405 an MCP mount answers a plain
        // GET with — this is the policy that applies.
        if (!response.Headers.ContainsKey("Content-Security-Policy"))
        {
            response.Headers["Content-Security-Policy"] = ApiContentSecurityPolicy;
        }
    }

    /// <summary>
    /// Apply the baseline plus the policy for a page that carries script.
    /// </summary>
    /// <param name="response">The response being written.</param>
    /// <param name="nonce">The nonce placed on this page's script tags.</param>
    /// <param name="policyFormat">
    /// An override for the policy, with <c>{0}</c> for the nonce. An empty
    /// value sends no policy at all — the escape hatch for a host whose own
    /// page composes Bowire with something this default forbids. Null uses
    /// <see cref="DefaultContentSecurityPolicyFormat"/>.
    /// </param>
    public static void ApplyForDocument(HttpResponse response, string nonce, string? policyFormat = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        ApplyBaseline(response);

        var format = policyFormat ?? DefaultContentSecurityPolicyFormat;
        if (format.Length == 0) return;

        response.Headers["Content-Security-Policy"] =
            string.Format(System.Globalization.CultureInfo.InvariantCulture, format, nonce);
    }
}
