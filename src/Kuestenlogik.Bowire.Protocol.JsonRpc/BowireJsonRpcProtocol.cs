// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.JsonRpc;

/// <summary>
/// Bowire protocol plugin for generic JSON-RPC 2.0 endpoints. Connects
/// to any JSON-RPC server (HTTP transport), tries to discover its
/// method surface via the OpenRPC <c>rpc.discover</c> convention, and
/// falls back to a freeform invoke mode when the server doesn't speak
/// OpenRPC.
/// </summary>
/// <remarks>
/// <para>
/// URL grammar follows the same scheme conventions as the gRPC plugin:
/// <c>http://</c> and <c>https://</c> URLs are used as-is, and the
/// <c>jsonrpc@http://...</c> / <c>jsonrpc@https://...</c> hint pins
/// the plugin without changing the underlying URL.
/// </para>
/// <para>
/// Discovery strategy:
/// </para>
/// <list type="number">
///   <item>Call <c>rpc.discover</c> (OpenRPC's standardised
///   reflection method). If the server returns an OpenRPC document,
///   each method maps to a <see cref="BowireMethodInfo"/>.</item>
///   <item>If <c>rpc.discover</c> returns Method-Not-Found (-32601),
///   the plugin still claims the URL but exposes an empty service
///   tree — users can invoke any method by typing the name into the
///   freeform request form.</item>
/// </list>
/// </remarks>
public sealed class BowireJsonRpcProtocol : IBowireProtocol, IDisposable
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    // Lazily created in Initialize so the localhost-cert opt-in flows
    // through BowireHttpClientFactory. Falls back to a default
    // HttpClient when Initialize is skipped (test paths).
    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string Name => "JSON-RPC";
    public string Description => "JSON-RPC 2.0 over HTTP or WebSocket — named methods with positional or keyword arguments.";
    public string Id => "jsonrpc";

    public void Initialize(IServiceProvider? serviceProvider)
    {
        var config = serviceProvider?.GetService<IConfiguration>();
        _http = BowireHttpClientFactory.Create(config, Id, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Dispose the lazily-built <see cref="HttpClient"/>. Same lifecycle
    /// contract as the other HTTP-based plugins — owners (registry or
    /// test 'using var') release the handler at scope exit.
    /// </summary>
    public void Dispose()
    {
        _http.Dispose();
    }

    // Generic JSON-RPC mark — three call-and-response arrows over a
    // square envelope; no vendor branding.
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" width="16" height="16" aria-hidden="true"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M7 10h6"/><path d="M11 10l-2-2"/><path d="M11 10l-2 2"/><path d="M17 14h-6"/><path d="M13 14l2 2"/><path d="M13 14l2-2"/></svg>""";

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (!TryResolveEndpoint(serverUrl, out var endpoint))
            return [];

        var client = new JsonRpcClient(_http, endpoint);
        JsonElement? openrpc;
        try
        {
            // OpenRPC's rpc.discover convention — the server returns an
            // OpenRPC document describing every method.
            openrpc = await client.CallAsync("rpc.discover", parameters: null, ct).ConfigureAwait(false);
        }
        catch (JsonRpcException)
        {
            // Either Method-Not-Found or any other RPC error — the server
            // is reachable as JSON-RPC, just doesn't advertise its
            // surface. Return an empty service tree; the user can still
            // invoke by hand.
            return [new BowireServiceInfo("Methods", "jsonrpc", new List<BowireMethodInfo>())
            {
                Source = "jsonrpc",
                OriginUrl = serverUrl,
                Description = "Server doesn't speak OpenRPC's rpc.discover — use Invoke with a method name.",
            }];
        }
        catch
        {
            // Transport-level failure (404, DNS, TLS) — we don't claim
            // the URL at all. Bowire's discovery loop tries other
            // plugins next.
            return [];
        }

        var methods = ExtractMethodsFromOpenRpc(openrpc!.Value);
        return
        [
            new BowireServiceInfo("Methods", "jsonrpc", methods)
            {
                Source = "jsonrpc",
                OriginUrl = serverUrl,
                Description = "JSON-RPC 2.0 methods discovered via OpenRPC.",
            }
        ];
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        if (!TryResolveEndpoint(serverUrl, out var endpoint))
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: $"Could not parse JSON-RPC server URL '{serverUrl}'. Expected http:// or https://.",
                Metadata: new Dictionary<string, string>());
        }

        var client = new JsonRpcClient(_http, endpoint, metadata);

        // Parameters can be either positional ([...]) or named ({...}).
        // We accept both shapes verbatim; empty/whitespace skips the
        // params field entirely.
        var parameters = ParseParameters(jsonMessages);

        try
        {
            var result = await client.CallAsync(method, parameters, ct).ConfigureAwait(false);
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            return new InvokeResult(
                Response: JsonSerializer.Serialize(result, s_indented),
                DurationMs: elapsedMs,
                Status: "OK",
                Metadata: new Dictionary<string, string>());
        }
        catch (JsonRpcException rpcEx)
        {
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            // Encode the spec's error code on the Status string so the
            // UI can distinguish JSON-RPC application errors from
            // transport / parse failures without an extra round-trip
            // through the message field.
            return new InvokeResult(
                Response: rpcEx.RpcData is { } d ? JsonSerializer.Serialize(d, s_indented) : rpcEx.Message,
                DurationMs: elapsedMs,
                Status: $"jsonrpc:{rpcEx.Code}",
                Metadata: new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            return new InvokeResult(
                Response: null,
                DurationMs: elapsedMs,
                Status: ex.Message,
                Metadata: new Dictionary<string, string>());
        }
    }

    public IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // JSON-RPC 2.0 has no streaming primitive. Server-to-client
        // notifications exist but need a separate transport (WS / SSE)
        // that the spec doesn't standardise. Leave streaming unwired
        // for now; embedded hosts that wrap JSON-RPC over WebSocket can
        // pair this plugin with the WebSocket plugin for that surface.
        return AsyncEnumerable.Empty<string>();
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    // -------- helpers --------

    /// <summary>
    /// Accept <c>http://</c> / <c>https://</c> URLs verbatim; reject
    /// anything else. The hint-prefix variant
    /// (<c>jsonrpc@http://...</c>) is stripped by Bowire's discovery
    /// dispatcher before the URL reaches the plugin, so we don't have
    /// to handle it here.
    /// </summary>
    private static bool TryResolveEndpoint(string? serverUrl, out Uri endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(serverUrl)) return false;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not "http" and not "https") return false;
        endpoint = uri;
        return true;
    }

    /// <summary>
    /// Decode <paramref name="jsonMessages"/>[0] into the JsonElement
    /// the JSON-RPC client will write as the <c>params</c> field.
    /// Empty or whitespace input means "no params" (the field is
    /// omitted entirely). The first message must be either a JSON
    /// object (named) or a JSON array (positional) — anything else
    /// also means "no params".
    /// </summary>
    internal static JsonElement? ParseParameters(List<string> jsonMessages)
    {
        if (jsonMessages.Count == 0 || string.IsNullOrWhiteSpace(jsonMessages[0]))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonMessages[0]);
            var root = doc.RootElement;
            if (root.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
                return null;
            return root.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Walk an OpenRPC document and pull out every method definition.
    /// We only need name + summary/description + parameter shape;
    /// the schemas-by-ref + complex content-descriptor system OpenRPC
    /// allows is flattened to a simple field list since Bowire's form
    /// UI doesn't render JSON-Schema natively.
    /// </summary>
    internal static List<BowireMethodInfo> ExtractMethodsFromOpenRpc(JsonElement document)
    {
        var methods = new List<BowireMethodInfo>();
        if (document.ValueKind != JsonValueKind.Object) return methods;
        if (!document.TryGetProperty("methods", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return methods;

        foreach (var m in arr.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;

            var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;

            var summary = m.TryGetProperty("summary", out var s) ? s.GetString() : null;
            var description = m.TryGetProperty("description", out var d) ? d.GetString() : null;

            var input = BuildInputType(name, m);
            var output = new BowireMessageInfo(name + "Result", "jsonrpc." + name + "Result", []);

            methods.Add(new BowireMethodInfo(
                Name: name,
                FullName: "Methods/" + name,
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: input,
                OutputType: output,
                MethodType: "Unary")
            {
                Summary = summary ?? description,
                Description = description ?? summary,
            });
        }
        return methods;
    }

    private static BowireMessageInfo BuildInputType(string methodName, JsonElement methodDef)
    {
        var fields = new List<BowireFieldInfo>();
        if (!methodDef.TryGetProperty("params", out var paramsArr)
            || paramsArr.ValueKind != JsonValueKind.Array)
        {
            return new BowireMessageInfo(methodName + "Input", methodName + "Input", fields);
        }

        var i = 1;
        foreach (var p in paramsArr.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            var pname = p.TryGetProperty("name", out var n) ? n.GetString() ?? $"arg{i}" : $"arg{i}";
            var pdesc = p.TryGetProperty("description", out var d) ? d.GetString() : null;
            var required = p.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;

            // OpenRPC nests JSON-Schema under .schema.type; treat
            // missing/non-string as 'string' (safe default for the
            // form UI).
            string type = "string";
            if (p.TryGetProperty("schema", out var sch) && sch.ValueKind == JsonValueKind.Object
                && sch.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            {
                type = t.GetString() ?? "string";
            }

            fields.Add(new BowireFieldInfo(
                Name: pname,
                Number: i++,
                Type: type,
                Label: required ? "required" : "optional",
                IsMap: false,
                IsRepeated: type == "array",
                MessageType: null,
                EnumValues: null)
            {
                Required = required,
                Description = pdesc,
                Source = "body",
            });
        }
        return new BowireMessageInfo(methodName + "Input", methodName + "Input", fields);
    }
}
