// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the internal <see cref="ChannelStore"/> — the in-memory
/// keyed map that the channel endpoints use to keep open
/// <see cref="IBowireChannel"/> instances alive between requests. The store is
/// a tiny wrapper around <c>ConcurrentDictionary</c>; these tests assert the
/// add / get / remove behaviour and the not-found semantics.
/// </summary>
public class ChannelStoreTests
{
    [Fact]
    public async Task Add_Then_Get_Returns_Same_Instance()
    {
        var id = "test-add-get-" + Guid.NewGuid().ToString("n");
        await using var channel = new FakeChannel(id);
        try
        {
            ChannelStore.Add(channel);

            var resolved = ChannelStore.Get(id);

            Assert.NotNull(resolved);
            Assert.Same(channel, resolved);
        }
        finally
        {
            ChannelStore.Remove(id);
        }
    }

    [Fact]
    public void Get_Unknown_Id_Returns_Null()
    {
        var resolved = ChannelStore.Get("unknown-id-" + Guid.NewGuid().ToString("n"));
        Assert.Null(resolved);
    }

    [Fact]
    public async Task Remove_Drops_Entry_So_Get_Returns_Null()
    {
        var id = "test-remove-" + Guid.NewGuid().ToString("n");
        await using var channel = new FakeChannel(id);
        ChannelStore.Add(channel);
        Assert.NotNull(ChannelStore.Get(id));

        ChannelStore.Remove(id);

        Assert.Null(ChannelStore.Get(id));
    }

    [Fact]
    public void Remove_Unknown_Id_Is_Silent()
    {
        // Removing a non-existent id must not throw — the channel endpoints
        // call Remove eagerly when a stream completes and don't track whether
        // the entry was ever added.
        var unknown = "no-such-channel-" + Guid.NewGuid().ToString("n");
        var ex = Record.Exception(() => ChannelStore.Remove(unknown));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Add_Twice_With_Same_Id_Replaces_Entry()
    {
        var id = "test-replace-" + Guid.NewGuid().ToString("n");
        await using var first = new FakeChannel(id);
        await using var second = new FakeChannel(id);
        try
        {
            ChannelStore.Add(first);
            ChannelStore.Add(second);

            Assert.Same(second, ChannelStore.Get(id));
        }
        finally
        {
            ChannelStore.Remove(id);
        }
    }

    private sealed class FakeChannel(string id) : IBowireChannel
    {
        public string Id { get; } = id;
        public bool IsClientStreaming => false;
        public bool IsServerStreaming => false;
        public int SentCount => 0;
        public bool IsClosed => false;
        public long ElapsedMs => 0;

        public Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS1998 // Stub — yield-break async iterator with no awaits
        public async IAsyncEnumerable<string> ReadResponsesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
