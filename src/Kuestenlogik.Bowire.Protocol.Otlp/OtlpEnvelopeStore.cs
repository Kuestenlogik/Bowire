// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Protocol.Otlp;

/// <summary>
/// In-memory ring buffer of received OTLP envelopes plus a fan-out
/// publish/subscribe channel.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 keeps the bound deliberately small (1024 envelopes) — the
/// workbench's channel UX surfaces the latest envelopes and operators
/// who want full retention should point their exporters at a proper
/// collector. Phase 2 may grow the bound + add disk spill, but the
/// passive-listener-for-debugging story stays in-memory.
/// </para>
/// <para>
/// The store is registered as a singleton so every <c>POST /v1/*</c>
/// request and every <see cref="BowireOtlpProtocol"/> subscriber sees
/// the same envelopes. Subscribers receive only envelopes published
/// after they subscribe — late subscribers can call
/// <see cref="Snapshot()"/> first to read what's already in the ring.
/// </para>
/// </remarks>
public sealed class OtlpEnvelopeStore
{
    private readonly object _gate = new();
    private readonly LinkedList<OtlpEnvelope> _ring = new();
    private readonly List<ChannelWriter<OtlpEnvelope>> _subscribers = [];

    /// <summary>Maximum number of envelopes the ring retains.</summary>
    public int Capacity { get; }

    public OtlpEnvelopeStore() : this(1024) { }

    public OtlpEnvelopeStore(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>Total envelopes currently retained in the ring.</summary>
    public int Count
    {
        get { lock (_gate) { return _ring.Count; } }
    }

    /// <summary>Add an envelope to the ring and notify every subscriber.</summary>
    public void Append(OtlpEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        List<ChannelWriter<OtlpEnvelope>>? snapshot = null;
        lock (_gate)
        {
            _ring.AddLast(envelope);
            while (_ring.Count > Capacity)
            {
                _ring.RemoveFirst();
            }
            if (_subscribers.Count > 0)
            {
                snapshot = [.. _subscribers];
            }
        }
        if (snapshot is null) return;
        foreach (var w in snapshot)
        {
            // TryWrite drops on a closed/full writer — Phase 1 prefers
            // dropped notifications over backpressure stalls in the
            // receive path. A slow subscriber doesn't stall the
            // receiver Map'd endpoint.
            w.TryWrite(envelope);
        }
    }

    /// <summary>Returns a snapshot copy of every envelope currently retained.</summary>
    public IReadOnlyList<OtlpEnvelope> Snapshot()
    {
        lock (_gate)
        {
            return [.. _ring];
        }
    }

    /// <summary>Returns the snapshot filtered to a single signal kind.</summary>
    public IReadOnlyList<OtlpEnvelope> Snapshot(OtlpSignalKind kind)
    {
        lock (_gate)
        {
            var list = new List<OtlpEnvelope>(_ring.Count);
            foreach (var e in _ring)
            {
                if (e.Kind == kind) list.Add(e);
            }
            return list;
        }
    }

    /// <summary>Returns the most recently appended envelope of the given kind, or null when none retained.</summary>
    public OtlpEnvelope? Latest(OtlpSignalKind kind)
    {
        lock (_gate)
        {
            for (var node = _ring.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Kind == kind) return node.Value;
            }
            return null;
        }
    }

    /// <summary>
    /// Subscribe to incoming envelopes. The returned reader yields
    /// every envelope published after the subscription begins.
    /// </summary>
    /// <returns>
    /// An <see cref="IAsyncEnumerable{T}"/> the caller awaits across
    /// the lifetime of the subscription. The subscription stays open
    /// until the caller cancels via the supplied token.
    /// </returns>
    public async IAsyncEnumerable<OtlpEnvelope> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateUnbounded<OtlpEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        lock (_gate)
        {
            _subscribers.Add(ch.Writer);
        }
        try
        {
            await foreach (var env in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return env;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(ch.Writer);
            }
            ch.Writer.TryComplete();
        }
    }

    /// <summary>Reset the ring (test helper). Subscribers stay attached and continue receiving new envelopes.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _ring.Clear();
        }
    }
}
