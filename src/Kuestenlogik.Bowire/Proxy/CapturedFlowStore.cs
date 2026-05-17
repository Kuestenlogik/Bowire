// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Proxy;

/// <summary>
/// In-memory ring buffer of <see cref="CapturedFlow"/>s plus a fan-out
/// channel for live subscribers (Workbench Proxy tab over SSE). Bounded
/// so a long-running <c>bowire proxy</c> session does not eat unbounded
/// memory — once <see cref="Capacity"/> flows are stored the oldest one
/// is evicted before the new one is added.
/// </summary>
public sealed class CapturedFlowStore
{
    private readonly object _lock = new();
    private readonly LinkedList<CapturedFlow> _flows = new();
    private readonly List<Channel<CapturedFlow>> _subscribers = new();
    private long _nextId;

    public CapturedFlowStore(int capacity = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    /// <summary>Maximum number of flows retained. Older flows are evicted FIFO.</summary>
    public int Capacity { get; }

    /// <summary>Allocate the next monotonic flow id. Call this before recording the request,
    /// so the workbench can correlate the half-captured "request seen" event with the eventual "response landed" event.</summary>
    public long NextId() => Interlocked.Increment(ref _nextId);

    /// <summary>Record a finished flow + fan it out to live subscribers.</summary>
    public void Add(CapturedFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        lock (_lock)
        {
            _flows.AddLast(flow);
            while (_flows.Count > Capacity)
                _flows.RemoveFirst();
        }

        // Snapshot subscribers under the lock — but write outside the lock
        // so a slow consumer cannot block fast-path capture.
        Channel<CapturedFlow>[] snapshot;
        lock (_lock)
        {
            snapshot = _subscribers.ToArray();
        }
        foreach (var ch in snapshot)
        {
            // Bounded channels w/ DropOldest below; TryWrite never blocks.
            ch.Writer.TryWrite(flow);
        }
    }

    /// <summary>Newest-first snapshot of currently retained flows.</summary>
    public IReadOnlyList<CapturedFlow> Snapshot()
    {
        lock (_lock)
        {
            var arr = new CapturedFlow[_flows.Count];
            var i = arr.Length - 1;
            foreach (var f in _flows)
                arr[i--] = f;
            return arr;
        }
    }

    /// <summary>Lookup a flow by id (newest-first scan; small N).</summary>
    public CapturedFlow? Get(long id)
    {
        lock (_lock)
        {
            for (var node = _flows.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Id == id) return node.Value;
            }
        }
        return null;
    }

    /// <summary>Drop everything (used by the workbench "Clear" button + tests).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _flows.Clear();
        }
    }

    /// <summary>
    /// Subscribe to live flow events. Returns a channel reader that
    /// yields newly-captured flows until cancellation. Slow consumers
    /// silently drop the oldest queued event instead of back-pressuring
    /// the proxy hot path.
    /// </summary>
    public ChannelReader<CapturedFlow> Subscribe(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<CapturedFlow>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        lock (_lock)
        {
            _subscribers.Add(channel);
        }
        cancellationToken.Register(() =>
        {
            lock (_lock)
            {
                _subscribers.Remove(channel);
            }
            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }
}
