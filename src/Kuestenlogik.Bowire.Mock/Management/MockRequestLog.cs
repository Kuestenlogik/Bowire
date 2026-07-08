// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using Kuestenlogik.Bowire.Telemetry;

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// Single inbound request against a running mock, captured for the
/// workbench's per-mock request-log view (issue #57). One per
/// <see cref="MockHandler.HandleAsync"/> invocation, emitted via
/// <see cref="IMockRequestObserver"/> after the response has been
/// written.
/// </summary>
public sealed record MockRequestEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string Method,
    string Path,
    int StatusCode,
    string? MatchedStepId,
    string Outcome,             // "matched" | "miss" | "passed-through" | "404"
    double DurationMs,
    string? Fault = null,       // #170 audit trail — human description of the injected fault, null when none fired
    string? Query = null,       // #409 raw query string (with leading '?'), for verify predicates
    IReadOnlyDictionary<string, string>? Headers = null); // #409 request headers, for verify predicates

/// <summary>
/// Sink the mock pipeline pushes <see cref="MockRequestEntry"/> records
/// into after each request resolution. The default implementation is
/// <see cref="MockRequestLog"/> (bounded ring buffer); plugins or tests
/// can substitute their own (e.g. a metrics emitter).
/// </summary>
public interface IMockRequestObserver
{
    void OnRequest(MockRequestEntry entry);
}

/// <summary>
/// Bounded ring buffer of request entries per mock instance. Capacity
/// defaults to 1000 — once full, the oldest entries drop. Reads return
/// a defensive snapshot in newest-first order so the UI can render the
/// log table without iterating live state.
/// </summary>
public sealed class MockRequestLog : IMockRequestObserver
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<MockRequestEntry> _queue = new();
    private long _sequence;

    public MockRequestLog(int capacity = 1000)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be >= 1");
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public long TotalRequests => Interlocked.Read(ref _sequence);

    public void OnRequest(MockRequestEntry entry)
    {
        // Stamp the sequence number here so the ring buffer's "newest"
        // ordering is deterministic regardless of arrival interleaving.
        var seqStamped = entry with { Sequence = Interlocked.Increment(ref _sequence) };
        _queue.Enqueue(seqStamped);
        while (_queue.Count > _capacity && _queue.TryDequeue(out _))
        {
            // Drop oldest until we're back at capacity.
        }

        // #29 self-telemetry. One increment per request hitting a
        // UI-started mock; outcome separates matched / miss / 404 for
        // the Grafana mock-traffic panel. Cheap when no listener is
        // attached -- the SDK's no-op fast path keeps the cost at a
        // single virtual call plus a tag-list build.
        BowireTelemetry.MockRequests.Add(1, new TagList
        {
            { "method", entry.Method },
            { "outcome", entry.Outcome },
            { "status_code", entry.StatusCode },
        });
    }

    /// <summary>
    /// Snapshot of the buffered entries, newest first. <paramref name="limit"/>
    /// caps the returned count (≤ <see cref="Capacity"/>); <paramref name="sinceSequence"/>
    /// excludes entries older or equal to that sequence (clients tailing).
    /// </summary>
    public IReadOnlyList<MockRequestEntry> Snapshot(int? limit = null, long sinceSequence = 0)
    {
        var arr = _queue.ToArray(); // ConcurrentQueue's snapshot is point-in-time consistent
        // Newest first.
        Array.Reverse(arr);
        var filtered = sinceSequence > 0
            ? arr.Where(e => e.Sequence > sinceSequence)
            : arr;
        if (limit is int n && n > 0)
        {
            filtered = filtered.Take(n);
        }
        return filtered.ToArray();
    }

    /// <summary>
    /// #409: run a <see cref="MockVerification"/> over the whole buffered
    /// journal — the embedded-host convenience behind
    /// <c>POST /api/mocks/{id}/verify</c>.
    /// </summary>
    public MockVerificationResult Verify(MockVerification verification)
        => MockRequestVerifier.Verify(Snapshot(), verification);

    /// <summary>
    /// #409: buffered requests that did NOT match any stub (outcome
    /// <c>miss</c> or <c>404</c>), newest first — the near-miss listing behind
    /// <c>GET /api/mocks/{id}/requests/unmatched</c>.
    /// </summary>
    public IReadOnlyList<MockRequestEntry> Unmatched(int? limit = null)
    {
        var unmatched = Snapshot()
            .Where(e => e.Outcome is "miss" or "404");
        if (limit is int n && n > 0) unmatched = unmatched.Take(n);
        return unmatched.ToArray();
    }
}
