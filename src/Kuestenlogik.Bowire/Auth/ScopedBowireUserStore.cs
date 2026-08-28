// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// The identity-scoped <see cref="IBowireUserStore"/> — one authenticated
/// subject, one directory under <c>&lt;storage root&gt;/users/</c> (#97).
/// </summary>
/// <remarks>
/// <para>
/// Phase B shipped the seam and a default that keeps the flat single-user
/// layout, and said a multi-tenant deployment would "swap in an implementation
/// that scopes paths by the authenticated user's <c>sub</c> claim". This is
/// that implementation. Until it existed there was nothing to swap in, and
/// nothing for a migration to migrate <em>into</em> — which is why it lands
/// with Phase E rather than after it.
/// </para>
/// <para>
/// It is deliberately not aware of requests, authentication or middleware. It
/// answers one question — where does this subject's state live — and
/// <see cref="BowireTenancy"/> answers the separate question of whose turn it
/// is. Keeping those apart is what lets the migration construct a store for a
/// subject that is not the caller, which is the whole of what a migration
/// does.
/// </para>
/// </remarks>
public sealed class ScopedBowireUserStore : IBowireUserStore, IBowireStorageRootProvider
{
    /// <summary>
    /// A store for <paramref name="subject"/> under
    /// <paramref name="storageRoot"/>.
    /// </summary>
    /// <param name="storageRoot">
    /// The <see cref="Projects.BowireStorageScope.Data"/> root — the same
    /// directory the flat single-user layout uses. The slot goes under
    /// <c>users/</c> inside it rather than beside it, so a project that opted
    /// its storage into <c>.bowire/</c> keeps its identities there too.
    /// </param>
    /// <param name="subject">The authenticated subject, verbatim from the token.</param>
    public ScopedBowireUserStore(string storageRoot, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        StorageRoot = Path.GetFullPath(storageRoot);
        Subject = subject.Trim();
        Slug = BowireUserSlot.Slug(Subject);
        Slot = Path.Combine(StorageRoot, BowireUserSlot.DirectoryName, Slug);
    }

    /// <summary>The authenticated subject this store belongs to.</summary>
    public string Subject { get; }

    /// <summary>The directory name <see cref="Subject"/> maps to.</summary>
    public string Slug { get; }

    /// <summary>Absolute path to this subject's slot.</summary>
    public string Slot { get; }

    /// <inheritdoc />
    public string StorageRoot { get; }

    /// <inheritdoc />
    public string GetUserPath(string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        // Same containment guarantee the default store gives: callers pass
        // multi-segment relatives like "workspaces/<id>/recordings" through
        // here, and one of them being attacker-shaped must not walk out of
        // the slot into a neighbouring identity's.
        return SafePath.Combine(Slot, filename);
    }
}
