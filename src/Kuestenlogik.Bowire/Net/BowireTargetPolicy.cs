// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Net;

/// <summary>
/// Which targets the server-side endpoints may dial on behalf of a caller.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint that opens a connection to a caller-named URL —
/// <c>/api/invoke</c>, <c>/api/invoke/stream</c>, <c>/api/channel/open</c>,
/// <c>/api/services</c>, <c>/api/security/fuzz</c> and the
/// <c>/api/parallel/*</c> runs — asks this policy before it dials. Without
/// it <see cref="BowireOptions.LockServerUrl"/> only made the URL input
/// read-only in the browser, while <c>?serverUrl=http://169.254.169.254/</c>
/// still went out from the server: an open relay into whatever network the
/// Bowire host sits in.
/// </para>
/// <para>
/// The policy is enforced when <see cref="BowireOptions.LockServerUrl"/> is
/// set or <see cref="BowireOptions.AllowedServerUrls"/> is non-empty.
/// Unenforced (the default), every target is allowed, exactly as before.
/// Enforced, a target is allowed when it matches one of:
/// </para>
/// <list type="bullet">
///   <item><see cref="BowireOptions.ServerUrl"/> and every entry of
///   <see cref="BowireOptions.ServerUrls"/> — what the UI is locked to;</item>
///   <item>every entry of <see cref="BowireOptions.AllowedServerUrls"/>;</item>
///   <item>in <see cref="BowireMode.Embedded"/> mode, the host's own origin
///   (the target the endpoints fall back to when no <c>serverUrl</c> is
///   given). It comes from the request's <c>Host</c> header, so a host that
///   is reachable under foreign names should restrict <c>AllowedHosts</c>
///   or set <see cref="BowireOptions.ServerUrl"/> explicitly.</item>
/// </list>
/// <para>
/// Matching compares normalised URLs, never raw strings: the
/// <c>hint@</c> prefix is stripped (<see cref="BowireServerUrl.Parse"/>),
/// scheme and host are compared case-insensitively, a default port equals
/// its explicit form (<c>http://h</c> = <c>http://h:80</c>), a trailing
/// slash is ignored, and dot segments are resolved. The path of an allowed
/// entry is a base path: the target's path must equal it or continue it
/// after a <c>/</c>, so <c>https://api/v1</c> allows <c>https://api/v1/users</c>
/// but not <c>https://api/v10</c>. Query and fragment are ignored. A value
/// that is not an absolute <c>scheme://</c> URL only matches an entry that
/// is the same string (case-insensitive, trailing slash ignored).
/// </para>
/// </remarks>
public sealed class BowireTargetPolicy
{
    /// <summary>Problem-details type of the 403 a refused target gets.</summary>
    public const string RefusedProblemType = "urn:bowire:target-not-allowed";

    private readonly Target[] _allowed;

    private BowireTargetPolicy(bool isEnforced, Target[] allowed)
    {
        IsEnforced = isEnforced;
        _allowed = allowed;
    }

    /// <summary>A policy that allows every target — what an unlocked host runs with.</summary>
    public static BowireTargetPolicy Unrestricted { get; } = new(false, []);

    /// <summary>Whether targets are checked at all.</summary>
    public bool IsEnforced { get; }

    /// <summary>
    /// The policy for <paramref name="options"/>. <paramref name="request"/>
    /// supplies the embedded host's own origin; it may be null outside a
    /// request, in which case that origin is not allowed implicitly.
    /// </summary>
    public static BowireTargetPolicy For(BowireOptions? options, HttpRequest? request = null)
    {
        if (options is null || (!options.LockServerUrl && options.AllowedServerUrls.Count == 0))
            return Unrestricted;

        var entries = new List<string?>(options.ServerUrls.Count + options.AllowedServerUrls.Count + 2)
        {
            options.ServerUrl,
        };
        entries.AddRange(options.ServerUrls);
        entries.AddRange(options.AllowedServerUrls);
        if (options.Mode == BowireMode.Embedded && request is not null)
            entries.Add(BowireEndpointHelpers.ResolveServerUrl(options, request));

        var allowed = entries
            .Select(e => TryNormalize(e, out var t) ? t : null)
            .OfType<Target>()
            .Distinct()
            .ToArray();
        return new BowireTargetPolicy(true, allowed);
    }

