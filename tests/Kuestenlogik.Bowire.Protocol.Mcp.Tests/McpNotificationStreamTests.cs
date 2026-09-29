// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// What an MCP server says while a tool runs, on Bowire's stream (#46).
/// </summary>
/// <remarks>
/// Against a server the SDK builds, so the notifications are the real wire
/// shape: progress tied to the call's progress token, log messages as
/// <c>notifications/message</c>. The tool's result is always the last frame.
/// </remarks>
public sealed class McpNotificationStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<JsonElement>> Stream(string url, string service, string method, string args = "{}")
    {
        var protocol = new BowireMcpProtocol();
        var frames = new List<JsonElement>();
        await foreach (var frame in protocol.InvokeStreamAsync(url, service, method, [args], false, null, Ct))
        {
            using var doc = JsonDocument.Parse(frame);
            frames.Add(doc.RootElement.Clone());
        }
        return frames;
    }

    private static string Kind(JsonElement frame) => frame.GetProperty("event").GetString()!;

    [Fact]
    public async Task Progress_Arrives_In_Order_And_The_Result_Is_The_Last_Frame()
    {
        await using var server = await McpTestServer.StartAsync(Ct);
        var frames = await Stream(server.Url, "Tools", "count", """{ "steps": 3 }""");

        Assert.Equal(["progress", "progress", "progress", "result"], frames.Where(f => Kind(f) != "log").Select(Kind));
        Assert.Equal([1d, 2d, 3d], frames.Where(f => Kind(f) == "progress").Select(f => f.GetProperty("data").GetProperty("progress").GetDouble()));
        Assert.Contains("counted 3", frames[^1].GetProperty("data").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_Messages_From_The_Server_Come_Through_Before_The_Result()
    {
        await using var server = await McpTestServer.StartAsync(Ct);
        var frames = await Stream(server.Url, "Tools", "count", """{ "steps": 2 }""");

        var logs = frames.Where(f => Kind(f) == "log").ToList();
        Assert.Equal(2, logs.Count);
        Assert.Contains("step 1", logs[0].GetProperty("data").GetRawText(), StringComparison.Ordinal);
        Assert.Equal("result", Kind(frames[^1]));
    }

    [Fact]
    public async Task A_Tool_That_Says_Nothing_Is_Its_Result_Alone()
    {
        await using var server = await McpTestServer.StartAsync(Ct);
        var frame = Assert.Single(await Stream(server.Url, "Tools", "quiet"));
        Assert.Equal("result", Kind(frame));
        Assert.Contains("shh", frame.GetProperty("data").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Tool_That_Fails_Ends_The_Stream_With_Its_Error_Result()
    {
        await using var server = await McpTestServer.StartAsync(Ct);
        var frames = await Stream(server.Url, "Tools", "fails");
        var last = frames[^1];
        // MCP reports a tool's own failure as a result with isError, not as a
        // protocol error — the stream shows exactly what the server said.
        Assert.Equal("result", Kind(last));
        Assert.True(last.GetProperty("data").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task A_Resource_Is_One_Result_Frame()
    {
        await using var server = await McpTestServer.StartAsync(Ct);
        var frame = Assert.Single(await Stream(server.Url, "Resources", "test://greeting"));
        Assert.Equal("result", Kind(frame));
        Assert.Contains("hello", frame.GetProperty("data").GetRawText(), StringComparison.Ordinal);
    }

    // ---- the server ----

    private sealed class McpTestServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public static async Task<McpTestServer> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Services.AddMcpServer()
                .WithHttpTransport()
                .WithTools<Tools>()
                .WithResources<Resources>();
            var app = builder.Build();
            app.MapMcp();
            await app.StartAsync(ct);
            return new McpTestServer(app, app.Urls.First());
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
        [McpServerTool(Name = "count")]
        [Description("Counts to steps, reporting progress and logging each step.")]
        public static async Task<string> Count(McpServer server, IProgress<ProgressNotificationValue> progress, int steps, CancellationToken ct)
        {
            for (var i = 1; i <= steps; i++)
            {
                // Deprecated in the newest spec, still what older servers send.
#pragma warning disable MCP9005
                await server.SendNotificationAsync(
                    NotificationMethods.LoggingMessageNotification,
                    new LoggingMessageNotificationParams { Level = LoggingLevel.Info, Data = JsonSerializer.SerializeToElement($"step {i}") },
                    cancellationToken: ct);
#pragma warning restore MCP9005
                progress.Report(new ProgressNotificationValue { Progress = i, Total = steps });
                await Task.Delay(20, ct);
            }
            return $"counted {steps}";
        }

        [McpServerTool(Name = "quiet")]
        [Description("Says nothing while it runs.")]
        public static string Quiet() => "shh";

        [McpServerTool(Name = "fails")]
        [Description("Always fails.")]
        public static string Fails() => throw new InvalidOperationException("broken on purpose");
    }

    [McpServerResourceType]
    internal sealed class Resources
    {
        [McpServerResource(UriTemplate = "test://greeting", Name = "greeting")]
        [Description("A fixed text.")]
        public static string Greeting() => "hello";
    }
}
