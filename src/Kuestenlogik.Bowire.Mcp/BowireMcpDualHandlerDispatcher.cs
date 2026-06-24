// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// Dispatches MCP <c>tools/*</c>, <c>resources/*</c> and
/// <c>prompts/*</c> traffic to the right backing implementation
/// (full server vs adapter) based on the URL the request hit.
/// </summary>
/// <remarks>
/// <para>
/// The MCP C# SDK installs a global tool/resource/prompt collection
/// populated by <c>WithTools&lt;T&gt;</c> + friends, and merges that
/// collection into every <c>tools/list</c> response — even when a
/// caller installs a <c>WithListToolsHandler</c> overlay (the SDK
/// wraps the overlay and *appends* the static tools to its result).
/// That means the union of static + dynamic tools leaks across
/// endpoints by default.
/// </para>
/// <para>
/// The dispatcher closes that leak by combining two SDK mechanisms:
/// </para>
/// <list type="number">
///   <item><b>WithListToolsHandler / WithCallToolHandler / &amp;c:</b>
///         the adapter's dynamic handlers fire here. For
///         <see cref="BowireMcpEndpointMode.Server"/> requests these
///         return empty results so the SDK's union ends up being
///         just the static tools; for
///         <see cref="BowireMcpEndpointMode.Adapter"/> they return
///         the dynamic ones.</item>
///   <item><b>Request filters (post-wrap):</b> a filter runs after
///         the SDK has merged dynamic + static tools and strips out
///         the wrong-side entries based on the URL path. So the
///         adapter URL's tools/list response loses the static tools,
///         and the server URL's response loses any dynamic overlay
///         (none today, but the filter keeps the contract sound).
///         The same gating applies to tools/call: dispatching a
///         server-side tool through the adapter URL throws a clean
///         "tool not found" rather than silently invoking it.</item>
/// </list>
/// <para>
/// Path routing reads
/// <see cref="HttpContext.Items"/>[<see cref="SessionRoutePathItemKey"/>],
/// which <see cref="BowireMcpHttpTransportSetup"/> latches at
/// session-init time. Falling back to live
/// <see cref="HttpRequest.Path"/> when Items is empty keeps the
/// path-detection sound in stateless transport mode.
/// </para>
/// </remarks>
public sealed class BowireMcpDualHandlerDispatcher
{
    private readonly BowireMcpEndpointRegistry _registry;

    // Adapter-side delegates. Null when AddBowireMcpAdapter wasn't
    // called. Assigning these is the contract Protocol.Mcp uses to
    // wire the adapter without touching the SDK builder directly.
    private McpRequestHandler<ListToolsRequestParams, ListToolsResult>? _adapterListTools;
    private McpRequestHandler<CallToolRequestParams, CallToolResult>? _adapterCallTool;
    private McpRequestHandler<ListResourcesRequestParams, ListResourcesResult>? _adapterListResources;
    private McpRequestHandler<ReadResourceRequestParams, ReadResourceResult>? _adapterReadResource;
    private McpRequestHandler<ListPromptsRequestParams, ListPromptsResult>? _adapterListPrompts;
    private McpRequestHandler<GetPromptRequestParams, GetPromptResult>? _adapterGetPrompt;

