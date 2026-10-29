// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mcp.Tasks;
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
/// #802 — <c>bowire.test.run</c> as a durable MCP task on Bowire's own MCP
/// server. A runner that steps through gates the test opens stands in for
/// <c>bowire test</c>, so every wait is a handshake, not a sleep.
/// </summary>
public sealed class BowireMcpTestRunTaskTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _tasks = Directory.CreateTempSubdirectory("bowire-mcp-tasks-").FullName;
    private readonly GatedRunner _runner = new(steps: 3);
    private WebApplication _app = null!;
    private Uri _endpoint = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);
        builder.Services.AddSingleton<IBowireTestRunner>(_runner);
        builder.Services
            .AddBowireMcp(o => { o.LoadAllowlistFromEnvironments = false; o.TaskStorePath = _tasks; })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<BowireMcpTestTools>();
        _app = builder.Build();
        _app.MapBowireMcp("/mcp");
        await _app.StartAsync(Ct);
        _endpoint = new Uri($"http://127.0.0.1:{LoopbackHost.Port(_app.Services)}/mcp");
    }

    public async ValueTask DisposeAsync()
    {
        _runner.ReleaseAll();
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

    private static CallToolRequestParams Call(string path = "suite.json") => new()
    {
        Name = BowireMcpTestTools.TestRunName,
        Arguments = new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement(path) },
    };

    [Fact]
    public async Task The_Server_Advertises_The_Tasks_Extension()
    {
        await using var client = await ConnectAsync();
        Assert.True(client.ServerCapabilities?.Extensions?.ContainsKey(TasksProtocol.ExtensionId));
    }

    [Fact]
    public async Task A_Task_Capable_Client_Gets_A_Task_That_Reports_Its_Steps_And_Its_Result()
    {
        await using var client = await ConnectAsync();
        var outcome = await client.CallToolAsTaskAsync(Call(), Ct);
        Assert.True(outcome.IsTask);
        var taskId = outcome.TaskCreated!.TaskId;

        // The runner is held inside step 1: the task is working and says which step.
        await _runner.Reached(1);
        var working = await client.GetTaskAsync(taskId, Ct);
        Assert.Equal(McpTaskStatus.Working, working.Status);
        Assert.Equal("step 1/3 · fake", working.StatusMessage);

        _runner.ReleaseAll();
        var done = await PollUntilTerminalAsync(client, taskId);
        Assert.Equal(McpTaskStatus.Completed, done.Status);
        var result = Assert.IsType<CompletedTaskResult>(done);
        Assert.Contains("\"exitCode\": 0", Text(result.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Reconnecting_Client_Gets_The_Result_With_The_Same_Task_Id()
    {
        string taskId;
        await using (var first = await ConnectAsync())
        {
            taskId = (await first.CallToolAsTaskAsync(Call(), Ct)).TaskCreated!.TaskId;
            await _runner.Reached(1);
        }
        // The agent is gone; the run goes on.
        _runner.ReleaseAll();

        await using var second = await ConnectAsync();
        var done = await PollUntilTerminalAsync(second, taskId);
        Assert.Equal(McpTaskStatus.Completed, done.Status);
    }

    [Fact]
    public async Task Cancelling_Stops_The_Run_Before_Its_Next_Step()
    {
        await using var client = await ConnectAsync();
        var taskId = (await client.CallToolAsTaskAsync(Call(), Ct)).TaskCreated!.TaskId;
        await _runner.Reached(1);

        await client.CancelTaskAsync(taskId, Ct);
        _runner.ReleaseAll();

        var done = await PollUntilTerminalAsync(client, taskId);
        Assert.Equal(McpTaskStatus.Cancelled, done.Status);
        Assert.Equal(1, await _runner.StepsStarted());
    }

    [Fact]
    public async Task A_Client_Without_The_Extension_Gets_The_Answer_Directly()
    {
        _runner.ReleaseAll();
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(BowireMcpTestTools.TestRunName,
            new Dictionary<string, object?> { ["path"] = "suite.json" }, cancellationToken: Ct);

        Assert.Contains("\"exitCode\": 0", ((TextContentBlock)result.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Finished_Task_Outlives_The_Server_And_An_Unfinished_One_Fails_With_The_Reason()
    {
        _runner.ReleaseAll();
        await using var client = await ConnectAsync();
        var taskId = (await client.CallToolAsTaskAsync(Call(), Ct)).TaskCreated!.TaskId;
        await PollUntilTerminalAsync(client, taskId);

        // A fresh store over the same folder is a restarted server.
        var restarted = new BowireMcpTaskStore(_tasks);
        var kept = await restarted.GetTaskAsync(taskId, Ct);
        Assert.Equal(McpTaskStatus.Completed, kept!.Status);

        var orphan = (await new BowireMcpTaskStore(_tasks).CreateTaskAsync(Ct)).TaskId;
        var afterRestart = await new BowireMcpTaskStore(_tasks).GetTaskAsync(orphan, Ct);
        Assert.Equal(McpTaskStatus.Failed, afterRestart!.Status);
        Assert.Contains("stopped before this task finished", afterRestart.Error!.Value.GetRawText(), StringComparison.Ordinal);
    }

    private static async Task<GetTaskResult> PollUntilTerminalAsync(McpClient client, string taskId)
    {
        // tasks/get is the protocol's own wait: the server sets the interval.
        while (true)
        {
            var task = await client.GetTaskAsync(taskId, Ct);
            if (task.Status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled) return task;
            await Task.Delay(TimeSpan.FromMilliseconds(task.PollIntervalMs ?? 100), Ct);
        }
    }

    private static string Text(JsonElement callToolResult) =>
        callToolResult.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    /// <summary>
    /// A runner of <c>steps</c> steps that reports each, stops for a
    /// cancellation before the next, and holds in each step until the test
    /// releases it.
    /// </summary>
    private sealed class GatedRunner(int steps) : IBowireTestRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TaskCompletionSource> _reached = [.. Enumerable.Range(0, steps).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        private int _started;

        public void ReleaseAll() => _release.TrySetResult();

        public Task Reached(int step) => _reached[step - 1].Task.WaitAsync(TimeSpan.FromSeconds(30));

        public Task<int> StepsStarted() => Task.FromResult(Volatile.Read(ref _started));

        public async Task<BowireTestRunOutcome> RunAsync(BowireTestRunRequest request, Func<string, Task> progress, CancellationToken cancellationToken)
        {
            for (var step = 1; step <= steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _started);
                await progress($"step {step}/{steps} · fake");
                _reached[step - 1].TrySetResult();
                await _release.Task;
            }
            return new BowireTestRunOutcome(0, "3/3 tests passed", "");
        }
    }
}
