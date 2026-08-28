// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// What an install has said about serving several identities from one Bowire
/// (#97). Bound from <c>Bowire:MultiTenant</c>.
/// </summary>
/// <remarks>
/// Off unless the operator turns it on. Turning it on moves where every store
/// reads and writes, so it is not something to infer from an auth provider
/// being present: plenty of single-user installs put a login in front of a
/// workbench that still has exactly one person behind it, and quietly moving
/// their data because they added OIDC would be the opposite of a migration
/// path.
/// </remarks>
public sealed class BowireTenancyOptions
{
    /// <summary>
    /// Whether each authenticated identity gets its own slot. Default
    /// <c>false</c> — the flat single-user layout.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The claim that identifies a person, when the default order does not
    /// suit the provider.
    /// </summary>
    /// <remarks>
    /// Left unset, <see cref="SubjectOf"/> tries <c>sub</c>, then
    /// <see cref="ClaimTypes.NameIdentifier"/> — ASP.NET's JWT handler maps
    /// the first onto the second unless the host disabled inbound claim
    /// mapping, so both have to be tried — then <c>oid</c> for Entra ID, which
    /// is the only one of the three that is stable when someone's e-mail
    /// address changes.
    /// </remarks>
    public string? SubjectClaim { get; set; }

    /// <summary>What to do about the state a single-user install left behind.</summary>
    public BowireUserMigrationMode Migration { get; set; } = BowireUserMigrationMode.Prompt;

    /// <summary>
    /// The subject of <paramref name="user"/>, or <c>null</c> when there is no
    /// authenticated identity to serve.
    /// </summary>
    public string? SubjectOf(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;

        if (!string.IsNullOrWhiteSpace(SubjectClaim))
        {
            // Configured explicitly: no fallback. An operator who named a
            // claim and got somebody's e-mail address instead would be filing
            // two identities into one slot without being told.
            return Trimmed(user.FindFirst(SubjectClaim)?.Value);
        }

        return Trimmed(user.FindFirst("sub")?.Value)
            ?? Trimmed(user.FindFirst(ClaimTypes.NameIdentifier)?.Value)
            ?? Trimmed(user.FindFirst("oid")?.Value);
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