    public BowireMcpDualHandlerDispatcher(BowireMcpEndpointRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>
    /// Wire the adapter-side handlers. Called exactly once by the
    /// adapter's DI registration; subsequent calls overwrite (so the
    /// last AddBowireMcpAdapter wins, which matches the SDK's
    /// last-registration-wins convention for global handlers).
    /// </summary>
    public void RegisterAdapterHandlers(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> listTools,
        McpRequestHandler<CallToolRequestParams, CallToolResult> callTool,
        McpRequestHandler<ListResourcesRequestParams, ListResourcesResult> listResources,
        McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> readResource,
        McpRequestHandler<ListPromptsRequestParams, ListPromptsResult> listPrompts,
        McpRequestHandler<GetPromptRequestParams, GetPromptResult> getPrompt)
    {
        _adapterListTools = listTools;
        _adapterCallTool = callTool;
        _adapterListResources = listResources;
        _adapterReadResource = readResource;
        _adapterListPrompts = listPrompts;
        _adapterGetPrompt = getPrompt;
    }

    /// <summary>
    /// True when an adapter has registered itself with the dispatcher.
    /// The DI registration uses this flag to decide whether to install
    /// any of the With*Handler shims at all — without it, the SDK's
    /// own default ListTools/CallTool path (which reads the static
    /// <c>ToolCollection</c>) is left in place.
    /// </summary>
    public bool HasAdapter => _adapterListTools is not null;

    /// <summary>
    /// Marker set by <c>AddBowireMcp</c> after it wires the dispatcher
    /// handlers onto the SDK <see cref="IMcpServerBuilder"/>. The
    /// adapter's <c>AddBowireMcpAdapter</c> consults this so a host
    /// that wired the server first doesn't install the same
    /// With*Handler shims a second time.
    /// </summary>
    public bool HasInstalledSdkShims { get; set; }

    // ---- handler shims installed via WithListToolsHandler etc. ----------
    //
    // Adapter requests delegate to whatever the adapter wired up;
    // server-side requests return empty so the SDK's wrap-and-append
    // logic ends up surfacing only the static ToolCollection. The
    // filter pipeline (Filter* below) then strips dynamic tools from
    // server responses and static tools from adapter responses for
    // strict isolation.

    internal ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> ctx, CancellationToken ct)
    {
        if (IsAdapterRequest(ctx) && _adapterListTools is not null)
            return _adapterListTools(ctx, ct);
        // Server (or no adapter wired): empty handler result; SDK appends
        // the static ToolCollection. The filter on top is a no-op.
        return new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
    }

    internal ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
    {
        if (IsAdapterRequest(ctx) && _adapterCallTool is not null)
            return _adapterCallTool(ctx, ct);
        // Server-side: nothing dynamic to call. The SDK's own wrapper
        // (see ListTools dispatch in McpServer.SetupHandlers) checks
        // MatchedPrimitive first and routes static tool calls through
        // McpServerTool.InvokeAsync before reaching this fallback.
        throw new McpException($"Tool not found: {ctx.Params?.Name}");
    }

    internal ValueTask<ListResourcesResult> ListResourcesAsync(
        RequestContext<ListResourcesRequestParams> ctx, CancellationToken ct)
    {
        if (IsAdapterRequest(ctx) && _adapterListResources is not null)
            return _adapterListResources(ctx, ct);
        return new ValueTask<ListResourcesResult>(new ListResourcesResult { Resources = [] });
    }

    internal ValueTask<ReadResourceResult> ReadResourceAsync(
        RequestContext<ReadResourceRequestParams> ctx, CancellationToken ct)
    {
        if (IsAdapterRequest(ctx) && _adapterReadResource is not null)
            return _adapterReadResource(ctx, ct);
        throw new McpException($"Resource not found: {ctx.Params?.Uri}");
    }

    internal ValueTask<ListPromptsResult> ListPromptsAsync(
        RequestContext<ListPromptsRequestParams> ctx, CancellationToken ct)
    {
        if (IsAdapterRequest(ctx) && _adapterListPrompts is not null)
            return _adapterListPrompts(ctx, ct);
        return new ValueTask<ListPromptsResult>(new ListPromptsResult { Prompts = [] });
    }

    internal ValueTask<GetPromptResult> GetPromptAsync(
        RequestContext<GetPromptRequestParams> ctx, CancellationToken ct)
    {
        if (IsAdapterRequest(ctx) && _adapterGetPrompt is not null)
            return _adapterGetPrompt(ctx, ct);
        throw new McpException($"Prompt not found: {ctx.Params?.Name}");
    }

    // ---- request-side filters (run AFTER the SDK's static-merge) --------
    //
    // The SDK builds a pipeline where the WithListToolsHandler overlay
    // runs first, then the static ToolCollection gets appended, then
    // the filter chain runs on the merged result. The filter is the
    // only place we can drop static tools from adapter responses,
    // because by then they're already part of the merged ListToolsResult.

