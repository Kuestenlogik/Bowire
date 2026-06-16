// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Workspace.Git;

/// <summary>
/// Singleton watcher fleet — keeps one <see cref="FileSystemWatcher"/>
/// per watched <c>storageRoot</c> and fans events out to every
/// subscriber listening on that root. The SSE endpoint (#150) gets a
/// <see cref="ChannelReader{T}"/> per subscription so each connected
/// browser tab consumes its own queue.
/// </summary>
/// <remarks>
/// <para>
/// Events are debounced per-path at <see cref="DebounceMilliseconds"/>:
/// the noisy save-shuffle from editors (temp-file dance, attribute
/// touches) collapses to one logical "this path changed" notification
/// before reaching subscribers. Each path's pending dispatch carries
/// its own <see cref="CancellationTokenSource"/>; a fresh FS event for
/// the same path cancels the in-flight delay and starts a new one, so
/// a steady stream of writes only fires once after the writer goes
/// quiet.
/// </para>
/// <para>
/// Reference-counted: when the last subscriber on a root unsubscribes
/// the underlying <see cref="FileSystemWatcher"/> is disposed. Re-
/// subscribing later spins up a fresh watcher. This keeps a CLI / web
/// process from leaking watchers when the operator switches workspaces.
/// </para>
/// </remarks>
public sealed class WorkspaceWatcher : IAsyncDisposable
{
    /// <summary>Debounce window for per-path event coalescing.</summary>
    public const int DebounceMilliseconds = 300;

    private readonly ConcurrentDictionary<string, RootBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    /// <summary>
    /// Subscribe to file-system events on <paramref name="storageRoot"/>.
    /// Returns the channel reader the SSE producer streams from and an
    /// <see cref="IDisposable"/> that, when disposed, unsubscribes and
    /// (if last) tears down the underlying watcher.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="storageRoot"/> is null / empty, doesn't exist, or
    /// isn't a directory. The watcher refuses to create a watcher on a
    /// missing path so the failure surfaces immediately to the SSE
    /// caller instead of as a silent zero-event stream.
    /// </exception>
    public (ChannelReader<WorkspaceFileEvent> Reader, IDisposable Subscription) Subscribe(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var normalised = Path.GetFullPath(storageRoot);
        if (!Directory.Exists(normalised))
        {
            throw new ArgumentException(
                $"Workspace storageRoot '{storageRoot}' does not exist or is not a directory.",
                nameof(storageRoot));
        }

        // Bounded so a wedged subscriber can't grow unbounded memory;
        // DropOldest matches "the workbench wants the latest state of
        // the world", not a guaranteed audit log.
        var channel = Channel.CreateBounded<WorkspaceFileEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var binding = _bindings.GetOrAdd(normalised, root => new RootBinding(root));
        binding.AddSubscriber(channel.Writer);

        var subscription = new Subscription(this, normalised, channel.Writer, channel);
        return (channel.Reader, subscription);
    }

