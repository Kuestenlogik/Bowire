// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Mcp.Tasks;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #802 / #945 — the runner behind <c>bowire.test.run</c>: the real
/// <c>TestRunner</c> against the stub protocol (which echoes the request's
/// id), reporting a line per step, taking a data set, and stopping before
/// the next step when cancelled.
/// </summary>
public sealed class McpTestRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bowire-mcp-run-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private async Task<string> SuiteAsync()
    {
        var path = Path.Combine(_dir, "suite.json");
        await File.WriteAllTextAsync(path, """
            {
              "name": "echo-suite", "protocol": "_test_runner_stub", "serverUrl": "http://stub.local",
              "tests": [
                { "name": "first", "service": "Svc", "method": "Echo", "messages": ["{\"id\": \"{{id}}\"}"],
                  "assert": [ { "path": "echo", "op": "eq", "expected": "{{id}}" } ] },
                { "name": "second", "service": "Svc", "method": "Echo", "messages": ["{\"id\": \"2\"}"],
                  "assert": [ { "path": "echo", "op": "eq", "expected": "2" } ] }
              ]
            }
            """, TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public async Task A_Data_Driven_Run_Reports_Every_Step_And_Passes()
    {
        var data = Path.Combine(_dir, "rows.csv");
        await File.WriteAllTextAsync(data, "id\n7\n8\n", TestContext.Current.CancellationToken);
        var lines = new List<string>();

        var outcome = await new McpTestRunner().RunAsync(
            new BowireTestRunRequest(await SuiteAsync(), data, FailFast: false, JUnitPath: null, ReportPath: null),
            line => { lines.Add(line); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(["step 1/4 · first (iteration 1/2)", "step 2/4 · second (iteration 1/2)",
                      "step 3/4 · first (iteration 2/2)", "step 4/4 · second (iteration 2/2)"], lines);
        Assert.Contains("2/2 iterations", outcome.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_Stops_Before_The_Next_Step()
    {
        using var cts = new CancellationTokenSource();
        var lines = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new McpTestRunner().RunAsync(
            new BowireTestRunRequest(SuiteAsync().GetAwaiter().GetResult(), null, false, null, null),
            async line =>
            {
                lines.Add(line);
                // The agent cancels while the first step runs.
                await cts.CancelAsync();
            },
            cts.Token));

        Assert.Equal(["step 1/2 · first"], lines);
    }

    [Fact]
    public async Task A_Bad_Data_File_Is_Refused_Before_Any_Step()
    {
        var data = Path.Combine(_dir, "rows.csv");
        await File.WriteAllTextAsync(data, "id\n", TestContext.Current.CancellationToken);
        var lines = new List<string>();

        var outcome = await new McpTestRunner().RunAsync(
            new BowireTestRunRequest(await SuiteAsync(), data, false, null, null),
            line => { lines.Add(line); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("rows.csv has a header but no rows", outcome.Errors, StringComparison.Ordinal);
        Assert.Empty(lines);
    }
}
