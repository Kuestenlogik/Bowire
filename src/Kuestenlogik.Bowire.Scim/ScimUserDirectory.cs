// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Scim;

/// <summary>
/// Answers "who is this, and do they administer the install" from the
/// provisioned directory (#98, on top of #96).
/// </summary>
/// <remarks>
/// <para>
/// The token knows what the person authenticated with; the directory knows
/// what their organisation says about them. This takes the token first for
/// anything the person would recognise as their own — a name is fresher in a
/// token than in a record synced overnight — and the directory for the one
/// thing a token cannot be trusted to carry here: whether they are an
/// administrator.
/// </para>
/// <para>
/// That distinction is the point. A role claim in a token is only as good as
/// the mapping that produced it, and Bowire has no way to check that mapping;
/// group membership in the provisioned directory is something the operator
/// configured deliberately and can audit.
/// </para>
/// </remarks>
public sealed class ScimUserDirectory(BowireScimStore store, BowireScimOptions options)
    : IBowireUserDirectory
{
    private readonly ClaimsUserDirectory _fromToken = new();

    /// <inheritdoc />
    public BowireUserProfile Describe(ClaimsPrincipal? principal, string subject)
    {
        var token = _fromToken.Describe(principal, subject);
        var record = store.FindBySubject(subject);

        // Unprovisioned but signed in — legitimate while a first sync is
        // running, and the honest answer is the token's, with no role.
        if (record is null) return token;

        var resource = record.Resource;
        return new BowireUserProfile
        {
            Subject = subject,
            DisplayName = token.DisplayName ?? resource.DisplayName ?? Formatted(resource) ?? resource.UserName,
            Email = token.Email ?? Primary(resource) ?? resource.UserName,
            Picture = token.Picture,
            IsAdmin = store.IsMemberOf(resource.Id, options.AdminGroup),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<BowireUserProfile> Search(string? term, int limit)
    {
        if (limit <= 0) return [];

        var needle = term?.Trim();
        return store.Users()
            // A deactivated identity is not somebody to pick: their slot is
            // archived, so acting as them would show an empty workbench and
            // look like data loss.
            .Where(r => r.Resource.Active)
            .Where(r => string.IsNullOrEmpty(needle) || Matches(r.Resource, needle))
            .OrderBy(r => r.Resource.UserName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(r => new BowireUserProfile
            {
                // The subject if one has been bound, otherwise the best
                // identifier the record has — which is what a sign-in would
                // match against anyway.
                Subject = r.Subject ?? r.Resource.ExternalId ?? r.Resource.UserName,
                DisplayName = r.Resource.DisplayName ?? Formatted(r.Resource) ?? r.Resource.UserName,
                Email = Primary(r.Resource) ?? r.Resource.UserName,
                IsAdmin = store.IsMemberOf(r.Resource.Id, options.AdminGroup),
            })
            .ToList();
    }

    private static bool Matches(ScimUser user, string needle)
        => Has(user.UserName, needle)
            || Has(user.DisplayName, needle)
            || Has(user.ExternalId, needle)
            || Has(Formatted(user), needle);

    private static bool Has(string? value, string needle)
        => value is not null && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string? Formatted(ScimUser user)
    {
        if (user.Name is not { } name) return null;
        if (!string.IsNullOrWhiteSpace(name.Formatted)) return name.Formatted;

        var parts = new[] { name.GivenName, name.FamilyName }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    private static string? Primary(ScimUser user)
        => user.Emails.FirstOrDefault(e => e.Primary == true)?.Value
            ?? user.Emails.FirstOrDefault()?.Value;
}
