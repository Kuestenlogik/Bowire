// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Management;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Unit tests for <see cref="MockRequestLog"/> — the bounded ring buffer
/// powering the per-mock request log view (#57). Three invariants matter
/// to the workbench: bounded capacity (old entries drop), monotonic
/// sequence numbers (so the UI can tail), and newest-first snapshot
/// order (so the log table renders without sorting).
/// </summary>
public sealed class MockRequestLogTests
{
    private static MockRequestEntry MakeEntry(string method = "GET", int statusCode = 200, string outcome = "matched") =>
        new(
            Sequence: 0,
            Timestamp: DateTimeOffset.UnixEpoch,
            Method: method,
            Path: "/api/test",
            StatusCode: statusCode,
            MatchedStepId: null,
            Outcome: outcome,
            DurationMs: 1.0);

    [Fact]
    public void Constructor_Rejects_Zero_Or_Negative_Capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MockRequestLog(capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MockRequestLog(capacity: -1));
    }

    [Fact]
    public void OnRequest_Assigns_Monotonic_Sequence_Numbers()
    {
        var log = new MockRequestLog();

        for (var i = 0; i < 5; i++) log.OnRequest(MakeEntry());

        var snapshot = log.Snapshot();
        Assert.Equal(5, snapshot.Count);
        // Snapshot is newest-first, so sequences run 5 → 1.
        var sequences = snapshot.Select(e => e.Sequence).ToArray();
        Assert.Equal(new long[] { 5, 4, 3, 2, 1 }, sequences);
        Assert.Equal(5, log.TotalRequests);
    }

    [Fact]
    public void Snapshot_Returns_Newest_First()
    {
        var log = new MockRequestLog();
        log.OnRequest(MakeEntry(method: "GET"));
        log.OnRequest(MakeEntry(method: "POST"));
        log.OnRequest(MakeEntry(method: "DELETE"));

        var snapshot = log.Snapshot();

        Assert.Equal("DELETE", snapshot[0].Method);
        Assert.Equal("POST", snapshot[1].Method);
        Assert.Equal("GET", snapshot[2].Method);
    }

    [Fact]
    public void OnRequest_Drops_Oldest_When_Capacity_Exceeded()
    {
        var log = new MockRequestLog(capacity: 3);
        for (var i = 0; i < 5; i++) log.OnRequest(MakeEntry());

        var snapshot = log.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(5, log.TotalRequests);
        // Newest-first → sequences 5, 4, 3. Oldest two (1, 2) dropped.
        Assert.Equal(5, snapshot[0].Sequence);
        Assert.Equal(3, snapshot[^1].Sequence);
    }

    [Fact]
    public void Snapshot_Limit_Caps_Returned_Count()
    {
        var log = new MockRequestLog();
        for (var i = 0; i < 10; i++) log.OnRequest(MakeEntry());

        var snapshot = log.Snapshot(limit: 3);

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(10, snapshot[0].Sequence);
    }

    [Fact]
    public void Snapshot_Since_Sequence_Filters_Older_Entries()
    {
        var log = new MockRequestLog();
        for (var i = 0; i < 10; i++) log.OnRequest(MakeEntry());

        var snapshot = log.Snapshot(sinceSequence: 7);

        // Only entries with Sequence > 7 → 8, 9, 10, newest-first.
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(new long[] { 10, 9, 8 }, snapshot.Select(e => e.Sequence));
    }

    [Fact]
    public void Capacity_Reflects_Constructor_Argument()
    {
        var log = new MockRequestLog(capacity: 42);
        Assert.Equal(42, log.Capacity);
    }

    [Fact]
    public void Total_Requests_Persists_After_Eviction()
    {
        // The on-disk "X requests served" counter shouldn't drop just
        // because the ring evicted older entries — the UI summary line
        // depends on TotalRequests counting lifetime hits, not buffered
        // hits. Regression-guard for that subtle semantic.
        var log = new MockRequestLog(capacity: 2);
        for (var i = 0; i < 5; i++) log.OnRequest(MakeEntry());
        Assert.Equal(5, log.TotalRequests);
        Assert.Equal(2, log.Snapshot().Count);
    }
}
