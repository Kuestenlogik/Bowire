// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Auth;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Parallel;

/// <summary>
/// What a Bowire host lets distributed parallel runs do (#313).
/// </summary>
/// <remarks>
/// <para>
/// An executor has to be reachable by its coordinator, so unlike the rest
/// of a laptop-default Bowire it listens beyond loopback — and then its
/// <c>/api/parallel/start-local</c> will load-test whatever a caller names.
/// Three settings close that:
/// </para>
/// <list type="bullet">
///   <item><b>Target allowlist</b> — <c>Bowire:Parallel:TargetAllowlist</c>
///   (a list, or one string separated by <c>,</c> / <c>;</c>) or the
///   <c>BOWIRE_PARALLEL_ALLOWLIST</c> environment variable. URL patterns
///   with <c>*</c> for any run of characters, matched against the whole
///   target URL, case-insensitively. A job naming a target none of them
///   covers is refused with 403 before a single request goes out. Unset,
///   every target is accepted, as before.</item>
///   <item><b>Token</b> — <c>Bowire:Parallel:Token</c> or
///   <c>BOWIRE_PARALLEL_TOKEN</c>. On an executor it is required as
///   <c>Authorization: Bearer …</c>; on a coordinator it is what is sent.
///   Phase 2 only sent it — no executor checked it.</item>
///   <item><b>Require signed executor</b> —
///   <c>Bowire:Parallel:RequireSignedExecutor</c> or
///   <c>BOWIRE_PARALLEL_REQUIRE_SIGNED_EXECUTOR</c>. The coordinator then
///   refuses any executor that is not loopback and not <c>https</c>, before
///   it sends anything — the token and the results never cross a network
///   in clear. (Relaxed certificate validation never applied beyond
///   loopback; see <see cref="LocalhostCertTrust"/>.)</item>
/// </list>
/// </remarks>
internal sealed class BowireParallelPolicy
{
    private readonly Regex[] _allow;

    private BowireParallelPolicy(IReadOnlyList<string> allowlist, string? token, bool requireSignedExecutor)
    {
        TargetAllowlist = allowlist;
        Token = string.IsNullOrWhiteSpace(token) ? null : token;
        RequireSignedExecutor = requireSignedExecutor;
        _allow = [.. allowlist.Select(p => new Regex(
            "^" + Regex.Escape(p).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)))];
    }

    /// <summary>The target URL patterns an executor accepts; empty means any.</summary>
    public IReadOnlyList<string> TargetAllowlist { get; }

    /// <summary>The shared token, or null when none is configured.</summary>
    public string? Token { get; }

    /// <summary>Whether a coordinator refuses non-loopback executors over plain HTTP.</summary>
    public bool RequireSignedExecutor { get; }

    public static BowireParallelPolicy From(IConfiguration? config)
    {
        var patterns = new List<string>();
        var section = config?.GetSection("Bowire:Parallel:TargetAllowlist");
        if (section is not null)
        {
            if (!string.IsNullOrWhiteSpace(section.Value)) patterns.AddRange(Split(section.Value));
            patterns.AddRange(section.GetChildren().Select(c => c.Value).OfType<string>().SelectMany(Split));
        }
        patterns.AddRange(Split(Environment.GetEnvironmentVariable("BOWIRE_PARALLEL_ALLOWLIST")));

        var token = config?["Bowire:Parallel:Token"];
        if (string.IsNullOrWhiteSpace(token)) token = Environment.GetEnvironmentVariable("BOWIRE_PARALLEL_TOKEN");

        var require = config?.GetValue<bool?>("Bowire:Parallel:RequireSignedExecutor")
            ?? (bool.TryParse(Environment.GetEnvironmentVariable("BOWIRE_PARALLEL_REQUIRE_SIGNED_EXECUTOR"), out var env) && env);

        return new BowireParallelPolicy([.. patterns.Distinct(StringComparer.OrdinalIgnoreCase)], token, require);
    }

    private static IEnumerable<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The target URLs the allowlist does not cover; none when there is no allowlist.</summary>
    public IReadOnlyList<string> RefusedTargets(IEnumerable<string> targetUrls)
    {
        if (_allow.Length == 0) return [];
        return [.. targetUrls.Where(url => !_allow.Any(p => Matches(p, url))).Distinct(StringComparer.Ordinal)];
    }

    private static bool Matches(Regex pattern, string url)
    {
        // A pattern that times out on a pathological URL does not allow it.
        try { return pattern.IsMatch(url ?? string.Empty); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>
    /// Whether an <c>Authorization</c> header carries the configured token;
    /// always true when no token is configured. Compared in constant time.
    /// </summary>
    public bool Authorizes(string? authorizationHeader)
    {
        if (Token is null) return true;
        const string scheme = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        var presented = Encoding.UTF8.GetBytes(authorizationHeader[scheme.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(Token);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    /// <summary>
    /// Why the coordinator may not send to <paramref name="executor"/>, or
    /// null when it may.
    /// </summary>
    public string? ExecutorRefusal(string executor)
    {
        if (!RequireSignedExecutor) return null;
        if (!Uri.TryCreate(executor, UriKind.Absolute, out var uri))
            return "not an absolute URL";
        if (LocalhostCertTrust.IsLocalhostUrl(executor)) return null;
        return uri.Scheme == Uri.UriSchemeHttps
            ? null
            : "requireSignedExecutor: a non-loopback executor must be https";
    }
}
