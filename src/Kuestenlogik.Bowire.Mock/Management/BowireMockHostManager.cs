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

    // #560: plugin-contributed schema sources + live handlers + hosting
    // extensions so the workbench can start a schema-only mock the SAME way
    // `bowire mock --schema` does (MockCommand feeds all three into
    // MockServerOptions). Empty for embedded hosts that only run
    // recording-driven mocks.
    private readonly IReadOnlyList<Mocking.IBowireMockSchemaSource> _schemaSources;
    private readonly IReadOnlyList<Mocking.IBowireMockLiveSchemaHandler> _liveSchemaHandlers;
    private readonly IReadOnlyList<Mocking.IBowireMockHostingExtension> _hostingExtensions;

    /// <summary>Recording-only manager (the embedded default). No schema-mock start.</summary>
    public BowireMockHostManager()
        : this(Array.Empty<Mocking.IBowireMockSchemaSource>(),
               Array.Empty<Mocking.IBowireMockLiveSchemaHandler>(),
               Array.Empty<Mocking.IBowireMockHostingExtension>())
    {
    }

    /// <summary>
    /// Manager wired with plugin-contributed schema sources, live handlers, and
    /// hosting extensions so <see cref="StartFromSchemaAsync"/> can spin up an
    /// OpenAPI / protobuf / GraphQL schema mock at CLI parity — the hosting
    /// extensions carry the gRPC reflection + REST schema-discovery endpoints a
    /// schema mock needs to be reachable. The standalone workbench host passes
    /// what it enumerated from the plugin loader.
    /// </summary>
    public BowireMockHostManager(
        IReadOnlyList<Mocking.IBowireMockSchemaSource> schemaSources,
        IReadOnlyList<Mocking.IBowireMockLiveSchemaHandler> liveSchemaHandlers,
        IReadOnlyList<Mocking.IBowireMockHostingExtension> hostingExtensions)
    {
        _schemaSources = schemaSources ?? Array.Empty<Mocking.IBowireMockSchemaSource>();
        _liveSchemaHandlers = liveSchemaHandlers ?? Array.Empty<Mocking.IBowireMockLiveSchemaHandler>();
        _hostingExtensions = hostingExtensions ?? Array.Empty<Mocking.IBowireMockHostingExtension>();
    }

    /// <summary>Schema kinds this manager can start (empty when no sources are wired).</summary>
    public IReadOnlyCollection<string> SchemaKinds =>
        _schemaSources.Select(s => s.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static readonly string[] SupportedSchemaKinds = ["openapi", "protobuf", "graphql"];

    /// <summary>Is <paramref name="kind"/> one of the schema kinds a schema mock accepts (case-insensitive)?</summary>
    public static bool IsSupportedSchemaKind(string? kind) =>
        !string.IsNullOrWhiteSpace(kind)
        && SupportedSchemaKinds.Contains(kind.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string CanonicalKind(string kind) =>
        SupportedSchemaKinds.First(k => string.Equals(k, kind.Trim(), StringComparison.OrdinalIgnoreCase));

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
    /// #561: re-apply a mock configuration to a RUNNING mock. Recomputes the
    /// stub set from the baseline — field overrides mutate the base responses,
    /// each conditional rule becomes a higher-priority match stub — and swaps
    /// it in live. Recomputing from the baseline means re-applying an edited
    /// config never compounds on a previous apply. False when not running.
    /// </summary>
    public bool ApplyConfig(string mockId, Mocking.MockConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var handler = HandlerFor(mockId);
        if (handler is null) return false;
        var stubs = Mocking.MockConfigApplier.ApplyToStubs(handler.BaselineStubs(), config);
        handler.ReplaceStubs(stubs);
        return true;
    }

    // #408: named-scenario state on a running mock.

    /// <summary>Current state of every scenario in a running mock (name → state). Null when not running.</summary>
    public IReadOnlyDictionary<string, string>? GetScenarioStates(string mockId) => HandlerFor(mockId)?.GetScenarioStates();

    /// <summary>Force a scenario to a state. False when the mock isn't running or the scenario is unknown.</summary>
    public bool SetScenarioState(string mockId, string name, string state) =>
        HandlerFor(mockId)?.SetScenarioState(name, state) ?? false;

    /// <summary>Reset all scenarios to Started. False when not running.</summary>
    public bool ResetScenarios(string mockId)
    {
        var handler = HandlerFor(mockId);
        if (handler is null) return false;
        handler.ResetScenarios();
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
        var options = new MockServerOptions
        {
            RecordingPath = tempPath,
            Host = "127.0.0.1",
            Port = resolvedPort,
            Watch = false,
            RequestObserver = requestLog,
        };

        return await StartServerAndTrackAsync(
            mockId, options, recordingId, label, tempPath, requestLog, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// #560: boot a schema-only mock — the workbench equivalent of
    /// <c>bowire mock --schema / --grpc-schema / --graphql-schema</c>. The
    /// schema is supplied inline (<paramref name="schemaInline"/>, written to
    /// a temp file) or as an existing file path (<paramref name="schemaPath"/>).
    /// Requires the manager to have been wired with the matching
    /// <see cref="Mocking.IBowireMockSchemaSource"/>.
    /// </summary>
    /// <param name="schemaKind"><c>openapi</c>, <c>protobuf</c>, or <c>graphql</c>.</param>
    /// <param name="schemaInline">Inline schema text (OpenAPI YAML/JSON or GraphQL SDL). Mutex with <paramref name="schemaPath"/>.</param>
    /// <param name="schemaPath">Path to an existing schema file (any kind, incl. a binary protobuf FileDescriptorSet). Mutex with <paramref name="schemaInline"/>.</param>
    /// <param name="label">Display label.</param>
    /// <param name="port">Requested port; 0 = rolling allocator.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MockHostHandle> StartFromSchemaAsync(
        string schemaKind,
        string? schemaInline,
        string? schemaPath,
        string label,
        int port,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaKind);
        if (!IsSupportedSchemaKind(schemaKind))
        {
            throw new ArgumentException(
                $"Unknown schema kind '{schemaKind}'. Use openapi, protobuf, or graphql.", nameof(schemaKind));
        }
        var kind = CanonicalKind(schemaKind);
        if (!_schemaSources.Any(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"No mock schema source is registered for '{kind}'. The host must be wired with the matching protocol plugin (Protocol.Rest / Protocol.Grpc / Protocol.GraphQL).");
        }

        var usePath = !string.IsNullOrWhiteSpace(schemaPath);
        if (!usePath && string.IsNullOrWhiteSpace(schemaInline))
        {
            throw new ArgumentException(
                "A schema mock needs either schemaInline or schemaPath.", nameof(schemaInline));
        }

        var mockId = Guid.NewGuid().ToString("N")[..12];
        string schemaFile;
        string tempPath = string.Empty; // non-empty only when we own the temp file
        if (usePath)
        {
            schemaFile = schemaPath!;
        }
        else
        {
            var dir = Path.Combine(Path.GetTempPath(), "bowire-mock-hosts");
            Directory.CreateDirectory(dir);
            tempPath = Path.Combine(dir, mockId + ExtensionFor(kind));
            await File.WriteAllTextAsync(tempPath, schemaInline!, ct).ConfigureAwait(false);
            schemaFile = tempPath;
        }

        var resolvedPort = port > 0 ? port : AllocateFreePort();
        if (resolvedPort < 0)
        {
            TryDeleteTemp(tempPath);
            throw new IOException(
                $"No free TCP port found in the range {BasePort}..{BasePort + MaxProbes - 1}; close some mock hosts and try again.");
        }

        var requestLog = new MockRequestLog();
        var options = new MockServerOptions
        {
            Host = "127.0.0.1",
            Port = resolvedPort,
            Watch = false,
            RequestObserver = requestLog,
            SchemaSources = _schemaSources,
            LiveSchemaHandlers = _liveSchemaHandlers,
            HostingExtensions = _hostingExtensions,
            SchemaPath = kind == "openapi" ? schemaFile : null,
            GrpcSchemaPath = kind == "protobuf" ? schemaFile : null,
            GraphQlSchemaPath = kind == "graphql" ? schemaFile : null,
        };

        return await StartServerAndTrackAsync(
            mockId, options, recordingId: string.Empty,
            string.IsNullOrWhiteSpace(label) ? $"{kind}-mock" : label,
            tempPath, requestLog, ct).ConfigureAwait(false);
    }

    // The single place MockServer ownership transfers into the registry.
    // CA2000 can't see the hand-off into _entries (StopAsync / DisposeAsync
    // owns teardown), so the suppression lives here alone and both start
    // paths funnel through it. tempPath is "" when the server reads an
    // operator-owned file we must not delete.
    private async Task<MockHostHandle> StartServerAndTrackAsync(
        string mockId,
        MockServerOptions options,
        string recordingId,
        string label,
        string tempPath,
        MockRequestLog requestLog,
        CancellationToken ct)
    {
#pragma warning disable CA2000
        MockServer server;
        try
        {
            server = await MockServer.StartAsync(options, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteTemp(tempPath);
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

    private static string ExtensionFor(string kind) => kind switch
    {
        "openapi" => ".yaml",
        "graphql" => ".graphql",
        "protobuf" => ".pb",
        _ => ".txt",
    };

    private static void TryDeleteTemp(string tempPath)
    {
        if (string.IsNullOrEmpty(tempPath)) return;
        try { File.Delete(tempPath); } catch { /* ignore */ }
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
