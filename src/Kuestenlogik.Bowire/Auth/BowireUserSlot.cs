// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// A user store that knows the storage root it lives under (#97).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Projects.BowirePathResolver"/> derives the
/// <see cref="Projects.BowireStorageScope.Data"/> root from the active user
/// store, because the store is what a project opt-in moves. That worked while
/// <see cref="DefaultBowireUserStore"/> was the only implementation and its
/// root <em>was</em> the data root.
/// </para>
/// <para>
/// It stops working the moment a store resolves somewhere <em>under</em> the
/// data root, which is exactly what an identity-scoped store does: taking its
/// root as the data root would resolve the next slot under the last one —
/// <c>users/a/users/a/…</c>, deeper on every swap. This interface is how a
/// store says "here is my slot, and here is the root it sits in", so the
/// resolver can keep answering the second question while the store answers the
/// first.
/// </para>
/// </remarks>
public interface IBowireStorageRootProvider
{
    /// <summary>
    /// The storage root this store resolves under — the directory the
    /// <see cref="Projects.BowireStorageScope.Data"/> scope means, not the
    /// per-identity slot inside it.
    /// </summary>
    string StorageRoot { get; }
}

/// <summary>
/// Turns an authenticated subject into the directory name that holds their
/// state (#97).
/// </summary>
/// <remarks>
/// <para>
/// A <c>sub</c> claim is whatever the identity provider felt like issuing: an
/// e-mail address, a GUID, a URL, <c>auth0|5f3c…</c>. None of those are
/// directory names, so something has to map one to the other, and the mapping
/// has exactly two jobs.
/// </para>
/// <para>
/// <b>It must be readable.</b> An operator looking at the storage root should
/// be able to tell whose slot is whose without a lookup table — a support
/// request that starts "delete my recordings" is answered by finding a
/// directory, and a tree of opaque hashes makes that a database query.
/// </para>
/// <para>
/// <b>It must not collide.</b> This is the one that would be a security bug
/// rather than an annoyance. Sanitising alone maps
/// <c>a.b@example.com</c> and <c>a-b@example.com</c> to the same name, and two
/// identities sharing a slot is cross-tenant disclosure — each would read the
/// other's environments, including the secrets in them. So the readable part
/// is a convenience and the fingerprint appended to it is the identity: it is
/// taken over the untouched subject, so any two distinct subjects land in
/// distinct directories no matter what sanitising did to them.
/// </para>
/// </remarks>
public static class BowireUserSlot
{
    /// <summary>
    /// The directory under the storage root that holds the per-identity slots.
    /// Reserved as an instance name for the usual reason — with no instance
    /// set the scope <em>is</em> the root, so an instance called
    /// <c>users</c> would land on top of the slots.
    /// </summary>
    public const string DirectoryName = "users";

    /// <summary>
    /// How much of the subject survives into the readable part. Long enough to
    /// recognise an e-mail address, short enough that the whole slot name
    /// stays well inside the path budget once a workspace path is appended.
    /// </summary>
    private const int MaxReadableLength = 48;

    /// <summary>Bytes of SHA-256 kept — 8 hex characters.</summary>
    private const int FingerprintBytes = 4;

    /// <summary>
    /// The directory name for <paramref name="subject"/>: a readable rendering
    /// of it, then a fingerprint of the original.
    /// </summary>
    /// <param name="subject">
    /// The authenticated subject, verbatim from the token. Trimmed, but
    /// otherwise not interpreted — the fingerprint is taken over what the
    /// provider issued.
    /// </param>
    /// <returns>
    /// A single path segment, lower-case, safe on every platform Bowire runs
    /// on.
    /// </returns>
    public static string Slug(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var trimmed = subject.Trim();
        var readable = new StringBuilder(MaxReadableLength);
        var lastWasSeparator = false;

        foreach (var ch in trimmed)
        {
            if (readable.Length >= MaxReadableLength) break;

            var lower = char.ToLowerInvariant(ch);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-')
            {
                readable.Append(lower);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && readable.Length > 0)
            {
                // One separator per run, so "auth0|5f3c" does not become
                // "auth0---5f3c" and the readable part stays readable.
                readable.Append('-');
                lastWasSeparator = true;
            }
        }

        // Trailing dots are stripped by the Windows filesystem, so a name
        // ending in one is not the name that gets created — trim them here
        // rather than discover the difference when a lookup misses.
        var text = readable.ToString().Trim('-', '.');
        if (text.Length == 0) text = "user";

        return text + "-" + Fingerprint(trimmed);
    }

    /// <summary>
    /// A short digest of the untouched subject. Not a secret and not
    /// reversible in any useful sense — it exists to keep two sanitised names
    /// apart, so a truncated hash is the right size for the job.
    /// </summary>
    private static string Fingerprint(string subject)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(subject));
        return Convert.ToHexStringLower(hash.AsSpan(0, FingerprintBytes));
    }
}
