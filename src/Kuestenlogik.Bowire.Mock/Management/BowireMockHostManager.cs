// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// Single owner of every UI-spun mock server. Replaces the v1.x
/// <c>MockRegistry</c> / v2.x <c>BowireMockHostManager</c> split — there's
/// now exactly one registry behind <c>/api/mocks*</c> (#223).
/// </summary>
/// <remarks>
/// <para>Lifecycle: singleton via <c>AddBowireMockManagement()</c>;
/// disposed on host shutdown. Every <see cref="StartAsync"/> writes the
/// recording JSON to a temp file (the underlying <see cref="MockServer"/>
/// ingests via path), opens the host on a free local port, and tracks
/// the entry in a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// <see cref="StopAsync"/> tears the server down + deletes the temp file.</para>
/// <para>Concurrency: List / Start / Stop / Get can interleave freely.
/// The temp-file write is awaited before <see cref="MockServer.StartAsync"/>,
/// so a successful start always has the file on disk.</para>
/// </remarks>
public sealed class BowireMockHostManager : IAsyncDisposable
{
    /// <summary>
    /// Starting port for the auto-allocator. 5180 sits just above the
    /// workbench's own default 5080 + plugin range, so picked ports
    /// don't collide with the workbench / its tooling in practice.
    /// </summary>
    private const int BasePort = 5180;

    /// <summary>Maximum probes before giving up. 20 is plenty in practice.</summary>
    private const int MaxProbes = 20;

    private readonly ConcurrentDictionary<string, MockHostEntry> _entries = new(StringComparer.Ordinal);
    private int _nextPort = BasePort;
    private readonly Lock _portLock = new();

    /// <summary>Snapshot of every running mock. Order undefined; UI sorts by StartedAtUtc.</summary>
    public IReadOnlyCollection<MockHostHandle> List() =>
        _entries.Values.Select(e => e.Handle).ToArray();

    /// <summary>Look up a single mock by id. Returns null if not running.</summary>
    public MockHostHandle? Get(string mockId) =>
        _entries.TryGetValue(mockId, out var entry) ? entry.Handle : null;

    /// <summary>
    /// Access the request log for a running mock (#57 — Mocks panel
    /// request tail). Returns null when the mock id isn't in the
    /// registry.
    /// </summary>
    public MockRequestLog? GetRequestLog(string mockId) =>
        _entries.TryGetValue(mockId, out var entry) ? entry.RequestLog : null;

    /// <summary>
    /// Live fault-injection rules of a running mock (#170). Null when
    /// the mock id isn't in the registry.
    /// </summary>
    public Chaos.FaultRuleSet? GetFaults(string mockId) =>
        _entries.TryGetValue(mockId, out var entry) ? entry.Server.Faults : null;

    /// <summary>
    /// Swap the fault rules of a RUNNING mock (#170 — the UI editor's
    /// apply path). Atomic reference swap; false when the mock id isn't
    /// in the registry.
    /// </summary>
    public bool TrySetFaults(string mockId, Chaos.FaultRuleSet faults)
    {
        ArgumentNullException.ThrowIfNull(faults);
        if (!_entries.TryGetValue(mockId, out var entry)) return false;
        entry.Server.Faults.Rules = faults.Rules;
        return true;
    }

    // #404: per-stub CRUD on a running mock. Each delegates to the captured
    // MockHandler; null / false when the mock id isn't running.
    private MockHandler? HandlerFor(string mockId) =>
        _entries.TryGetValue(mockId, out var entry) ? entry.Server.Handler : null;

    /// <summary>List the stubs (recording steps) of a running mock.</summary>
    public IReadOnlyList<Mocking.BowireRecordingStep>? GetStubs(string mockId) => HandlerFor(mockId)?.ListStubs();

    /// <summary>Get a single stub by id.</summary>
    public Mocking.BowireRecordingStep? GetStub(string mockId, string stubId) => HandlerFor(mockId)?.GetStub(stubId);

    /// <summary>Add a stub to a running mock. Null when the mock isn't running.</summary>
    public Mocking.BowireRecordingStep? AddStub(string mockId, Mocking.BowireRecordingStep stub) => HandlerFor(mockId)?.AddStub(stub);

    /// <summary>Replace a stub by id. False when the mock or stub isn't found.</summary>
    public bool UpdateStub(string mockId, string stubId, Mocking.BowireRecordingStep stub) =>
        HandlerFor(mockId)?.UpdateStub(stubId, stub) ?? false;

    /// <summary>Remove a stub by id. False when the mock or stub isn't found.</summary>
    public bool RemoveStub(string mockId, string stubId) => HandlerFor(mockId)?.RemoveStub(stubId) ?? false;

    /// <summary>Restore a running mock's stubs to its baseline recording. False when not running.</summary>
    public bool ResetStubs(string mockId)
    {
        var handler = HandlerFor(mockId);
        if (handler is null) return false;
        handler.ResetStubs();
        return true;
    }

    /// <summary>
    /// Boot a mock host for the supplied recording JSON.
    /// </summary>
    /// <param name="recordingJson">Single recording document (NOT the
    /// {"recordings":[...]} envelope).</param>
    /// <param name="recordingId">Stable id of the source recording (so
    /// the workbench can correlate the running mock back to the
    /// recording that produced it). Empty when the start came from an
    /// embedded host that passed a recording payload directly.</param>
    /// <param name="label">Display label (recording name or operator-
    /// supplied alias).</param>
    /// <param name="port">Requested port. 0 = use the rolling
    /// allocator; any positive value pins the mock to that port (and
    /// fails the call if the port is busy).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MockHostHandle> StartAsync(
        string recordingJson,
        string recordingId,
        string label,
        int port,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recordingJson);

        // Persist the recording to a temp file the MockServer can read.
        var dir = Path.Combine(Path.GetTempPath(), "bowire-mock-hosts");
        Directory.CreateDirectory(dir);
        var mockId = Guid.NewGuid().ToString("N")[..12];
        var tempPath = Path.Combine(dir, $"{mockId}.json");
        await File.WriteAllTextAsync(tempPath, recordingJson, ct).ConfigureAwait(false);

        var resolvedPort = port > 0 ? port : AllocateFreePort();
        if (resolvedPort < 0)
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw new IOException(
                $"No free TCP port found in the range {BasePort}..{BasePort + MaxProbes - 1}; close some mock hosts and try again.");
        }

        // #57 — per-mock request log, fed by MockServer via the
        // IMockRequestObserver seam.
        var requestLog = new MockRequestLog();

        // CA2000: MockServer ownership transfers into the dictionary
        // entry below; StopAsync / DisposeAsync tears it down.
#pragma warning disable CA2000
        MockServer server;
        try
        {
            server = await MockServer.StartAsync(new MockServerOptions
            {
                RecordingPath = tempPath,
                Host = "127.0.0.1",
                Port = resolvedPort,
                Watch = false,
                RequestObserver = requestLog,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
#pragma warning restore CA2000

        var handle = new MockHostHandle(
            MockId: mockId,
            RecordingId: recordingId ?? string.Empty,
            Label: string.IsNullOrWhiteSpace(label) ? "unnamed" : label,
            Port: server.Port,
            Url: $"http://127.0.0.1:{server.Port}",
            StartedAtUtc: DateTime.UtcNow);

        _entries[mockId] = new MockHostEntry(handle, server, tempPath, requestLog);
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

    private sealed record MockHostEntry(
        MockHostHandle Handle,
        MockServer Server,
        string TempPath,
        MockRequestLog RequestLog);
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
