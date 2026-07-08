// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Mock;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// MCP tool surface for Bowire. Each method is one tool the agent sees;
/// parameter descriptions surface as JSON-Schema descriptions in
/// <c>tools/list</c>. The class is registered with the SDK via
/// <c>WithTools&lt;BowireMcpTools&gt;()</c> in
/// <see cref="BowireMcpServiceCollectionExtensions"/>.
///
/// <para>
/// Three families of tool live here:
/// </para>
/// <list type="bullet">
///   <item>Live calls (<c>discover</c>, <c>invoke</c>, <c>subscribe</c>)
///         hit the loaded protocol plugins directly via
///         <see cref="BowireProtocolRegistry"/>.</item>
///   <item>State queries (<c>env.list</c>, <c>record.list</c>) read the
///         on-disk Bowire files under <c>~/.bowire/</c>.</item>
///   <item>Diagnostic (<c>allowlist.show</c>) — surfaces the current
///         security configuration so an agent can self-debug "why is my
///         invoke failing?".</item>
/// </list>
/// </summary>
[McpServerToolType]
public sealed class BowireMcpTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    { PropertyNameCaseInsensitive = true, WriteIndented = false };

    private readonly BowireProtocolRegistry _registry;
    private readonly BowireMockHandleRegistry _mockHandles;
    private readonly BowireMcpConfirmationStore _confirmations;
    private readonly BowireRecordingSession _recordingSession;
    private readonly BowireMcpOptions _options;
    private readonly ILogger<BowireMcpTools> _logger;

    public BowireMcpTools(
        BowireProtocolRegistry registry,
        BowireMockHandleRegistry mockHandles,
        BowireMcpConfirmationStore confirmations,
        BowireRecordingSession recordingSession,
        IOptions<BowireMcpOptions> options,
        ILogger<BowireMcpTools> logger)
    {
        _registry = registry;
        _mockHandles = mockHandles;
        _confirmations = confirmations;
        _recordingSession = recordingSession;
        _options = options.Value;
        _logger = logger;

        // Seeding is idempotent on repeated DI activations because the
        // helper checks `seen` before adding. Tool-class lifetime is
        // singleton via the SDK's registration, so this runs once.
        if (_options.LoadAllowlistFromEnvironments)
        {
            try { SeedAllowlistFromEnvironments(_options); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Couldn't seed allowlist from environments.json; continuing with the explicit list only.");
            }
        }

        // --allow-invoke: also seed from the typed-URL history. Strictly
        // additive; never narrows the env-seeded allowlist. Failures here
        // are silent so a missing or corrupt file doesn't break MCP startup.
        if (_options.LoadAllowlistFromTypedUrls)
        {
            try { SeedAllowlistFromTypedUrls(_options); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Couldn't seed allowlist from typed-urls.json; continuing with the explicit list only.");
            }
        }
    }

    [McpServerTool(Name = "bowire.discover")]
    [Description("Run discovery against a server URL and return the discovered services and methods. Optional `protocol` filters to one plugin (grpc, rest, graphql, signalr, mqtt, ws, sse, mcp, odata, socketio); without it every registered protocol that handles the URL gets a turn.")]
    public async Task<string> Discover(
        [Description("Server URL to discover (must be on the allowlist unless arbitrary URLs are allowed).")] string url,
        [Description("Optional protocol id (grpc, rest, graphql, signalr, mqtt, ws, sse, mcp, odata, socketio).")] string? protocol = null,
        CancellationToken ct = default)
    {
        if (!IsUrlAllowed(url)) return AllowlistDeniedMessage(url);

        var protocols = SelectProtocols(protocol);
        if (protocols.Count == 0)
            return $"No matching protocol plugin{(protocol is null ? "" : $" for id \"{protocol}\"")}.";

        var collected = new List<object>();
        foreach (var plugin in protocols)
        {
            try
            {
                var services = await plugin.DiscoverAsync(url, false, ct).ConfigureAwait(false);
                foreach (var svc in services)
                {
                    collected.Add(new
                    {
                        protocol = plugin.Id,
                        service = svc.Name,
                        methods = svc.Methods.Select(mi => new
                        {
                            name = mi.Name,
                            type = mi.MethodType.ToString(),
                            input = mi.InputType,
                            output = mi.OutputType
                        }).ToArray()
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Discovery via {Protocol} failed for {Url}", plugin.Id, url);
            }
        }

        return JsonSerializer.Serialize(new { url, services = collected }, JsonOpts);
    }

    [McpServerTool(Name = "bowire.invoke")]
    [Description("Invoke a unary method discovered via bowire.discover. body is a JSON string; metadata is a flat object of header / gRPC metadata key-value pairs.")]
    public async Task<string> Invoke(
        [Description("Server URL.")] string url,
        [Description("Service name.")] string service,
        [Description("Method name.")] string method,
        [Description("Optional protocol id; defaults to the first registered plugin.")] string? protocol = null,
        [Description("JSON request body (default: \"{}\").")] string body = "{}",
        [Description("Optional headers / gRPC metadata as a flat string-to-string map.")] Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        if (!IsUrlAllowed(url)) return AllowlistDeniedMessage(url);

        var plugin = ResolveProtocol(protocol);
        if (plugin is null) return "No protocol plugin matched.";

        var result = await plugin.InvokeAsync(
            url, service, method, [body], false, metadata, ct).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            protocol = plugin.Id,
            service,
            method,
            status = result.Status,
            durationMs = result.DurationMs,
            response = result.Response
        }, JsonOpts);
    }

    [McpServerTool(Name = "bowire.subscribe")]
    [Description("Subscribe to a streaming method (server-streaming, SSE, MQTT topic, websocket) and return collected frames after the sample window elapses. Frames are returned as raw strings; the agent decodes them in follow-up reasoning.")]
    public async Task<string> Subscribe(
        [Description("Server URL.")] string url,
        [Description("Service name.")] string service,
        [Description("Method name (or topic / channel name in pub-sub protocols).")] string method,
        [Description("Optional protocol id.")] string? protocol = null,
        [Description("Optional JSON subscribe payload.")] string body = "{}",
        [Description("Optional headers / metadata.")] Dictionary<string, string>? metadata = null,
        [Description("Sample window in ms; clamped to MaxSubscribeMs (default 30000).")] int? durationMs = null,
        CancellationToken ct = default)
    {
        if (!IsUrlAllowed(url)) return AllowlistDeniedMessage(url);

        var plugin = ResolveProtocol(protocol);
        if (plugin is null) return "No protocol plugin matched.";

        var window = Math.Min(durationMs ?? _options.DefaultSubscribeMs, _options.MaxSubscribeMs);
        var frames = new List<string>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        try
        {
            await foreach (var frame in plugin.InvokeStreamAsync(
                url, service, method, [body], false, metadata, cts.Token).ConfigureAwait(false))
            {
                frames.Add(frame);
                if (frames.Count >= _options.MaxSubscribeFrames) break;
            }
        }
        catch (OperationCanceledException) { /* sample window elapsed */ }

        return JsonSerializer.Serialize(new
        {
            protocol = plugin.Id,
            service,
            method,
            frameCount = frames.Count,
            frames,
            durationMs = window,
            truncated = frames.Count >= _options.MaxSubscribeFrames
        }, JsonOpts);
    }

    [McpServerTool(Name = "bowire.env.list")]
    [Description("List Bowire environments stored under ~/.bowire/environments.json — names, server URLs, variables.")]
    public static string EnvList()
    {
        var path = BowireConfigPath("environments.json");
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { path, environments = Array.Empty<object>() }, JsonOpts);
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            return JsonSerializer.Serialize(new { path, environments = doc.RootElement.Clone() }, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"Failed to read environments.json: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bowire.record.list")]
    [Description("List Bowire recordings stored under ~/.bowire/recordings.json — id, name, step count, captured at. Step bodies omitted; ask for details via the (planned) record.replay tool.")]
    public static string RecordList()
    {
        var path = BowireConfigPath("recordings.json");
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { path, recordings = Array.Empty<object>() }, JsonOpts);
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            // On-disk shape is the wrapper { "recordings": [ ... ] } —
            // pre-2026 builds of this tool wrongly expected a bare array
            // and silently returned empty; fall back to that branch for
            // the rare malformed file just in case.
            JsonElement array;
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("recordings", out var wrapped)
                && wrapped.ValueKind == JsonValueKind.Array)
            {
                array = wrapped;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                array = doc.RootElement;
            }
            else
            {
                return JsonSerializer.Serialize(new { path, recordings = Array.Empty<object>() }, JsonOpts);
            }

            var summary = new List<object>();
            foreach (var rec in array.EnumerateArray())
            {
                summary.Add(new
                {
                    id = rec.TryGetProperty("id", out var i) ? i.GetString() : null,
                    name = rec.TryGetProperty("name", out var n) ? n.GetString() : null,
                    createdAt = rec.TryGetProperty("createdAt", out var c)
                        ? (c.ValueKind == JsonValueKind.Number ? c.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : c.GetString())
                        : null,
                    stepCount = rec.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0
                });
            }
            return JsonSerializer.Serialize(new { path, recordings = summary }, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"Failed to read recordings.json: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bowire.har.import")]
    [Description("Convert a HAR 1.2 trace into a Bowire recording. Reads the HAR file at `harPath`, optionally writes the recording JSON to `outPath` (use \"-\" or omit to return the JSON inline), and returns a summary { recordingId, stepCount, authHeaders, redacted, outPath?, recording? }. Set `redactSecrets` to strip credential headers (Authorization, Cookie, X-Api-Key, …) before import. Pair with bowire.mock.start { recording: <outPath> } to serve the trace back.")]
    public static async Task<string> HarImport(
        [Description("Path to a HAR 1.2 document on disk.")] string harPath,
        [Description("Optional output path for the resulting recording JSON. Use \"-\" or omit to return the recording inline in the response.")] string? outPath = null,
        [Description("Optional recording name. Defaults to the HAR's creator name or \"Imported HAR\".")] string? name = null,
        [Description("When true, strip credential-bearing header values (Authorization, Cookie, X-Api-Key, …) before import so live tokens/cookies aren't persisted. Default false.")] bool redactSecrets = false)
    {
        if (string.IsNullOrWhiteSpace(harPath))
            return "bowire.har.import: harPath is required.";
        if (!File.Exists(harPath))
            return $"bowire.har.import: HAR file not found at {harPath}.";

        BowireRecording recording;
        IReadOnlyList<string> authHeaders;
        try
        {
            var content = await File.ReadAllTextAsync(harPath).ConfigureAwait(false);
            authHeaders = BowireHarConverter.DetectAuthHeaders(content);
            recording = BowireHarConverter.Convert(content, name, redactSecrets);
        }
        catch (BowireHarImportException ex)
        {
            return $"bowire.har.import: HAR import failed — {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"bowire.har.import: unexpected failure — {ex.Message}";
        }

        var recordingJson = JsonSerializer.Serialize(recording, JsonOpts);
        if (string.IsNullOrEmpty(outPath) || outPath == "-")
        {
            return JsonSerializer.Serialize(new
            {
                recordingId = recording.Id,
                stepCount = recording.Steps.Count,
                authHeaders,
                redacted = redactSecrets,
                recording = JsonDocument.Parse(recordingJson).RootElement
            }, JsonOpts);
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(outPath, recordingJson).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            recordingId = recording.Id,
            stepCount = recording.Steps.Count,
            authHeaders,
            redacted = redactSecrets,
            outPath = Path.GetFullPath(outPath)
        }, JsonOpts);
    }

    [McpServerTool(Name = "bowire.assert")]
    [Description("Append a test assertion onto a step inside an on-disk recording. Targets either ~/.bowire/recordings.json (when recordingId is set) or the supplied recordingPath. The assertion shape matches the Newman-style probe Bowire runs after replay: { path, op, expected }. Supported ops: eq, ne, gt, gte, lt, lte, contains, matches, exists, notexists, type. Use path=\"status\" to assert on the HTTP/gRPC status name; otherwise path is a JSONPath rooted at the response body. Returns the assertion id + the step it was attached to.")]
    public static async Task<string> Assert(
        [Description("0-based index of the step inside the recording.")] int stepIndex,
        [Description("Assertion path. Use \"status\" for the status-name slot, or a JSONPath like \"$.users[0].id\" for a response field.")] string path,
        [Description("Operator: eq, ne, gt, gte, lt, lte, contains, matches, exists, notexists, type.")] string op,
        [Description("Expected value. For exists / notexists / type the value semantics are op-specific (type takes a string like \"number\").")] JsonElement expected,
        [Description("Recording id to target — looked up inside ~/.bowire/recordings.json. Mutually exclusive with recordingPath.")] string? recordingId = null,
        [Description("Path to a stand-alone recording JSON file to mutate. Mutually exclusive with recordingId.")] string? recordingPath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "bowire.assert: path is required (use \"status\" or a JSONPath).";
        if (string.IsNullOrWhiteSpace(op))
            return "bowire.assert: op is required.";

        if (!IsKnownAssertOp(op))
            return $"bowire.assert: unknown op \"{op}\". Supported: {string.Join(", ", KnownAssertOps)}.";

        var hasId = !string.IsNullOrWhiteSpace(recordingId);
        var hasPath = !string.IsNullOrWhiteSpace(recordingPath);
        if (hasId == hasPath)
            return "bowire.assert: provide exactly one of recordingId or recordingPath.";

        // Resolve the target file + recording — defaults to the
        // ~/.bowire/recordings.json wrapper, but a stand-alone recording
        // path is also legal (HAR imports + the CLI all write to that
        // shape).
        var targetFile = hasPath ? Path.GetFullPath(recordingPath!) : BowireConfigPath("recordings.json");
        if (!File.Exists(targetFile))
            return $"bowire.assert: file not found at {targetFile}.";

        JsonNode? root;
        try
        {
            var content = await File.ReadAllTextAsync(targetFile).ConfigureAwait(false);
            root = JsonNode.Parse(content);
        }
        catch (Exception ex)
        {
            return $"bowire.assert: failed to parse {targetFile} — {ex.Message}";
        }

        // Locate the recording node we'll mutate. Either the bare
        // recording (when the file is one recording) or the wrapper's
        // entry under "recordings".
        JsonNode? recording = null;
        if (hasPath)
        {
            recording = root;
        }
        else if (root?["recordings"] is JsonArray arr)
        {
            foreach (var r in arr)
            {
                if (r?["id"]?.GetValue<string>() == recordingId)
                {
                    recording = r;
                    break;
                }
            }
        }

        if (recording is null)
            return hasId
                ? $"bowire.assert: no recording with id \"{recordingId}\" in {targetFile}."
                : $"bowire.assert: {targetFile} is not a recording document.";

        if (recording["steps"] is not JsonArray steps)
            return "bowire.assert: target recording has no \"steps\" array.";
        if (stepIndex < 0 || stepIndex >= steps.Count)
            return $"bowire.assert: stepIndex {stepIndex} is out of range (recording has {steps.Count} step{(steps.Count == 1 ? "" : "s")}).";

        // The step's assertions array gets created on first call. The id
        // format mirrors the workbench's nextTestId() in test-assertions.js
        // so a recording exported from the workbench and one mutated via
        // MCP look indistinguishable downstream.
        var step = steps[stepIndex]!;
        if (step["assertions"] is not JsonArray assertions)
        {
            assertions = new JsonArray();
            step["assertions"] = assertions;
        }

        var assertionId = $"t_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}_{Guid.NewGuid().ToString("N")[..4]}";
        var assertion = new JsonObject
        {
            ["id"] = assertionId,
            ["path"] = path,
            ["op"] = op,
            // Clone via re-parse so the JsonElement we received from MCP
            // is detached from its owning document and safely embeddable
            // inside the JsonNode tree.
            ["expected"] = JsonNode.Parse(expected.GetRawText())
        };
        assertions.Add(assertion);

        try
        {
            await File.WriteAllTextAsync(targetFile,
                root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"bowire.assert: failed to write {targetFile} — {ex.Message}";
        }

        return JsonSerializer.Serialize(new
        {
            assertionId,
            stepIndex,
            stepId = step["id"]?.GetValue<string>(),
            recordingId = hasId ? recordingId : recording["id"]?.GetValue<string>(),
            file = targetFile,
            assertionCount = assertions.Count
        }, JsonOpts);
    }

    [McpServerTool(Name = "bowire.allowlist.permit")]
    [Description("Record that a URL is now allowed for bowire.invoke / bowire.subscribe — appends to ~/.bowire/typed-urls.json (the same file --allow-invoke seeds from) and adds the URL to the in-memory allowlist for the rest of this session. Use to surface \"the user just typed this URL into the workbench\" without re-reading the file. Returns whether the URL was new + the resulting list size.")]
    public string AllowlistPermit(
        [Description("Server URL to allow.")] string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "bowire.allowlist.permit: url is required.";

        bool addedToFile = false;
        try { addedToFile = BowireMcpTypedUrlStore.Add(url); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't persist typed-url addition; in-memory allowlist still updated.");
        }

        bool addedToMemory = false;
        if (!_options.AllowedServerUrls.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)))
        {
            _options.AllowedServerUrls.Add(url);
            addedToMemory = true;
        }

        return JsonSerializer.Serialize(new
        {
            url,
            addedToFile,
            addedToMemory,
            allowlistSize = _options.AllowedServerUrls.Count
        }, JsonOpts);
    }

    [McpServerTool(Name = "bowire.mock.start")]
    [Description("Spin up a local Bowire mock server that replays a recording (or synthesises responses from a schema). Returns a handle the agent uses with bowire.mock.stop. Multiple mocks can run concurrently on different ports. Two-step by default (controlled by RequireConfirmationForMutations): the first call returns { pending: true, confirmationToken, plan }; re-invoke with confirm=true or pass the token back as confirmationToken to actually start the mock.")]
    public async Task<string> MockStart(
        [Description("Path to a Bowire recording JSON.")] string? recording = null,
        [Description("Path to an OpenAPI 3 document for schema-only mocks.")] string? schema = null,
        [Description("Path to a protobuf FileDescriptorSet (.pb).")] string? grpcSchema = null,
        [Description("Path to a GraphQL SDL file.")] string? graphqlSchema = null,
        [Description("Listen port. 0 picks an OS-assigned port.")] int port = 0,
        [Description("Listen host. Default 'localhost'.")] string host = "localhost",
        [Description("Skip the pending-confirmation step. Equivalent to passing the confirmationToken from a prior call.")] bool confirm = false,
        [Description("Confirmation token returned by a prior pending call. Either this or confirm=true is required when RequireConfirmationForMutations is enabled.")] string? confirmationToken = null,
        CancellationToken ct = default)
    {
        var sources = new[] { recording, schema, grpcSchema, graphqlSchema }.Count(s => !string.IsNullOrEmpty(s));
        if (sources != 1)
            return sources == 0
                ? "bowire.mock.start: provide exactly one of recording / schema / grpcSchema / graphqlSchema."
                : "bowire.mock.start: recording / schema / grpcSchema / graphqlSchema are mutually exclusive — pick one.";

        // Confirmation gate. When the option is off the call falls
        // through unchanged (parity with the pre-#37 behaviour); when
        // on, the first call parks a plan and asks for a second-step
        // confirmation.
        var plan = $"Start a mock server on {host}:{port} backed by {recording ?? schema ?? grpcSchema ?? graphqlSchema}.";
        if (TryConfirmOrPark("bowire.mock.start", plan, confirm, confirmationToken, out var pendingResponse))
            return pendingResponse!;

        var options = new MockServerOptions
        {
            RecordingPath = recording,
            SchemaPath = schema,
            GrpcSchemaPath = grpcSchema,
            GraphQlSchemaPath = graphqlSchema,
            Port = port,
            Host = host
        };

        try
        {
            var server = await MockServer.StartAsync(options, ct).ConfigureAwait(false);
            var handle = _mockHandles.Register(server);
            return JsonSerializer.Serialize(new
            {
                handle,
                port = server.Port,
                host,
                url = $"http://{host}:{server.Port}",
                source = recording ?? schema ?? grpcSchema ?? graphqlSchema
            }, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mock server start failed");
            return $"bowire.mock.start failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bowire.mock.stop")]
    [Description("Stop a mock server previously started by bowire.mock.start. Idempotent — returns false if the handle is unknown or already stopped.")]
    public async Task<string> MockStop(
        [Description("Handle returned by bowire.mock.start.")] string handle)
    {
        var stopped = await _mockHandles.RemoveAndDisposeAsync(handle).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { handle, stopped }, JsonOpts);
    }

    [McpServerTool(Name = "bowire.mock.list")]
    [Description("List currently running mock servers spawned via bowire.mock.start. Includes handle, port, and a connect URL per mock.")]
    public string MockList()
    {
        var snapshot = _mockHandles.Snapshot();
        var entries = snapshot.Select(kvp => new
        {
            handle = kvp.Key,
            port = kvp.Value.Port,
            transportPorts = kvp.Value.TransportPorts.Count > 0 ? kvp.Value.TransportPorts : null,
            url = $"http://localhost:{kvp.Value.Port}"
        }).ToArray();
        return JsonSerializer.Serialize(new { count = entries.Length, mocks = entries }, JsonOpts);
    }

    [McpServerTool(Name = "bowire.record.start")]
    [Description("Begin a new server-side recording (#285). Subsequent capture-mode invocations on the same workspace land in the buffer until bowire.record.stop. Two-step by default: the first call returns { pending, confirmationToken, plan }; re-invoke with confirm=true or pass the token back. Mode: capture (default, record live invokes), proxy (record proxied flows), replay (drive replay from a pre-existing recording). One active session per process — start fails with a 409-equivalent message when a session is already open.")]
    public string RecordStart(
        [Description("Workspace id the recording is scoped under. Empty string accepted for legacy unscoped workspaces.")] string? workspaceId = null,
        [Description("Mode: capture, proxy, or replay. Default capture.")] string? mode = null,
        [Description("Optional human-readable recording name. Defaults to \"Untitled recording\".")] string? name = null,
        [Description("Optional pre-existing recording id (replay scenarios). When null, a fresh rec_* id is generated.")] string? recordingId = null,
        [Description("Skip the pending-confirmation step.")] bool confirm = false,
        [Description("Confirmation token returned by a prior pending call.")] string? confirmationToken = null)
    {
        var resolvedMode = ParseMode(mode);
        var plan = $"Start a {ModeWireName(resolvedMode)} recording named \"{name ?? "Untitled recording"}\" on workspace \"{workspaceId ?? "(unscoped)"}\".";
        if (TryConfirmOrPark("bowire.record.start", plan, confirm, confirmationToken, out var pendingResponse))
            return pendingResponse!;

        try
        {
            var state = _recordingSession.Start(
                workspaceId: workspaceId ?? string.Empty,
                mode: resolvedMode,
                name: name,
                recordingId: recordingId);
            return JsonSerializer.Serialize(new
            {
                started = true,
                recordingId = state.RecordingId,
                workspaceId = state.WorkspaceId,
                mode = ModeWireName(state.Mode),
                name = state.Name,
                startedAt = state.StartedAt,
            }, JsonOpts);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new
            {
                started = false,
                error = ex.Message,
            }, JsonOpts);
        }
    }

    [McpServerTool(Name = "bowire.record.stop")]
    [Description("Stop the active server-side recording (#285) and flush the buffer into ~/.bowire/recordings.json. Returns { stopped, recordingId, stepCount }. Idempotent — returns { stopped: false, reason: \"no-active-session\" } when no session is open. Two-step by default; pass confirm=true to skip the confirmation gate.")]
    public string RecordStop(
        [Description("Skip the pending-confirmation step.")] bool confirm = false,
        [Description("Confirmation token returned by a prior pending call.")] string? confirmationToken = null)
    {
        var plan = "Stop the active server-side recording and persist it to ~/.bowire/recordings.json.";
        if (TryConfirmOrPark("bowire.record.stop", plan, confirm, confirmationToken, out var pendingResponse))
            return pendingResponse!;

        try
        {
            var recording = _recordingSession.Stop(flush: PersistRecording);
            if (recording is null)
            {
                return JsonSerializer.Serialize(new
                {
                    stopped = false,
                    reason = "no-active-session",
                }, JsonOpts);
            }
            return JsonSerializer.Serialize(new
            {
                stopped = true,
                recordingId = recording.Id,
                stepCount = recording.Steps.Count,
                name = recording.Name,
            }, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "bowire.record.stop failed");
            return JsonSerializer.Serialize(new
            {
                stopped = false,
                error = ex.Message,
            }, JsonOpts);
        }
    }

    [McpServerTool(Name = "bowire.record.replay")]
    [Description("Switch the active server-side recording session into replay mode for the given recording id (#285). When no session is open, opens a fresh one bound to the supplied recording id so the workbench can drive replay. Returns the resulting session state. Two-step by default; pass confirm=true to skip the confirmation gate.")]
    public string RecordReplay(
        [Description("Workspace id the replay is scoped under.")] string? workspaceId = null,
        [Description("Recording id to replay (from bowire.record.list).")] string? recordingId = null,
        [Description("Skip the pending-confirmation step.")] bool confirm = false,
        [Description("Confirmation token returned by a prior pending call.")] string? confirmationToken = null)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
        {
            return JsonSerializer.Serialize(new
            {
                replaying = false,
                error = "recordingId is required.",
            }, JsonOpts);
        }

        var plan = $"Replay recording \"{recordingId}\" on workspace \"{workspaceId ?? "(unscoped)"}\".";
        if (TryConfirmOrPark("bowire.record.replay", plan, confirm, confirmationToken, out var pendingResponse))
            return pendingResponse!;

        try
        {
            BowireRecordingSessionState state;
            if (_recordingSession.Active is null)
            {
                state = _recordingSession.Start(
                    workspaceId: workspaceId ?? string.Empty,
                    mode: BowireRecordingMode.Replay,
                    name: $"Replay of {recordingId}",
                    recordingId: recordingId);
            }
            else
            {
                state = _recordingSession.SwitchToReplay();
            }
            return JsonSerializer.Serialize(new
            {
                replaying = true,
                recordingId = state.RecordingId,
                workspaceId = state.WorkspaceId,
                mode = ModeWireName(state.Mode),
            }, JsonOpts);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new
            {
                replaying = false,
                error = ex.Message,
            }, JsonOpts);
        }
    }

    /// <summary>
    /// Parse the <c>mode</c> argument from the MCP tool surface. Accepts
    /// the three documented strings + any casing; falls back to Capture
    /// for null/empty/unknown so a typo doesn't park a session in a
    /// surprising mode.
    /// </summary>
    private static BowireRecordingMode ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return BowireRecordingMode.Capture;
        var trimmed = mode.Trim();
        if (string.Equals(trimmed, "proxy", StringComparison.OrdinalIgnoreCase)) return BowireRecordingMode.Proxy;
        if (string.Equals(trimmed, "replay", StringComparison.OrdinalIgnoreCase)) return BowireRecordingMode.Replay;
        return BowireRecordingMode.Capture;
    }

    /// <summary>
    /// Wire-form name for a mode enum value. Used in tool responses + the
    /// plan strings the confirmation gate echoes back. Kept as a tiny
    /// table instead of <c>ToString().ToLowerInvariant()</c> so the
    /// names are stable (a future rename of the enum case doesn't break
    /// the tool wire contract) and so CA1308 (which discourages
    /// ToLowerInvariant) is satisfied.
    /// </summary>
    private static string ModeWireName(BowireRecordingMode mode) => mode switch
    {
        BowireRecordingMode.Capture => "capture",
        BowireRecordingMode.Proxy => "proxy",
        BowireRecordingMode.Replay => "replay",
        _ => "capture",
    };

    /// <summary>
    /// Flush sink for <see cref="RecordStop"/>: append the stopped
    /// recording into the <c>~/.bowire/recordings.json</c> wrapper file
    /// so it shows up alongside the workbench-captured recordings.
    /// Failures are swallowed — the session has already closed; a disk
    /// hiccup can't unwind that, and the recording is still returned to
    /// the agent in the tool response.
    /// </summary>
    private BowireRecording PersistRecording(BowireRecording recording)
    {
        try
        {
            var path = BowireConfigPath("recordings.json");
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            JsonNode? root = null;
            if (File.Exists(path))
            {
                try { root = JsonNode.Parse(File.ReadAllText(path)); }
                catch (JsonException) { root = null; }
            }
            root ??= new JsonObject { ["recordings"] = new JsonArray() };
            if (root["recordings"] is not JsonArray arr)
            {
                arr = new JsonArray();
                root["recordings"] = arr;
            }

            var serialized = JsonSerializer.Serialize(recording, JsonOpts);
            arr.Add(JsonNode.Parse(serialized));

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Recording flush to {Path} failed", BowireConfigPath("recordings.json"));
        }
        return recording;
    }

    [McpServerTool(Name = "bowire.allowlist.show")]
    [Description("Diagnostic: show the URLs the agent is currently allowed to call via bowire.invoke / bowire.subscribe, plus the toggles that fed the list (environments seed, typed-URLs seed, --allow-arbitrary-urls).")]
    public string AllowlistShow() =>
        JsonSerializer.Serialize(new
        {
            allowArbitraryUrls = _options.AllowArbitraryUrls,
            loadFromEnvironments = _options.LoadAllowlistFromEnvironments,
            loadFromTypedUrls = _options.LoadAllowlistFromTypedUrls,
            requireConfirmationForMutations = _options.RequireConfirmationForMutations,
            urls = _options.AllowedServerUrls
        }, JsonOpts);

    // -------------------- Helpers --------------------

    /// <summary>
    /// Known assertion operators — kept in lock-step with the workbench's
    /// ASSERT_OPERATORS array in <c>test-assertions.js</c>. Passing an
    /// op outside this set produces a user-visible error before the
    /// assertion is appended so a typo doesn't park a permanently-broken
    /// test on the step.
    /// </summary>
    internal static readonly string[] KnownAssertOps =
        ["eq", "ne", "gt", "gte", "lt", "lte", "contains", "matches", "exists", "notexists", "type"];

    private static bool IsKnownAssertOp(string op) =>
        Array.IndexOf(KnownAssertOps, op) >= 0;

    /// <summary>
    /// Two-step confirm gate for mutator tools. Returns <see langword="true"/>
    /// when the caller still needs to confirm (pending response is in
    /// <paramref name="pendingResponse"/>); returns <see langword="false"/>
    /// when the call may execute. Bypasses entirely when
    /// <see cref="BowireMcpOptions.RequireConfirmationForMutations"/> is off.
    /// </summary>
    private bool TryConfirmOrPark(string kind, string plan, bool confirm, string? confirmationToken,
        out string? pendingResponse)
    {
        pendingResponse = null;

        // Opt-out: no gate, every call executes immediately. Matches
        // the pre-#37 behaviour for hosts that want fully-autonomous
        // agent runs.
        if (!_options.RequireConfirmationForMutations) return false;

        // Token shortcut — agents that already hold a token from a
        // prior pending response can redeem it instead of re-stating
        // every input.
        if (!string.IsNullOrWhiteSpace(confirmationToken))
        {
            var entry = _confirmations.Consume(confirmationToken);
            if (entry is null)
            {
                pendingResponse = JsonSerializer.Serialize(new
                {
                    error = $"No pending confirmation matches token \"{confirmationToken}\" (or it expired).",
                    kind
                }, JsonOpts);
                return true;
            }
            return false;
        }

        if (confirm) return false;

        var token = _confirmations.Issue(kind, plan);
        pendingResponse = JsonSerializer.Serialize(new
        {
            pending = true,
            confirmationToken = token,
            kind,
            plan,
            note = "Re-invoke with confirm=true (or confirmationToken from this response) to execute. Token expires in 5 minutes."
        }, JsonOpts);
        return true;
    }

    private bool IsUrlAllowed(string? url)
    {
        if (_options.AllowArbitraryUrls) return true;
        if (string.IsNullOrWhiteSpace(url)) return false;
        foreach (var allowed in _options.AllowedServerUrls)
        {
            if (string.Equals(allowed, url, StringComparison.OrdinalIgnoreCase)) return true;
            // Allow path-prefixed siblings of allowlisted base URLs so an
            // env entry "https://api.example/v1" also covers
            // ".../v1/users" without double-listing.
            if (!string.IsNullOrEmpty(allowed) &&
                url.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string AllowlistDeniedMessage(string url) =>
        $"URL \"{url}\" is not on the allowlist. Allowed URLs come from ~/.bowire/environments.json or BowireMcpOptions.AllowedServerUrls. Pass --allow-arbitrary-urls to the CLI (or set BowireMcpOptions.AllowArbitraryUrls = true) to drop this check; only do that in sandboxed contexts.";

    private static void SeedAllowlistFromEnvironments(BowireMcpOptions options)
    {
        var path = BowireConfigPath("environments.json");
        if (!File.Exists(path)) return;
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var seen = new HashSet<string>(options.AllowedServerUrls, StringComparer.OrdinalIgnoreCase);
        WalkForServerUrls(doc.RootElement, options, seen);
    }

    /// <summary>
    /// Seed the allowlist from the user's typed-URL history
    /// (<c>~/.bowire/typed-urls.json</c>). Backs the <c>--allow-invoke</c>
    /// CLI flag — widens the allowlist to "anywhere the user has actually
    /// pointed Bowire" without dropping the gate entirely.
    /// </summary>
    private static void SeedAllowlistFromTypedUrls(BowireMcpOptions options)
    {
        var seen = new HashSet<string>(options.AllowedServerUrls, StringComparer.OrdinalIgnoreCase);
        foreach (var url in BowireMcpTypedUrlStore.LoadAll())
        {
            if (seen.Add(url)) options.AllowedServerUrls.Add(url);
        }
    }

    private static void WalkForServerUrls(JsonElement el, BowireMcpOptions options, HashSet<string> seen)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "serverUrl", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var url = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(url) && seen.Add(url))
                            options.AllowedServerUrls.Add(url);
                    }
                    else
                    {
                        WalkForServerUrls(prop.Value, options, seen);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) WalkForServerUrls(item, options, seen);
                break;
        }
    }

    /// <summary>
    /// Test-only override for the home directory the MCP tools read
    /// <c>environments.json</c> / <c>recordings.json</c> from. Lets the
    /// coverage tests point at a temp folder instead of the real
    /// <c>~/.bowire/</c> — `Environment.GetFolderPath(UserProfile)` ignores
    /// <c>USERPROFILE</c> on Windows, so without this hook a test that
    /// crashed mid-run would leave the developer's actual config files
    /// in a half-written state. Production callers leave it null and the
    /// regular user-profile lookup wins.
    /// </summary>
    internal static string? HomeDirOverride { get; set; }

    private static string BowireConfigPath(string filename) =>
        Path.Combine(
            HomeDirOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bowire", filename);

    private IReadOnlyList<IBowireProtocol> SelectProtocols(string? protocolId)
    {
        if (string.IsNullOrWhiteSpace(protocolId)) return _registry.Protocols;
        var p = _registry.GetById(protocolId);
        return p is null ? Array.Empty<IBowireProtocol>() : [p];
    }

    private IBowireProtocol? ResolveProtocol(string? protocolId)
    {
        if (!string.IsNullOrWhiteSpace(protocolId)) return _registry.GetById(protocolId);
        return _registry.Protocols.Count > 0 ? _registry.Protocols[0] : null;
    }
}
