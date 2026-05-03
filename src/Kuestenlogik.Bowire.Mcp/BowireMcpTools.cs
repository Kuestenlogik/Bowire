// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Mock;
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
    private readonly BowireMcpOptions _options;
    private readonly ILogger<BowireMcpTools> _logger;

    public BowireMcpTools(
        BowireProtocolRegistry registry,
        BowireMockHandleRegistry mockHandles,
        IOptions<BowireMcpOptions> options,
        ILogger<BowireMcpTools> logger)
    {
        _registry = registry;
        _mockHandles = mockHandles;
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
            var summary = new List<object>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rec in doc.RootElement.EnumerateArray())
                {
                    summary.Add(new
                    {
                        id = rec.TryGetProperty("id", out var i) ? i.GetString() : null,
                        name = rec.TryGetProperty("name", out var n) ? n.GetString() : null,
                        createdAt = rec.TryGetProperty("createdAt", out var c) ? c.GetString() : null,
                        stepCount = rec.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0
                    });
                }
            }
            return JsonSerializer.Serialize(new { path, recordings = summary }, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"Failed to read recordings.json: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bowire.mock.start")]
    [Description("Spin up a local Bowire mock server that replays a recording (or synthesises responses from a schema). Returns a handle the agent uses with bowire.mock.stop. Multiple mocks can run concurrently on different ports.")]
    public async Task<string> MockStart(
        [Description("Path to a Bowire recording JSON.")] string? recording = null,
        [Description("Path to an OpenAPI 3 document for schema-only mocks.")] string? schema = null,
        [Description("Path to a protobuf FileDescriptorSet (.pb).")] string? grpcSchema = null,
        [Description("Path to a GraphQL SDL file.")] string? graphqlSchema = null,
        [Description("Listen port. 0 picks an OS-assigned port.")] int port = 0,
        [Description("Listen host. Default 'localhost'.")] string host = "localhost",
        CancellationToken ct = default)
    {
        var sources = new[] { recording, schema, grpcSchema, graphqlSchema }.Count(s => !string.IsNullOrEmpty(s));
        if (sources != 1)
            return sources == 0
                ? "bowire.mock.start: provide exactly one of recording / schema / grpcSchema / graphqlSchema."
                : "bowire.mock.start: recording / schema / grpcSchema / graphqlSchema are mutually exclusive — pick one.";

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
    [Description("(planned, Phase 2) Begin a new recording so subsequent invokes are captured.")]
    public static string RecordStart(
        [Description("Optional recording name.")] string? name = null) =>
        "bowire.record.start is not yet implemented in this Bowire.Mcp build (planned for Phase 2). See the ROADMAP entry on Bowire.Mcp.";

    [McpServerTool(Name = "bowire.record.stop")]
    [Description("(planned, Phase 2) Stop the active recording and persist it.")]
    public static string RecordStop() =>
        "bowire.record.stop is not yet implemented in this Bowire.Mcp build (planned for Phase 2). See the ROADMAP entry on Bowire.Mcp.";

    [McpServerTool(Name = "bowire.record.replay")]
    [Description("(planned, Phase 3) Replay a recording by id and return per-step pass/fail.")]
    public static string RecordReplay(
        [Description("Recording id from bowire.record.list.")] string id) =>
        $"bowire.record.replay({id}) is not yet implemented in this Bowire.Mcp build (planned for Phase 3). See the ROADMAP entry on Bowire.Mcp.";

    [McpServerTool(Name = "bowire.allowlist.show")]
    [Description("Diagnostic: show the URLs the agent is currently allowed to call via bowire.invoke / bowire.subscribe.")]
    public string AllowlistShow() =>
        JsonSerializer.Serialize(new
        {
            allowArbitraryUrls = _options.AllowArbitraryUrls,
            loadFromEnvironments = _options.LoadAllowlistFromEnvironments,
            urls = _options.AllowedServerUrls
        }, JsonOpts);

    // -------------------- Helpers --------------------

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

    private static string BowireConfigPath(string filename) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bowire", filename);

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
