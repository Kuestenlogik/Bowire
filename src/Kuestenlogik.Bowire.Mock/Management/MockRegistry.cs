// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// In-host registry of running <see cref="MockServer"/> instances. The
/// CLI's <c>bowire mock</c> command spins up one mock per process; the
/// workbench needs many — one per recording the user clicks "Run as
/// mock" on — and a single place to enumerate them for the Mocks
/// panel. <see cref="MockRegistry"/> is that place.
/// </summary>
/// <remarks>
/// <para>Lifecycle: registered as a singleton via
/// <c>AddBowire()</c>, disposed when the host shuts down. Each
/// <see cref="StartAsync"/> writes the recording payload to a temp
/// file under <c>~/.bowire/mocks/&lt;mockId&gt;.bwr</c> (MockServer's
/// only ingest is a file path); <see cref="StopAsync"/> tears the
/// server down and deletes the temp file.</para>
/// <para>Concurrency: backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// — Start/Stop/List can interleave freely. The temp-file write is
/// awaited before MockServer.StartAsync, so a successful start always
/// has the file on disk.</para>
/// </remarks>
internal sealed class MockRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, MockInstance> _mocks = new(StringComparer.Ordinal);
    private readonly ILogger<MockRegistry> _logger;
    private readonly string _mocksDir;

    public MockRegistry(ILogger<MockRegistry> logger)
    {
        _logger = logger;
        _mocksDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bowire", "mocks");
    }

    /// <summary>
    /// Snapshot of every mock currently running in this host. Order is
    /// undefined (ConcurrentDictionary); UI should sort by StartedAt.
    /// </summary>
    public IReadOnlyCollection<MockInstance> List() =>
        _mocks.Values.ToArray();

    public MockInstance? Get(string mockId) =>
        _mocks.TryGetValue(mockId, out var inst) ? inst : null;

    /// <summary>
    /// Start a new mock from an in-memory recording document. Writes
    /// the payload to a temp <c>.bwr</c> file so MockServer can ingest
    /// it via its existing path-based API.
    /// </summary>
    public async Task<MockInstance> StartAsync(
        string recordingJson,
        string recordingDisplayName,
        int port,
        CancellationToken ct)
    {
        var mockId = Guid.NewGuid().ToString("N")[..12];
        Directory.CreateDirectory(_mocksDir);
        var bwrPath = Path.Combine(_mocksDir, $"{mockId}.bwr");
        await File.WriteAllTextAsync(bwrPath, recordingJson, ct);

        MockServer? server = null;
        try
        {
            var opts = new MockServerOptions
            {
                RecordingPath = bwrPath,
                Host = "127.0.0.1",
                Port = port,
                Watch = false, // watching makes no sense for UI-started mocks
            };
            // CA2000 fires here because the analyzer can't see that the
            // returned MockServer's lifetime moves into _mocks (and from
            // there into MockRegistry.DisposeAsync / StopAsync). The
            // catch below handles the not-yet-handed-off path; once
            // _mocks.TryAdd wins, we set `server = null` and the catch
            // becomes a no-op for the success case.
#pragma warning disable CA2000
            server = await MockServer.StartAsync(opts, ct);
#pragma warning restore CA2000
            var instance = new MockInstance(
                mockId,
                recordingDisplayName,
                server,
                server.Port, // resolves OS-assigned port when port=0
                DateTimeOffset.UtcNow,
                bwrPath);
            if (!_mocks.TryAdd(mockId, instance))
            {
                // GUID collision is functionally impossible but be defensive.
                throw new InvalidOperationException($"Mock id collision: {mockId}");
            }
            _logger.LogInformation(
                "Mock {MockId} started for {Recording} on port {Port}",
                mockId, recordingDisplayName, instance.Port);
            server = null; // ownership transferred to _mocks
            return instance;
        }
        catch
        {
            // Either MockServer.StartAsync threw (server stays null), or
            // a later step threw with `server` already set. The CA1508
            // analyzer can't see the latter branch and tags this as
            // dead code -- suppress because the dispose IS reachable
            // when TryAdd fails (which throws after server was assigned).
            var orphan = server;
#pragma warning disable CA1508
            if (orphan is not null)
            {
                await orphan.DisposeAsync();
            }
#pragma warning restore CA1508
            try { File.Delete(bwrPath); } catch { /* ignored */ }
            throw;
        }
    }

    public async Task<bool> StopAsync(string mockId)
    {
        if (!_mocks.TryRemove(mockId, out var instance))
            return false;

        await instance.Server.DisposeAsync();
        try { File.Delete(instance.BwrPath); } catch { /* ignored */ }
        _logger.LogInformation("Mock {MockId} stopped", mockId);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _mocks.Keys.ToArray())
        {
            await StopAsync(key);
        }
    }
}

/// <summary>One running mock — the registry's entry shape.</summary>
internal sealed record MockInstance(
    string MockId,
    string RecordingDisplayName,
    MockServer Server,
    int Port,
    DateTimeOffset StartedAt,
    string BwrPath);
