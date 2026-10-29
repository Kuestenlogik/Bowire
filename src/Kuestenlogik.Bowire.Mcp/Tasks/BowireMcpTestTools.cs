// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Mcp.Tasks;

/// <summary>
/// <c>bowire.test.run</c> (#802): what <c>bowire test</c> does, for agents.
/// It runs long, so a client that declares the Tasks extension gets a task
/// handle and follows the run's status (<c>step 3/12 · …</c>) instead of
/// holding the call open; every other client gets the synchronous answer.
/// </summary>
[McpServerToolType]
public sealed class BowireMcpTestTools
{
    /// <summary>The tool's name, also what the task selector matches.</summary>
    public const string TestRunName = "bowire.test.run";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [McpServerTool(Name = TestRunName)]
    [Description("Run a Bowire test collection or flow, as `bowire test` does: invoke every test, evaluate its assertions, and report what passed and failed. With `data` (a CSV or JSON file), the collection runs once per row, with the row's columns as variables; failures name the iteration and the source row. Runs as an MCP task for clients that declare the tasks extension (status messages report each step; tasks/cancel stops before the next step). Returns { exitCode, passed, output, errors } — exitCode 0 when everything passed, 1 when a test failed, 2 when the input was refused.")]
    public static async Task<string> TestRun(
        McpServer server,
        IServiceProvider services,
        [Description("Path to a test collection or flow file.")] string path,
        [Description("Optional: a CSV or JSON data set; the collection runs once per row.")] string? data = null,
        [Description("Optional, with data: stop after the first iteration that fails.")] bool failFast = false,
        [Description("Optional: write a JUnit XML report here.")] string? junitPath = null,
        [Description("Optional: write an HTML report here.")] string? reportPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(path)) return TestRunName + ": path is required.";
        var runner = services.GetService<IBowireTestRunner>();
        if (runner is null)
            return TestRunName + ": this host cannot run tests; run the tool through `bowire mcp serve`.";

        var store = services.GetService<BowireMcpTaskStore>();
        var taskId = BowireMcpTaskStore.CurrentTaskId;

        async Task Progress(string line)
        {
            if (store is null || taskId is null) return;
            var info = await store.SetStatusMessageAsync(taskId, line, cancellationToken).ConfigureAwait(false);
            if (info is null) return;
            try
            {
                await server.SendTaskStatusNotificationAsync(new WorkingTaskNotificationParams
                {
                    TaskId = info.TaskId,
                    StatusMessage = info.StatusMessage,
                    CreatedAt = info.CreatedAt,
                    LastUpdatedAt = info.LastUpdatedAt,
                    TimeToLive = info.TimeToLive,
                    PollIntervalMs = info.PollIntervalMs,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A stateless HTTP request has no channel back once it has
                // answered; tasks/get still reads the message from the store.
            }
        }

        var outcome = await runner.RunAsync(
            new BowireTestRunRequest(path, data, failFast, junitPath, reportPath), Progress, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            exitCode = outcome.ExitCode,
            passed = outcome.ExitCode == 0,
            output = outcome.Output,
            errors = outcome.Errors,
        }, s_json);
    }
}
