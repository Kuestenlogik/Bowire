// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Management;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Lifecycle tests for <see cref="BowireMockHostManager"/> — the single
/// owner of UI-spun mock servers after #223's consolidation. Covers
/// Start / List / Get / Stop / DisposeAsync plus the request-log wiring
/// used by #57's per-mock log view.
/// </summary>
public sealed class BowireMockHostManagerTests
{
    private static string BuildRecording()
    {
        var rec = new BowireRecording
        {
            Id = "rec_test",
            Name = "manager-test",
            RecordingFormatVersion = 2,
        };
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "step_one",
            Protocol = "rest",
            Service = "S",
            Method = "M",
            MethodType = "Unary",
            HttpPath = "/probe",
            HttpVerb = "GET",
            Status = "OK",
            Response = "ok",
        });
        return JsonSerializer.Serialize(rec);
    }

    private static BowireMockHostManager NewManager() => new();

    [Fact]
    public async Task Start_Then_List_Surfaces_Handle()
    {
        await using var manager = NewManager();

        var handle = await manager.StartAsync(BuildRecording(), "rec_test", "demo", port: 0, TestContext.Current.CancellationToken);

        Assert.NotNull(handle);
        Assert.Equal("demo", handle.Label);
        Assert.Equal("rec_test", handle.RecordingId);
        Assert.True(handle.Port > 0, "port=0 should resolve to an OS-assigned port via the rolling allocator");
        Assert.Equal($"http://127.0.0.1:{handle.Port}", handle.Url);

        var list = manager.List();
        Assert.Single(list);
        Assert.Equal(handle.MockId, list.First().MockId);
    }

    [Fact]
    public async Task Get_Returns_Null_For_Unknown_Mock()
    {
        await using var manager = NewManager();
        Assert.Null(manager.Get("does-not-exist"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Get_Returns_Handle_After_Start()
    {
        await using var manager = NewManager();
        var handle = await manager.StartAsync(BuildRecording(), "rec_test", "lookup", port: 0, TestContext.Current.CancellationToken);
        Assert.Equal(handle, manager.Get(handle.MockId));
    }

    [Fact]
    public async Task GetRequestLog_Returns_Log_While_Running_And_Null_After_Stop()
    {
        await using var manager = NewManager();
        var handle = await manager.StartAsync(BuildRecording(), "rec_test", "log-pin", port: 0, TestContext.Current.CancellationToken);

        var log = manager.GetRequestLog(handle.MockId);
        Assert.NotNull(log);

        await manager.StopAsync(handle.MockId, TestContext.Current.CancellationToken);

        Assert.Null(manager.GetRequestLog(handle.MockId));
    }

    [Fact]
    public async Task Stop_Removes_Mock()
    {
        await using var manager = NewManager();
        var handle = await manager.StartAsync(BuildRecording(), "rec_test", "lifecycle", port: 0, TestContext.Current.CancellationToken);

        var stopped = await manager.StopAsync(handle.MockId, TestContext.Current.CancellationToken);

        Assert.True(stopped);
        Assert.Empty(manager.List());
        Assert.Null(manager.Get(handle.MockId));
    }

    [Fact]
    public async Task Stop_Returns_False_For_Unknown_Mock()
    {
        await using var manager = NewManager();
        Assert.False(await manager.StopAsync("nope", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_Stops_All_Running_Mocks()
    {
        var manager = NewManager();
        var a = await manager.StartAsync(BuildRecording(), "rec_a", "a", port: 0, TestContext.Current.CancellationToken);
        var b = await manager.StartAsync(BuildRecording(), "rec_b", "b", port: 0, TestContext.Current.CancellationToken);
        Assert.Equal(2, manager.List().Count);

        await manager.DisposeAsync();

        Assert.Empty(manager.List());
        Assert.Null(manager.Get(a.MockId));
        Assert.Null(manager.Get(b.MockId));
    }

    [Fact]
    public async Task Start_Generates_Distinct_Mock_Ids()
    {
        await using var manager = NewManager();
        var first = await manager.StartAsync(BuildRecording(), "rec_test", "first", port: 0, TestContext.Current.CancellationToken);
        var second = await manager.StartAsync(BuildRecording(), "rec_test", "second", port: 0, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.MockId, second.MockId);
    }

    [Fact]
    public async Task RequestLog_Receives_Live_Traffic()
    {
        // The MockRequestLog observer is wired into the MockServer
        // pipeline at Start. A real GET against the mock should land
        // an entry in the log so #57's per-mock log view has data.
        await using var manager = NewManager();
        var handle = await manager.StartAsync(BuildRecording(), "rec_test", "logged", port: 0, TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{handle.Port}") };
        try
        {
            using var _ = await http.GetAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken);
        }
        catch (HttpRequestException)
        {
            // CI loopback hiccup — the log assertion below is the signal that matters.
        }

        var log = manager.GetRequestLog(handle.MockId)!;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && log.TotalRequests == 0)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.True(log.TotalRequests >= 1,
            $"expected the mock's request log to have ≥ 1 entry within 5 s, got {log.TotalRequests}");
        var snapshot = log.Snapshot();
        Assert.Contains(snapshot, e => e.Path == "/probe");
    }
}
