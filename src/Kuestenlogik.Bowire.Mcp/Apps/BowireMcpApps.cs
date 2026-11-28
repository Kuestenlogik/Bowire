// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Mcp.Apps;

/// <summary>
/// #811 — the MCP Apps Bowire's own MCP server ships, and how they reach a
/// host: a client that declared <c>io.modelcontextprotocol/ui</c> sees the
/// tools' <c>_meta.ui</c> and the <c>ui://</c> resources; every other client
/// gets the tools and the JSON exactly as before.
/// </summary>
public static class BowireMcpApps
{
    /// <summary>The test-run app's resource, as <c>[McpAppUi]</c> on <c>bowire.test.run</c> names it.</summary>
    public const string TestRunUri = "ui://bowire/test-run.html";

    /// <summary>The discovery app's resource, as <c>[McpAppUi]</c> on <c>bowire.discover</c> names it.</summary>
    public const string DiscoverUri = "ui://bowire/discover.html";

    /// <summary><c>bowire.test.run</c>'s result, step by step, with a re-run per test.</summary>
    public static readonly BowireMcpApp TestRun = new(
        TestRunUri, "bowire-test-run",
        "The result of bowire.test.run: every test or flow step with its checks, and a re-run per test.",
        "test-run.html");

    /// <summary><c>bowire.discover</c>'s services and methods, browsable, each invokable.</summary>
    public static readonly BowireMcpApp Discover = new(
        DiscoverUri, "bowire-discover",
        "The services and methods bowire.discover found, each that takes one request invokable through bowire.invoke.",
        "discover.html");

    /// <summary>Every app, in the order they are listed.</summary>
    public static IReadOnlyList<BowireMcpApp> All { get; } = [TestRun, Discover];

    /// <summary>
    /// Serve the apps. Call after the tools are registered: the tools'
    /// <c>[McpAppUi]</c> become their <c>_meta.ui</c> here.
    /// </summary>
    public static IMcpServerBuilder WithBowireMcpApps(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithResources([.. All.Select(a => a.Resource())])
            .WithMcpApps()
            .WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(next => async (ctx, ct) =>
                {
                    var result = await next(ctx, ct).ConfigureAwait(false);
                    return HostsApps(ctx.Server) ? result : WithoutUi(result);
                });
                filters.AddListResourcesFilter(next => async (ctx, ct) =>
                {
                    var result = await next(ctx, ct).ConfigureAwait(false);
                    if (HostsApps(ctx.Server) || result.Resources is null) return result;
                    result.Resources = [.. result.Resources.Where(r => !IsApp(r.Uri))];
                    return result;
                });
            });
    }

    /// <summary>Whether the client on this request declared the MCP Apps extension.</summary>
    internal static bool HostsApps(McpServer server) => McpApps.GetUiCapability(server.ClientCapabilities) is not null;

    internal static bool IsApp(string? uri) => All.Any(a => string.Equals(a.ResourceUri, uri, StringComparison.Ordinal));

    // The tools are the server's own instances, shared by every request:
    // each one that carries _meta.ui goes out as a copy without it.
    private static ListToolsResult WithoutUi(ListToolsResult result)
    {
        if (result.Tools is null || result.Tools.All(t => t.Meta?.ContainsKey("ui") != true)) return result;
        result.Tools = [.. result.Tools.Select(t =>
        {
            if (t.Meta?.ContainsKey("ui") != true) return t;
            var copy = JsonSerializer.Deserialize<Tool>(JsonSerializer.SerializeToUtf8Bytes(t, McpJsonUtilities.DefaultOptions), McpJsonUtilities.DefaultOptions)!;
            copy.Meta!.Remove("ui");
            if (copy.Meta.Count == 0) copy.Meta = null;
            return copy;
        })];
        return result;
    }
}
