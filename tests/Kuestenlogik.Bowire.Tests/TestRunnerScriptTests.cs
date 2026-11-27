// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Scripting;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #672 — pre-request and post-response scripts in <c>bowire test</c>, against
/// the stub protocol, which echoes the request's <c>id</c> back: a script
/// changes the request before it goes out, reads the response after, shares
/// values across the run, and a failing or runaway script fails its test.
/// </summary>
public sealed class TestRunnerScriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bowire-tr-scripts-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private async Task<string> CollectionAsync(string tests)
    {
        var path = Path.Combine(_dir, "suite.json");
        await File.WriteAllTextAsync(path, $$"""
            {
              "name": "scripted",
              "protocol": "_test_runner_stub",
              "serverUrl": "http://stub.local",
              "environment": { "seed": "41" },
              "tests": [ {{tests}} ]
            }
            """, TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<(int Code, string Out)> RunAsync(string path, IConfiguration? cfg = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path, Configuration = cfg }, stdout, stderr,
            TestContext.Current.CancellationToken);
        return (code, stdout + stderr.ToString());
    }

    [Fact]
    public async Task A_Pre_Script_Changes_The_Request_And_The_Assertions_See_It()
    {
        var path = await CollectionAsync("""
            {
              "name": "signed",
              "service": "Svc", "method": "Echo",
              "messages": ["{\"id\": \"0\"}"],
              "preScript": "var b = JSON.parse(ctx.request.body); b.id = String(Number(ctx.env.get('seed')) + 1); ctx.request.body = JSON.stringify(b); ctx.log('signed', b.id);",
              "assert": [ { "path": "response.echo", "op": "eq", "expected": "42" } ]
            }
            """);

        var (code, output) = await RunAsync(path);

        Assert.Equal(0, code);
        Assert.Contains("log: signed 42", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Env_Set_And_Captured_Values_Carry_Over_To_Later_Tests()
    {
        var path = await CollectionAsync("""
            {
              "name": "first",
              "service": "Svc", "method": "Echo",
              "messages": ["{\"id\": \"7\"}"],
              "postScript": "ctx.vars.captured.lastEcho = ctx.response.json().echo; ctx.env.set('token', 'tok-' + ctx.response.json().echo);"
            },
            {
              "name": "second",
              "service": "Svc", "method": "Echo",
              "messages": ["{\"id\": \"${token}\"}"],
              "preScript": "ctx.assert.equal(ctx.vars.captured.lastEcho, '7', 'captured from the first test');",
              "assert": [ { "path": "response.echo", "op": "eq", "expected": "tok-7" } ]
            }
            """);

        var (code, output) = await RunAsync(path);

        Assert.True(code == 0, output);
    }

    [Fact]
    public async Task A_Failing_Post_Script_Fails_The_Test_With_Its_Message()
    {
        var path = await CollectionAsync("""
            {
              "name": "checked",
              "service": "Svc", "method": "Echo",
              "messages": ["{\"id\": \"5\"}"],
              "postScript": "ctx.assert.equal(ctx.response.json().echo, '6', 'echo');"
            }
            """);

        var (code, output) = await RunAsync(path);

        Assert.Equal(1, code);
        Assert.Contains("post-response script: assert.equal failed: echo (actual=\"5\", expected=\"6\")", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Throwing_Pre_Script_Stops_The_Call_And_Fails_The_Run()
    {
        var path = await CollectionAsync("""
            {
              "name": "broken",
              "service": "Svc", "method": "Echo",
              "messages": ["{}"],
              "preScript": "throw new Error('no signing key');"
            }
            """);

        var (code, output) = await RunAsync(path);

        Assert.NotEqual(0, code);
        Assert.Contains("pre-request script: no signing key", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Runaway_Script_Is_Stopped_At_The_Configured_Limit()
    {
        var path = await CollectionAsync("""
            { "name": "loop", "service": "Svc", "method": "Echo", "messages": ["{}"], "preScript": "for (;;) {}" }
            """);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bowire:Scripting:TimeoutMs"] = "200" })
            .Build();

        var (code, output) = await RunAsync(path, cfg);

        Assert.NotEqual(0, code);
        Assert.Contains("longer than 0.2 s and was stopped", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_A_Script_Engine_A_Script_Is_A_Failure_Not_A_Skip()
    {
        var scripts = new TestScripts(new BowireScriptLimits(), runner: null);
        var failure = scripts.RunPre("ctx.log(1)", "rest", "Svc", "Echo", new BowireScriptRequest(),
            new Dictionary<string, string>(), new List<string>());
        Assert.Equal(BowireScriptRunners.NotInstalled, failure);
    }
}
