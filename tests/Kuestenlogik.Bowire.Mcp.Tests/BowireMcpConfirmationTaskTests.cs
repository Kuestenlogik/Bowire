// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Recording;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// #802 slice B — a mutator's confirmation, for a client that declares the
/// tasks extension: <c>bowire.record.start</c> runs as a task, asks through
/// an elicitation (the task turns <c>input_required</c>), and goes on or
/// ends declined with the answer the client sends through
/// <c>tasks/update</c>. A client without the extension keeps the token.
/// </summary>
public sealed class BowireMcpConfirmationTaskTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _tasks = Directory.CreateTempSubdirectory("bowire-mcp-confirm-").FullName;
    private WebApplication _app = null!;
    private Uri _endpoint = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);
        builder.Services
            .AddBowireMcp(o =>
            {
                o.LoadAllowlistFromEnvironments = false;
                o.RequireConfirmationForMutations = true;
                o.TaskStorePath = _tasks;
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<BowireMcpTools>();
        _app = builder.Build();
        _app.MapBowireMcp("/mcp");
        await _app.StartAsync(Ct);
        _endpoint = new Uri($"http://127.0.0.1:{LoopbackHost.Port(_app.Services)}/mcp");
    }

    public async ValueTask DisposeAsync()
    {
        _app.Services.GetRequiredService<BowireRecordingSession>().Stop();
        await _app.DisposeAsync();
        try { Directory.Delete(_tasks, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private async Task<McpClient> ConnectAsync()
    {
#pragma warning disable CA2000 // McpClient owns and disposes the transport.
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = _endpoint, TransportMode = HttpTransportMode.StreamableHttp });
#pragma warning restore CA2000
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static CallToolRequestParams RecordStart() => new()
    {
        Name = "bowire.record.start",
        Arguments = new Dictionary<string, JsonElement>
        {
            ["workspaceId"] = JsonSerializer.SerializeToElement("ws-1"),
            ["name"] = JsonSerializer.SerializeToElement("alpha"),
        },
    };

    private BowireRecordingSession Session => _app.Services.GetRequiredService<BowireRecordingSession>();

    [Fact]
    public async Task Confirming_Through_The_Elicitation_Starts_The_Recording()
    {
        await using var client = await ConnectAsync();
        var taskId = (await client.CallToolAsTaskAsync(RecordStart(), Ct)).TaskCreated!.TaskId;

        var key = await AwaitQuestionAsync(client, taskId);
        await AnswerAsync(client, taskId, key, """{"action":"accept","content":{"confirm":true}}""");

        var done = Assert.IsType<CompletedTaskResult>(await PollUntilTerminalAsync(client, taskId));
        Assert.Contains("\"started\":true", Text(done.Result), StringComparison.Ordinal);
        Assert.NotNull(Session.Active);
    }

    [Fact]
    public async Task Declining_Ends_The_Task_Without_Starting_Anything()
    {
        await using var client = await ConnectAsync();
        var taskId = (await client.CallToolAsTaskAsync(RecordStart(), Ct)).TaskCreated!.TaskId;

        var key = await AwaitQuestionAsync(client, taskId);
        await AnswerAsync(client, taskId, key, """{"action":"decline"}""");

        var done = Assert.IsType<CompletedTaskResult>(await PollUntilTerminalAsync(client, taskId));
        Assert.Contains("\"confirmed\":false", Text(done.Result), StringComparison.Ordinal);
        Assert.Contains("\"action\":\"decline\"", Text(done.Result), StringComparison.Ordinal);
        Assert.Null(Session.Active);
    }

    [Fact]
    public async Task A_Client_Without_The_Extension_Keeps_The_Confirmation_Token()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("bowire.record.start",
            new Dictionary<string, object?> { ["workspaceId"] = "ws-1", ["name"] = "alpha" }, cancellationToken: Ct);

        var text = ((TextContentBlock)result.Content[0]).Text;
        Assert.Contains("\"confirmationToken\"", text, StringComparison.Ordinal);
        Assert.Null(Session.Active);
    }

    // The task asks once; returns the key the answer must carry.
    private static async Task<string> AwaitQuestionAsync(McpClient client, string taskId)
    {
        while (true)
        {
            var task = await client.GetTaskAsync(taskId, Ct);
            if (task is InputRequiredTaskResult asking)
            {
                var (key, request) = Assert.Single(asking.InputRequests!);
                Assert.Equal("elicitation/create", request.Method);
                Assert.Contains("bowire.record.start", request.Params!.Value.GetRawText(), StringComparison.Ordinal);
                return key;
            }
            Assert.Equal(McpTaskStatus.Working, task.Status);
            await Task.Delay(TimeSpan.FromMilliseconds(task.PollIntervalMs ?? 100), Ct);
        }
    }

    private static async Task AnswerAsync(McpClient client, string taskId, string key, string response)
    {
        var responses = JsonSerializer.Deserialize("{\"" + key + "\":" + response + "}", McpTasksJsonContext.Default.IDictionaryStringInputResponse)!;
        await client.UpdateTaskAsync(new UpdateTaskRequestParams { TaskId = taskId, InputResponses = responses }, Ct);
    }

    private static async Task<GetTaskResult> PollUntilTerminalAsync(McpClient client, string taskId)
    {
        while (true)
        {
            var task = await client.GetTaskAsync(taskId, Ct);
            if (task.Status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled) return task;
            await Task.Delay(TimeSpan.FromMilliseconds(task.PollIntervalMs ?? 100), Ct);
        }
    }

    private static string Text(JsonElement callToolResult) =>
        callToolResult.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
}
