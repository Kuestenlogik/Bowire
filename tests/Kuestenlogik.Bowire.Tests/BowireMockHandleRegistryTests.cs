// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Mock;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the MCP-side registry that owns running <see cref="MockServer"/>
/// handles. The negative paths (unknown handle, empty snapshot,
/// dispose-on-empty) run without a real Kestrel instance; the
/// register/get/remove/drain happy paths spin up minimal recording-backed
/// servers so the dispose-while-populated branch is exercised.
/// </summary>
public sealed class BowireMockHandleRegistryTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task New_Registry_Snapshot_Is_Empty()
    {
        await using var registry = new BowireMockHandleRegistry();

        var snapshot = registry.Snapshot();

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task TryGet_Unknown_Handle_Returns_False()
    {
        await using var registry = new BowireMockHandleRegistry();

        var found = registry.TryGet("does-not-exist", out var server);

        Assert.False(found);
        Assert.Null(server);
    }

    [Fact]
    public async Task RemoveAndDisposeAsync_Unknown_Handle_Returns_False()
    {
        await using var registry = new BowireMockHandleRegistry();

        var stopped = await registry.RemoveAndDisposeAsync("nope");

        Assert.False(stopped);
    }

    [Fact]
    public async Task DisposeAsync_On_Empty_Registry_Is_Idempotent()
    {
        var registry = new BowireMockHandleRegistry();

        await registry.DisposeAsync();
        await registry.DisposeAsync(); // second call must not throw

        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task TryGet_With_Empty_Or_Whitespace_Returns_False()
    {
        await using var registry = new BowireMockHandleRegistry();

        Assert.False(registry.TryGet("", out var s1));
        Assert.Null(s1);

        Assert.False(registry.TryGet(" ", out var s2));
        Assert.Null(s2);
    }

    [Fact]
    public async Task Register_Returns_Twelve_Char_Handle_And_TryGet_Resolves_Server()
    {
        var path = await WriteSingleStepRecordingAsync("ping1");
        await using var registry = new BowireMockHandleRegistry();
        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        var handle = registry.Register(server);

        Assert.Equal(12, handle.Length);
        Assert.True(registry.TryGet(handle, out var resolved));
        Assert.Same(server, resolved);

        var snapshot = registry.Snapshot();
        Assert.Single(snapshot);
        Assert.True(snapshot.ContainsKey(handle));
    }

    [Fact]
    public async Task RemoveAndDisposeAsync_Known_Handle_Removes_Server_And_Returns_True()
    {
        var path = await WriteSingleStepRecordingAsync("ping2");
        await using var registry = new BowireMockHandleRegistry();
        var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);
        var handle = registry.Register(server);

        var stopped = await registry.RemoveAndDisposeAsync(handle);

        Assert.True(stopped);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task DisposeAsync_With_Active_Handles_Drains_Dictionary()
    {
        var path1 = await WriteSingleStepRecordingAsync("ping3");
        var path2 = await WriteSingleStepRecordingAsync("ping4");
        var registry = new BowireMockHandleRegistry();
        var s1 = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path1, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);
        var s2 = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path2, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);
        registry.Register(s1);
        registry.Register(s2);

        await registry.DisposeAsync();

        Assert.Empty(registry.Snapshot());
    }

    private async Task<string> WriteSingleStepRecordingAsync(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bowire-mockreg-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var path = Path.Combine(dir, "rec.json");
        await File.WriteAllTextAsync(path, /*lang=json,strict*/ """
            {
              "id": "rec_ping",
              "name": "ping",
              "recordingFormatVersion": 2,
              "steps": [
                {
                  "id": "step_ping",
                  "protocol": "rest",
                  "service": "Ping",
                  "method": "Ping",
                  "methodType": "Unary",
                  "httpPath": "/ping",
                  "httpVerb": "GET",
                  "status": "OK",
                  "response": "pong"
                }
              ]
            }
            """);
        return path;
    }
}