    /// <summary>
    /// Whether the endpoints may dial <paramref name="rawTarget"/>. An empty
    /// value names no target (nothing is dialed for it) and is always allowed.
    /// </summary>
    public bool Allows(string? rawTarget)
    {
        if (!IsEnforced || string.IsNullOrWhiteSpace(rawTarget)) return true;
        if (!TryNormalize(rawTarget, out var target)) return false;
        return _allowed.Any(a => a.Covers(target));
    }

    /// <summary>
    /// A 403 problem-details result when any of <paramref name="rawTargets"/>
    /// is refused, or null when all are allowed. Each refusal is logged.
    /// </summary>
    internal IResult? Refuse(HttpContext ctx, params ReadOnlySpan<string?> rawTargets)
    {
        if (!IsEnforced) return null;
        foreach (var raw in rawTargets)
        {
            if (Allows(raw)) continue;
            BowireEndpointHelpers.GetLogger(ctx).LogWarning(
                "Refused target {Target} on {Path}: not in the server's allowed targets",
                BowireEndpointHelpers.SafeLog(raw), BowireEndpointHelpers.SafeLog(ctx.Request.Path));
            return BowireEndpointHelpers.Problem(
                type: RefusedProblemType,
                title: "Target not allowed on this server",
                status: StatusCodes.Status403Forbidden,
                detail: "This Bowire host only dials the server URLs it was configured with "
                    + "(LockServerUrl / AllowedServerUrls). Refused: " + BowireEndpointHelpers.SafeLog(raw),
                instance: ctx.Request.Path,
                extensions: new Dictionary<string, object?> { ["target"] = raw });
        }
        return null;
    }

    /// <summary>
    /// Normalise <paramref name="raw"/> for comparison — hint stripped, then
    /// scheme/host/port/base path for an absolute URL, or the trimmed string
    /// for anything else. False for an empty value.
    /// </summary>
    internal static bool TryNormalize(string? raw, out Target target)
    {
        target = null!;
        var url = BowireServerUrl.StripHint(raw?.Trim()).Trim();
        if (url.Length == 0) return false;

        if (url.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            target = new Target(
                uri.Scheme.ToUpperInvariant(),
                uri.IdnHost.ToUpperInvariant(),
                uri.Port,
                uri.AbsolutePath.TrimEnd('/'),
                Opaque: null);
            return true;
        }

        target = new Target(null, null, 0, null, url.TrimEnd('/').ToUpperInvariant());
        return true;
    }

    /// <summary>
    /// A normalised target. Either the URL parts or <see cref="Opaque"/> are
    /// set; scheme, host and the opaque form are upper-cased so equality is
    /// case-insensitive, the path keeps its case.
    /// </summary>
    internal sealed record Target(string? Scheme, string? Host, int Port, string? Path, string? Opaque)
    {
        /// <summary>Whether <paramref name="candidate"/> is this entry or lies under its base path.</summary>
        public bool Covers(Target candidate)
        {
            if (Opaque is not null || candidate.Opaque is not null)
                return Opaque is not null && string.Equals(Opaque, candidate.Opaque, StringComparison.Ordinal);

            if (!string.Equals(Scheme, candidate.Scheme, StringComparison.Ordinal)
                || !string.Equals(Host, candidate.Host, StringComparison.Ordinal)
                || Port != candidate.Port)
                return false;

            var basePath = Path ?? string.Empty;
            var path = candidate.Path ?? string.Empty;
            return basePath.Length == 0
                || string.Equals(path, basePath, StringComparison.Ordinal)
                || path.StartsWith(basePath + "/", StringComparison.Ordinal);
        }
    }
}
