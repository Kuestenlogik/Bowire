// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Recording;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// DI registration for <see cref="BowireMcpTools"/>. Registers the
/// Bowire protocol registry as a singleton and pulls the
/// <c>ModelContextProtocol</c> server fluent API in. Choose the transport
/// in your <c>Program.cs</c> after calling this — stdio for the CLI,
/// HTTP/SSE for the embedded host:
/// <code>
/// // stdio (CLI)
/// services.AddBowireMcp(o => o.AllowArbitraryUrls = false)
///         .WithStdioServerTransport()
///         .WithTools&lt;BowireMcpTools&gt;()
///         .WithResources&lt;BowireMcpResources&gt;()
///         .WithPrompts&lt;BowireMcpPrompts&gt;();
///
/// // HTTP/SSE (embedded)
/// services.AddBowireMcp()
///         .WithHttpTransport(o => o.Stateless = true)
///         .WithTools&lt;BowireMcpTools&gt;()
///         .WithResources&lt;BowireMcpResources&gt;()
///         .WithPrompts&lt;BowireMcpPrompts&gt;();
/// // …then `app.MapBowireMcp();` (defaults to /bowire/mcp)
/// </code>
/// </summary>
public static class BowireMcpServiceCollectionExtensions
{
    /// <summary>
    /// Register Bowire-MCP services and return the SDK's
    /// <see cref="IMcpServerBuilder"/> so the caller can chain a
    /// transport (<c>WithStdioServerTransport</c> or
    /// <c>WithHttpTransport</c>) and tool registration.
    /// </summary>
    public static IMcpServerBuilder AddBowireMcp(
        this IServiceCollection services,
        Action<BowireMcpOptions>? configure = null)
    {
        services.AddOptions<BowireMcpOptions>();
        if (configure is not null) services.Configure(configure);

        // The protocol registry is shared across every tool call. Use a
        // singleton so the assembly-scan cost is paid once at startup.
        // The factory takes ILoggerFactory so plugin-load failures land
        // in the host's normal logging pipeline.
        services.AddSingleton(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("Kuestenlogik.Bowire.Mcp");
            return BowireProtocolRegistry.Discover(logger);
        });

        // Mock handle registry — backs bowire.mock.start / .stop / .list.
        // Singleton so handles outlive individual tool calls; disposal
        // happens with the host so a Ctrl+C cleanly shuts down spawned
        // mock instances.
        services.AddSingleton<BowireMockHandleRegistry>();

        // Pending-confirmation store — backs the two-step confirm
        // pattern on mutator tools (#37). Singleton so tokens issued
        // by one tool call can be redeemed by a later one in the
        // same session.
        services.AddSingleton<BowireMcpConfirmationStore>();

        // Server-side recording session (#285). The MCP record.start /
        // stop / replay tools mutate this singleton; when the embedded
        // workbench host also calls AddBowire(), TryAddSingleton there
        // sees this descriptor and skips its own registration so both
        // paths share one instance — meaning a CLI agent's record.start
        // and the workbench's badge end up looking at the same state.
        services.TryAddSingleton<BowireRecordingSession>();

        // Dual-mount coordination (#287). The registry tracks every
        // MCP endpoint mounted on the host so MapBowireMcp +
        // MapBowireMcpAdapter can coexist without clobbering each
        // other; the dispatcher routes tools/list, tools/call, &c. to
        // either the static server tools or the adapter handlers based
        // on which prefix the incoming request hit. Both are TryAdd
        // singletons so the adapter's own AddBowireMcpAdapter can wire
        // them when AddBowireMcp wasn't called.
        services.TryAddSingleton<BowireMcpEndpointRegistry>();
        services.TryAddSingleton<BowireMcpDualHandlerDispatcher>();

        // The dispatcher reads HttpContext.Request.Path inside the
        // SDK's session-scoped RequestContext. The accessor is required
        // for that lookup; idempotent on repeat registration.
        services.AddHttpContextAccessor();

        // Per-session path-latching: stash the request URL on the
        // dispatcher at the moment the SDK creates an McpServer for
        // a freshly-arrived JSON-RPC session. This is the robust
        // routing signal — the IHttpContextAccessor path can come
        // back stale or null in stateful transport mode where the
        // long-lived server runs outside the original request scope.
        // The setup wraps any existing caller-supplied
        // ConfigureSessionOptions delegate so the test-suite + embedded
        // host can still install their own callbacks.
        services.AddSingleton<IConfigureOptions<HttpServerTransportOptions>,
            BowireMcpHttpTransportSetup>();

