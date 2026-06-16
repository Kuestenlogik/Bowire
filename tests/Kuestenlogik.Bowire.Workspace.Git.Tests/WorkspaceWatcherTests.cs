// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Workspace.Git;

namespace Kuestenlogik.Bowire.Workspace.Git.Tests;

/// <summary>
/// Behavioural coverage for <see cref="WorkspaceWatcher"/> — the
/// FileSystemWatcher fanout shipped in #196 Phase 2.4. Pinned through
/// the public Subscribe API; the per-root binding + debounce machinery
/// are internal implementation details.
/// </summary>
public sealed class WorkspaceWatcherTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly WorkspaceWatcher _watcher;

    public WorkspaceWatcherTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("bowire-watcher-tests-").FullName;
        _watcher = new WorkspaceWatcher();
    }

    public async ValueTask DisposeAsync()
    {
        await _watcher.DisposeAsync();
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Subscribe_With_Missing_Path_Throws_ArgumentException()
    {
        var bogus = Path.Combine(_tempRoot, "does-not-exist");
        Assert.Throws<ArgumentException>(() => _watcher.Subscribe(bogus));
    }

    [Fact]
    public void Subscribe_With_NullOrEmpty_Path_Throws_ArgumentException()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace differentiates:
        // null -> ArgumentNullException (subclass of ArgumentException),
        // empty / whitespace -> ArgumentException. Accept either.
        Assert.ThrowsAny<ArgumentException>(() => _watcher.Subscribe(null!));
        Assert.ThrowsAny<ArgumentException>(() => _watcher.Subscribe(""));
        Assert.ThrowsAny<ArgumentException>(() => _watcher.Subscribe("  "));
    }

    [Fact]
    public async Task Subscribe_Surfaces_FileCreated_Event()
    {
        var (reader, subscription) = _watcher.Subscribe(_tempRoot);
        using var _ = subscription;

        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "hello.json"), "{}",
            TestContext.Current.CancellationToken);

        var evt = await AwaitOneAsync(reader, TimeSpan.FromSeconds(3));
        Assert.NotNull(evt);
        // FileSystemWatcher reports the Created event first; debounce
        // collapses any follow-up Changed within the window. Either
        // creation marker is acceptable as long as the relative path
        // matches and a forward-slash separator is used.
        Assert.True(evt!.Value.Kind == "file-created" || evt.Value.Kind == "file-changed",
            $"Unexpected event kind '{evt.Value.Kind}'.");
        Assert.Equal("hello.json", evt.Value.RelativePath);
        Assert.True(evt.Value.TimestampMs > 0);
    }

    [Fact]
    public async Task Debounce_Coalesces_Burst_Of_Writes_To_One_Event_Per_Path()
    {
        var (reader, subscription) = _watcher.Subscribe(_tempRoot);
        using var _ = subscription;

        var target = Path.Combine(_tempRoot, "burst.json");
        // Rapid burst — every Write under the 300 ms debounce window
        // should collapse to one final emission.
        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(target, "{\"i\":" + i + "}",
                TestContext.Current.CancellationToken);
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        // Collect up to 6 events with a tight per-event timeout — we
        // expect 1 (debounce coalesces the burst). Accept up to a
        // handful as long as they're for the same path; the test is
        // about coalescing, not exact arithmetic.
        var events = await CollectAsync(reader, TimeSpan.FromSeconds(2),
            maxItems: 6, idleTimeout: TimeSpan.FromMilliseconds(800));
        Assert.NotEmpty(events);
        Assert.True(events.Count <= 3,
            $"Debounce should keep the burst under 3 events; got {events.Count}.");
        Assert.All(events, e => Assert.Equal("burst.json", e.RelativePath));
    }

    [Fact]
    public async Task Multiple_Subscribers_Each_Receive_Their_Own_Copy()
    {
        var (readerA, subA) = _watcher.Subscribe(_tempRoot);
        var (readerB, subB) = _watcher.Subscribe(_tempRoot);
        using var _a = subA;
        using var _b = subB;

        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "shared.json"), "{}",
            TestContext.Current.CancellationToken);

        var evtA = await AwaitOneAsync(readerA, TimeSpan.FromSeconds(3));
        var evtB = await AwaitOneAsync(readerB, TimeSpan.FromSeconds(3));
        Assert.NotNull(evtA);
        Assert.NotNull(evtB);
        Assert.Equal("shared.json", evtA!.Value.RelativePath);
        Assert.Equal("shared.json", evtB!.Value.RelativePath);
    }

    [Fact]
    public async Task Subscribe_Surfaces_FileDeleted_Event()
    {
        var target = Path.Combine(_tempRoot, "doomed.json");
        await File.WriteAllTextAsync(target, "{}",
            TestContext.Current.CancellationToken);

        var (reader, subscription) = _watcher.Subscribe(_tempRoot);
        using var _ = subscription;

        File.Delete(target);

        // Drain until we see the delete or hit the timeout.
        var events = await CollectAsync(reader, TimeSpan.FromSeconds(3),
            maxItems: 8, idleTimeout: TimeSpan.FromMilliseconds(800));
        Assert.Contains(events, e => e.Kind == "file-deleted" && e.RelativePath == "doomed.json");
    }

    [Fact]
    public async Task Unsubscribe_Drops_Subscriber_From_Fanout()
    {
        var (reader, subscription) = _watcher.Subscribe(_tempRoot);
        subscription.Dispose();

        // Writes after dispose shouldn't appear in the reader; channel
        // should be marked complete (TryReadAsync returns false quickly).
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "post-unsub.json"), "{}",
            TestContext.Current.CancellationToken);

        // Wait for completion or short timeout; this should NOT yield
        // an event.
        var evt = await AwaitOneAsync(reader, TimeSpan.FromMilliseconds(600));
        Assert.Null(evt);
    }

    // ----------------------------------------------------------------

    private static async Task<WorkspaceFileEvent?> AwaitOneAsync(
        System.Threading.Channels.ChannelReader<WorkspaceFileEvent> reader,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var evt in reader.ReadAllAsync(cts.Token))
            {
                return evt;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout — no event arrived.
        }
        return null;
    }

    private static async Task<List<WorkspaceFileEvent>> CollectAsync(
        System.Threading.Channels.ChannelReader<WorkspaceFileEvent> reader,
        TimeSpan totalTimeout,
        int maxItems,
        TimeSpan idleTimeout)
    {
        var list = new List<WorkspaceFileEvent>();
        using var totalCts = new CancellationTokenSource(totalTimeout);
        while (list.Count < maxItems && !totalCts.IsCancellationRequested)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
            idleCts.CancelAfter(idleTimeout);
            try
            {
                if (await reader.WaitToReadAsync(idleCts.Token))
                {
                    while (reader.TryRead(out var item)) list.Add(item);
                }
                else
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // Either total timeout or idle — drain whatever's queued and return.
                while (reader.TryRead(out var item)) list.Add(item);
                break;
            }
        }
        return list;
    }
}
