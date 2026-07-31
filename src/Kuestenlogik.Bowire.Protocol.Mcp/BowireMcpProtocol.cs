// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// Bowire protocol plugin for the Model Context Protocol (MCP). Connects to
/// a remote MCP server, discovers its tools / resources / prompts, and lets
/// the user invoke them — analogous to the gRPC and REST plugins.
/// </summary>
/// <remarks>
/// <para>
/// Built on the official <c>ModelContextProtocol</c> C# SDK
/// (<see cref="McpClient"/> + <see cref="HttpClientTransport"/>) — the
/// previous hand-rolled JSON-RPC client predates the SDK's client
/// surface. The SDK owns the entire negotiation: on MCP revision
/// 2026-07-28 it probes <c>server/discover</c> first and only falls back
/// to the legacy <c>initialize</c> handshake when that errors or the
/// probe timeout elapses; it auto-detects Streamable HTTP vs SSE, stamps
/// the SEP-2243 <c>MCP-Protocol-Version</c> / <c>Mcp-Method</c> /
/// <c>Mcp-Name</c> headers onto every POST, and writes the JSON-RPC
/// envelope.
/// </para>
/// <para>
/// There is no session left to resume: 2026-07-28 removed
/// <c>Mcp-Session-Id</c> along with the initialize handshake, so
/// per-request identity (protocol version, client info, client
/// capabilities) travels inside <c>params._meta</c> and the SDK injects
/// it. Bowire stores, echoes and transmits none of it — which is also
/// why a fresh throwaway client per call costs nothing but the probe
/// round trip.
/// </para>
/// <para>
/// The companion <see cref="McpAdapterEndpoints.MapBowireMcpAdapter"/>
/// extension lives in the same assembly but is unrelated to discovery: it is
/// an opt-in development feature that goes the other direction (Bowire's
/// services exposed as MCP tools so AI agents can call them). It runs on
/// the SDK's own server transport (<c>AddBowireMcpAdapter</c> →
/// <c>WithHttpTransport</c>); only the tool/resource/prompt <em>contents</em>
/// are synthesised from Bowire's <c>BowireProtocolRegistry</c>.
/// </para>
/// </remarks>
public sealed class BowireMcpProtocol : IBowireProtocol, IBowireDiscoveryDiagnostics
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    // Tool definitions captured by the last successful DiscoverAsync, keyed
    // by normalised server URL. InvokeAsync builds a *fresh* McpClient per
    // call, so the SDK's own tool cache is guaranteed empty at tools/call
    // time and the SEP-2243 Mcp-Param-* headers would be dropped (the SDK
    // logs a cache miss and sends the call header-less). Replaying these
    // through McpClient.AddKnownTools closes that gap without a second
    // tools/list round trip — and without letting one malformed tool on the
    // server break an unrelated invoke, which a pre-call ListToolsAsync
    // would. Unbounded on purpose, like the REST plugin's schema cache: the
    // key space is the set of MCP URLs one operator discovered in one
    // process lifetime.
    private readonly ConcurrentDictionary<string, IReadOnlyList<Tool>> _knownTools =
        new(StringComparer.Ordinal);

    public string Name => "MCP";
    public string Description => "Model Context Protocol — Claude / Cursor / Copilot tool + resource server discovery + invoke.";
    public string Id => "mcp";

    // Initialize stays a no-op: the SDK owns its own HttpClient through
    // HttpClientTransportOptions, and the localhost-cert opt-in we used
    // to thread through BowireHttpClientFactory isn't reachable from
    // the SDK transport. Embedded hosts that need a custom HttpClient
    // can subclass the plugin or wait for the SDK to expose the seam.
    public void Initialize(IServiceProvider? serviceProvider) { }

    // Model Context Protocol — official three-stroke mark (modelcontextprotocol.io).
    public string IconSvg => """<svg viewBox="0 0 180 180" fill="none" stroke="currentColor" stroke-width="14" stroke-linecap="round" width="16" height="16" aria-hidden="true"><path d="M18 84.8528L85.8822 16.9706C95.2548 7.59798 110.451 7.59798 119.823 16.9706C129.196 26.3431 129.196 41.5391 119.823 50.9117L68.5581 102.177"/><path d="M69.2652 101.47L119.823 50.9117C129.196 41.5391 144.392 41.5391 153.765 50.9117L154.118 51.2652C163.491 60.6378 163.491 75.8338 154.118 85.2063L92.7248 146.6C89.6006 149.724 89.6006 154.789 92.7248 157.913L105.331 170.52"/><path d="M102.853 33.9411L52.6482 84.1457C43.2756 93.5183 43.2756 108.714 52.6482 118.087C62.0208 127.459 77.2167 127.459 86.5893 118.087L136.794 67.8822"/></svg>""";

    /// <summary>
    /// The lossy channel. Everything happens in
    /// <see cref="DiscoverWithDiagnosticsAsync"/>; this only has to decide
    /// what to do with a fault when the caller has no field to put it in.
    /// </summary>
    /// <remarks>
    /// This signature stays all-or-nothing: a fault throws, even when some
    /// surfaces answered. That looks like the opposite of #544, and it is
    /// deliberate — the lossy channel cannot carry "and by the way, part of
    /// this is missing", so the only two options are a silent truncation or
    /// a throw, and a silent truncation is the dangerous one. Bowire's own
    /// security scanner calls this signature directly
    /// (<c>McpToolInjectionProbe</c>, <c>McpDiscoveryProbe</c>,
    /// <c>McpResourceTraversalProbe</c> via <c>OwaspApiSuite</c>): handing it
    /// a half-list without a word would make it report a clean bill of
    /// health for an attack surface it never saw.
    /// <para>
    /// Callers that want the services AND the fault use
    /// <see cref="IBowireDiscoveryDiagnostics"/>, which is where #544 lives
    /// — <see cref="BowireDiscoveryProbe"/> takes that path, so the
    /// workbench and the CLI keep the working surfaces and show the
    /// diagnostic.
    /// </para>
    /// </remarks>
    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        var report = await DiscoverWithDiagnosticsAsync(serverUrl, showInternalServices, ct)
            .ConfigureAwait(false);

        if (report.Diagnostic is { Severity: BowireDiscoverySeverity.Fault } fault)
        {
            throw new InvalidOperationException(fault.Message);
        }

        return report.Services;
    }

    public async Task<BowireDiscoveryReport> DiscoverWithDiagnosticsAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return new BowireDiscoveryReport([], null);

        McpClient client;
        try
        {
            client = await CreateClientAsync(serverUrl, metadata: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The probe ceiling or the caller aborted — let it out so
            // BowireDiscoveryProbe records `timeout` rather than `empty`.
            throw;
        }
        catch
        {
            // The connect/negotiate leg failed, so this URL is not an MCP
            // endpoint (or nothing is listening). Bowire probes EVERY
            // plugin against EVERY URL, so raising this would tag the MCP
            // plugin `error` on every REST/gRPC/MQTT URL in the fan-out.
            // `empty` — "the URL simply is not this plugin's" — is the
            // honest outcome here. Failures *after* a successful
            // handshake are the opposite case and do get reported below.
            // No diagnostic either, for the same reason: even a Note here
            // would put an MCP line on every row of the diagnostics table.
            // CreateClientAsync already disposed the transport it built.
            return new BowireDiscoveryReport([], null);
        }

        await using (client)
        {
            var services = new List<BowireServiceInfo>();
            var failures = new List<string>();

            // Tools, resources, and prompts are all optional on the server
            // side, so "this surface is absent" must stay silent — but
            // anything else is a real fault on a server we just shook
            // hands with, and swallowing it used to render as an empty
            // tree indistinguishable from "this server has nothing".
            await AddToolsAsync(client, services, serverUrl, failures, ct).ConfigureAwait(false);
            await AddResourcesAsync(client, services, serverUrl, failures, ct).ConfigureAwait(false);
            await AddPromptsAsync(client, services, serverUrl, failures, ct).ConfigureAwait(false);

            if (failures.Count == 0)
                return new BowireDiscoveryReport(services, null);

            // The surfaces that DID answer stay in `services`. Before #544
            // this threw, which suppressed them — the message even had to
            // apologise for it ("Resources, Prompts answered, but discovery
            // reports the whole probe as failed"), because there was no way
            // to hand back both. There is now: the probe pairs these
            // services with this Fault and records `partial`, so a server
            // with one malformed tool keeps contributing everything else.
            return new BowireDiscoveryReport(
                services,
                new BowireDiscoveryDiagnostic(
                    BowireDiscoverySeverity.Fault, string.Join("; ", failures))
                {
                    Details = failures,
                });
        }
    }

    /// <summary>
    /// Decide whether a failed <c>*/list</c> call is a missing capability
    /// (silent — the surface simply is not there) or a diagnosable fault
    /// worth surfacing through the discovery-diagnostics channel.
    /// </summary>
    /// <returns><see langword="null"/> when the surface is merely absent.</returns>
    private static string? ClassifyListFailure(string surface, Exception ex)
    {
        // Capability absent is not a fault. A server that ships only tools
        // answers resources/list with -32601 MethodNotFound, and the SDK
        // raises NotSupportedException when the server never advertised
        // the capability in the first place. Both mean "no entries".
        if (ex is NotSupportedException)
            return null;
        if (ex is McpProtocolException { ErrorCode: McpErrorCode.MethodNotFound })
            return null;

        // SDK 2.0 made Tool.inputSchema required at deserialization time,
        // so ONE malformed tool now throws for the whole page. Say so in
        // MCP's vocabulary: the raw JsonException text names a CLR type
        // and a JSON property, never the server or the surface.
        if (FindJsonException(ex) is { } json)
            return $"{surface} returned a payload this MCP revision rejects — {json.Message}";

        return $"{surface} failed: {ex.Message}";
    }

    /// <summary>
    /// Walk the inner-exception chain for a <see cref="JsonException"/>:
    /// the SDK may hand back the deserialization failure directly or
    /// wrapped in a transport/protocol exception depending on where in the
    /// pipeline the malformed payload was read.
    /// </summary>
    private static JsonException? FindJsonException(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is JsonException json) return json;
        }
        return null;
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        McpClient? client = null;
        try
        {
            client = await CreateClientAsync(serverUrl, metadata, ct).ConfigureAwait(false);

            await using (client)
            {
                // SEP-2243: the transport only stamps Mcp-Param-* headers
                // on a tools/call when the client holds that tool's
                // definition. This client is seconds old and never listed
                // anything, so replay what DiscoverAsync captured — that is
                // the difference between Bowire's request and the one
                // Claude Desktop or Cursor would send to a header-routing
                // gateway.
                if (service == "Tools"
                    && _knownTools.TryGetValue(NormalizeUrl(serverUrl), out var known)
                    && known.Count > 0)
                {
                    PrimeToolCache(client, known);
                }

                JsonElement payload = service switch
                {
                    "Tools" => await CallToolAsync(client, method, jsonMessages, ct).ConfigureAwait(false),
                    "Resources" => await ReadResourceAsync(client, method, ct).ConfigureAwait(false),
                    "Prompts" => await GetPromptAsync(client, method, jsonMessages, ct).ConfigureAwait(false),
                    _ => throw new InvalidOperationException(
                        $"MCP service '{service}' is not supported. Use 'Tools', 'Resources', or 'Prompts'."),
                };

                var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                var json = JsonSerializer.Serialize(payload, s_indented);
                return new InvokeResult(json, elapsedMs, "OK", new Dictionary<string, string>());
            }
        }
        catch (Exception ex)
        {
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            return new InvokeResult(null, elapsedMs, ex.Message, new Dictionary<string, string>());
        }
    }

    public IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // MCP tool calls are unary in the request/response sense. Server
        // notifications (progress, log entries) are a separate seam the
        // SDK exposes via handlers — when we wire them through Bowire's
        // streaming surface this method will fan them out.
        return AsyncEnumerable.Empty<string>();
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    // ----- SDK plumbing -------------------------------------------------

    /// <summary>
    /// The cache key for a target: the same string
    /// <see cref="CreateClientAsync"/> turns into the transport endpoint,
    /// so a trailing slash cannot split one server into two entries.
    /// </summary>
    private static string NormalizeUrl(string serverUrl) => serverUrl.TrimEnd('/');

    /// <summary>
    /// Replay discovered tool definitions into a fresh client's cache so the
    /// transport can stamp the SEP-2243 <c>Mcp-Param-*</c> headers on the
    /// <c>tools/call</c> that follows.
    /// </summary>
    /// <remarks>
    /// Deliberately best-effort. <c>AddKnownTools</c> validates the
    /// <c>x-mcp-header</c> annotations of the whole batch and throws
    /// <see cref="ArgumentException"/> all-or-nothing, so a single tool with
    /// a malformed annotation would otherwise fail an invoke that the SDK is
    /// perfectly willing to send without the headers. Header fidelity is a
    /// nicety; completing the call the user asked for is not.
    /// </remarks>
    private static void PrimeToolCache(McpClient client, IReadOnlyList<Tool> known)
    {
        try { client.AddKnownTools(known); }
        catch (ArgumentException) { /* header stamping is not worth a failed invoke */ }
    }

    private static async Task<McpClient> CreateClientAsync(
        string serverUrl,
        Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(NormalizeUrl(serverUrl), UriKind.Absolute),
            // AutoDetect lets the SDK try Streamable HTTP first and fall
            // back to SSE+POST (the 2024-11-05 transport) when the server
            // answers 405 or text/event-stream. On SDK 2.0 the POST that
            // triggers that detection is the `server/discover` probe, not
            // the old `initialize` call — the client only reaches
            // `initialize` when discover errors or its probe timeout
            // (5 s by default) elapses.
            TransportMode = HttpTransportMode.AutoDetect,
            AdditionalHeaders = metadata is null
                ? null
                : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
        };

        // McpClient.CreateAsync takes ownership of the transport on
        // success (closing the McpClient closes the transport too).
        // On a throw before hand-off we dispose explicitly via the
        // async path; CA2000 sees IAsyncDisposable but doesn't track
        // it across awaits, so silence it here.