        var builder = services.AddMcpServer(o =>
        {
            o.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "bowire-mcp",
                Version = typeof(BowireMcpTools).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Title = "Bowire workbench (self-MCP)"
            };
        });

        // Install dispatcher-aware handlers. With both endpoints mounted,
        // these route per-request: server-mode requests fall through to
        // the SDK's tool/resource/prompt collections (populated by
        // WithTools<T>/WithResources<T>/WithPrompts<T> in the caller's
        // builder chain), adapter-mode requests forward to whatever
        // delegates the adapter registered via
        // BowireMcpDualHandlerDispatcher.RegisterAdapterHandlers.
        //
        // Resolving the dispatcher from the *root* container at builder
        // time is safe — it's a singleton and the field reads inside the
        // delegates are all final at request time.
        var dispatcher = ResolveDispatcherFromCollection(services);
        builder
            .WithListToolsHandler(dispatcher.ListToolsAsync)
            .WithCallToolHandler(dispatcher.CallToolAsync)
            .WithListResourcesHandler(dispatcher.ListResourcesAsync)
            .WithReadResourceHandler(dispatcher.ReadResourceAsync)
            .WithListPromptsHandler(dispatcher.ListPromptsAsync)
            .WithGetPromptHandler(dispatcher.GetPromptAsync);

        // Post-merge filters. The SDK's tool/list dispatch wraps our
        // handler and appends ToolCollection — so by the time the
        // result reaches the wire it always contains the static tools.
        // The filter runs after that merge and strips out the
        // wrong-side entries based on the request URL: adapter responses
        // drop static tools, server responses drop dynamic ones (none
        // in practice today, but the filter keeps the contract sound
        // even if a custom AddBowireMcpAdapter installs additional
        // tools).
        builder.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(dispatcher.FilterListToolsByPath);
            filters.AddCallToolFilter(dispatcher.FilterCallToolByPath);
        });
        dispatcher.HasInstalledSdkShims = true;

        return builder;
    }

    // Pulls the dispatcher out of the (still-being-configured) service
    // collection without building the provider. Safe because we just
    // registered it as a singleton above; we instantiate eagerly to
    // close over a stable reference inside the With*Handler delegates.
    private static BowireMcpDualHandlerDispatcher ResolveDispatcherFromCollection(
        IServiceCollection services)
    {
        // The pair was added via TryAddSingleton just above, so the
        // descriptors are present. Find them, instantiate by hand, and
        // replace the descriptors with the materialised instances so
        // every other consumer (e.g. the adapter's AddBowireMcpAdapter
        // call later) ends up with the same dispatcher.
        var registry = MaterialiseSingleton<BowireMcpEndpointRegistry>(services,
            () => new BowireMcpEndpointRegistry());
        var dispatcher = MaterialiseSingleton(services,
            () => new BowireMcpDualHandlerDispatcher(registry));
        return dispatcher;
    }

    private static T MaterialiseSingleton<T>(IServiceCollection services, Func<T> factory)
        where T : class
    {
        for (var i = 0; i < services.Count; i++)
        {
            var d = services[i];
            if (d.ServiceType != typeof(T)) continue;
            if (d.ImplementationInstance is T existing) return existing;
            var created = factory();
            services[i] = ServiceDescriptor.Singleton<T>(created);
            return created;
        }
        // Not yet registered — add a singleton-instance descriptor so
        // later registrants find this same instance.
        var fresh = factory();
        services.AddSingleton(fresh);
        return fresh;
    }

    /// <summary>
    /// Register a forwarder-mode MCP server (#286). Every incoming MCP
    /// request is relayed to <paramref name="parentEndpoint"/> via an
    /// outbound <see cref="BowireForwardingMcpTransport"/>; no Bowire
    /// tools / resources / prompts are bound locally — the parent's
    /// surface is what the LLM caller sees.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="parentEndpoint">
    /// HTTP(S) URI of the parent Bowire MCP endpoint (e.g.
    /// <c>http://localhost:5198/bowire/mcp</c>).
    /// </param>
    /// <param name="bearerToken">
    /// Optional bearer token; attached as <c>Authorization: Bearer …</c>
    /// on every request to the parent. Required when the parent has
    /// <c>--token</c> set.
    /// </param>
    public static IMcpServerBuilder AddBowireMcpForwarder(
        this IServiceCollection services,
        Uri parentEndpoint,
        string? bearerToken = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(parentEndpoint);

        // Singleton so the parent connection is established once + reused
        // across every incoming MCP request. The host's shutdown calls
        // IAsyncDisposable.DisposeAsync, which closes the parent client.
        services.AddSingleton(_ => new BowireForwardingMcpTransport(parentEndpoint, bearerToken));

        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation
                {
                    Name = "bowire-mcp-forwarder",
                    Version = typeof(BowireForwardingMcpTransport).Assembly
                        .GetName().Version?.ToString() ?? "0.0.0",
                    Title = "Bowire workbench (MCP forwarder)",
                };
            })
            .WithListToolsHandler(ForwardListToolsAsync)
            .WithCallToolHandler(ForwardCallToolAsync)
            .WithListPromptsHandler(ForwardListPromptsAsync)
            .WithGetPromptHandler(ForwardGetPromptAsync)
            .WithListResourcesHandler(ForwardListResourcesAsync)
            .WithReadResourceHandler(ForwardReadResourceAsync)
            .WithListResourceTemplatesHandler(ForwardListResourceTemplatesAsync);
    }

    // ----- forwarder handlers ----------------------------------------
    //
    // Every handler resolves the singleton forwarder, gets (or lazily
    // builds) the parent McpClient, and re-issues the typed request.
    // McpException from the parent surfaces verbatim; non-MCP exceptions
    // are wrapped so the SDK turns them into a JSON-RPC error envelope
    // with the upstream cause attached.

    private static async ValueTask<ListToolsResult> ForwardListToolsAsync(
        RequestContext<ListToolsRequestParams> ctx, CancellationToken ct)
    {
        var forwarder = ResolveForwarder(ctx);
        var client = await GetParentClientOrThrow(forwarder, ct).ConfigureAwait(false);
        return await client.ListToolsAsync(ctx.Params ?? new ListToolsRequestParams(), ct).ConfigureAwait(false);
    }

    private static async ValueTask<CallToolResult> ForwardCallToolAsync(
        RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
    {
        var forwarder = ResolveForwarder(ctx);
        var client = await GetParentClientOrThrow(forwarder, ct).ConfigureAwait(false);
        var p = ctx.Params ?? throw new McpException("tools/call request had no parameters.");
        return await client.CallToolAsync(p, ct).ConfigureAwait(false);
    }

    private static async ValueTask<ListPromptsResult> ForwardListPromptsAsync(
        RequestContext<ListPromptsRequestParams> ctx, CancellationToken ct)
    {
        var forwarder = ResolveForwarder(ctx);
        var client = await GetParentClientOrThrow(forwarder, ct).ConfigureAwait(false);
        return await client.ListPromptsAsync(ctx.Params ?? new ListPromptsRequestParams(), ct).ConfigureAwait(false);
    }

    private static async ValueTask<GetPromptResult> ForwardGetPromptAsync(
        RequestContext<GetPromptRequestParams> ctx, CancellationToken ct)
    {
        var forwarder = ResolveForwarder(ctx);
        var client = await GetParentClientOrThrow(forwarder, ct).ConfigureAwait(false);
        var p = ctx.Params ?? throw new McpException("prompts/get request had no parameters.");
        return await client.GetPromptAsync(p, ct).ConfigureAwait(false);
    }

    private static async ValueTask<ListResourcesResult> ForwardListResourcesAsync(
        RequestContext<ListResourcesRequestParams> ctx, CancellationToken ct)
    {
        var forwarder = ResolveForwarder(ctx);
        var client = await GetParentClientOrThrow(forwarder, ct).ConfigureAwait(false);
        return await client.ListResourcesAsync(ctx.Params ?? new ListResourcesRequestParams(), ct).ConfigureAwait(false);
    }

    private static async ValueTask<ReadResourceResult> ForwardReadResourceAsync(
        RequestContext<ReadResourceRequestParams> ctx, CancellationToken ct)
    {
        var forwarder = ResolveForwarder(ctx);
        var client = await GetParentClientOrThrow(forwarder, ct).ConfigureAwait(false);
        var p = ctx.Params ?? throw new McpException("resources/read request had no parameters.");
        return await client.ReadResourceAsync(p, ct).ConfigureAwait(false);
    }

    private static async ValueTask<ListResourceTemplatesResult> ForwardListResourceTemplatesAsync(
        RequestContext<ListResourceTemplatesRequestParams> ctx, CancellationToken ct)
    {
        var forwarder = ResolveForwarder(ctx);
        var client = await GetParentClientOrThrow(forwarder, ct).ConfigureAwait(false);
        return await client.ListResourceTemplatesAsync(ctx.Params ?? new ListResourceTemplatesRequestParams(), ct).ConfigureAwait(false);
    }

    private static BowireForwardingMcpTransport ResolveForwarder<T>(RequestContext<T> ctx)
    {
        var services = ctx.Services
            ?? throw new McpException("Forwarder handler ran without a service provider — DI wiring is broken.");
        return services.GetRequiredService<BowireForwardingMcpTransport>();
    }

    private static async Task<ModelContextProtocol.Client.McpClient> GetParentClientOrThrow(
        BowireForwardingMcpTransport forwarder, CancellationToken ct)
    {
        try
        {
            return await forwarder.GetClientAsync(ct).ConfigureAwait(false);
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Surface the upstream cause in the JSON-RPC error message so
            // the caller can tell a "parent unreachable" from a "tool
            // failed" — same convention as the protocol adapter.
            throw new McpException(
                $"Bowire MCP forwarder: failed to reach parent endpoint '{forwarder.ParentEndpoint}' — {ex.Message}", ex);
        }
    }
}
