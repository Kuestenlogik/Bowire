// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Proxy;

namespace Kuestenlogik.Bowire.Tests.Proxy;

/// <summary>
/// Coverage for <see cref="CapturedFlowStore"/> — the in-memory ring
/// buffer behind <c>bowire proxy</c>. Verifies FIFO eviction, lookup,
/// the live SSE subscription channel, and basic invariants
/// (monotonic NextId, snapshot ordering newest-first).
/// </summary>
public sealed class CapturedFlowStoreTests
{
    private static CapturedFlow Flow(long id, string url = "http://example/foo") => new()
    {
        Id = id,
        CapturedAt = DateTimeOffset.UtcNow,
        Method = "GET",
        Url = url,
    };

    [Fact]
    public void NextId_IsMonotonic()
    {
        var store = new CapturedFlowStore();
        var first = store.NextId();
        var second = store.NextId();
        var third = store.NextId();
        Assert.Equal(first + 1, second);
        Assert.Equal(second + 1, third);
    }

    [Fact]
    public void Constructor_RejectsZeroOrNegativeCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapturedFlowStore(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapturedFlowStore(-1));
    }

    [Fact]
    public void Snapshot_ReturnsNewestFirst()
    {
        var store = new CapturedFlowStore(capacity: 10);
        store.Add(Flow(1));
        store.Add(Flow(2));
        store.Add(Flow(3));

        var snap = store.Snapshot();

        Assert.Equal(new long[] { 3, 2, 1 }, snap.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void Add_EvictsOldestWhenCapacityExceeded()
    {
        var store = new CapturedFlowStore(capacity: 3);
        store.Add(Flow(1));
        store.Add(Flow(2));
        store.Add(Flow(3));
        store.Add(Flow(4));

        var ids = store.Snapshot().Select(f => f.Id).ToArray();
        Assert.Equal(new long[] { 4, 3, 2 }, ids);
        Assert.Null(store.Get(1));
        Assert.NotNull(store.Get(4));
    }

    [Fact]
    public void Get_FindsById_OrReturnsNull()
    {
        var store = new CapturedFlowStore();
        store.Add(Flow(42));
        Assert.Equal(42L, store.Get(42)!.Id);
        Assert.Null(store.Get(9999));
    }

    [Fact]
    public void Clear_EmptiesTheStore()
    {
        var store = new CapturedFlowStore();
        store.Add(Flow(1));
        store.Add(Flow(2));
        store.Clear();
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task Subscribe_DeliversNewFlowsToSubscribers()
    {
        var store = new CapturedFlowStore();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var reader = store.Subscribe(cts.Token);

        store.Add(Flow(7));
        store.Add(Flow(8));

        var first = await reader.ReadAsync(cts.Token);
        var second = await reader.ReadAsync(cts.Token);

        Assert.Equal(7L, first.Id);
        Assert.Equal(8L, second.Id);
    }

    [Fact]
    public async Task Subscribe_CancellationCompletesChannel()
    {
        var store = new CapturedFlowStore();
        using var cts = new CancellationTokenSource();
        var reader = store.Subscribe(cts.Token);

        await cts.CancelAsync();

        // After cancellation, ReadAllAsync should complete without yielding more items.
        var received = new List<CapturedFlow>();
        try
        {
            await foreach (var f in reader.ReadAllAsync(TestContext.Current.CancellationToken))
                received.Add(f);
        }
        catch (OperationCanceledException) { /* acceptable */ }
        Assert.Empty(received);
    }
}
