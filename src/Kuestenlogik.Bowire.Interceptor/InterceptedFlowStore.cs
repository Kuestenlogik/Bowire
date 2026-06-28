// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// In-memory ring buffer of <see cref="InterceptedFlow"/>s plus a fan-out
/// channel for live subscribers (Workbench "Intercepted" rail over SSE).
/// Bounded so a long-running host with
/// <c>app.UseBowireInterceptor()</c> doesn't grow unbounded — once
/// <see cref="Capacity"/> flows are stored the oldest one is evicted before
/// the new one is added.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="Kuestenlogik.Bowire.Proxy.CapturedFlowStore"/> on
/// purpose: same TryWrite-never-blocks semantics, same DropOldest channel
/// policy, same Snapshot / Get / Subscribe surface. Two separate stores
/// (one for proxy traffic, one for in-process intercept traffic) keep the
/// rails independent — turning the interceptor on never pollutes the
/// proxy rail's snapshot, and clearing one doesn't clear the other.
/// </para>
/// <para>
/// Registered as a singleton by <see cref="BowireInterceptorMiddleware"/>'s
/// service-collection extension. When the host never opts in (no
/// <c>UseBowireInterceptor()</c> call), the singleton is still registered
/// but stays empty — the rail's "no traffic yet" empty state covers that
/// case without spending any per-request cost in the pipeline.
/// </para>
/// </remarks>
public sealed class InterceptedFlowStore
{
    private readonly object _lock = new();
    private readonly LinkedList<InterceptedFlow> _flows = new();
    private readonly List<Channel<InterceptedFlow>> _subscribers = new();
    private long _nextId;

    public InterceptedFlowStore(int capacity = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    /// <summary>Maximum number of flows retained. Older flows are evicted FIFO.</summary>
    public int Capacity { get; }

    /// <summary>
    /// Allocate the next monotonic flow id. Called before recording the
    /// request so a half-captured "request seen" event can be correlated
    /// with the eventual "response landed" event by Phase B's auto-record
    /// hook.
    /// </summary>
    public long NextId() => Interlocked.Increment(ref _nextId);

    /// <summary>Record a finished flow + fan it out to live subscribers.</summary>
    public void Add(InterceptedFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        Channel<InterceptedFlow>[] snapshot;
        lock (_lock)
        {
            _flows.AddLast(flow);
            while (_flows.Count > Capacity)
                _flows.RemoveFirst();
            snapshot = _subscribers.ToArray();
        }

        // Write outside the lock so a slow subscriber cannot back-pressure
        // the request pipeline. TryWrite respects the DropOldest policy.
        foreach (var ch in snapshot)
        {
            ch.Writer.TryWrite(flow);
        }
    }

    /// <summary>Newest-first snapshot of currently retained flows.</summary>
    public IReadOnlyList<InterceptedFlow> Snapshot()
    {
        lock (_lock)
        {
            var arr = new InterceptedFlow[_flows.Count];
            var i = arr.Length - 1;
            foreach (var f in _flows)
                arr[i--] = f;
            return arr;
        }
    }

    /// <summary>Lookup a flow by id (newest-first scan; small N).</summary>
    public InterceptedFlow? Get(long id)
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

    /// <summary>Drop everything (workbench "Clear all" button + tests).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _flows.Clear();
        }
    }

    /// <summary>
    /// Subscribe to live flow events. Returns a channel reader that yields
    /// newly-recorded flows until cancellation. Slow consumers silently
    /// drop the oldest queued event instead of back-pressuring the
    /// intercept hot path.
    /// </summary>
    public ChannelReader<InterceptedFlow> Subscribe(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<InterceptedFlow>(new BoundedChannelOptions(64)
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