#pragma warning disable CA2000
        var transport = new HttpClientTransport(options);
#pragma warning restore CA2000
        try
        {
            return await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ----- discovery: tools / resources / prompts -----------------------

    private async Task AddToolsAsync(
        McpClient client, List<BowireServiceInfo> services, string serverUrl,
        List<string> failures, CancellationToken ct)
    {
        IList<McpClientTool> tools;
        try { tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (ClassifyListFailure("tools/list", ex) is { } failure) failures.Add(failure);
            return;
        }

        // Keep the definitions for InvokeAsync's fresh client (Mcp-Param-*).
        // Written even when the list is empty so a server that dropped its
        // tools doesn't leave a stale entry behind.
        _knownTools[NormalizeUrl(serverUrl)] = [.. tools.Select(tool => tool.ProtocolTool)];

        if (tools.Count == 0) return;

        var methods = tools.Select(tool => new BowireMethodInfo(
            Name: tool.Name,
            FullName: "Tools/" + tool.Name,
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: MapToolInputSchema(tool),
            OutputType: new BowireMessageInfo("ToolResult", "mcp.ToolResult", []),
            MethodType: "Unary")
        {
            Summary = tool.Description,
            Description = tool.Description,
        }).ToList();

        services.Add(new BowireServiceInfo("Tools", "mcp", methods)
        {
            Source = "mcp",
            OriginUrl = serverUrl,
            Description = "MCP tools — invoke with the same form-based UI as gRPC unary methods.",
        });
    }

    private static async Task AddResourcesAsync(
        McpClient client, List<BowireServiceInfo> services, string serverUrl,
        List<string> failures, CancellationToken ct)
    {
        IList<McpClientResource> resources;
        try { resources = await client.ListResourcesAsync(cancellationToken: ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (ClassifyListFailure("resources/list", ex) is { } failure) failures.Add(failure);
            return;
        }
        if (resources.Count == 0) return;

        var methods = resources.Select(res => new BowireMethodInfo(
            Name: res.Uri,
            FullName: "Resources/" + res.Uri,
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: new BowireMessageInfo("ResourceRead", "mcp.ResourceRead", []),
            OutputType: new BowireMessageInfo("ResourceContent", "mcp.ResourceContent", []),
            MethodType: "Unary")
        {
            Summary = string.IsNullOrEmpty(res.Name) ? res.Uri : $"{res.Name} — {res.Uri}",
            Description = res.Description,
        }).ToList();

        services.Add(new BowireServiceInfo("Resources", "mcp", methods)
        {
            Source = "mcp",
            OriginUrl = serverUrl,
            Description = "MCP resources — read by URI.",
        });
    }

    private static async Task AddPromptsAsync(
        McpClient client, List<BowireServiceInfo> services, string serverUrl,
        List<string> failures, CancellationToken ct)
    {
        IList<McpClientPrompt> prompts;
        try { prompts = await client.ListPromptsAsync(cancellationToken: ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (ClassifyListFailure("prompts/list", ex) is { } failure) failures.Add(failure);
            return;
        }
        if (prompts.Count == 0) return;

        var methods = prompts.Select(prompt => new BowireMethodInfo(
            Name: prompt.Name,
            FullName: "Prompts/" + prompt.Name,
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: BuildPromptInput(prompt),
            OutputType: new BowireMessageInfo("PromptResult", "mcp.PromptResult", []),
            MethodType: "Unary")
        {
            Summary = prompt.Description,
            Description = prompt.Description,
        }).ToList();

        services.Add(new BowireServiceInfo("Prompts", "mcp", methods)
        {
            Source = "mcp",
            OriginUrl = serverUrl,
            Description = "MCP prompts — render templated prompts with arguments.",
        });
    }

    // ----- invocation ---------------------------------------------------

    private static async Task<JsonElement> CallToolAsync(
        McpClient client, string toolName, List<string> jsonMessages, CancellationToken ct)
    {
        var args = ParseArguments(jsonMessages);
        var result = await client.CallToolAsync(toolName, args, cancellationToken: ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result);
    }

    private static async Task<JsonElement> ReadResourceAsync(
        McpClient client, string uri, CancellationToken ct)
    {
        var result = await client
            .ReadResourceAsync(new Uri(uri, UriKind.RelativeOrAbsolute), cancellationToken: ct)
            .ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result);
    }

    private static async Task<JsonElement> GetPromptAsync(
        McpClient client, string name, List<string> jsonMessages, CancellationToken ct)
    {
        var args = ParseArguments(jsonMessages)
            ?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        var result = await client.GetPromptAsync(name, args, cancellationToken: ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result);
    }

    private static Dictionary<string, object?>? ParseArguments(List<string> jsonMessages)
    {
        if (jsonMessages.Count == 0 || string.IsNullOrWhiteSpace(jsonMessages[0]))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonMessages[0]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.Clone(),
                };
            }
            return dict;
        }
        catch
        {
            return null;
        }
    }

    // ----- schema mapping -----------------------------------------------

    private static BowireMessageInfo MapToolInputSchema(McpClientTool tool)
    {
        // McpClientTool.JsonSchema is the same JSON-Schema-object MCP
        // ships in tools/list responses. Walk top-level properties +
        // required[] into BowireFieldInfos so Bowire's form UI can render
        // it the same way it does gRPC inputs.
        var fields = new List<BowireFieldInfo>();
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object)
            return new BowireMessageInfo(tool.Name + "Input", tool.Name + "Input", fields);

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in req.EnumerateArray())
            {
                var name = item.GetString();
                if (!string.IsNullOrEmpty(name)) required.Add(name);
            }
        }

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var i = 1;
            foreach (var prop in props.EnumerateObject())
            {
                var type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string";
                var description = prop.Value.TryGetProperty("description", out var d) ? d.GetString() : null;
                var isRequired = required.Contains(prop.Name);

                fields.Add(new BowireFieldInfo(
                    Name: prop.Name,
                    Number: i++,
                    Type: type,
                    Label: isRequired ? "required" : "optional",
                    IsMap: false,
                    IsRepeated: type == "array",
                    MessageType: null,
                    EnumValues: null)
                {
                    Required = isRequired,
                    Description = description,
                    Source = "body",
                });
            }
        }

        return new BowireMessageInfo(tool.Name + "Input", tool.Name + "Input", fields);
    }

    private static BowireMessageInfo BuildPromptInput(McpClientPrompt prompt)
    {
        var fields = new List<BowireFieldInfo>();
        var args = prompt.ProtocolPrompt.Arguments;
        if (args is null || args.Count == 0)
            return new BowireMessageInfo(prompt.Name + "Input", prompt.Name + "Input", fields);

        var i = 1;
        foreach (var arg in args)
        {
            var argRequired = arg.Required ?? false;
            fields.Add(new BowireFieldInfo(
                Name: arg.Name,
                Number: i++,
                Type: "string",
                Label: argRequired ? "required" : "optional",
                IsMap: false,
                IsRepeated: false,
                MessageType: null,
                EnumValues: null)
            {
                Required = argRequired,
                Description = arg.Description,
                Source = "body",
            });
        }

        return new BowireMessageInfo(prompt.Name + "Input", prompt.Name + "Input", fields);
    }
}
