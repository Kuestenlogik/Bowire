// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Protocol.GraphQL.Tests;

/// <summary>
/// The shared graphql-transport-ws socket (#713), driven through an
/// in-memory socket so the order of what goes on the wire is exact rather
/// than a matter of network timing.
/// </summary>
/// <remarks>
/// The in-memory socket answers every open and every send synchronously.
/// That makes the first <c>MoveNextAsync</c> run up to the first thing the
/// pool really has to wait for, which is the observation point these tests
/// use: whatever has been sent by then was sent without waiting.
/// </remarks>
public sealed class GraphQLSocketPoolTests
{
    private const string Url = "ws://localhost/graphql";
    private static readonly object s_payload = new { query = "subscription { tick }" };

    [Fact]
    public async Task Subscribe_Waits_For_Connection_Ack()
    {
        // #923 - graphql-transport-ws forbids `subscribe` before
        // `connection_ack`; a server answers it by closing the socket with
        // 4401. The pool sent it straight after `connection_init`.
        var factory = new InMemorySockets();
        await using var pool = new GraphQLSocketPool();
        var ct = TestContext.Current.CancellationToken;

        await using var subscription = pool.SubscribeAsync(factory, Url, null, s_payload, ct).GetAsyncEnumerator(ct);
        var first = subscription.MoveNextAsync();

        var socket = Assert.Single(factory.Opened);
        var sentBeforeAck = socket.SentTypes();

        socket.Receive("""{"type":"connection_ack"}""");
        var id = await socket.Subscribed;
        socket.Receive(Next(id, "after-ack"));

        Assert.True(await first);
        Assert.Contains("after-ack", subscription.Current, StringComparison.Ordinal);

        socket.Receive($$"""{"type":"complete","id":"{{id}}"}""");
        Assert.False(await subscription.MoveNextAsync());

        // Checked last so the subscription is drained either way.
        Assert.Equal(["connection_init"], sentBeforeAck);
    }

    [Fact]
    public async Task A_New_Subscription_Opens_A_New_Socket_When_The_Shared_One_Has_Died()
    {
        // #923 - a dead socket stayed in the pool for as long as one of its
        // subscribers had not yet let go, and a subscription arriving in
        // that window joined it: its subscribe went nowhere and its stream
        // waited on a reader that had already stopped.
        var factory = new InMemorySockets();
        await using var pool = new GraphQLSocketPool();
        var ct = TestContext.Current.CancellationToken;

        await using var early = pool.SubscribeAsync(factory, Url, null, s_payload, ct).GetAsyncEnumerator(ct);
        var earlyFirst = early.MoveNextAsync();
        var dead = Assert.Single(factory.Opened);
        dead.Receive("""{"type":"connection_ack"}""");
        var earlyId = await dead.Subscribed;
        dead.Receive(Next(earlyId, "before"));
        Assert.True(await earlyFirst);

        // The socket dies. The early subscriber reads why and stops there:
        // told, but still attached.
        dead.Die();
        Assert.True(await early.MoveNextAsync());
        Assert.Equal(BowireStreamErrorKinds.Transport, BowireStreamErrorEnvelope.TryRead(early.Current)?.Kind);

        using var lateCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var late = pool.SubscribeAsync(factory, Url, null, s_payload, lateCancel.Token).GetAsyncEnumerator(lateCancel.Token);
        try
        {
            var lateFirst = late.MoveNextAsync();
            Assert.Equal(2, factory.Opened.Count);

            var fresh = factory.Opened[1];
            fresh.Receive("""{"type":"connection_ack"}""");
            var lateId = await fresh.Subscribed;
            fresh.Receive(Next(lateId, "fresh"));

            Assert.True(await lateFirst);
            Assert.Contains("fresh", late.Current, StringComparison.Ordinal);
        }
        finally
        {
            // Unblocks a subscription that joined the dead socket, so the
            // failure above is reported instead of hanging the run.
            await lateCancel.CancelAsync();
            try { await late.DisposeAsync(); }
            catch (NotSupportedException) { /* disposed mid-MoveNext in the failing case */ }
        }
    }

    private static string Next(string id, string tick)
        => "{\"type\":\"next\",\"id\":\"" + id + "\",\"payload\":{\"data\":{\"tick\":\"" + tick + "\"}}}";

    /// <summary>Hands out one <see cref="InMemorySocket"/> per open.</summary>
    private sealed class InMemorySockets : IInlineWebSocketChannel
    {
        private readonly List<InMemorySocket> _opened = [];
        private readonly Lock _lock = new();

        public IReadOnlyList<InMemorySocket> Opened
        {
            get { lock (_lock) return [.. _opened]; }
        }

        public Task<IBowireChannel> OpenAsync(
            string url, IReadOnlyList<string>? subProtocols,
            Dictionary<string, string>? headers, CancellationToken ct = default)
        {
            var socket = new InMemorySocket();
            lock (_lock) _opened.Add(socket);
            return Task.FromResult<IBowireChannel>(socket);
        }
    }

    /// <summary>
    /// A socket whose incoming frames the test writes and whose outgoing
    /// frames the test reads. Sends complete synchronously.
    /// </summary>
    private sealed class InMemorySocket : IBowireChannel
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly List<string> _sent = [];
        private readonly Lock _lock = new();
        private readonly TaskCompletionSource<string> _subscribed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The id of the first <c>subscribe</c> sent on this socket.</summary>
        public Task<string> Subscribed => _subscribed.Task;

        public string Id => "in-memory";
        public bool IsClientStreaming => true;
        public bool IsServerStreaming => true;
        public int SentCount { get { lock (_lock) return _sent.Count; } }
        public bool IsClosed => _incoming.Reader.Completion.IsCompleted;
        public long ElapsedMs => 0;

        public List<string?> SentTypes()
        {
            lock (_lock)
            {
                return _sent.Select(s => JsonDocument.Parse(s).RootElement.GetProperty("type").GetString()).ToList();
            }
        }

        public void Receive(string json) => _incoming.Writer.TryWrite(json);

        public void Die() => _incoming.Writer.TryComplete();

        public Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
        {
            lock (_lock) _sent.Add(jsonMessage);
            using var doc = JsonDocument.Parse(jsonMessage);
            if (doc.RootElement.GetProperty("type").GetString() == "subscribe")
                _subscribed.TrySetResult(doc.RootElement.GetProperty("id").GetString()!);
            return Task.FromResult(true);
        }

        public Task CloseAsync(CancellationToken ct = default)
        {
            Die();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReadResponsesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in _incoming.Reader.ReadAllAsync(ct))
                yield return frame;
        }

        public ValueTask DisposeAsync()
        {
            Die();
            return ValueTask.CompletedTask;
        }
    }
}