    internal McpRequestHandler<ListToolsRequestParams, ListToolsResult>
        FilterListToolsByPath(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next)
    {
        return async (ctx, ct) =>
        {
            var result = await next(ctx, ct).ConfigureAwait(false);
            if (result.Tools is null || result.Tools.Count == 0) return result;
            var isAdapter = IsAdapterRequest(ctx);
            var adapterToolNames = isAdapter ? CollectAdapterToolNames(ctx, ct) : null;

            // Drop tools that don't belong to the URL the request hit.
            // For the adapter path we keep tools NOT in the static
            // ToolCollection (i.e. those the adapter synthesised); for
            // the server path we keep tools that ARE in the static
            // collection. The split is robust because static tools
            // live in IOptions<McpServerOptions>.ToolCollection at
            // server startup and never change.
            var keep = new List<Tool>(result.Tools.Count);
            var staticNames = StaticToolNames(ctx);
            foreach (var tool in result.Tools)
            {
                var inStatic = staticNames.Contains(tool.Name);
                if (isAdapter && !inStatic) keep.Add(tool);
                else if (!isAdapter && inStatic) keep.Add(tool);
            }
            // Avoid mutating the result the SDK returned to other
            // filters — produce a fresh one.
            return new ListToolsResult
            {
                Tools = keep,
                NextCursor = result.NextCursor,
            };
        };
    }

    internal McpRequestHandler<CallToolRequestParams, CallToolResult>
        FilterCallToolByPath(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        return async (ctx, ct) =>
        {
            var isAdapter = IsAdapterRequest(ctx);
            var name = ctx.Params?.Name ?? string.Empty;
            var inStatic = StaticToolNames(ctx).Contains(name);
            if (isAdapter && inStatic)
            {
                // Calling a server tool through the adapter URL is a
                // routing error; surface clean "tool not found" rather
                // than silently invoking the server-side tool.
                throw new McpException($"Tool not found: {name}");
            }
            if (!isAdapter && !inStatic)
            {
                throw new McpException($"Tool not found: {name}");
            }
            return await next(ctx, ct).ConfigureAwait(false);
        };
    }

    private static HashSet<string> StaticToolNames<T>(RequestContext<T> ctx)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var options = ctx.Services?.GetService<
            Microsoft.Extensions.Options.IOptions<McpServerOptions>>()?.Value;
        if (options?.ToolCollection is null) return names;
        foreach (var primitive in options.ToolCollection)
        {
            names.Add(primitive.ProtocolTool.Name);
        }
        return names;
    }

    private HashSet<string> CollectAdapterToolNames(
        RequestContext<ListToolsRequestParams> ctx, CancellationToken ct)
    {
        if (_adapterListTools is null) return [];
        // We don't want to call the adapter twice per request — but
        // this is only invoked in the rare "is the tool name a static
        // one" path which doesn't fire on the hot loop. For simplicity
        // we materialise it synchronously via GetAwaiter; the adapter's
        // discover happens on a non-CPU-bound path so this is fine.
        var task = _adapterListTools(ctx, ct).AsTask();
        var result = task.GetAwaiter().GetResult();
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (result.Tools is not null)
        {
            foreach (var t in result.Tools) set.Add(t.Name);
        }
        return set;
    }

    // ---- request-path routing -----------------------------------------

    private bool IsAdapterRequest<T>(RequestContext<T> ctx)
    {
        // The streamable-HTTP transport runs in stateless mode by default
        // (see BowireMcpServiceCollectionExtensions.AddBowireMcp), which
        // reuses HttpContext.RequestServices as the McpServer's service
        // provider — so the IHttpContextAccessor sees the live request.
        // Stateful hosts get the path latched on HttpContext.Items at
        // session-init time (see BowireMcpHttpTransportSetup) so the
        // dispatcher still has a path to consult.
        var sp = ctx.Services;
        if (sp is null) return false;

        var http = sp.GetService<IHttpContextAccessor>()?.HttpContext;
        string? path = null;
        if (http is not null)
        {
            if (http.Items.TryGetValue(SessionRoutePathItemKey, out var stashed)
                && stashed is string s
                && !string.IsNullOrEmpty(s))
            {
                path = s;
            }
            else
            {
                path = http.Request.Path.Value;
            }
        }

        if (string.IsNullOrEmpty(path)) return false;
        return _registry.ResolveMode(path) == BowireMcpEndpointMode.Adapter;
    }

    /// <summary>
    /// Key under which the originating request URL is stashed on
    /// <see cref="HttpContext.Items"/> by
    /// <see cref="BowireMcpHttpTransportSetup"/>.
    /// </summary>
    public const string SessionRoutePathItemKey
        = "kuestenlogik.bowire.mcp.sessionRoutePath";
}
