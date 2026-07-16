// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Kuestenlogik.Bowire.Oast;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>One out-of-band callback, shaped for the workbench feed (#486).</summary>
public sealed record OastCallback
{
    /// <summary>Transport the callback arrived on: <c>dns</c>, <c>http</c>, …</summary>
    public required string Protocol { get; init; }

    /// <summary>The callback host that was contacted.</summary>
    public string? Id { get; init; }

    /// <summary>Address the callback came from — usually the target being tested.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>DNS query type, when the callback was a lookup.</summary>
    public string? QueryType { get; init; }

    /// <summary>Server-side timestamp, Unix epoch milliseconds.</summary>
    public long TimestampUnixMs { get; init; }

    /// <summary>The raw callback as the catcher recorded it.</summary>
    public string? RawRequest { get; init; }
}

/// <summary>
/// The long-lived OAST session behind the manual pen-test panel (#486). One
/// session per workbench process: the operator generates callback payloads,
/// pastes them into whatever they're testing by hand, and watches the feed for
/// the target reaching back — the interactive counterpart to the scanner's
/// automated <c>--oast-server</c> path.
/// </summary>
/// <remarks>
/// <para>
/// Registration is <b>eager</b> on the first payload, not deferred to the first
/// poll: the interaction server drops callbacks for a correlation id it has not
/// seen registered, so a payload handed out before registration would silently
/// lose any callback it caught. All payloads share one correlation id, so one
/// poll drains every probe's callbacks.
/// </para>
/// <para>
/// Configured from <c>Bowire:Oast:Server</c> / <c>Bowire:Oast:Token</c>. With no
/// server set, <see cref="Configured"/> is false and the panel shows how to
/// enable it rather than pretending to work.
/// </para>
/// </remarks>
public sealed class OastWorkbenchSession : IAsyncDisposable
{
    // A manual session is bounded by how fast a human plants payloads, but a
    // looping / hostile target could flood the DNS catcher — so the feed keeps
    // only the most-recent slice rather than growing without bound.
    private const int MaxFeed = 500;

    private readonly IOastClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<OastCallback> _feed = [];
    private bool _registered;

    /// <summary>Reason the configured server URL was rejected, or null.</summary>
    public string? ConfigError { get; }

    /// <summary>Whether an interaction server is configured and usable.</summary>
    public bool Configured => _client is not null;

    /// <summary>The interaction-server domain callbacks are addressed under, or null.</summary>
    public string? ServerDomain => _client?.ServerDomain;

    /// <summary>Create the session from the configured server + token (either may be null).</summary>
    public OastWorkbenchSession(string? server, string? token)
    {
        if (string.IsNullOrWhiteSpace(server)) return;
        try
        {
            _client = new InteractshClient(server, token);
        }
        catch (ArgumentException ex)
        {
            // A malformed URL disables OAST with a visible reason rather than
            // throwing out of the container build.
            ConfigError = ex.Message;
        }
    }

    /// <summary>
    /// Test seam — drive the session against an injected client so the
    /// eager-register / accumulation / mapping logic is covered without a live
    /// interaction server (the client↔server protocol itself is proven in the
    /// Oast package's tests).
    /// </summary>
    internal OastWorkbenchSession(IOastClient client) => _client = client;

    /// <summary>
    /// Hand out a fresh callback host to plant. Registers the session first if
    /// needed. Throws <see cref="InvalidOperationException"/> when no server is
    /// configured — the endpoint maps that to a clear 409, not a 500.
    /// </summary>
    public async Task<string> AllocateAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("No OAST interaction server is configured.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureRegisteredAsync(ct).ConfigureAwait(false);
            return _client.Allocate().CallbackHost;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The full accumulated feed after draining any new callbacks from the
    /// server. Returns the whole feed (not just the delta) so the panel can
    /// poll at its own cadence and always render the complete history. Empty
    /// when no server is configured.
    /// </summary>
    public async Task<IReadOnlyList<OastCallback>> PollAsync(CancellationToken ct = default)
    {
        if (_client is null) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureRegisteredAsync(ct).ConfigureAwait(false);
            foreach (var interaction in await _client.PollAsync(ct).ConfigureAwait(false))
            {
                _feed.Add(Map(interaction));
            }
            // Cap AFTER appending so a burst can't push older-but-unseen
            // callbacks out before the panel has rendered them once; the panel
            // polls faster than 500 callbacks arrive in one interval.
            if (_feed.Count > MaxFeed) _feed.RemoveRange(0, _feed.Count - MaxFeed);
            return [.. _feed];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureRegisteredAsync(CancellationToken ct)
    {
        if (_registered) return;
        await _client!.RegisterAsync(ct).ConfigureAwait(false);
        _registered = true;
    }

    private static OastCallback Map(OastInteraction i) => new()
    {
        Protocol = i.Protocol,
        Id = i.FullId ?? i.UniqueId,
        RemoteAddress = i.RemoteAddress,
        QueryType = i.QType,
        TimestampUnixMs = i.Timestamp.ToUnixTimeMilliseconds(),
        RawRequest = i.RawRequest,
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
