// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// #812 — a flow asserts on an MCP App with one step, <c>Apps/&lt;tool&gt;</c>:
/// the tool names an app, the app loads, and it is an MCP App document. An app
/// whose resource is missing fails the flow and says which one.
/// </summary>
public sealed class FlowTestRunnerMcpAppTests : IDisposable
{
    private readonly string _tempDir = SafePath.Combine(Path.GetTempPath(), "bowire-flow-mcp-app-" + Guid.NewGuid().ToString("N"));

    public FlowTestRunnerMcpAppTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private async Task<(int Code, string Output)> RunFlowAsync(string serverUrl, string tool)
    {
        var flow = $$"""
        {
          "id":"flow_mcp_app",
          "name":"MCP App",
          "nodes":[
            {
              "id":"app","type":"request","protocol":"mcp","serverUrl":"{{serverUrl}}",
              "service":"Apps","method":"{{tool}}","body":"{}",
              "expectations":[
                { "id":"e1","kind":"body-path","operator":"equals","target":"$.isMcpApp","expected":"true" },
                { "id":"e2","kind":"body-path","operator":"equals","target":"$.isHtml","expected":"true" }
              ]
            }
          ]
        }
        """;
        var ct = TestContext.Current.CancellationToken;
        var flowPath = SafePath.Combine(_tempDir, "flow.json");
        await File.WriteAllTextAsync(flowPath, flow, ct);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await FlowTestRunner.RunAsync(new FlowTestCliOptions { FlowPath = flowPath }, stdout, stderr, ct);
        return (rc, stdout + stderr.ToString());
    }

    [Fact]
    public async Task A_Flow_Passes_When_The_App_Loads_As_An_Mcp_App()
    {
        await using var server = await AppServer.StartAsync(TestContext.Current.CancellationToken);

        var (code, output) = await RunFlowAsync(server.Url, "board");

        Assert.True(code == 0, output);
        Assert.Contains("2/2 expectations passed", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Flow_Fails_And_Names_The_App_Whose_Resource_Is_Missing()
    {
        await using var server = await AppServer.StartAsync(TestContext.Current.CancellationToken);

        var (code, output) = await RunFlowAsync(server.Url, "broken");

        Assert.NotEqual(0, code);
        Assert.Contains("names the app ui://test/missing.html, which the server does not serve", output, StringComparison.Ordinal);
    }

    private sealed class AppServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public static async Task<AppServer> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            static JsonObject Ui(string uri) => new() { ["ui"] = new JsonObject { ["resourceUri"] = uri } };
            builder.Services.AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithTools(
                [
                    McpServerTool.Create(() => "board", new McpServerToolCreateOptions { Name = "board", Meta = Ui("ui://test/board.html") }),
                    McpServerTool.Create(() => "broken", new McpServerToolCreateOptions { Name = "broken", Meta = Ui("ui://test/missing.html") }),
                ])
                .WithResources(
                [
                    McpServerResource.Create(
                        () => new TextResourceContents { Uri = "ui://test/board.html", MimeType = BowireMcpProtocol.AppsMimeType, Text = "<!doctype html><p>board</p>" },
                        new McpServerResourceCreateOptions { UriTemplate = "ui://test/board.html", Name = "board", MimeType = BowireMcpProtocol.AppsMimeType }),
                ]);
            var app = builder.Build();
            app.MapMcp();
            await app.StartAsync(ct);
            return new AppServer(app, app.Urls.First());
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
