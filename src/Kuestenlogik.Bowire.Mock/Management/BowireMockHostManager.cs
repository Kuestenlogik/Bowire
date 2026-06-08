// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// Tracks mock-server instances spun up by the workbench's
/// "Use as mock" one-click flow (#94). Each <see cref="StartFromJson"/>
/// allocates a fresh local port from <see cref="BasePort"/> upward,
/// writes the recording JSON to a per-host temp file, and boots a
/// <see cref="MockServer"/> against it. Caller gets a handle they can
/// reference later to stop or list active hosts.
/// <para>
/// Lifetime is tied to the workbench process: dispose this manager
/// (DI's <c>IHostedService</c> shutdown hook) and every running mock
/// host stops. We deliberately don't persist active mocks — the
/// "persistent mock" lane is owned by the <c>bowire mock</c> CLI.
/// </para>
/// </summary>
public sealed class BowireMockHostManager : IAsyncDisposable
{
    /// <summary>
    /// Starting port for the allocator. 5180 sits just above the
    /// workbench's own default 5080 + plugin range, so picked ports
    /// don't collide with the workbench / its tooling in practice.
    /// </summary>
    private const int BasePort = 5180;

    /// <summary>Maximum probes before giving up. 20 is plenty in practice.</summary>
    private const int MaxProbes = 20;

    private readonly ConcurrentDictionary<string, MockHostEntry> _entries = new();
    private int _nextPort = BasePort;
    private readonly Lock _portLock = new();

    public IReadOnlyCollection<MockHostHandle> List() =>
        _entries.Values.Select(e => e.Handle).ToArray();

    /// <summary>
    /// Boot a mock host for the supplied recording JSON. The document
    /// shape must be the same that <see cref="RecordingStore"/> writes:
    /// a single recording (not the {"recordings":[...]} envelope).
    /// </summary>
    public async Task<MockHostHandle> StartFromJson(string recordingJson, string recordingId, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recordingJson);

        // Persist the recording to a temp file the MockServer can read.
        var dir = Path.Combine(Path.GetTempPath(), "bowire-mock-hosts");
        Directory.CreateDirectory(dir);
        var mockId = Guid.NewGuid().ToString("N")[..10];
        var tempPath = Path.Combine(dir, $"{mockId}.json");
        await File.WriteAllTextAsync(tempPath, recordingJson, ct).ConfigureAwait(false);

        var port = AllocateFreePort();
        if (port < 0)
        {
            // Cleanup the temp file we wrote.
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw new IOException(
                $"No free TCP port found in the range {BasePort}..{BasePort + MaxProbes - 1}; close some mock hosts and try again.");
        }

        // CA2000: MockServer ownership transfers into the dictionary
        // entry below; DisposeAsync on StopAsync / disposal hook
        // tears it down. Suppress the false positive.
#pragma warning disable CA2000
        var server = await MockServer.StartAsync(new MockServerOptions
        {
            RecordingPath = tempPath,
            Port = port,
        }, ct).ConfigureAwait(false);
#pragma warning restore CA2000

        var handle = new MockHostHandle(
            MockId: mockId,
            RecordingId: recordingId,
            Label: label,
            Port: server.Port,
            Url: $"http://127.0.0.1:{server.Port}",
            StartedAtUtc: DateTime.UtcNow);

        _entries[mockId] = new MockHostEntry(handle, server, tempPath);
        return handle;
    }

    public async Task<bool> StopAsync(string mockId, CancellationToken ct)
    {
        if (!_entries.TryRemove(mockId, out var entry)) return false;
        try { await entry.Server.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        try { if (File.Exists(entry.TempPath)) File.Delete(entry.TempPath); } catch { /* swallow */ }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _entries.Keys.ToArray())
        {
            await StopAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Probe ports from the rolling allocator. Returns -1 if every
    /// candidate in [BasePort, BasePort+MaxProbes) is in use.
    /// </summary>
    private int AllocateFreePort()
    {
        lock (_portLock)
        {
            for (var i = 0; i < MaxProbes; i++)
            {
                var p = _nextPort++;
                if (_nextPort >= BasePort + MaxProbes) _nextPort = BasePort;
                if (IsPortFree(p)) return p;
            }
            return -1;
        }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record MockHostEntry(MockHostHandle Handle, MockServer Server, string TempPath);
}

/// <summary>
/// User-facing handle returned by the manager. Same shape the
/// workbench renders + the API exposes.
/// </summary>
public sealed record MockHostHandle(
    string MockId,
    string RecordingId,
    string Label,
    int Port,
    string Url,
    DateTime StartedAtUtc);
