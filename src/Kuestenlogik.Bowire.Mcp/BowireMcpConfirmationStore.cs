// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// In-memory pending-confirmation store for the two-step mutator pattern
/// (<c>bowire.mock.start</c>, <c>bowire.record.start</c>,
/// <c>bowire.env.switch</c>, …). Step 1 of a mutation parks a description
/// of the intended action here keyed by a short token; step 2 looks up
/// the token, executes, and discards it. Tokens expire after
/// <see cref="DefaultTtl"/> so a forgotten confirmation can't be replayed
/// hours later from another agent context.
///
/// <para>
/// Registered as a singleton via
/// <see cref="BowireMcpServiceCollectionExtensions.AddBowireMcp"/>; lives
/// for the lifetime of the MCP host process so multiple tool calls in
/// the same session can hand the token back and forth.
/// </para>
/// </summary>
public sealed class BowireMcpConfirmationStore
{
    /// <summary>
    /// Default pending-confirmation lifetime — five minutes. Long enough
    /// that an agent can read the plan, reason about it, and reply;
    /// short enough that a forgotten pending token can't be re-played
    /// out of band an hour later. Settable for tests so they don't have
    /// to wait wall-clock minutes to exercise expiry.
    /// </summary>
    public static TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, BowireMcpPendingConfirmation> _pending = new();

    /// <summary>
    /// Park a description of an action and return the token an agent has
    /// to echo back to execute it. The <paramref name="kind"/> arg is the
    /// tool name (e.g. <c>"bowire.mock.start"</c>) — used in the human-
    /// readable token-not-found message.
    /// </summary>
    public string Issue(string kind, string plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);

        // Sweep expired entries on the same call so the dictionary
        // doesn't grow without bound across a long session — common
        // pattern for token caches that don't want a dedicated timer.
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _pending)
        {
            if (kvp.Value.ExpiresAt < now)
                _pending.TryRemove(kvp.Key, out _);
        }

        // 12 hex chars is enough entropy for a per-session token and
        // matches the mock-handle width (BowireMockHandleRegistry).
        var token = Guid.NewGuid().ToString("N")[..12];
        _pending[token] = new BowireMcpPendingConfirmation(kind, plan, now + DefaultTtl);
        return token;
    }

    /// <summary>
    /// Remove and return the pending entry under <paramref name="token"/>.
    /// Returns <c>null</c> when the token is unknown or expired; the
    /// caller surfaces this as a user-facing "no pending confirmation
    /// matches that token" message.
    /// </summary>
    public BowireMcpPendingConfirmation? Consume(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (!_pending.TryRemove(token, out var entry)) return null;
        if (entry.ExpiresAt < DateTimeOffset.UtcNow) return null;
        return entry;
    }

    /// <summary>
    /// Read-only count for tests / observability — how many pending
    /// confirmations are currently parked. Includes expired entries
    /// that haven't been swept yet.
    /// </summary>
    public int Count => _pending.Count;
}

/// <summary>
/// One parked confirmation. <see cref="Kind"/> identifies the tool
/// the agent issued; <see cref="Plan"/> is the human-readable
/// description the first-step response showed the user; <see cref="ExpiresAt"/>
/// caps how long it stays redeemable. Top-level so consumers don't
/// hit CA1034 when destructuring through <see cref="BowireMcpConfirmationStore.Consume"/>.
/// </summary>
public sealed record BowireMcpPendingConfirmation(string Kind, string Plan, DateTimeOffset ExpiresAt);
