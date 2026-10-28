// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// #819 — an SDK-built server with tools and nothing else, over stateless
/// HTTP. It answers resources/list and prompts/list with HTTP 404 carrying a
/// JSON-RPC -32601. A surface the server does not offer is absent, not a
/// fault: discovery hands back the tools and no diagnostic.
/// </summary>
public sealed class McpToolsOnlyDiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Tools_Only_Server_Discovers_Cleanly()
    {
        await using var server = await ToolsOnlyServer.StartAsync(Ct);

        var services = await new BowireMcpProtocol().DiscoverAsync(server.Url, showInternalServices: false, Ct);

        var tools = Assert.Single(services);
        Assert.Equal("Tools", tools.Name);
        Assert.Equal("ping", Assert.Single(tools.Methods).Name);
    }

    [Fact]
    public async Task A_Tools_Only_Server_Leaves_No_Diagnostic()
    {
        await using var server = await ToolsOnlyServer.StartAsync(Ct);

        var report = await new BowireMcpProtocol().DiscoverWithDiagnosticsAsync(server.Url, showInternalServices: false, Ct);

        Assert.Equal("Tools", Assert.Single(report.Services).Name);
        Assert.Null(report.Diagnostic);
    }

    private sealed class ToolsOnlyServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public static async Task<ToolsOnlyServer> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Services.AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithTools<PingTools>();
            var app = builder.Build();
            app.MapMcp();
            await app.StartAsync(ct);
            return new ToolsOnlyServer(app, app.Urls.First());
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [McpServerToolType]
    internal sealed class PingTools
    {
        [McpServerTool(Name = "ping")]
        public static string Ping() => "pong";
    }
}
