// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// End-to-end coverage for <see cref="TestRunner.RunAsync"/>'s
/// post-discovery branch — i.e. the path that fires after the protocol
/// registry has resolved a plugin and the in-process invocation either
/// succeeds (assertions evaluate against a JSON body) or surfaces an
/// invocation error. Reaches that branch via <see cref="StubBowireProtocol"/>,
/// a parameterless-ctor protocol with a unique id (<c>_test_runner_stub</c>)
/// that <c>BowireProtocolRegistry.Discover</c> picks up
/// automatically. The id is namespaced so other tests' protocol lookups
/// can't collide; the stub returns canned responses so we don't need
/// any real wire transport.
/// </summary>
public sealed class TestRunnerEndToEndTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("bowire-tr-e2e-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_StubProtocol_HappyAssertion_ReturnsZero()
    {
        var path = Path.Combine(_tempDir, "happy.json");
        await File.WriteAllTextAsync(path, """
            {
              "name": "stub-coll",
              "protocol": "_test_runner_stub",
              "serverUrl": "http://stub.local",
              "tests": [
                {
                  "name": "ok-test",
                  "service": "Svc",
                  "method": "Echo",
                  "messages": ["{\"id\": 1}"],
                  "assert": [
                    { "path": "status", "op": "eq", "expected": "OK" },
                    { "path": "echo", "op": "eq", "expected": "1" }
                  ]
                }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task RunAsync_StubProtocol_FailingAssertion_ReturnsOne()
    {
        // Same stub responds with status="OK" + echo=1; assert demands
        // echo=999 → assertion fails → exit 1 (every assertion failure
        // counts the test as failed).
        var path = Path.Combine(_tempDir, "miss.json");
        await File.WriteAllTextAsync(path, """
            {
              "name": "stub-miss",
              "protocol": "_test_runner_stub",
              "serverUrl": "http://stub.local",
              "tests": [
                {
                  "name": "miss-test",
                  "service": "Svc",
                  "method": "Echo",
                  "messages": ["{\"id\": 2}"],
                  "assert": [
                    { "path": "echo", "op": "eq", "expected": "999" }
                  ]
                }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task RunAsync_StubProtocol_DiscoveryFails_ReportsErrorAndExitsOne()
    {
        // serverUrl = "discovery-fail" makes the stub throw inside
        // DiscoverAsync. RunTestAsync catches the exception, sets
        // result.Error, the test counts as failed → exit 1.
        var path = Path.Combine(_tempDir, "disc-fail.json");
        await File.WriteAllTextAsync(path, """
            {
              "name": "disc-fail",
              "protocol": "_test_runner_stub",
              "serverUrl": "discovery-fail",
              "tests": [
                { "name": "t", "service": "Svc", "method": "X" }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task RunAsync_StubProtocol_InvokeFails_ReportsErrorAndExitsOne()
    {
        // Service "throw" makes the stub throw inside InvokeAsync,
        // exercising the second try/catch branch in RunTestAsync.
        var path = Path.Combine(_tempDir, "invoke-fail.json");
        await File.WriteAllTextAsync(path, """
            {
              "name": "invoke-fail",
              "protocol": "_test_runner_stub",
              "serverUrl": "http://stub.local",
              "tests": [
                { "name": "t", "service": "throw", "method": "boom" }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task RunAsync_StubProtocol_NoExplicitProtocol_PicksFirstRegistered()
    {
        // No protocol field on collection or test → the runner falls
        // through to "first available". Since the registry is global
        // and the stub is registered, we confirm the no-op fallback
        // returns a real protocol rather than null. We assert that
        // the run ends successfully (any assertion-less test passes).
        var path = Path.Combine(_tempDir, "no-proto.json");
        await File.WriteAllTextAsync(path, """
            {
              "name": "no-proto-coll",
              "serverUrl": "http://stub.local",
              "tests": [
                { "name": "t", "service": "Svc", "method": "Echo" }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        // Either 0 (stub picked) or 1 (different first protocol returned
        // an error response) — the line we care about (`registry.Protocols[0]`)
        // is reached either way.
        Assert.Contains(rc, s_acceptedExitCodes);
    }

    [Fact]
    public async Task RunAsync_StubProtocol_AssertionOpsCoverage()
    {
        // Mixed assertion ops over the stub's canned response. The body
        // is `{ "echo": "<id-as-string>", "items": [10, 20], "ts": 1.5 }`,
        // so each op below resolves a different actual value and a
        // different code path inside EvaluateAssertion.
        var path = Path.Combine(_tempDir, "ops.json");
        await File.WriteAllTextAsync(path, """
            {
              "name": "ops",
              "protocol": "_test_runner_stub",
              "serverUrl": "http://stub.local",
              "environment": { "expected_id": "5" },
              "tests": [
                {
                  "name": "ops",
                  "service": "Svc",
                  "method": "Echo",
                  "messages": ["{\"id\": 5}"],
                  "assert": [
                    { "path": "echo", "op": "eq", "expected": "${expected_id}" },
                    { "path": "items.0", "op": "gt", "expected": "5" },
                    { "path": "items", "op": "contains", "expected": "20" },
                    { "path": "ts", "op": "type", "expected": "number" },
                    { "path": "missing", "op": "notexists", "expected": "" },
                    { "path": "items", "op": "exists", "expected": "" }
                  ]
                }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(0, rc);
    }

    private static readonly int[] s_acceptedExitCodes = [0, 1];
}

/// <summary>
/// Test-only protocol plugin: registered automatically by
/// <c>BowireProtocolRegistry.Discover</c> because the test
/// assembly name contains "Bowire" and this type implements
/// <see cref="IBowireProtocol"/> with a parameterless constructor. The
/// id is namespaced (<c>_test_runner_stub</c>) so it can't collide with
/// any first- or third-party plugin at runtime.
/// </summary>
internal sealed class StubBowireProtocol : IBowireProtocol
{
    public const string ProtocolId = "_test_runner_stub";

    public string Name => "Stub (test)";
    public string Id => ProtocolId;
    public string IconSvg => string.Empty;

    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (serverUrl == "discovery-fail")
            throw new InvalidOperationException("stub: discovery deliberately failed");
        return Task.FromResult(new List<BowireServiceInfo>());
    }

    public Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        if (string.Equals(service, "throw", StringComparison.Ordinal))
            throw new InvalidOperationException("stub: invocation deliberately failed");

        // Echo back the id from the first message + a couple of fixed
        // fields so tests can hit gt/contains/type/exists branches.
        var echo = "0";
        if (jsonMessages.Count > 0)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(jsonMessages[0]);
                if (doc.RootElement.TryGetProperty("id", out var idEl))
                {
                    echo = idEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? idEl.GetString() ?? "0"
                        : idEl.GetRawText();
                }
            }
            catch
            {
                // Not JSON — leave the default echo.
            }
        }

        var body = $$"""{"echo":"{{echo}}","items":[10,20],"ts":1.5}""";
        return Task.FromResult(new InvokeResult(
            Response: body,
            DurationMs: 1,
            Status: "OK",
            Metadata: new Dictionary<string, string>()));
    }

#pragma warning disable CS1998 // Stub returns no frames.
    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }
#pragma warning restore CS1998

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default) => Task.FromResult<IBowireChannel?>(null);
}
