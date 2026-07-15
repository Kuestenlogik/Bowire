// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Kuestenlogik.Bowire.Oast.Server;

/// <summary>
/// One client session: the keys it registered with, plus the callbacks the
/// catchers have recorded for it and not yet handed back.
/// </summary>
internal sealed class OastSession
{
    /// <summary>The client's public key — the AES key is wrapped to it on every poll.</summary>
    public required RSA PublicKey { get; init; }

    /// <summary>Shared secret from the register call; a poll without it is refused.</summary>
    public required string Secret { get; init; }

    /// <summary>
    /// Per-session AES key, minted at register time. It is what keeps a shared
    /// instance from reading one tenant's callbacks into another's poll.
    /// </summary>
    public required byte[] AesKey { get; init; }

    /// <summary>Callbacks recorded but not yet polled. Drained on read.</summary>
    public ConcurrentQueue<OastInteraction> Pending { get; } = new();

    /// <summary>Last register/poll, for idle eviction.</summary>
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The server's session + interaction registry (#35 Phase 2f). Sessions are
/// keyed by correlation id — the first 20 characters of a callback host — which
/// is how a DNS query for <c>&lt;corr&gt;&lt;nonce&gt;.oast.example.com</c> is
/// routed back to the client that planted it.
/// </summary>
/// <remarks>
/// In-memory and deliberately so: an interaction catcher holds other people's
/// callback traffic, so the less it persists the better. Sessions idle out, and
/// nothing survives a restart.
/// </remarks>
public sealed class OastInteractionStore
{
    // The client slices exactly this many characters off the front of a
    // callback label; a mismatch here silently breaks every correlation.
    internal const int CorrelationIdLength = 20;

    private readonly ConcurrentDictionary<string, OastSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private readonly TimeSpan _idleTimeout;

    /// <summary>
    /// Create a store. <paramref name="idleTimeout"/> evicts sessions that stop
    /// polling (default 1h) so a long-lived catcher doesn't accumulate other
    /// people's traffic indefinitely.
    /// </summary>
    public OastInteractionStore(TimeProvider? time = null, TimeSpan? idleTimeout = null)
    {
        _time = time ?? TimeProvider.System;
        _idleTimeout = idleTimeout ?? TimeSpan.FromHours(1);
    }

    /// <summary>Live session count — surfaced by the status endpoint.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>
    /// Register a client. <paramref name="publicKeyPem"/> is the PEM text the
    /// client base64'd into the request. Returns false when the key is
    /// unusable, which the endpoint turns into a 400 rather than a 500.
    /// </summary>
    public bool TryRegister(string correlationId, string secret, string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        // Dispose on the reject path: this endpoint is reachable by anyone who
        // can hit the catcher, so leaking a key handle per malformed PEM would
        // be a free memory-exhaustion vector.
        var rsa = RSA.Create();
        OastSession session;
        try
        {
            rsa.ImportFromPem(publicKeyPem);
            session = new OastSession
            {
                PublicKey = rsa,
                Secret = secret,
                AesKey = RandomNumberGenerator.GetBytes(32),
                LastSeen = _time.GetUtcNow(),
            };
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            rsa.Dispose();
            return false;
        }

        // Re-registering the same id replaces the session — the old keys are
        // disposed rather than leaked.
        _sessions.AddOrUpdate(correlationId, session, (_, existing) =>
        {
            existing.PublicKey.Dispose();
            return session;
        });
        return true;
    }

    /// <summary>Drop a session (client deregistered).</summary>
    public bool Remove(string correlationId, string secret)
    {
        if (!_sessions.TryGetValue(correlationId, out var s)) return false;
        // A wrong secret must not let a stranger evict someone's session.
        if (!FixedTimeEquals(s.Secret, secret)) return false;
        if (_sessions.TryRemove(correlationId, out var removed))
        {
            removed.PublicKey.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Record a callback against whichever session planted the host. Unknown
    /// hosts are dropped: the internet scans port 53, and traffic nobody
    /// registered for is noise, not evidence.
    /// </summary>
    public bool Record(string callbackHost, OastInteraction interaction)
    {
        var id = CorrelationIdOf(callbackHost);
        if (id is null || !_sessions.TryGetValue(id, out var session)) return false;
        session.Pending.Enqueue(interaction);
        return true;
    }

    /// <summary>
    /// Drain a session's callbacks. Returns null when the id is unknown or the
    /// secret is wrong — the endpoint maps both to 401 so a caller can't probe
    /// which correlation ids exist.
    /// </summary>
    public (byte[] AesKey, RSA PublicKey, List<OastInteraction> Interactions)? Poll(string correlationId, string secret)
    {
        if (!_sessions.TryGetValue(correlationId, out var session)) return null;
        if (!FixedTimeEquals(session.Secret, secret)) return null;

        session.LastSeen = _time.GetUtcNow();
        var drained = new List<OastInteraction>();
        while (session.Pending.TryDequeue(out var i)) drained.Add(i);
        return (session.AesKey, session.PublicKey, drained);
    }

    /// <summary>
    /// Evict sessions idle past the timeout. Returns the number dropped.
    /// Called on a timer by the host.
    /// </summary>
    public int EvictIdle()
    {
        var cutoff = _time.GetUtcNow() - _idleTimeout;
        var dropped = 0;
        foreach (var (id, session) in _sessions)
        {
            if (session.LastSeen > cutoff) continue;
            if (_sessions.TryRemove(id, out var removed))
            {
                removed.PublicKey.Dispose();
                dropped++;
            }
        }
        return dropped;
    }

    /// <summary>
    /// The correlation id inside a callback host — the first label's leading 20
    /// characters. Null when the host is too short to carry one.
    /// </summary>
    internal static string? CorrelationIdOf(string callbackHost)
    {
        if (string.IsNullOrWhiteSpace(callbackHost)) return null;
        var label = callbackHost.Split('.', 2)[0];
        return label.Length < CorrelationIdLength ? null : label[..CorrelationIdLength];
    }

    /// <summary>
    /// Constant-time secret comparison — a length-independent early return here
    /// would leak the secret a character at a time to a patient caller.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
