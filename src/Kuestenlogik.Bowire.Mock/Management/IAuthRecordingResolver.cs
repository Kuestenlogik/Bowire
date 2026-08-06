// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// Resolves a mock auth requirement's <c>authRecordingId</c> (#563) into a
/// concrete credential at config-apply time. The Mock package doesn't know how
/// a host stores auth recordings — each host registers an adapter (the
/// standalone tool's <c>WorkbenchAuthRecordingResolver</c> reads the
/// per-workspace <c>AuthRecordingStore</c>; embedded hosts plug their own).
/// </summary>
public interface IAuthRecordingResolver
{
    /// <summary>
    /// Resolve a captured credential by id, or null when no recording has that
    /// id. <paramref name="workspaceId"/> scopes the lookup to the mock's own
    /// workspace when known — an auth-recording id is an operator-chosen label
    /// (unlike a GUID recording id), so a bare id can collide across
    /// workspaces; pass the workspace to resolve deterministically.
    /// </summary>
    MockAuthResolution? TryResolve(string authRecordingId, string? workspaceId);
}

/// <summary>A resolved credential: the value #562's gate accepts, plus the optional scheme / header it is presented under.</summary>
/// <param name="Credential">The captured credential the gate compares against.</param>
/// <param name="Scheme">Credential scheme override (bearer / apikey / basic), or null to keep the config's.</param>
/// <param name="Header">Header the credential is carried in, or null to keep the config's.</param>
public sealed record MockAuthResolution(string Credential, string? Scheme = null, string? Header = null);

/// <summary>
/// Applies an <see cref="IAuthRecordingResolver"/> to a parsed
/// <see cref="MockConfiguration"/> in place: when its auth block references an
/// <c>authRecordingId</c>, resolve it and populate the direct
/// <see cref="MockAuthRequirement.Credential"/> (+ scheme / header) so #562's
/// gate — which only reads <c>Credential</c> — enforces the captured value. A
/// referenced-but-unresolvable id is surfaced as an <see cref="Outcome"/> the
/// endpoint maps to an error, never a silent fall-through to a weaker
/// (presence-only) gate.
/// </summary>
internal static class MockAuthRecordingResolution
{
    internal enum Outcome
    {
        /// <summary>No <c>authRecordingId</c> on the auth block — nothing to resolve, proceed as-is.</summary>
        NoRecordingRef,

        /// <summary>Resolved and the config's credential was populated.</summary>
        Resolved,

        /// <summary>An id was referenced but no resolver is registered on this host.</summary>
        NoResolver,

        /// <summary>An id was referenced but no recording has it.</summary>
        NotFound,
    }

    internal static Outcome Apply(MockConfiguration config, IAuthRecordingResolver? resolver, string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(config);

        var id = config.Auth?.AuthRecordingId;
        if (string.IsNullOrEmpty(id)) return Outcome.NoRecordingRef;
        if (resolver is null) return Outcome.NoResolver;

        var resolved = resolver.TryResolve(id, workspaceId);
        // Fail closed: a missing recording OR an empty resolved credential must
        // NOT proceed. An empty credential would arm #562's gate in
        // presence-only mode (any token of the scheme passes) — a silent
        // downgrade of a requirement the operator asked to be exact.
        if (resolved is null || string.IsNullOrEmpty(resolved.Credential)) return Outcome.NotFound;

        config.Auth!.Credential = resolved.Credential;
        if (!string.IsNullOrEmpty(resolved.Scheme)) config.Auth.Scheme = resolved.Scheme;
        if (!string.IsNullOrEmpty(resolved.Header)) config.Auth.Header = resolved.Header;
        return Outcome.Resolved;
    }
}
