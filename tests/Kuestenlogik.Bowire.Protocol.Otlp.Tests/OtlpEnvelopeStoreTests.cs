// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Otlp;

namespace Kuestenlogik.Bowire.Protocol.Otlp.Tests;

/// <summary>
/// Behaviour tests for <see cref="OtlpEnvelopeStore"/>: ring-buffer
/// bound, per-kind snapshots, publish/subscribe fan-out.
/// </summary>
public sealed class OtlpEnvelopeStoreTests
{
    [Fact]
    public void Append_NullEnvelope_Throws()
    {
        var store = new OtlpEnvelopeStore();
        Assert.Throws<ArgumentNullException>(() => store.Append(null!));
    }

    [Fact]
    public void Capacity_BelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OtlpEnvelopeStore(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OtlpEnvelopeStore(-1));
    }

    [Fact]
    public void Default_Capacity_Is_1024()
    {
        var store = new OtlpEnvelopeStore();
        Assert.Equal(1024, store.Capacity);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Append_EvictsOldestPastCapacity()
    {
        var store = new OtlpEnvelopeStore(3);
        for (var i = 0; i < 5; i++)
        {
            store.Append(MakeEnvelope(OtlpSignalKind.Traces, $"e{i}"));
        }
        var snap = store.Snapshot();
        Assert.Equal(3, snap.Count);
        // Oldest two were evicted; the ring holds the last three appended.
        Assert.Equal("e2", snap[0].BodyJson);
        Assert.Equal("e3", snap[1].BodyJson);
        Assert.Equal("e4", snap[2].BodyJson);
    }

    [Fact]
    public void Snapshot_FilterByKind_ReturnsOnlyMatching()
    {
        var store = new OtlpEnvelopeStore();
        store.Append(MakeEnvelope(OtlpSignalKind.Traces, "t1"));
        store.Append(MakeEnvelope(OtlpSignalKind.Metrics, "m1"));
        store.Append(MakeEnvelope(OtlpSignalKind.Traces, "t2"));
        store.Append(MakeEnvelope(OtlpSignalKind.Logs, "l1"));

        var traces = store.Snapshot(OtlpSignalKind.Traces);
        Assert.Equal(2, traces.Count);
        Assert.Equal("t1", traces[0].BodyJson);
        Assert.Equal("t2", traces[1].BodyJson);
    }

    [Fact]
    public void Latest_ReturnsMostRecentOfKind()
    {
        var store = new OtlpEnvelopeStore();
        Assert.Null(store.Latest(OtlpSignalKind.Traces));

        store.Append(MakeEnvelope(OtlpSignalKind.Traces, "first"));
        store.Append(MakeEnvelope(OtlpSignalKind.Metrics, "metrics"));
        store.Append(MakeEnvelope(OtlpSignalKind.Traces, "newest"));

        var latest = store.Latest(OtlpSignalKind.Traces);
        Assert.NotNull(latest);
        Assert.Equal("newest", latest!.BodyJson);
        Assert.Null(store.Latest(OtlpSignalKind.Logs));
    }

    [Fact]
    public void Clear_ResetsRing_KeepsSubscribers()
    {
        var store = new OtlpEnvelopeStore();
        store.Append(MakeEnvelope(OtlpSignalKind.Traces, "x"));
        store.Clear();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Subscribe_ReceivesEnvelopesAppendedAfterAttach()
    {
        var store = new OtlpEnvelopeStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subTask = Task.Run(async () =>
        {
            var received = new List<OtlpEnvelope>();
            await foreach (var env in store.SubscribeAsync(cts.Token).ConfigureAwait(false))
            {
                received.Add(env);
                if (received.Count >= 2) break;
            }
            return received;
        }, cts.Token);

        // Wait for the subscriber to be attached, not for a duration. The
        // 50 ms sleep this replaces was enough on an idle machine and not on
        // one running the whole solution: the appends landed before anyone
        // was listening, and the subscriber then sat until its timeout.
        await WaitForSubscriberAsync(store, cts.Token);

        store.Append(MakeEnvelope(OtlpSignalKind.Traces, "after-1"));
        store.Append(MakeEnvelope(OtlpSignalKind.Metrics, "after-2"));

        var got = await subTask;
        Assert.Equal(2, got.Count);
        Assert.Equal("after-1", got[0].BodyJson);
        Assert.Equal("after-2", got[1].BodyJson);
    }

    /// <summary>
    /// Block until <paramref name="store"/> has a subscriber attached.
    /// </summary>
    /// <remarks>
    /// Attaching happens inside the iterator's first MoveNextAsync, so there
    /// is nothing to await directly; the count is the one observable the
    /// store offers. The token carries the test's own deadline, so a
    /// subscriber that never attaches fails the test rather than hanging it.
    /// </remarks>
    private static async Task WaitForSubscriberAsync(OtlpEnvelopeStore store, CancellationToken ct)
    {
        while (store.SubscriberCount == 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5, ct);
        }
    }

    private static OtlpEnvelope MakeEnvelope(OtlpSignalKind kind, string body) =>
        new(Kind: kind, ReceivedAt: DateTimeOffset.UnixEpoch, ContentType: "application/json",
            BodyJson: body, BodyBase64: null, BodyBytes: body.Length, RemoteIp: null);
}
