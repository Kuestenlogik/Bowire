// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// What the workbench can say about the person it is serving (#98).
/// </summary>
public sealed class BowireUserProfile
{
    /// <summary>The subject their storage slot is keyed on.</summary>
    public required string Subject { get; init; }

    /// <summary>A name to show. Falls back through the claims that carry one.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Their e-mail address, when the token carries one.</summary>
    public string? Email { get; init; }

    /// <summary>An avatar URL, when the identity provider issues one.</summary>
    public string? Picture { get; init; }

    /// <summary>
    /// Whether this identity administers the install.
    /// </summary>
    /// <remarks>
    /// Always <c>false</c> without a directory that knows about roles. That is
    /// the safe direction: an install with no source of truth for who is an
    /// administrator has no administrators, rather than everybody.
    /// </remarks>
    public bool IsAdmin { get; init; }
}

/// <summary>
/// Resolves who an authenticated caller is, beyond the subject their storage
/// is keyed on (#98).
/// </summary>
/// <remarks>
/// <para>
/// A seam because the answer comes from different places. A token carries a
/// name and an e-mail address; whether somebody is an administrator comes from
/// the directory that provisions them, which lives in an optional package
/// Core does not reference. Without one, <see cref="ClaimsUserDirectory"/>
/// answers from the token alone.
/// </para>
/// <para>
/// <see cref="Search"/> is here rather than on a second interface because the
/// two questions have one answer-source: whatever knows that somebody is an
/// administrator is the same thing that can list who else exists.
/// </para>
/// </remarks>
public interface IBowireUserDirectory
{
    /// <summary>
    /// Describe the caller. <paramref name="principal"/> is the token,
    /// <paramref name="subject"/> the value their storage is keyed on.
    /// </summary>
    BowireUserProfile Describe(ClaimsPrincipal? principal, string subject);

    /// <summary>
    /// Identities matching <paramref name="term"/>, for a picker.
    /// </summary>
    /// <remarks>
    /// Empty when nothing in this install knows who else exists — which is the
    /// honest answer, and the one that keeps a picker from offering a list it
    /// made up.
    /// </remarks>
    IReadOnlyList<BowireUserProfile> Search(string? term, int limit);
}

/// <summary>
/// The default directory: everything comes from the token (#98).
/// </summary>
public sealed class ClaimsUserDirectory : IBowireUserDirectory
{
    /// <inheritdoc />
    public BowireUserProfile Describe(ClaimsPrincipal? principal, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        return new BowireUserProfile
        {
            Subject = subject,
            DisplayName = First(principal, "name", ClaimTypes.Name, "preferred_username", "given_name"),
            Email = First(principal, "email", ClaimTypes.Email, "upn"),
            Picture = First(principal, "picture"),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<BowireUserProfile> Search(string? term, int limit) => [];

    /// <summary>
    /// The first of these claims that carries something.
    /// </summary>
    /// <remarks>
    /// Several rather than one, because providers disagree about which of them
    /// holds a person's name — and a chip showing a raw subject where a name
    /// was expected reads as a bug rather than as a missing claim.
    /// </remarks>
    private static string? First(ClaimsPrincipal? principal, params string[] types)
    {
        if (principal is null) return null;

        foreach (var type in types)
        {
            var value = principal.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }
}