    internal void Unsubscribe(string normalisedRoot, ChannelWriter<WorkspaceFileEvent> writer)
    {
        if (!_bindings.TryGetValue(normalisedRoot, out var binding)) return;
        if (binding.RemoveSubscriber(writer))
        {
            // Last subscriber on this root — tear down the watcher.
            if (_bindings.TryRemove(normalisedRoot, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        foreach (var kvp in _bindings)
        {
            kvp.Value.Dispose();
        }
        _bindings.Clear();
        return ValueTask.CompletedTask;
    }

    // -------------------------------------------------------------------

    private sealed class RootBinding : IDisposable
    {
        private readonly string _root;
        private readonly FileSystemWatcher _watcher;
        private readonly List<ChannelWriter<WorkspaceFileEvent>> _subscribers = [];
        private readonly Lock _gate = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
        private int _disposed;

        public RootBinding(string root)
        {
            _root = root;
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.CreationTime,
            };
            _watcher.Created += (_, e) => Schedule(e.FullPath, "file-created");
            _watcher.Changed += (_, e) => Schedule(e.FullPath, "file-changed");
            _watcher.Deleted += (_, e) => Schedule(e.FullPath, "file-deleted");
            _watcher.Renamed += (_, e) =>
            {
                Schedule(e.OldFullPath, "file-deleted");
                Schedule(e.FullPath, "file-created");
            };
            _watcher.EnableRaisingEvents = true;
        }

        public void AddSubscriber(ChannelWriter<WorkspaceFileEvent> writer)
        {
            lock (_gate) _subscribers.Add(writer);
        }

        /// <returns><c>true</c> when this was the last subscriber.</returns>
        public bool RemoveSubscriber(ChannelWriter<WorkspaceFileEvent> writer)
        {
            lock (_gate)
            {
                _subscribers.Remove(writer);
                writer.TryComplete();
                return _subscribers.Count == 0;
            }
        }

        private void Schedule(string fullPath, string kind)
        {
            if (_disposed != 0) return;

            var newCts = new CancellationTokenSource();
            // AddOrUpdate atomic so two concurrent fs events for the
            // same path can't leak a CTS — the loser's pending delay
            // gets cancelled by the winner replacing it. Disposal of
            // a superseded CTS is left to its DispatchAfterDebounceAsync
            // task; we just signal cancellation here so that task can
            // observe and dispose itself (CA2025 — don't dispose a
            // CTS while a task is still awaiting its token).
            var winner = _pending.AddOrUpdate(fullPath,
                _ => newCts,
                (_, existing) =>
                {
                    try { existing.Cancel(); } catch (ObjectDisposedException) { /* race with task self-disposal */ }
                    return newCts;
                });
            // If our CTS was replaced atomically by a concurrent fs
            // event, drop the just-created CTS (it's no longer the
            // pending dispatch).
            if (!ReferenceEquals(winner, newCts))
            {
                newCts.Dispose();
                return;
            }

            // CA2025: DispatchAfterDebounceAsync owns the CTS lifecycle —
            // it disposes after Task.Delay observes either cancellation or
            // completion. The analyzer can't trace the ownership transfer
            // through a fire-and-forget Task.Run-shape, so we suppress.
#pragma warning disable CA2025
            _ = DispatchAfterDebounceAsync(fullPath, kind, newCts);
#pragma warning restore CA2025
        }

        private async Task DispatchAfterDebounceAsync(string fullPath, string kind, CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(DebounceMilliseconds, cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // Newer event for same path superseded us.
                cts.Dispose();
                return;
            }

            // Remove only if our cts is still the pending one — a third
            // event could have replaced us between Delay return and
            // here; the third event owns the dispatch in that case.
            _pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(fullPath, cts));
            cts.Dispose();

            var relative = RelativeForwardSlash(_root, fullPath);
            if (string.IsNullOrEmpty(relative)) return;

            var evt = new WorkspaceFileEvent(kind, relative, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            List<ChannelWriter<WorkspaceFileEvent>> snapshot;
            lock (_gate) snapshot = [.. _subscribers];
            foreach (var writer in snapshot)
            {
                writer.TryWrite(evt);
            }
        }

        private static string RelativeForwardSlash(string root, string fullPath)
        {
            try
            {
                var rel = Path.GetRelativePath(root, fullPath);
                // GetRelativePath returns "." or "" when the path equals
                // the root — that's the root itself changing, not a file.
                if (rel == "." || rel.Length == 0) return string.Empty;
                return rel.Replace('\\', '/');
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _watcher.EnableRaisingEvents = false; } catch { /* ignore */ }
            _watcher.Dispose();
            foreach (var kvp in _pending)
            {
                try { kvp.Value.Cancel(); kvp.Value.Dispose(); } catch { /* ignore */ }
            }
            _pending.Clear();
            List<ChannelWriter<WorkspaceFileEvent>> snapshot;
            lock (_gate) { snapshot = [.. _subscribers]; _subscribers.Clear(); }
            foreach (var w in snapshot) w.TryComplete();
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly WorkspaceWatcher _owner;
        private readonly string _root;
        private readonly ChannelWriter<WorkspaceFileEvent> _writer;
        private readonly Channel<WorkspaceFileEvent> _channel;
        private int _disposed;

        public Subscription(WorkspaceWatcher owner, string root, ChannelWriter<WorkspaceFileEvent> writer, Channel<WorkspaceFileEvent> channel)
        {
            _owner = owner;
            _root = root;
            _writer = writer;
            _channel = channel;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Unsubscribe(_root, _writer);
            _ = _channel;
        }
    }
}
