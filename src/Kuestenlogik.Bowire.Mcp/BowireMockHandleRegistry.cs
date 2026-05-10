// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Kuestenlogik.Bowire.Mock;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// In-memory registry of <see cref="MockServer"/> instances spawned by
/// <c>bowire.mock.start</c>. The MCP host is the owner — handles live
/// for the duration of the host process. <c>bowire.mock.stop</c> looks
/// up and disposes; <c>bowire.mock.list</c> enumerates.
///
/// <para>
/// Registered as a singleton in <see cref="BowireMcpServiceCollectionExtensions.AddBowireMcp"/>
/// so the dictionary is shared across the tool class's lifetime.
/// </para>
/// </summary>
public sealed class BowireMockHandleRegistry : IAsyncDisposable
{
    // Stored as IAsyncDisposable so the dispose-loop's catch-all is
    // reachable from tests via RegisterRaw (MockServer is sealed and
    // never throws on dispose, so we can't otherwise exercise the
    // fault path). Public surface still operates on MockServer — the
    // typed accessors filter with `as MockServer`.
    private readonly ConcurrentDictionary<string, IAsyncDisposable> _handles = new();

    public string Register(MockServer server)
    {
        var handle = Guid.NewGuid().ToString("N")[..12];
        _handles[handle] = server;
        return handle;
    }

    public bool TryGet(string handle, out MockServer? server)
    {
        if (_handles.TryGetValue(handle, out var s) && s is MockServer ms)
        {
            server = ms;
            return true;
        }
        server = null;
        return false;
    }

    public IReadOnlyDictionary<string, MockServer> Snapshot()
    {
        var result = new Dictionary<string, MockServer>(_handles.Count);
        foreach (var kvp in _handles)
        {
            if (kvp.Value is MockServer ms) result[kvp.Key] = ms;
        }
        return result;
    }

    public async Task<bool> RemoveAndDisposeAsync(string handle)
    {
        if (!_handles.TryRemove(handle, out var server)) return false;
        await server.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Test-only seam: register an arbitrary <see cref="IAsyncDisposable"/>
    /// (e.g. a stub that throws on dispose) so <see cref="DisposeAsync"/>'s
    /// catch-all path can be exercised. Production callers use
    /// <see cref="Register(MockServer)"/>.
    /// </summary>
    internal string RegisterRaw(IAsyncDisposable disposable)
    {
        var handle = Guid.NewGuid().ToString("N")[..12];
        _handles[handle] = disposable;
        return handle;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _handles.ToArray())
        {
            // CA2000 false-positive: this *is* the dispose path.
#pragma warning disable CA2000
            if (_handles.TryRemove(kvp.Key, out var server))
#pragma warning restore CA2000
            {
                try { await server.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort shutdown; agent is going away */ }
            }
        }
    }
}
