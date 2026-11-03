// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Net;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// #803 — a flow tests an MCP server's task lifecycle with one step,
/// <c>Tasks/run</c>: it starts the task, answers its input request, waits for
/// its end, and the flow's ordinary expectations assert on what it reports. A
/// task that ends otherwise than expected fails the flow and says how.
/// </summary>
public sealed class FlowTestRunnerMcpTaskTests : IDisposable
{
    private readonly string _tempDir = SafePath.Combine(Path.GetTempPath(), "bowire-flow-mcp-" + Guid.NewGuid().ToString("N"));

    public FlowTestRunnerMcpTaskTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private async Task<(int Code, string Output)> RunFlowAsync(string serverUrl, string expectedStatus)
    {
        // The step's body is the Tasks/run request, as a JSON string inside the flow.
        var flow = $$"""
        {
          "id":"flow_mcp_task",
          "name":"MCP task lifecycle",
          "nodes":[
            {
              "id":"ask",
              "type":"request",
              "protocol":"mcp",
              "serverUrl":"{{serverUrl}}",
              "service":"Tasks",
              "method":"run",
              "body":"{ \"tool\": \"ask\", \"inputResponses\": { \"*\": { \"action\": \"accept\", \"content\": { \"name\": \"Ada\" } } }, \"timeoutMs\": 30000 }",
              "expectations":[
                { "id":"e1","kind":"body-path","operator":"equals","target":"$.status","expected":"{{expectedStatus}}" },
                { "id":"e2","kind":"body-path","operator":"contains","target":"$.statuses","expected":"input_required" },
                { "id":"e3","kind":"body-path","operator":"contains","target":"$.result.content[0].text","expected":"hello Ada" }
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
    public async Task A_Flow_Waits_For_The_Task_Answers_Its_Input_And_Passes()
    {
        await using var server = await TaskServer.StartAsync(TestContext.Current.CancellationToken);
        var (code, output) = await RunFlowAsync(server.Url, "completed");

        Assert.True(code == 0, output);
        Assert.Contains("3/3 expectations passed", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Task_That_Ends_Otherwise_Than_Expected_Fails_The_Flow_And_Says_How()
    {
        await using var server = await TaskServer.StartAsync(TestContext.Current.CancellationToken);
        var (code, output) = await RunFlowAsync(server.Url, "cancelled");

        Assert.Equal(1, code);
        Assert.Contains("2/3 expectations passed", output, StringComparison.Ordinal);
        Assert.Contains("$.status", output, StringComparison.Ordinal);
        Assert.Contains("completed", output, StringComparison.Ordinal);
    }

    private sealed class TaskServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public static async Task<TaskServer> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Services.AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithTools<Tools>()
                .WithTasks(new InMemoryMcpTaskStore { DefaultPollIntervalMs = 20 }, o => o.ExecutionModeSelector = _ => McpTaskExecutionMode.Optional);
            var app = builder.Build();
            app.MapMcp();
            await app.StartAsync(ct);
            return new TaskServer(app, app.Urls.First());
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [McpServerToolType]
    internal sealed class Tools
    {
        [McpServerTool(Name = "ask")]
        [Description("Asks for a name, then greets it.")]
        public static async Task<string> Ask(McpServer server, CancellationToken ct)
        {
            var answer = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = "Your name?",
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> { ["name"] = new ElicitRequestParams.StringSchema() },
                },
            }, ct);
            return "hello " + (answer.Content?["name"].GetString() ?? "nobody");
        }
    }
}
