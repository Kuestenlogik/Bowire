// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.AgentHub;

/// <summary>
/// Who may talk to a Bowire hub (#128).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Token</b> — <c>Bowire:Hub:Token</c> or <c>BOWIRE_HUB_TOKEN</c>.
///   Set, every <c>/hub/*</c> call needs <c>Authorization: Bearer …</c>.
///   Unset, the hub only takes registrations from loopback — anyone else
///   could otherwise put links of their choosing into the fleet list.</item>
///   <item><b>Trusted agent prefixes</b> — <c>Bowire:Hub:TrustedAgentPrefixes</c>
///   (a list, or one string separated by <c>,</c> / <c>;</c>). Set, an
///   agent's callback URL has to start with one of them.</item>
/// </list>
/// </remarks>
internal sealed class BowireHubPolicy
{
    private BowireHubPolicy(string? token, IReadOnlyList<string> trustedPrefixes)
    {
        Token = token;
        TrustedAgentPrefixes = trustedPrefixes;
    }

    public string? Token { get; }

    public IReadOnlyList<string> TrustedAgentPrefixes { get; }

    public static BowireHubPolicy From(IConfiguration? config)
    {
        var token = config?["Bowire:Hub:Token"];
        if (string.IsNullOrWhiteSpace(token)) token = Environment.GetEnvironmentVariable("BOWIRE_HUB_TOKEN");

        var prefixes = new List<string>();
        var section = config?.GetSection("Bowire:Hub:TrustedAgentPrefixes");
        if (section is not null)
        {
            if (!string.IsNullOrWhiteSpace(section.Value)) prefixes.AddRange(Split(section.Value));
            prefixes.AddRange(section.GetChildren().Select(c => c.Value).OfType<string>().SelectMany(Split));
        }
        return new BowireHubPolicy(string.IsNullOrWhiteSpace(token) ? null : token.Trim(), prefixes);
    }

    /// <summary>Whether the caller may read the hub.</summary>
    public bool MayRead(HttpContext ctx) => Token is null || HasToken(ctx);

    /// <summary>Whether the caller may register or deregister an agent.</summary>
    public bool MayWrite(HttpContext ctx) => Token is null ? IsLoopback(ctx) : HasToken(ctx);

    /// <summary>Why a callback URL is not accepted, or null when it is.</summary>
    public string? CallbackRefusal(string? callbackUrl)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "callbackUrl must be an absolute http or https URL";
        }
        if (TrustedAgentPrefixes.Count > 0
            && !TrustedAgentPrefixes.Any(p => callbackUrl!.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return "callbackUrl is outside Bowire:Hub:TrustedAgentPrefixes";
        }
        return null;
    }

    private bool HasToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var presented = Encoding.UTF8.GetBytes(header[scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(Token!));
    }

    private static bool IsLoopback(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress);

    private static IEnumerable<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
