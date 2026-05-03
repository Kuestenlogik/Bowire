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
    private readonly ConcurrentDictionary<string, MockServer> _handles = new();

    public string Register(MockServer server)
    {
        var handle = Guid.NewGuid().ToString("N")[..12];
        _handles[handle] = server;
        return handle;
    }

    public bool TryGet(string handle, out MockServer? server)
    {
        if (_handles.TryGetValue(handle, out var s)) { server = s; return true; }
        server = null;
        return false;
    }

    public IReadOnlyDictionary<string, MockServer> Snapshot() => _handles;

    public async Task<bool> RemoveAndDisposeAsync(string handle)
    {
        if (!_handles.TryRemove(handle, out var server)) return false;
        await server.DisposeAsync().ConfigureAwait(false);
        return true;
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
