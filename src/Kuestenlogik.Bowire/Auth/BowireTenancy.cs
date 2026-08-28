// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Routes each call to the slot of whichever identity is currently being
/// served (#97).
/// </summary>
/// <remarks>
/// <para>
/// The stores are static — <c>EnvironmentStore</c>, <c>RecordingStore</c> and
/// the rest resolve their paths through <see cref="BowireUserContext"/> with
/// no request in sight. That was the point of the Phase B seam: the stores
/// keep their shape and the seam becomes identity-aware underneath them. Doing
/// that in a server means the "who" has to travel with the execution context
/// rather than through a parameter, so it rides an
/// <see cref="AsyncLocal{T}"/>. That is ambient state, which is normally worth
/// avoiding; here it is the only shape that reaches a static store from a
/// request without rewriting every store to take a user.
/// </para>
/// <para>
/// <b>What happens with no identity.</b> Background work has no request —
/// the plugin update check, a warm-up, a hosted service. Those fall through to
/// the store the host was using before tenancy was enabled, which keeps
/// process-wide state process-wide instead of filing it under whoever
/// happened to be served last. Requests do not take that path: in multi-tenant
/// mode Bowire's endpoints require an authenticated caller, so a request
/// without a subject has already been rejected before a store is touched.
/// </para>
/// </remarks>
public sealed class BowireTenancy : IBowireUserStore, IBowireStorageRootProvider
{
    private static readonly AsyncLocal<string?> s_subject = new();

    private readonly ConcurrentDictionary<string, ScopedBowireUserStore> _slots =
        new(StringComparer.Ordinal);
    private readonly IBowireUserStore _shared;

    /// <summary>
    /// Tenancy rooted at <paramref name="storageRoot"/>.
    /// </summary>
    /// <param name="storageRoot">
    /// The data root — resolved once, before this replaces
    /// <see cref="BowireUserContext.Current"/>. Reading it afterwards would
    /// ask this very object where the root is.
    /// </param>
    /// <param name="shared">
    /// Where calls with no identity go. Normally the store the host had
    /// before, i.e. the flat single-user layout.
    /// </param>
    public BowireTenancy(string storageRoot, IBowireUserStore shared)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(shared);

        StorageRoot = Path.GetFullPath(storageRoot);
        _shared = shared;
    }

    /// <inheritdoc />
    public string StorageRoot { get; }

    /// <summary>The directory holding every identity's slot.</summary>
    public string UsersRoot => Path.Combine(StorageRoot, BowireUserSlot.DirectoryName);

    /// <summary>
    /// The subject being served on this execution context, or <c>null</c>
    /// outside a request.
    /// </summary>
    public static string? CurrentSubject => s_subject.Value;

    /// <summary>
    /// Serve <paramref name="subject"/> until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// Restoring the previous value rather than clearing it: a nested scope is
    /// legitimate — an admin acting on another identity's behalf (#98) is
    /// exactly that — and clearing would silently drop the outer identity for
    /// the remainder of the request.
    /// </remarks>
    public static IDisposable Enter(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var previous = s_subject.Value;
        s_subject.Value = subject.Trim();
        return new Scope(previous);
    }

    /// <summary>The store for <paramref name="subject"/>, whoever is being served.</summary>
    /// <remarks>
    /// Cached because <see cref="GetUserPath"/> sits on read paths that run
    /// per request, and building a slot hashes the subject.
    /// </remarks>
    public ScopedBowireUserStore For(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return _slots.GetOrAdd(subject.Trim(), s => new ScopedBowireUserStore(StorageRoot, s));
    }

    /// <inheritdoc />
    public string GetUserPath(string filename)
    {
        var subject = s_subject.Value;
        return subject is null
            ? _shared.GetUserPath(filename)
            : For(subject).GetUserPath(filename);
    }

    /// <summary>
    /// The slot directories that exist on disk, by name.
    /// </summary>
    /// <remarks>
    /// Directory names, not subjects: the mapping only runs one way, which is
    /// what lets the slot name be readable without the subject having to be
    /// recoverable from disk. Callers that need the subject already have it.
    /// </remarks>
    public IEnumerable<string> EnumerateSlots()
    {
        if (!Directory.Exists(UsersRoot)) return [];

        // A slug never starts with a dot, so this drops the bookkeeping that
        // shares the directory — including a staging tree left behind by a
        // migration whose process died before it could move it into place.
        return Directory.EnumerateDirectories(UsersRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && name[0] != '.')!;
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            s_subject.Value = previous;
        }
    }
}
