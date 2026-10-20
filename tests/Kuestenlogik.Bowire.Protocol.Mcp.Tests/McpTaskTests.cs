// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// MCP tasks on the client side (#801): a tool call that comes back as a
/// task, followed to its end, answered, cancelled — against an SDK-built
/// server for the 2026-07-28 extension, and against
/// <see cref="FakeTaskServer"/> for what that server cannot be made to do.
/// </summary>
public sealed class McpTaskTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Kind(JsonElement frame) => frame.GetProperty("event").GetString()!;
    private static JsonElement Data(JsonElement frame) => frame.GetProperty("data");
    private static string Status(JsonElement frame) => Data(frame).GetProperty("status").GetString()!;

    /// <summary>
    /// Read a stream frame by frame; <paramref name="onFrame"/> runs before
    /// the next frame is read, so a test can act on what it just saw.
    /// </summary>
    private static async Task<List<JsonElement>> Stream(
        BowireMcpProtocol protocol, string url, string service, string method, string body = "{}",
        Func<JsonElement, Task>? onFrame = null)
    {
        var frames = new List<JsonElement>();
        await foreach (var raw in protocol.InvokeStreamAsync(url, service, method, [body], false, null, Ct))
        {
            using var doc = JsonDocument.Parse(raw);
            var frame = doc.RootElement.Clone();
            frames.Add(frame);
            if (onFrame is not null) await onFrame(frame);
        }
        return frames;
    }

    /// <summary>
    /// The services, whatever the diagnostic says: this server has only
    /// tools, and the resources/prompts probes it answers with 404 are not
    /// what these tests are about.
    /// </summary>
    private static async Task<List<Kuestenlogik.Bowire.Models.BowireServiceInfo>> Discover(string url, BowireMcpProtocol? protocol = null) =>
        (await (protocol ?? new BowireMcpProtocol()).DiscoverWithDiagnosticsAsync(url, false, Ct)).Services;

    private static async Task<JsonElement> Unary(BowireMcpProtocol protocol, string url, string service, string method, object body)
    {
        var result = await protocol.InvokeAsync(url, service, method, [JsonSerializer.Serialize(body)], false, null, Ct);
        Assert.True(result.Status == "OK", result.Status);
        using var doc = JsonDocument.Parse(result.Response!);
        return doc.RootElement.Clone();
    }

    // ---- 2026-07-28 extension, SDK-built server ----

    [Fact]
    public async Task Discovery_Adds_A_Tasks_Service_When_The_Server_Advertises_The_Extension()
    {
        await using var server = await SdkTaskServer.StartAsync(Ct);
        var services = await Discover(server.Url);

        var tasks = Assert.Single(services, s => s.Name == "Tasks");
        Assert.Equal(["get", "follow", "update", "cancel"], tasks.Methods.Select(m => m.Name));
        Assert.True(tasks.Methods.Single(m => m.Name == "follow").ServerStreaming);
        Assert.Contains("2026-07-28", tasks.Description, StringComparison.Ordinal);
        Assert.Contains("as a task", services.Single(s => s.Name == "Tools").Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Task_That_Asks_For_Input_Is_Answered_With_Update_And_Ends_In_Its_Result()
    {
        await using var server = await SdkTaskServer.StartAsync(Ct);
        var protocol = new BowireMcpProtocol();
        var answered = false;

        var frames = await Stream(protocol, server.Url, "Tools", "ask", onFrame: async frame =>
        {
            if (Kind(frame) != "task" || Status(frame) != "input_required" || answered) return;
            answered = true;
            var data = Data(frame);
            var key = data.GetProperty("inputRequests").EnumerateObject().Single().Name;
            var ack = await Unary(protocol, server.Url, "Tasks", "update", new
            {
                taskId = data.GetProperty("taskId").GetString(),
                inputResponses = new Dictionary<string, object> { [key] = new { action = "accept", content = new { name = "Ada" } } },
            });
            Assert.True(ack.GetProperty("acknowledged").GetBoolean());
        });

        var first = frames[0];
        Assert.Equal("task", Kind(first));
        Assert.Equal("create", Data(first).GetProperty("via").GetString());
        Assert.Equal("2026-07-28", Data(first).GetProperty("shape").GetString());
        var input = frames.Single(f => Kind(f) == "task" && Status(f) == "input_required");
        Assert.Contains("Your name?", Data(input).GetProperty("inputRequests").GetRawText(), StringComparison.Ordinal);
        Assert.Equal("result", Kind(frames[^1]));
        Assert.Contains("hello Ada", Data(frames[^1]).GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_Is_Acknowledged_And_The_Followed_Task_Ends_Cancelled()
    {
        await using var server = await SdkTaskServer.StartAsync(Ct);
        var protocol = new BowireMcpProtocol();
        JsonElement? ack = null;

        var frames = await Stream(protocol, server.Url, "Tools", "slow", onFrame: async frame =>
        {
            if (ack is not null || Kind(frame) != "task") return;
            ack = await Unary(protocol, server.Url, "Tasks", "cancel", new { taskId = Data(frame).GetProperty("taskId").GetString() });
        });

        Assert.Contains("cooperative", ack!.Value.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Equal("cancelled", Status(frames[^1]));
        Assert.DoesNotContain(frames, f => Kind(f) == "result");
    }

    [Fact]
    public async Task A_Tool_The_Server_Runs_Synchronously_Is_Its_Result_Alone()
    {
        await using var server = await SdkTaskServer.StartAsync(Ct);
        var frame = Assert.Single(await Stream(new BowireMcpProtocol(), server.Url, "Tools", "echo", """{ "text": "hi" }"""));
        Assert.Equal("result", Kind(frame));
        Assert.Contains("hi", Data(frame).GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Synchronous_Tool_On_A_Task_Server_Still_Streams_Its_Progress()
    {
        await using var server = await SdkTaskServer.StartAsync(Ct);
        var frames = await Stream(new BowireMcpProtocol(), server.Url, "Tools", "count", """{ "steps": 3 }""");

        Assert.Contains(frames, f => Kind(f) == "progress");
        Assert.DoesNotContain(frames, f => Kind(f) == "task");
        Assert.Equal("result", Kind(frames[^1]));
    }

    [Fact]
    public async Task A_Unary_Call_Follows_The_Task_And_Answers_With_Its_Result()
    {
        await using var server = await SdkTaskServer.StartAsync(Ct);
        var result = await Unary(new BowireMcpProtocol(), server.Url, "Tools", "quick", new { });
        Assert.Contains("done", result.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Unary_Call_On_A_Task_That_Needs_Input_Says_How_To_Answer_It()
    {
        await using var server = await SdkTaskServer.StartAsync(Ct);
        var result = await new BowireMcpProtocol().InvokeAsync(server.Url, "Tools", "ask", ["{}"], false, null, Ct);
        Assert.Contains("needs input", result.Status, StringComparison.Ordinal);
        Assert.Contains("Tasks/update", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_And_Follow_Pick_Up_A_Task_By_Id()
    {
        await using var server = await SdkTaskServer.StartAsync(Ct);
        var protocol = new BowireMcpProtocol();
        var frames = await Stream(protocol, server.Url, "Tools", "quick");
        var taskId = Data(frames[0]).GetProperty("taskId").GetString();

        var got = await Unary(protocol, server.Url, "Tasks", "get", new { taskId });
        Assert.Equal("completed", got.GetProperty("status").GetString());
        Assert.Contains("done", got.GetProperty("result").GetRawText(), StringComparison.Ordinal);

        // What a reload does: follow the kept id again.
        var followed = await Stream(protocol, server.Url, "Tasks", "follow", JsonSerializer.Serialize(new { taskId }));
        Assert.Equal(["task", "result"], followed.Select(Kind));
        Assert.Equal("poll", Data(followed[0]).GetProperty("via").GetString());
    }

    [Fact]
    public async Task A_Tasks_Call_Without_A_TaskId_Is_Refused()
    {
        await using var server = await SdkTaskServer.StartAsync(Ct);
        var result = await new BowireMcpProtocol().InvokeAsync(server.Url, "Tasks", "get", ["{}"], false, null, Ct);
        Assert.Contains("taskId", result.Status, StringComparison.Ordinal);
    }

    // ---- 2026-07-28 extension, wire-level server ----

    [Fact]
    public async Task The_Call_Declares_The_Extension_On_The_Request_Itself()
    {
        await using var server = await FakeTaskServer.StartAsync(FakeTaskServer.Shape.Extension, Ct);
        await Stream(new BowireMcpProtocol(), server.Url, "Tools", "job", onFrame: frame =>
        {
            if (Kind(frame) == "task" && Status(frame) == "working")
                server.Transition(Data(frame).GetProperty("taskId").GetString()!, "completed", result: Text("ok"));
            return Task.CompletedTask;
        });
        Assert.True(server.LastCallOptedIn);
    }

    [Fact]
    public async Task Pushed_Status_Replaces_Polling_When_The_Server_Agrees_To_It()
    {
        await using var server = await FakeTaskServer.StartAsync(FakeTaskServer.Shape.Extension, Ct, pushStatus: true);
        var frames = await Stream(new BowireMcpProtocol(), server.Url, "Tools", "job", onFrame: frame =>
        {
            if (Kind(frame) != "task") return Task.CompletedTask;
            var taskId = Data(frame).GetProperty("taskId").GetString()!;
            // Move on only once Bowire is listening: the create frame comes
            // before the subscription, the first pushed one after it.
            if (Data(frame).GetProperty("via").GetString() == "create")
                _ = Task.Run(async () =>
                {
                    await server.Listening;
                    server.Transition(taskId, "working", message: "half way");
                });
            else if (Status(frame) == "working")
                server.Transition(taskId, "completed", result: Text("pushed"));
            return Task.CompletedTask;
        });

        Assert.All(frames.Where(f => Kind(f) == "task").Skip(1), f => Assert.Equal("notification", Data(f).GetProperty("via").GetString()));
        Assert.Contains("pushed", Data(frames[^1]).GetRawText(), StringComparison.Ordinal);
        Assert.Empty(server.Polls);
    }

    [Fact]
    public async Task A_Task_Past_Its_TTL_Is_Given_Up_On()
    {
        await using var server = await FakeTaskServer.StartAsync(FakeTaskServer.Shape.Extension, Ct, ttlMs: 150);
        var frames = await Stream(new BowireMcpProtocol(), server.Url, "Tools", "job");

        Assert.Equal("error", Kind(frames[^1]));
        Assert.Contains("past its TTL", Data(frames[^1]).GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Task_The_Server_Purged_Ends_The_Stream_With_Why()
    {
        await using var server = await FakeTaskServer.StartAsync(FakeTaskServer.Shape.Extension, Ct);
        var frames = await Stream(new BowireMcpProtocol(), server.Url, "Tools", "job", onFrame: frame =>
        {
            if (Kind(frame) == "task") server.Transition(Data(frame).GetProperty("taskId").GetString()!, "expired");
            return Task.CompletedTask;
        });

        var error = Data(frames[^1]);
        Assert.Equal("error", Kind(frames[^1]));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Contains("expired", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Failed_Task_Ends_In_Its_JsonRpc_Error()
    {
        await using var server = await FakeTaskServer.StartAsync(FakeTaskServer.Shape.Extension, Ct);
        var frames = await Stream(new BowireMcpProtocol(), server.Url, "Tools", "job", onFrame: frame =>
        {
            if (Kind(frame) == "task" && Status(frame) == "working")
            {
                server.Transition(Data(frame).GetProperty("taskId").GetString()!, "failed",
                    error: new JsonObject { ["code"] = -32603, ["message"] = "backend down" });
            }
            return Task.CompletedTask;
        });

        Assert.Equal("failed", Status(frames[^2]));
        Assert.Equal("error", Kind(frames[^1]));
        Assert.Equal("backend down", Data(frames[^1]).GetProperty("message").GetString());
        Assert.Equal(-32603, Data(frames[^1]).GetProperty("code").GetInt32());
    }

    // ---- 2025-11-25 core shape ----

    [Fact]
    public async Task A_2025_11_25_Server_Gets_The_Tasks_Service_And_Its_Task_Tools_Are_Marked()
    {
        await using var server = await FakeTaskServer.StartAsync(FakeTaskServer.Shape.Legacy, Ct);
        var services = await new BowireMcpProtocol().DiscoverAsync(server.Url, false, Ct);

        var tools = services.Single(s => s.Name == "Tools");
        Assert.Contains("taskSupport: required", tools.Methods.Single(m => m.Name == "job").Description, StringComparison.Ordinal);
        Assert.DoesNotContain("taskSupport", tools.Methods.Single(m => m.Name == "plain").Description ?? "", StringComparison.Ordinal);
        Assert.Contains("2025-11-25", services.Single(s => s.Name == "Tasks").Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_2025_11_25_Task_Is_Followed_And_Its_Result_Fetched_With_Tasks_Result()
    {
        await using var server = await FakeTaskServer.StartAsync(FakeTaskServer.Shape.Legacy, Ct);
        var frames = await Stream(new BowireMcpProtocol(), server.Url, "Tools", "job", onFrame: frame =>
        {
            if (Kind(frame) == "task" && Status(frame) == "working")
                server.Transition(Data(frame).GetProperty("taskId").GetString()!, "completed", result: Text("from tasks/result"));
            return Task.CompletedTask;
        });

        Assert.Equal("2025-11-25", Data(frames[0]).GetProperty("shape").GetString());
        Assert.Equal(20, Data(frames[0]).GetProperty("pollIntervalMs").GetInt64());
        Assert.Equal("result", Kind(frames[^1]));
        Assert.Contains("from tasks/result", Data(frames[^1]).GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_2025_11_25_Tool_Without_Task_Support_Is_Called_The_Ordinary_Way()
    {
        await using var server = await FakeTaskServer.StartAsync(FakeTaskServer.Shape.Legacy, Ct);
        var frame = Assert.Single(await Stream(new BowireMcpProtocol(), server.Url, "Tools", "plain"));
        Assert.Contains("ran without a task", Data(frame).GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_2025_11_25_Failure_Is_The_Error_Tasks_Result_Returns()
    {
        await using var server = await FakeTaskServer.StartAsync(FakeTaskServer.Shape.Legacy, Ct);
        var frames = await Stream(new BowireMcpProtocol(), server.Url, "Tools", "job", onFrame: frame =>
        {
            if (Kind(frame) == "task" && Status(frame) == "working")
            {
                server.Transition(Data(frame).GetProperty("taskId").GetString()!, "failed",
                    error: new JsonObject { ["code"] = -32603, ["message"] = "legacy boom" });
            }
            return Task.CompletedTask;
        });

        Assert.Equal("error", Kind(frames[^1]));
        Assert.Contains("legacy boom", Data(frames[^1]).GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answering_A_2025_11_25_Task_Says_Why_It_Cannot()
    {
        await using var server = await FakeTaskServer.StartAsync(FakeTaskServer.Shape.Legacy, Ct);
        var result = await new BowireMcpProtocol().InvokeAsync(server.Url, "Tasks", "update",
            ["""{ "taskId": "task-001", "inputResponses": {} }"""], false, null, Ct);
        Assert.Contains("elicitation/create", result.Status, StringComparison.Ordinal);
    }

    // ---- no tasks ----

    [Fact]
    public async Task A_Server_Without_Tasks_Has_No_Tasks_Service_And_Refuses_Tasks_Calls()
    {
        await using var server = await SdkTaskServer.StartAsync(Ct, withTasks: false);
        var protocol = new BowireMcpProtocol();
        var services = await Discover(server.Url, protocol);
        Assert.Contains(services, s => s.Name == "Tools");
        Assert.DoesNotContain(services, s => s.Name == "Tasks");

        var result = await protocol.InvokeAsync(server.Url, "Tasks", "get", ["""{ "taskId": "x" }"""], false, null, Ct);
        Assert.Contains("does not advertise tasks", result.Status, StringComparison.Ordinal);
    }

    private static JsonObject Text(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = false,
    };

    // ---- the SDK-built server ----

    private sealed class SdkTaskServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public static async Task<SdkTaskServer> StartAsync(CancellationToken ct, bool withTasks = true)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            var mcp = builder.Services.AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithTools<TaskTools>();
            if (withTasks)
            {
                // echo and count stay synchronous: the server, not the
                // client, decides which calls become tasks.
                mcp.WithTasks(new InMemoryMcpTaskStore { DefaultPollIntervalMs = 20 }, o => o.ExecutionModeSelector = ctx =>
                    ctx.MatchedPrimitive is McpServerTool { ProtocolTool.Name: "echo" or "count" }
                        ? McpTaskExecutionMode.Synchronous
                        : McpTaskExecutionMode.Optional);
            }
            var app = builder.Build();
            app.MapMcp();
            await app.StartAsync(ct);
            return new SdkTaskServer(app, app.Urls.First());
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [McpServerToolType]
    internal sealed class TaskTools
    {
        [McpServerTool(Name = "ask")]
        public static async Task<string> Ask(McpServer server, CancellationToken ct)
        {
            var answer = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = "Your name?",
                RequestedSchema = new()
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> { ["name"] = new ElicitRequestParams.StringSchema() },
                    Required = ["name"],
                },
            }, ct);
            return "hello " + answer.Content?["name"];
        }

        [McpServerTool(Name = "slow")]
        public static async Task<string> Slow(CancellationToken ct)
        {
            // Ends only by cancellation; the test cancels it.
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }

        [McpServerTool(Name = "quick")]
        public static string Quick() => "done";

        [McpServerTool(Name = "echo")]
        public static string Echo(string text) => text;

        [McpServerTool(Name = "count")]
        public static async Task<string> Count(IProgress<ProgressNotificationValue> progress, int steps, CancellationToken ct)
        {
            for (var i = 1; i <= steps; i++)
            {
                progress.Report(new ProgressNotificationValue { Progress = i, Total = steps });
                // Work between steps, as in McpNotificationStreamTests: a
                // report sent in the same instant as the response may be
                // dispatched after it, and is then (rightly) dropped.
                await Task.Delay(20, ct);
            }
            return $"counted {steps}";
        }
    }
}
