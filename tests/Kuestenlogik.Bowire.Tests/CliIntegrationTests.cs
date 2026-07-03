// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Black-box CLI integration tests. There's no dedicated CLI-test
/// framework (System.CommandLine 2.0 dropped its <c>TestConsole</c>), so
/// these drive the real entry point — <see cref="BowireCli.RunAsync"/>
/// with an args array + captured writers — and assert on the exit code
/// and stdout. That exercises the actual wiring: option parsing (the
/// <c>--flag</c> tokens), subcommand dispatch, exit-code contract, and
/// report side-effects, in-process (no process spawn, CI-safe).
/// </summary>
public sealed class CliIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public CliIntegrationTests()
    {
        _tempDir = SafePath.Combine(Path.GetTempPath(), "bowire-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var cfg = BowireConfiguration.Build(args);
        await using var outW = new StringWriter();
        await using var errW = new StringWriter();
        var code = await BowireCli.RunAsync(args, cfg, pluginDir: "", outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    // ---- help / dispatch ----

    [Fact]
    public async Task Help_ListsTheTestAndContractSubcommands()
    {
        var (code, stdout, _) = await RunAsync("--help");
        Assert.Equal(0, code);
        Assert.Contains("test", stdout, StringComparison.Ordinal);
        Assert.Contains("contract", stdout, StringComparison.Ordinal);
        Assert.Contains("mock", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOption_IsAParseError_NonZeroExit()
    {
        var (code, _, _) = await RunAsync("test", "--totally-not-a-flag", "x");
        Assert.NotEqual(0, code);
    }

    [Fact]
    public async Task FailOn_RejectsUnknownValue()
    {
        // AcceptOnlyFromAmong("any","never") — anything else is a parse error.
        var (code, _, _) = await RunAsync("test", "whatever.json", "--fail-on", "sometimes");
        Assert.NotEqual(0, code);
    }

    // ---- bowire test ----

    [Fact]
    public async Task Test_FlowAgainstProvider_PassesWithExitZero()
    {
        using var server = new LoopbackServer((_, _) => (200, "{\"user\":{\"id\":42}}"));
        var flow = await WriteFlowAsync("ok.json", server.Url, "42"); // expects id==42

        var (code, stdout, _) = await RunAsync("test", flow);
        Assert.Equal(0, code);
        Assert.Contains("expectations passed", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_FailingFlow_ExitsOne_ButNeverSoftensToZero()
    {
        using var server = new LoopbackServer((_, _) => (200, "{\"user\":{\"id\":1}}"));
        var flow = await WriteFlowAsync("bad.json", server.Url, "999"); // expects id==999

        var (strict, _, _) = await RunAsync("test", flow);
        Assert.Equal(1, strict);

        var (soft, _, _) = await RunAsync("test", flow, "--fail-on", "never");
        Assert.Equal(0, soft);
    }

    [Fact]
    public async Task Test_Workspace_RunsEveryFlow_AndEmitsJUnit()
    {
        using var server = new LoopbackServer((_, _) => (200, "{\"user\":{\"id\":1}}"));
        var wsFlows = SafePath.Combine(_tempDir, "ws", "flows");
        Directory.CreateDirectory(wsFlows);
        await WriteFlowAtAsync(SafePath.Combine(wsFlows, "happy.json"), server.Url, "1");
        await WriteFlowAtAsync(SafePath.Combine(wsFlows, "broken.json"), server.Url, "999");
        var junit = SafePath.Combine(_tempDir, "results.xml");

        var (code, stdout, _) = await RunAsync("test", "--workspace", SafePath.Combine(_tempDir, "ws"), "--junit", junit);

        Assert.Equal(1, code); // one flow failed → aggregate non-zero
        Assert.Contains("2 files", stdout, StringComparison.Ordinal);
        // Per-flow reports written (results.<flow>.xml).
        Assert.True(File.Exists(SafePath.Combine(_tempDir, "results.happy.xml")));
        Assert.True(File.Exists(SafePath.Combine(_tempDir, "results.broken.xml")));
    }

    // ---- bowire contract publish + verify ----

    [Fact]
    public async Task Contract_Publish_ThenVerify_RoundTrips()
    {
        var recPath = SafePath.Combine(_tempDir, "rec.json");
        await File.WriteAllTextAsync(recPath, """
        { "recordings": [ {
          "id":"r","name":"orders-consumer","recordingFormatVersion":2,
          "steps":[ { "id":"s1","protocol":"rest","httpVerb":"GET","httpPath":"/orders/42",
                      "status":"OK","response":"{\"id\":42,\"status\":\"shipped\"}" } ]
        } ] }
        """, TestContext.Current.CancellationToken);
        var contractPath = SafePath.Combine(_tempDir, "orders.pact.json");

        var (pubCode, pubOut, _) = await RunAsync(
            "contract", "publish", recPath, "--provider", "order-service", "--out", contractPath);
        Assert.Equal(0, pubCode);
        Assert.Contains("1 interaction", pubOut, StringComparison.Ordinal);
        Assert.True(File.Exists(contractPath));

        // Provider that honours the contract (status field present, id kept).
        using var provider = new LoopbackServer((_, _) => (200, "{\"id\":42,\"status\":\"delivered\"}"));
        var (verOk, _, _) = await RunAsync("contract", "verify", contractPath, "--provider-url", provider.Url);
        Assert.Equal(0, verOk);

        // Provider that drops the depended-on field → contract broken.
        using var broken = new LoopbackServer((_, _) => (200, "{\"id\":42}"));
        var (verFail, _, _) = await RunAsync("contract", "verify", contractPath, "--provider-url", broken.Url);
        Assert.Equal(1, verFail);
    }

    [Fact]
    public async Task Contract_Publish_RequiresProvider()
    {
        var recPath = SafePath.Combine(_tempDir, "rec.json");
        await File.WriteAllTextAsync(recPath, """{ "recordings":[{"id":"r","name":"n","recordingFormatVersion":2,"steps":[{"id":"s","protocol":"rest","httpVerb":"GET","httpPath":"/x","status":"OK"}]}] }""", TestContext.Current.CancellationToken);
        var (code, _, _) = await RunAsync("contract", "publish", recPath);
        Assert.NotEqual(0, code); // --provider is required (exit 64)
    }

    // ---- helpers ----

    private async Task<string> WriteFlowAsync(string name, string serverBase, string expectedId)
    {
        var path = SafePath.Combine(_tempDir, name);
        await WriteFlowAtAsync(path, serverBase, expectedId);
        return path;
    }

    private static async Task WriteFlowAtAsync(string path, string serverBase, string expectedId)
    {
        var flow = $$"""
        {
          "id":"f","name":"f",
          "nodes":[ {
            "id":"n1","type":"request","protocol":"rest",
            "serverUrl":"{{serverBase}}/x","service":"","method":"GET","body":"{}",
            "expectations":[ { "id":"e1","kind":"body-path","operator":"equals","target":"$.user.id","expected":"{{expectedId}}" } ]
          } ]
        }
        """;
        await File.WriteAllTextAsync(path, flow, TestContext.Current.CancellationToken);
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string Url { get; }

        public LoopbackServer(Func<string, string, (int Status, string Body)> responder)
        {
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            Url = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token); }
                    catch { return; }
                    try
                    {
                        var (status, body) = responder(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath);
                        ctx.Response.StatusCode = status;
                        ctx.Response.ContentType = "application/json";
                        var bytes = Encoding.UTF8.GetBytes(body);
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token);
                    }
                    catch { /* best-effort */ }
                    finally { try { ctx.Response.OutputStream.Close(); } catch { } }
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { ((IDisposable)_listener).Dispose(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
